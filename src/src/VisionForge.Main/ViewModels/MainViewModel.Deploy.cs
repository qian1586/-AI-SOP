using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Models;
using VisionForge.Infrastructure.Storage;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 模板池与一键下发：一条线的 30 个工位，换产品时不用一台台改。
///
/// <para><b>两个方向：</b></para>
/// <list type="number">
///   <item><b>汇总</b>：每台工位第一次把配方/SOP 调好之后，把这份配置"交"到终端（模板池）</item>
///   <item><b>下发</b>：终端在池子里挑一份 → 一键下发 → 所有工位自动拉取并生效</item>
/// </list>
///
/// <para>工位是"定时来问有没有新版本"（拉模式），所以哪怕某台关机了，
/// 开机后也会自动补上最新版本，不用人去补配置。</para>
/// </summary>
public sealed partial class MainViewModel
{
    private System.Windows.Threading.DispatcherTimer? _templateTimer;
    private int _appliedTemplateVersion;
    private string _deployStatusText = "未启用（先在上面勾选「启用工位联网」并填终端地址）";
    private string _templateName = "默认模板";
    private bool _templateIncludeRois = true;
    private bool _templateIncludeSamples;
    private DeployPoolItem? _selectedPoolItem;

    /// <summary>终端上的模板池（列表）。</summary>
    public ObservableCollection<DeployPoolItem> DeployPool { get; } = new();

    /// <summary>把本机当前配置打包汇总到终端（第一次建模板时用）。</summary>
    public ICommand UploadTemplateCommand { get; }

    /// <summary>一键下发：把模板池里选中的那份下发到全部工位。</summary>
    public ICommand PublishTemplateCommand { get; }

    /// <summary>刷新下发状态（当前版本 / 已应用工位数）与模板池。</summary>
    public ICommand RefreshDeployStatusCommand { get; }

    public string TemplateName
    {
        get => _templateName;
        set => SetProperty(ref _templateName, value);
    }

    /// <summary>打包时是否带上 ROI 坐标（工位机位完全一致才勾；不一致就只下发流程与参数）。</summary>
    public bool TemplateIncludeRois
    {
        get => _templateIncludeRois;
        set => SetProperty(ref _templateIncludeRois, value);
    }

    /// <summary>打包时是否带上教示样本（默认不带：样本和机位/光照强相关）。</summary>
    public bool TemplateIncludeSamples
    {
        get => _templateIncludeSamples;
        set => SetProperty(ref _templateIncludeSamples, value);
    }

