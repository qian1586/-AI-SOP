using System.Text.Json;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Integration;

/// <summary>
/// 汇总终端：接收各工位上报的实时状态，并对外提供一个"一眼看全场"的看板页面。
///
/// <para><b>现场怎么用：</b>在任意一台常开的电脑（车间看板机 / 班长电脑）上，
/// 把本机设为"汇总终端"并启动看板服务；浏览器打开 <c>http://本机IP:8088/</c> 即可。
/// 几十个工位都会定时把自己的状态 POST 过来，页面每 2 秒自动刷新。</para>
///
/// <para><b>为什么把看板做成网页：</b>不用给看板机装客户端、不用管版本一致，
/// 手机、平板、大屏、办公室电脑都能打开同一个地址。数据接口同时也是
/// 第三方（MES/报表/大屏系统）取数的入口。</para>
/// </summary>
public sealed class StationHub : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // 统一 camelCase：匿名类型写出来本来就是小写开头，而模型类型默认是 PascalCase，
        // 两边混着会变成 {"ok":true,"status":{"AppliedCount":1}} 这种"一半小写一半大写"的接口，
        // 对接方极容易踩坑（自检里就是这么发现问题的：按 camelCase 找 appliedCount 找不到）。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, StationReport> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeployPackage> _pool = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeployStationStatus> _applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ILogger? _log;
    private readonly SimpleHttpServer _server;
    private readonly string _hubName;
    private string _dataRoot = string.Empty;
    private DeployPackage? _assigned;
    private int _assignedVersion;
    private DateTime _assignedAt;

    public StationHub(string hubName, ILogger? log = null, string? dataRoot = null)
    {
        _hubName = string.IsNullOrWhiteSpace(hubName) ? "SOP 汇总终端" : hubName;
        _log = log;
        _server = new SimpleHttpServer(Route);
        _dataRoot = dataRoot ?? string.Empty;
    }

    public string HubName => _hubName;
    public int Port => _server.Port;
    public bool IsRunning => _server.IsRunning;

    /// <summary>超过这个秒数没收到上报，就显示成"离线"。</summary>
    public int OfflineSeconds { get; set; } = 30;

    public void Start(int port)
    {
        LoadTemplateState();
        _server.Start(port);
        _log?.Info($"汇总终端看板已启动：http://本机IP:{port}/（共可接收任意个工位上报）");
    }

    public void Stop()
    {
        if (_server.IsRunning) _log?.Info("汇总终端看板已停止");
        _server.Stop();
    }

    /// <summary>当前所有工位（按产线、工位号排序），供看板与接口使用。</summary>
    public List<StationReport> Snapshot()
    {
        lock (_gate)
        {
            return _stations.Values
                .OrderBy(s => s.LineName, StringComparer.Ordinal)
                .ThenBy(s => s.StationCode, StringComparer.Ordinal)
                .ToList();
        }
    }

    private HttpResponse Route(HttpRequest request)
    {
        string path = request.Path.Split('?')[0].TrimEnd('/');
        if (path.Length == 0) path = "/";

        return (request.Method, path) switch
        {
            ("GET", "/") => HttpResponse.Html(BuildWallboardHtml()),
            ("GET", "/health") => HttpResponse.Text("ok"),
            ("GET", "/api/stations") => HttpResponse.Json(BuildStationsJson()),
            ("POST", "/api/report") => AcceptReport(request.Body),

            // ---- 模板池与一键下发 ----
            ("POST", "/api/template/upload") => UploadTemplate(request.Body),      // 工位把本机模板汇总上来
            ("GET", "/api/template/pool") => HttpResponse.Json(BuildPoolJson()),   // 终端看模板池
            ("POST", "/api/template/publish") => PublishTemplate(request.Body),    // 终端一键下发
            ("GET", "/api/template/assigned") => AssignedTemplate(request.Path),   // 工位来问"有没有新版本"
            ("POST", "/api/template/applied") => MarkApplied(request.Body),        // 工位回执"已应用"
            ("GET", "/api/template/status") => HttpResponse.Json(BuildDeployStatusJson()),

            ("OPTIONS", _) => HttpResponse.Text(string.Empty),
            _ => HttpResponse.Json("{\"ok\":false,\"message\":\"not found\"}", 404),
        };
    }

    // ==================================================================
    // 模板池 / 一键下发
    // ==================================================================
    /// <summary>工位把本机模板汇总到终端的池子里（第一次建模板时做一次即可）。</summary>
    private HttpResponse UploadTemplate(string body)
    {
        try
        {
            var package = JsonSerializer.Deserialize<DeployPackage>(body, JsonOptions);
            if (package is null) return HttpResponse.Json("{\"ok\":false,\"message\":\"空内容\"}", 400);

            package.Version = 0;   // 池子里存的是"待下发"的模板，版本号由下发时决定
            lock (_gate) _pool[package.PackageId] = package;
            SavePoolItem(package);

            _log?.Info($"收到模板：{package.Name}（来自 {package.SourceStation}）");
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, poolId = package.PackageId }));
        }
        catch (Exception ex)
        {
            _log?.Warn("模板汇总失败：" + ex.Message);
            return HttpResponse.Json("{\"ok\":false,\"message\":\"模板解析失败\"}", 400);
        }
    }

    private string BuildPoolJson()
    {
        List<DeployPoolItem> items;
        lock (_gate)
        {
            items = _pool.Values
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new DeployPoolItem
                {
                    PackageId = p.PackageId,
                    Name = p.Name,
                    SourceStation = p.SourceStation,
                    CreatedAt = p.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                    Summary = p.Summary,
                    IsCurrent = _assigned is not null && _assigned.PackageId == p.PackageId,
                    Version = _assigned is not null && _assigned.PackageId == p.PackageId ? _assignedVersion : 0,
                })
                .ToList();
        }

        return JsonSerializer.Serialize(new { ok = true, items, status = BuildDeployStatus() }, JsonOptions);
    }

    /// <summary>一键下发：把池子里某个模板设为"当前版本"，所有工位会在下一轮拉取时生效。</summary>
    private HttpResponse PublishTemplate(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string? poolId = doc.RootElement.TryGetProperty("poolId", out var idEl) ? idEl.GetString() : null;

            DeployPackage? package = null;
            if (!string.IsNullOrWhiteSpace(poolId))
            {
                lock (_gate) _pool.TryGetValue(poolId!, out package);
            }
            else if (doc.RootElement.TryGetProperty("package", out var pkgEl))
            {
                package = pkgEl.Deserialize<DeployPackage>(JsonOptions);
            }

            if (package is null)
                return HttpResponse.Json("{\"ok\":false,\"message\":\"找不到要下发的模板\"}", 400);

            lock (_gate)
            {
                _assignedVersion++;
                package.Version = _assignedVersion;
                _assigned = package;
                _assignedAt = DateTime.Now;
            }

            SaveAssigned();
            _log?.Info($"已下发模板 v{_assignedVersion}：{package.Name}（工位会在下一轮自动拉取）");

            return HttpResponse.Json(JsonSerializer.Serialize(new
            {
                ok = true,
                version = _assignedVersion,
                name = package.Name,
            }));
        }
        catch (Exception ex)
        {
            _log?.Warn("下发模板失败：" + ex.Message);
            return HttpResponse.Json("{\"ok\":false,\"message\":\"下发失败\"}", 400);
        }
    }

    /// <summary>工位定时来问：我手上的版本是不是最新的？是的话只回一个 unchanged。</summary>
    private HttpResponse AssignedTemplate(string path)
    {
        var query = ParseQuery(path);
        query.TryGetValue("applied", out string? appliedText);
        int applied = int.TryParse(appliedText, out int v) ? v : 0;

        DeployPackage? assigned;
        int version;
        lock (_gate)
        {
            assigned = _assigned;
            version = _assignedVersion;
        }

        if (assigned is null || version <= 0)
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, version = 0, unchanged = true }));

        if (applied >= version)
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, version, unchanged = true }));

        return HttpResponse.Json(JsonSerializer.Serialize(new
        {
            ok = true,
            version,
            unchanged = false,
            package = assigned,
        }, JsonOptions));
    }

    /// <summary>工位回执：我已经应用到这个版本了（终端据此显示 24/30）。</summary>
    private HttpResponse MarkApplied(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string station = doc.RootElement.TryGetProperty("station", out var sEl) ? (sEl.GetString() ?? "") : "";
            int version = doc.RootElement.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : 0;
            if (string.IsNullOrWhiteSpace(station))
                return HttpResponse.Json("{\"ok\":false,\"message\":\"缺少 station\"}", 400);

            lock (_gate)
            {
                _applied[station] = new DeployStationStatus
                {
                    StationCode = station,
                    AppliedVersion = version,
                    AppliedAt = DateTime.Now.ToString("HH:mm:ss"),
                    UpToDate = version >= _assignedVersion,
                };
            }

            return HttpResponse.Json("{\"ok\":true}");
        }
        catch
        {
            return HttpResponse.Json("{\"ok\":false,\"message\":\"回执解析失败\"}", 400);
        }
    }

    private DeployStatus BuildDeployStatus()
    {
        lock (_gate)
        {
            var stations = _applied.Values
                .OrderBy(s => s.StationCode, StringComparer.Ordinal)
                .Select(s => new DeployStationStatus
                {
                    StationCode = s.StationCode,
                    AppliedVersion = s.AppliedVersion,
                    AppliedAt = s.AppliedAt,
                    UpToDate = s.AppliedVersion >= _assignedVersion,
                })
                .ToList();

            return new DeployStatus
            {
                Version = _assignedVersion,
                Name = _assigned?.Name ?? string.Empty,
                PublishedAt = _assignedAt == default ? string.Empty : _assignedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                AppliedCount = stations.Count(s => s.UpToDate),
                KnownStations = stations.Count,
                Stations = stations,
            };
        }
    }

    private string BuildDeployStatusJson() =>
        JsonSerializer.Serialize(new { ok = true, status = BuildDeployStatus() }, JsonOptions);

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int q = path.IndexOf('?');
        if (q < 0) return result;

        foreach (var pair in path[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2) result[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }
        return result;
    }

    // ---- 落盘：终端重启后模板池与"当前版本"不能丢 ----
    private string PoolDirectory => Path.Combine(_dataRoot, "hub-templates", "pool");
    private string AssignedPath => Path.Combine(_dataRoot, "hub-templates", "assigned.json");

    private void SavePoolItem(DeployPackage package)
    {
        if (string.IsNullOrEmpty(_dataRoot)) return;
        try
        {
            Directory.CreateDirectory(PoolDirectory);
            File.WriteAllText(Path.Combine(PoolDirectory, package.PackageId + ".json"),
                              JsonSerializer.Serialize(package, JsonOptions));
        }
        catch (Exception ex) { _log?.Warn("模板池落盘失败：" + ex.Message); }
    }

    private void SaveAssigned()
    {
        if (string.IsNullOrEmpty(_dataRoot)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AssignedPath)!);
            File.WriteAllText(AssignedPath, JsonSerializer.Serialize(new
            {
                version = _assignedVersion,
                publishedAt = _assignedAt,
                package = _assigned,
            }, JsonOptions));
        }
        catch (Exception ex) { _log?.Warn("下发状态落盘失败：" + ex.Message); }
    }

    private void LoadTemplateState()
    {
        if (string.IsNullOrEmpty(_dataRoot)) return;

        try
        {
            if (Directory.Exists(PoolDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(PoolDirectory, "*.json"))
                {
                    try
                    {
                        var package = JsonSerializer.Deserialize<DeployPackage>(File.ReadAllText(file), JsonOptions);
                        if (package is not null) _pool[package.PackageId] = package;
                    }
                    catch { /* 单个坏文件不影响其它 */ }
                }
            }

            if (File.Exists(AssignedPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(AssignedPath));
                if (doc.RootElement.TryGetProperty("version", out var vEl)) _assignedVersion = vEl.GetInt32();
                if (doc.RootElement.TryGetProperty("publishedAt", out var tEl) &&
                    tEl.TryGetDateTime(out var publishedAt)) _assignedAt = publishedAt;
                if (doc.RootElement.TryGetProperty("package", out var pEl))
                    _assigned = pEl.Deserialize<DeployPackage>(JsonOptions);
            }

            _log?.Info($"模板状态已载入：池 {_pool.Count} 个 · 当前下发 v{_assignedVersion}");
        }
        catch (Exception ex)
        {
            _log?.Warn("模板状态载入失败（不影响运行）：" + ex.Message);
        }
    }

    private HttpResponse AcceptReport(string body)
    {
        try
        {
            var report = JsonSerializer.Deserialize<StationReport>(body, JsonOptions);
            if (report is null || string.IsNullOrWhiteSpace(report.StationCode))
                return HttpResponse.Json("{\"ok\":false,\"message\":\"缺少 StationCode\"}", 400);

            report.ReportedAt = DateTime.Now;   // 以终端时间为准，避免各工位时间不同步

            lock (_gate) _stations[report.StationCode] = report;

            return HttpResponse.Json("{\"ok\":true}");
        }
        catch (Exception ex)
        {
            _log?.Warn("工位上报解析失败：" + ex.Message);
            return HttpResponse.Json("{\"ok\":false,\"message\":\"JSON 解析失败\"}", 400);
        }
    }

    private string BuildStationsJson()
    {
        var stations = Snapshot();
        DateTime now = DateTime.Now;

        var online = stations.Where(s => (now - s.ReportedAt).TotalSeconds <= OfflineSeconds).ToList();

        var payload = new
        {
            hub = _hubName,
            serverTime = now.ToString("yyyy-MM-dd HH:mm:ss"),
            offlineSeconds = OfflineSeconds,
            summary = new
            {
                total = stations.Count,
                online = online.Count,
                ng = online.Count(s => s.StateKey == "ng"),
                pieces = online.Sum(s => s.CompletedPieces),
                avgCycleSec = online.Any() ? Math.Round(online.Average(s => s.LastCycleSec), 1) : 0,
            },
            stations = stations.Select(s => new
            {
                s.StationCode,
                s.StationName,
                s.LineName,
                s.ProductModel,
                s.RecipeName,
                s.BatchNo,
                s.Operator,
                state = s.StateKey,
                s.StateText,
                s.CurrentStepName,
                s.StepDone,
                s.StepTotal,
                s.OkCount,
                s.NgCount,
                s.CompletedPieces,
                passRate = Math.Round(s.PassRate, 1),
                lastCycleSec = Math.Round(s.LastCycleSec, 1),
                averageCycleSec = Math.Round(s.AverageCycleSec, 1),
                s.TodayAlarm,
                s.TodaySkipped,
                reportedAt = s.ReportedAt.ToString("HH:mm:ss"),
                offline = (now - s.ReportedAt).TotalSeconds > OfflineSeconds,
                s.Steps,
            }),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// 看板页面（自包含：内联 CSS/JS，不依赖任何外部资源 ——
    /// 车间网络往往连不上 CDN，能离线打开是硬要求）。
    /// </summary>
    private string BuildWallboardHtml() => """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>AI-SOP 工位实时看板</title>
<style>
  :root{
    --bg:#0B1220; --card:#131C2E; --deep:#0F1827; --line:#243349;
    --txt:#E8EEF7; --sub:#93A5BD; --accent:#00D4D4;
    --ok:#22C55E; --warn:#F59E0B; --ng:#EF4444;
  }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--txt);
       font-family:"Microsoft YaHei","PingFang SC",system-ui,sans-serif;}
  header{display:flex;align-items:center;gap:24px;padding:14px 20px;
         background:var(--deep);border-bottom:1px solid var(--line);flex-wrap:wrap}
  header h1{font-size:20px;margin:0;font-weight:600}
  header .kpi{font-size:13px;color:var(--sub)}
  header .kpi b{color:var(--txt);font-size:18px;margin-left:6px}
  header .clock{margin-left:auto;color:var(--sub);font-size:13px}
  main{padding:16px 20px 40px}
  .grid{display:grid;gap:12px;
        grid-template-columns:repeat(auto-fill,minmax(260px,1fr))}
  .st{background:var(--card);border:1px solid var(--line);border-left-width:6px;
      border-radius:8px;padding:12px 14px}
  .st.ok{border-left-color:var(--ok)}
  .st.active{border-left-color:var(--warn)}
  .st.pending{border-left-color:#3A4A66}
  .st.ng{border-left-color:var(--ng);background:#2A1620}
  .st.offline{opacity:.45}
  .st h2{margin:0;font-size:16px;display:flex;align-items:center;gap:8px}
  .st h2 small{font-weight:400;color:var(--sub);font-size:12px}
  .badge{margin-left:auto;font-size:11px;padding:2px 8px;border-radius:10px;
         color:#0A101A;font-weight:700}
  .badge.ok{background:var(--ok)} .badge.active{background:var(--warn)}
  .badge.ng{background:var(--ng);color:#fff} .badge.pending{background:#3A4A66;color:#fff}
  .row{display:flex;justify-content:space-between;font-size:12px;
       color:var(--sub);margin-top:6px}
  .row b{color:var(--txt)}
  .step{font-size:13px;margin-top:8px;color:var(--accent)}
  .nums{display:grid;grid-template-columns:repeat(4,1fr);gap:4px;margin-top:10px;
        text-align:center}
  .nums div{background:var(--deep);border-radius:6px;padding:6px 2px}
  .nums span{display:block;font-size:11px;color:var(--sub)}
  .nums strong{font-size:16px}
  .empty{color:var(--sub);padding:40px;text-align:center}
</style>
</head>
<body>
<header>
  <h1>AI-SOP 工位实时看板</h1>
  <div class="kpi">工位<b id="k-total">0</b></div>
  <div class="kpi">在线<b id="k-online">0</b></div>
  <div class="kpi">报警<b id="k-ng">0</b></div>
  <div class="kpi">今日产量<b id="k-pieces">0</b></div>
  <div class="kpi">平均节拍<b id="k-cycle">0s</b></div>
  <div class="clock" id="k-clock">—</div>
</header>
<main>
  <div class="grid" id="grid"></div>
  <div class="empty" id="empty" style="display:none">还没有工位上报 —— 在工位端「系统设置 → 工位联网」里填上本机地址即可</div>
</main>
<script>
const stateText = { ok:'运行中', active:'待机', ng:'已锁线', pending:'未连接' };

async function tick(){
  try{
    const r = await fetch('/api/stations', {cache:'no-store'});
    const d = await r.json();
    document.getElementById('k-total').textContent   = d.summary.total;
    document.getElementById('k-online').textContent  = d.summary.online;
    document.getElementById('k-ng').textContent      = d.summary.ng;
    document.getElementById('k-pieces').textContent  = d.summary.pieces;
    document.getElementById('k-cycle').textContent   = d.summary.avgCycleSec + 's';
    document.getElementById('k-clock').textContent   = d.serverTime + ' · ' + d.hub;

    const grid = document.getElementById('grid');
    document.getElementById('empty').style.display = d.stations.length ? 'none' : 'block';
    grid.innerHTML = d.stations.map(s => {
      const cls = s.offline ? 'offline' : s.state;
      const badge = s.offline ? 'pending' : s.state;
      const text  = s.offline ? '离线' : (stateText[s.state] || s.state);
      return `<div class="st ${cls}">
        <h2>${s.stationCode} <small>${s.stationName || ''} ${s.lineName ? '· ' + s.lineName : ''}</small>
            <span class="badge ${badge}">${text}</span></h2>
        <div class="step">当前：${s.currentStepName || '—'}　（${s.stepDone}/${s.stepTotal}）</div>
        <div class="nums">
          <div><span>OK</span><strong style="color:var(--ok)">${s.okCount}</strong></div>
          <div><span>NG</span><strong style="color:var(--ng)">${s.ngCount}</strong></div>
          <div><span>合格率</span><strong>${s.passRate}%</strong></div>
          <div><span>节拍</span><strong>${s.lastCycleSec}s</strong></div>
        </div>
        <div class="row"><span>产品型号</span><b>${s.productModel || '—'}</b></div>
        <div class="row"><span>今日产量</span><b>${s.completedPieces}</b></div>
        <div class="row"><span>今日告警 / 漏步</span><b>${s.todayAlarm} / ${s.todaySkipped}</b></div>
        <div class="row"><span>操作员</span><b>${s.operator || '—'}</b></div>
        <div class="row"><span>最后上报</span><b>${s.reportedAt}</b></div>
      </div>`;
    }).join('');
  }catch(e){
    document.getElementById('k-clock').textContent = '看板服务连接中断：' + e;
  }
}
tick();
setInterval(tick, 2000);
</script>
</body>
</html>
""";

    public void Dispose() => _server.Dispose();
}
