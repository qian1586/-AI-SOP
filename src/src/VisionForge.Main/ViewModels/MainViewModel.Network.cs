using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Infrastructure.Integration;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 多工位联网：本机把实时状态上报给"汇总终端"，并把每件数据推给 MES。
///
/// <para><b>三种角色，同一套程序：</b></para>
/// <list type="number">
///   <item><b>纯工位</b>：只上报（填工位编号 + 终端地址）</item>
///   <item><b>汇总终端</b>：启动内置看板服务，浏览器打开就能看全场（可同时兼工位）</item>
///   <item><b>单机</b>：都不启用，一切照旧跑</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly JsonSerializerOptions NetworkJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private StationHub? _hub;
    private DispatcherTimer? _reportTimer;
    private DispatcherTimer? _mesTimer;
    private HttpClient? _reportClient;
    private IMesClient? _mesClient;
    private MesUploadQueue? _mesQueue;

    private readonly List<PieceStepReport> _currentPieceSteps = new();
    private string _networkStatusText = "未启用（在「系统设置 → 工位联网」里配置）";

    /// <summary>打开汇总看板（浏览器）。</summary>
    public ICommand OpenWallboardCommand { get; }

    /// <summary>复制看板地址（现场要在别的电脑/手机上输入时用）。</summary>
    public ICommand CopyWallboardUrlCommand { get; }

    /// <summary>测试 MES 连接。</summary>
    public ICommand TestMesConnectionCommand { get; }

    /// <summary>立即补发 MES 待发队列里积压的数据。</summary>
    public ICommand FlushMesQueueCommand { get; }

    /// <summary>联网状态一句话（设置页显示）。</summary>
    public string NetworkStatusText
    {
        get => _networkStatusText;
        private set => SetProperty(ref _networkStatusText, value);
    }

    public bool IsHubRunning => _hub?.IsRunning == true;

    /// <summary>
    /// 看板地址（设置页直接显示出来，不用点也知道该在浏览器里输什么）。
    ///
    /// <para>本机是终端时给出"本机局域网 IP:端口"—— 现场另一台电脑/手机要访问的就是这个地址；
    /// 不是终端时显示配置里的终端地址。</para>
    /// </summary>
    public string WallboardUrlText
    {
        get
        {
            if (_hub?.IsRunning == true) return $"本机看板：http://{LocalIpv4()}:{_hub.Port}/";

            string? hubUrl = _settings.Current.Station?.HubUrl;
            return string.IsNullOrWhiteSpace(hubUrl)
                ? "看板地址：未配置（勾选「本机作为汇总终端」或填「汇总终端地址」）"
                : "看板地址：" + hubUrl.TrimEnd('/') + "/";
        }
    }

    /// <summary>取本机局域网 IPv4（取第一个非回环地址；取不到就退回 localhost）。</summary>
    private static string LocalIpv4()
    {
        try
        {
            foreach (var address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(address))
                    return address.ToString();
            }
        }
        catch { /* 取不到就用 localhost */ }

        return "localhost";
    }

    /// <summary>MES 待发积压条数（现场据此判断"是不是网络不通"）。</summary>
    public string MesQueueText =>
        _mesQueue is null ? "MES：未启用" : $"MES 待发积压：{_mesQueue.PendingCount} 条";

    // ==================================================================
    // 启停
    // ==================================================================
    private void StartNetwork()
    {
        StopNetwork();

        var options = _settings.Current.Station;
        if (options is null || !options.Enabled)
        {
            NetworkStatusText = "未启用（单机模式）";
            OnPropertyChanged(nameof(IsHubRunning));
            OnPropertyChanged(nameof(MesQueueText));
            return;
        }

        var parts = new List<string>();

        // ① 作为汇总终端：对外提供看板
        if (options.IsHub)
        {
            try
            {
                _hub = new StationHub("AI-SOP 汇总终端", _log, _settings.Current.DataRoot);
                _hub.Start(Math.Clamp(options.HubPort, 1024, 65535));
                parts.Add($"看板服务已启动：http://本机IP:{_hub.Port}/");
            }
            catch (Exception ex)
            {
                _hub = null;
                parts.Add("看板服务启动失败（端口可能被占用）：" + ex.Message);
                _log.Error("汇总终端启动失败", ex);
            }
        }

        // ② 作为工位：定时上报
        if (!string.IsNullOrWhiteSpace(options.HubUrl))
        {
            _reportClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            _reportTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(options.ReportIntervalSec, 1, 60)),
            };
            _reportTimer.Tick += (_, _) => _ = ReportOnceAsync();
            _reportTimer.Start();

            _ = ReportOnceAsync();   // 立即报一次，看板上不用等
            parts.Add($"已向 {options.HubUrl} 上报（每 {options.ReportIntervalSec}s）");
        }

        // ③ MES：客户端 + 补发队列 + 定时补发
        _mesQueue = new MesUploadQueue(Path.Combine(_settings.Current.DataRoot, "mes-queue"), _log);
        _mesClient = CreateMesClient();

        if (_mesClient is { IsEnabled: true })
        {
            _mesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _mesTimer.Tick += (_, _) => _ = FlushMesQueueAsync();
            _mesTimer.Start();
            parts.Add("MES 上传已启用：" + _mesClient.Name);
        }

        // ④ 模板下发：定时来问终端有没有新版本（关机的工位一开机就会自动补上）
        StartTemplatePolling();

        NetworkStatusText = parts.Count == 0
            ? "已启用但没填终端地址：只做本机运行"
            : string.Join("；", parts);

        _log.Info("工位联网：" + NetworkStatusText);
        OnPropertyChanged(nameof(IsHubRunning));
        OnPropertyChanged(nameof(MesQueueText));
        OnPropertyChanged(nameof(WallboardUrlText));
    }

    private void StopNetwork()
    {
        _reportTimer?.Stop();
        _reportTimer = null;
        _mesTimer?.Stop();
        _mesTimer = null;

        _hub?.Dispose();
        _hub = null;

        _reportClient?.Dispose();
        _reportClient = null;

        _mesClient = null;

        StopTemplatePolling();
    }

    private IMesClient CreateMesClient()
    {
        var mes = _settings.Current.Mes;
        if (mes is null) return new FileMesClient(Path.Combine(_settings.Current.DataRoot, "mes-outbox"), _log);

        // 有接口地址就按 HTTP 传；没地址（或没启用）就落本地文件，保证数据不丢、也方便对接方看格式
        if (mes.Enabled && !string.IsNullOrWhiteSpace(mes.EndpointUrl))
            return new HttpMesClient(mes, _log);

        return new FileMesClient(Path.Combine(_settings.Current.DataRoot, "mes-outbox"), _log);
    }

    // ==================================================================
    // 工位上报
    // ==================================================================
    /// <summary>把当前状态打包成一条上报（看板要的就是这些字段）。</summary>
    private StationReport BuildStationReport()
    {
        var station = _settings.Current.Station;
        var recipe = ActiveRecipe;

        var steps = Sop.Steps.Select(s => new StationStepReport
        {
            Seq = s.Seq,
            Name = s.Name,
            StateKey = s.StateKey,
            DurationSec = _lastDurationMs.TryGetValue(s.Seq, out double ms) ? Math.Round(ms / 1000.0, 1) : 0,
        }).ToList();

        return new StationReport
        {
            StationCode = station.StationCode,
            StationName = station.StationName,
            LineName = station.LineName,
            ProductModel = ProductModel,
            RecipeName = recipe?.Name ?? string.Empty,
            BatchNo = BatchNo,
            Operator = _settings.Current.OperatorName,
            StateText = StationStateText,
            StateKey = StationStateKey,
            CurrentStepName = Sop.CurrentStep?.Name ?? "—",
            StepDone = Sop.PassedCount,
            StepTotal = Sop.TotalSteps,
            OkCount = _session.OkCount,
            NgCount = _session.NgCount,
            CompletedPieces = _session.CompletedPieces,
            PassRate = _session.PassRate,
            LastCycleSec = _recentCycleSec.Count > 0 ? _recentCycleSec[^1] : 0,
            AverageCycleSec = _recentCycleSec.Count > 0 ? _recentCycleSec.Average() : 0,
            TodayAlarm = int.TryParse(TodayAlarmCountText, out int alarm) ? alarm : 0,
            TodaySkipped = int.TryParse(TodaySkippedCountText, out int skip) ? skip : 0,
            ReportedAt = DateTime.Now,
            Steps = steps,
        };
    }

    private async Task ReportOnceAsync()
    {
        var client = _reportClient;
        var hubUrl = _settings.Current.Station?.HubUrl;
        if (client is null || string.IsNullOrWhiteSpace(hubUrl)) return;

        try
        {
            string url = hubUrl.TrimEnd('/') + "/api/report";
            string json = JsonSerializer.Serialize(BuildStationReport(), NetworkJson);

            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content).ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
                NetworkStatusText = $"上报失败：HTTP {(int)response.StatusCode}（检查终端地址 / 防火墙）";
            else
                NetworkStatusText = $"已向 {hubUrl} 上报 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            // 上报失败不影响生产：本地一切照常，界面只提示一句
            NetworkStatusText = "上报失败：" + ex.Message;
        }
    }

    // ==================================================================
    // MES 上传
    // ==================================================================
    /// <summary>
    /// 一件做完时把这一件的完整数据推给 MES（含每道工序的开始/结束时刻与用时）。
    ///
    /// <para>由 <c>NotePieceCompleted</c> 调用 —— 那里正好是"整件完成"的唯一出口。</para>
    /// </summary>
    private void QueuePieceToMes(bool isOk, string reason)
    {
        var station = _settings.Current.Station;
        var mes = _settings.Current.Mes;
        if (mes is null || (!mes.Enabled && !mes.SaveLocalCopy)) return;

        var piece = new PieceReport
        {
            StationCode = station?.StationCode ?? string.Empty,
            PieceId = string.IsNullOrEmpty(_currentPieceId) ? EnsurePieceId() : _currentPieceId,
            BatchNo = BatchNo,
            ProductModel = ProductModel,
            RecipeName = ActiveRecipe?.Name ?? string.Empty,
            Operator = _settings.Current.OperatorName,
            StartedAt = _pieceStartedAt,
            FinishedAt = DateTime.Now,
            CycleSec = Math.Round(Math.Max(0, (DateTime.Now - _pieceStartedAt).TotalSeconds), 1),
            IsOk = isOk,
            JudgementReason = reason,
            Steps = new List<PieceStepReport>(_currentPieceSteps),
        };

        var payload = ToMesPayload(piece);
        _currentPieceSteps.Clear();

        var client = _mesClient;
        if (client is null || !client.IsEnabled)
        {
            _log.Info($"MES 未启用，跳过上传：件号 {piece.PieceId}");
            return;
        }

        _ = Task.Run(async () =>
        {
            var result = await client.UploadAsync(payload).ConfigureAwait(false);
            if (result.Success) return;

            if (result.ShouldRetry)
            {
                _mesQueue?.Enqueue(payload);
                _log.Warn($"MES 上传失败，已进队列等待补发（{result.Message}）：件号 {payload.PieceId}");
            }

            OnUiThread(() => OnPropertyChanged(nameof(MesQueueText)));
        });
    }

    /// <summary>把内部件数据翻译成对外的 MES 契约（时间统一字符串、判定统一 OK/NG）。</summary>
    private static MesUploadPayload ToMesPayload(PieceReport piece) => new()
    {
        StationCode = piece.StationCode,
        PieceId = piece.PieceId,
        BatchNo = piece.BatchNo,
        ProductModel = piece.ProductModel,
        RecipeName = piece.RecipeName,
        Operator = piece.Operator,
        StartedAt = piece.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        FinishedAt = piece.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        CycleSec = piece.CycleSec,
        Verdict = piece.IsOk ? "OK" : "NG",
        ViolationCode = piece.IsOk ? -1 : (int)ViolationCode.StepNotDone,
        JudgementReason = piece.JudgementReason,
        Steps = piece.Steps.Select(s => new MesStepPayload
        {
            Seq = s.Seq,
            Name = s.Name,
            StartedAt = s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            FinishedAt = s.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            DurationSec = s.DurationSec,
            StandardSec = s.StandardSec,
            IsOk = s.IsOk,
        }).ToList(),
    };

    private async Task FlushMesQueueAsync()
    {
        var client = _mesClient;
        var queue = _mesQueue;
        if (client is null || queue is null || !client.IsEnabled) return;
        if (queue.PendingCount == 0) { OnPropertyChanged(nameof(MesQueueText)); return; }

        await queue.FlushAsync(client).ConfigureAwait(false);
        OnUiThread(() => OnPropertyChanged(nameof(MesQueueText)));
    }

    private void OnUiThread(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null) { action(); return; }
        app.Dispatcher.BeginInvoke(action);
    }

    // ==================================================================
    // 界面命令
    // ==================================================================
    /// <summary>打开看板：本机是终端就打开本机地址，否则打开配置里的终端地址。</summary>
    private void OpenWallboard()
    {
        var station = _settings.Current.Station;

        // 勾了"本机作为汇总终端"但服务还没起来 → 现在就地起来。
        // 现场常见情况是"填完忘了点保存"，这样点一下也能用，不会静静地没反应。
        if (station is { IsHub: true } && _hub?.IsRunning != true)
        {
            station.Enabled = true;   // "作为汇总终端"本身就意味着要用联网功能
            _settings.Save();
            StartNetwork();
        }

        string? url = _hub?.IsRunning == true
            ? $"http://localhost:{_hub.Port}/"
            : (string.IsNullOrWhiteSpace(station?.HubUrl) ? null : station!.HubUrl.TrimEnd('/') + "/");

        if (url is null)
        {
            string hint = "还没配看板地址：到「系统设置 → 工位联网」勾选「本机作为汇总终端」，" +
                          "或填上「汇总终端地址」，然后点「保存设置」";
            StatusMessage = hint;
            NetworkStatusText = hint;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

            // 两处都提示：卡片上和底部状态条，避免"点了没反应"的错觉
            NetworkStatusText = "已用浏览器打开看板：" + url +
                                "（本机是终端时，其它电脑/手机访问 http://本机IP:" +
                                (_hub?.Port ?? 8088) + "/）";
            StatusMessage = NetworkStatusText;
            OnPropertyChanged(nameof(WallboardUrlText));
            _log.Info("已打开看板：" + url);
        }
        catch (Exception ex)
        {
            // 打不开浏览器时至少把地址给出来，让人能手动输入
            NetworkStatusText = $"打开浏览器失败：{ex.Message} —— 请手动在浏览器里输入 {url}";
            StatusMessage = NetworkStatusText;
            _log.Warn("打开看板失败：" + ex.Message);
        }
    }

    /// <summary>把看板地址复制到剪贴板（现场要在别的电脑/手机上输入时用）。</summary>
    private void CopyWallboardUrl()
    {
        var station = _settings.Current.Station;
        string url = _hub?.IsRunning == true
            ? $"http://{LocalIpv4()}:{_hub.Port}/"
            : (string.IsNullOrWhiteSpace(station?.HubUrl) ? string.Empty : station!.HubUrl.TrimEnd('/') + "/");

        if (string.IsNullOrEmpty(url))
        {
            StatusMessage = "还没有看板地址可复制：先勾选「本机作为汇总终端」或填「汇总终端地址」";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(url);
            StatusMessage = "看板地址已复制：" + url;
        }
        catch (Exception ex)
        {
            StatusMessage = "复制失败：" + ex.Message + "（地址：" + url + "）";
        }
    }

    private async Task TestMesAsync()
    {
        var client = _mesClient ?? CreateMesClient();
        var result = await client.TestAsync().ConfigureAwait(true);
        StatusMessage = result.Success
            ? "MES 连接正常：" + result.Message
            : $"MES 连接失败：{result.Message}（检查地址 / 令牌 / 网络）";

        if (result.Success) _log.Info("MES 测试连接：" + result.Message);
        else _log.Warn("MES 测试连接失败：" + result.Message);
    }
}