    public DeployPoolItem? SelectedPoolItem
    {
        get => _selectedPoolItem;
        set
        {
            if (SetProperty(ref _selectedPoolItem, value))
                (PublishTemplateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>下发状态一句话。</summary>
    public string DeployStatusText
    {
        get => _deployStatusText;
        private set => SetProperty(ref _deployStatusText, value);
    }

    public string AppliedTemplateText =>
        _appliedTemplateVersion <= 0 ? "本机模板版本：—" : $"本机模板版本：v{_appliedTemplateVersion}";

    // ==================================================================
    // 打包 / 汇总
    // ==================================================================
    /// <summary>把本机当前配置打成一个可下发的包。</summary>
    public DeployPackage BuildDeployPackage()
    {
        var recipe = ActiveRecipe;
        var station = _settings.Current.Station;

        // ROI 坐标是否随包下发由开关决定：工位机位不一致时带上反而会把别人的坐标覆盖过来
        Recipe? recipeCopy = null;
        if (recipe is not null)
        {
            recipeCopy = recipe.Clone();
            if (!TemplateIncludeRois) recipeCopy.Rois = new List<RoiRegion>();
        }

        var samples = TemplateIncludeSamples && recipe is not null
            ? _roiSamples.Where(s => s.RecipeId == recipe.Id).ToList()
            : null;

        return new DeployPackage
        {
            Name = string.IsNullOrWhiteSpace(TemplateName) ? "默认模板" : TemplateName.Trim(),
            SourceStation = station?.StationCode ?? string.Empty,
            Recipe = recipeCopy,
            Sop = Sop.Definition,
            ProcessRule = _processConfig,
            TimingStandards = _settings.Current.Timing?.Standards?.ToList() ?? new List<StepTimeStandard>(),
            RoiSamples = samples,
            Remark = $"来自 {station?.StationName}（{station?.StationCode}）" +
                     (TemplateIncludeRois ? "" : "，不含 ROI 坐标"),
        };
    }

    private async Task UploadTemplateAsync()
    {
        var client = _reportClient;
        string? hub = _settings.Current.Station?.HubUrl;
        if (client is null || string.IsNullOrWhiteSpace(hub))
        {
            DeployStatusText = "先把「汇总终端地址」填上（系统设置 → 工位联网）";
            return;
        }

        try
        {
            var package = BuildDeployPackage();
            string json = JsonSerializer.Serialize(package, NetworkJson);

            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(hub.TrimEnd('/') + "/api/template/upload", content)
                                               .ConfigureAwait(true);

            DeployStatusText = response.IsSuccessStatusCode
                ? $"已把本机模板汇总到终端：{package.Name}（{package.Summary}）"
                : $"汇总失败：HTTP {(int)response.StatusCode}";

            _log.Info("模板汇总：" + DeployStatusText);
        }
        catch (Exception ex)
        {
            DeployStatusText = "汇总失败：" + ex.Message;
        }
    }

    // ==================================================================
    // 一键下发（终端侧）
    // ==================================================================
    private async Task PublishTemplateAsync()
    {
        var client = _reportClient;
        string? hub = _settings.Current.Station?.HubUrl;
        if (client is null || string.IsNullOrWhiteSpace(hub))
        {
            DeployStatusText = "先把「汇总终端地址」填上";
            return;
        }

        try
        {
            string body = SelectedPoolItem is not null
                ? JsonSerializer.Serialize(new { poolId = SelectedPoolItem.PackageId })
                : JsonSerializer.Serialize(new { package = BuildDeployPackage() });

            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(hub.TrimEnd('/') + "/api/template/publish", content)
                                               .ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
            {
                DeployStatusText = $"下发失败：HTTP {(int)response.StatusCode}";
                return;
            }

            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            DeployStatusText = "已下发：" + (text.Contains("\"ok\":true") ? "工位将在下一轮自动拉取生效" : text);
            _log.Info("模板已下发：" + (SelectedPoolItem?.Name ?? TemplateName));

            await RefreshDeployStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DeployStatusText = "下发失败：" + ex.Message;
        }
    }

    private async Task RefreshDeployStatusAsync()
    {
        var client = _reportClient;
        string? hub = _settings.Current.Station?.HubUrl;
        if (client is null || string.IsNullOrWhiteSpace(hub)) return;

        try
        {
            string json = await client.GetStringAsync(hub.TrimEnd('/') + "/api/template/pool")
                                      .ConfigureAwait(true);

            using var doc = JsonDocument.Parse(json);
            var items = new List<DeployPoolItem>();
            if (doc.RootElement.TryGetProperty("items", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var item = el.Deserialize<DeployPoolItem>(new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                    if (item is not null) items.Add(item);
                }
            }

            DeployPool.Clear();
            foreach (var item in items) DeployPool.Add(item);

            if (doc.RootElement.TryGetProperty("status", out var statusEl))
            {
                var status = statusEl.Deserialize<DeployStatus>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

                if (status is not null)
                {
                    DeployStatusText = status.Version <= 0
                        ? $"模板池 {DeployPool.Count} 个 · 还没有下发过"
                        : $"当前下发 v{status.Version}「{status.Name}」· " +
                          $"已应用 {status.AppliedCount}/{Math.Max(status.KnownStations, DeployPool.Count)} 个工位 · " +
                          $"{status.PublishedAt}";
                }
            }
        }
        catch (Exception ex)
        {
            DeployStatusText = "读取下发状态失败：" + ex.Message;
        }
    }

    // ==================================================================
    // 自动拉取（工位侧）
    // ==================================================================
    /// <summary>工位定时来问：终端有没有下发新版本？有就自动应用并回执。</summary>
    private async Task PollTemplateAsync()
    {
        var client = _reportClient;
        var station = _settings.Current.Station;
        if (client is null || station is null || string.IsNullOrWhiteSpace(station.HubUrl)) return;

        try
        {
            string url = $"{station.HubUrl.TrimEnd('/')}/api/template/assigned" +
                         $"?station={Uri.EscapeDataString(station.StationCode)}&applied={_appliedTemplateVersion}";

            string json = await client.GetStringAsync(url).ConfigureAwait(true);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("unchanged", out var uEl) && uEl.GetBoolean()) return;

            if (!doc.RootElement.TryGetProperty("version", out var vEl)) return;
            int version = vEl.GetInt32();
            if (version <= _appliedTemplateVersion) return;
            if (!doc.RootElement.TryGetProperty("package", out var pkgEl)) return;

            var package = pkgEl.Deserialize<DeployPackage>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (package is null) return;

            ApplyDeployPackage(package, version);

            // 回执：终端据此显示"已应用 N/M"
            using var content = new StringContent(
                JsonSerializer.Serialize(new { station = station.StationCode, version }),
                System.Text.Encoding.UTF8, "application/json");
            using var _ = await client.PostAsync(station.HubUrl.TrimEnd('/') + "/api/template/applied", content)
                                      .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 拉取失败不打扰现场（终端没开、网络抖都算正常），只在日志留痕
            _log.Debug("拉取下发模板失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 应用一个下发模板：配方 / SOP / 过程规则 / 标准工时（可选样本）四样一起落盘并生效。
    ///
    /// <para>调用方在 UI 线程上（定时器回调），所以这里直接改状态再落盘。</para>
    /// </summary>
    public void ApplyDeployPackage(DeployPackage package, int version)
    {
        var applied = new List<string>();

        // ① 配方：按 Id 覆盖（同 Id 的就是同一个产品的配方，参数以终端下发的为准）
        if (package.Recipe is not null)
        {
            var incoming = package.Recipe;
            var existing = RecipeList.FirstOrDefault(r => r.Id == incoming.Id);
            if (existing is not null) RecipeList.Remove(existing);
            RecipeList.Insert(0, incoming);
            ActiveRecipe = incoming;   // 切到这份配方（会顺带重建参数面板与叠加框）

            SaveRecipeInBackground(incoming);
            applied.Add($"配方「{incoming.Name}」");
        }

        // ② SOP：写 sop.json 并重新载入（工序顺序、名称、超时、是否锁线都跟着变）
        if (package.Sop is not null)
        {
            try
            {
                SopDefinitionStore.Save(package.Sop, Path.Combine(_settings.Current.DataRoot, "sop.json"));
                Sop.Load(package.Sop);
                applied.Add($"SOP {package.Sop.Steps.Count} 道工序");
            }
            catch (Exception ex) { _log.Warn("下发 SOP 失败：" + ex.Message); }
        }

        // ③ 过程层规则：K 值 / 时间窗 / 顺序强制 / 置信度阈值
        if (package.ProcessRule is not null)
        {
            try
            {
                ProcessConfigStore.SaveJson(package.ProcessRule,
                    Path.Combine(_settings.Current.DataRoot, "process-rule.json"));
                _processConfig = package.ProcessRule;
                _processEngine = new VisionForge.Core.Services.HandActionRuleEngine(_processConfig);
                BuildProcessTargetStates();
                OnPropertyChanged(nameof(ProcessRuleSummary));
                applied.Add("过程层规则");
            }
            catch (Exception ex) { _log.Warn("下发过程层规则失败：" + ex.Message); }
        }

        // ④ 标准工时
        if (package.TimingStandards.Count > 0)
        {
            _settings.Current.Timing ??= new VisionForge.Infrastructure.Config.TimingOptions();
            _settings.Current.Timing.Standards = package.TimingStandards.ToList();
            _settings.Save();
            applied.Add($"标准工时 {package.TimingStandards.Count} 项");
        }

        // ⑤ 教示样本（可选）：整包替换该配方的样本
        if (package.RoiSamples is { Count: > 0 } && package.Recipe is not null)
        {
            _roiSamples.RemoveAll(s => s.RecipeId == package.Recipe.Id);
            foreach (var sample in package.RoiSamples)
            {
                sample.RecipeId = package.Recipe.Id;
                _roiSamples.Add(sample);
            }
            SaveRoiSamples();
            RebuildRoiLibrary();
            RefreshRoiTargetOptions();
            applied.Add($"教示样本 {package.RoiSamples.Count} 条");
        }

        _appliedTemplateVersion = version;
        OnPropertyChanged(nameof(AppliedTemplateText));
        RefreshRoiTargetOptions();
        RaiseStatusBarProps();

        DeployStatusText = $"已应用终端下发模板 v{version}：{string.Join("、", applied)}";
        StatusMessage = DeployStatusText;
        _log.Info("已应用下发模板：" + DeployStatusText);
    }

    private void StartTemplatePolling()
    {
        _templateTimer?.Stop();

        var station = _settings.Current.Station;
        if (station is null || !station.Enabled || string.IsNullOrWhiteSpace(station.HubUrl)) return;

        // 15 秒问一次：比"实时"慢一点没关系，但要让换产品时 30 个工位在十几秒内全部切过去
        _templateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _templateTimer.Tick += (_, _) => _ = PollTemplateAsync();
        _templateTimer.Start();

        _ = PollTemplateAsync();
    }

    private void StopTemplatePolling()
    {
        _templateTimer?.Stop();
        _templateTimer = null;
    }
}
