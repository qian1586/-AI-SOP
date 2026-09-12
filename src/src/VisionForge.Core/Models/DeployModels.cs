using VisionForge.Core.Services;

namespace VisionForge.Core.Models;

/// <summary>
/// 一个可以"一键下发"的配置包：配方 + SOP + 过程层规则 + 标准工时（可选带教示样本）。
///
/// <para><b>解决的问题：</b>一条线 30 个工位，换产线/换产品时要改 30 次配置 ——
/// 用这个包在终端上点一次"下发"，所有工位自动拉到最新版本并生效。</para>
///
/// <para><b>为什么用"工位来拉"而不是"终端去推"：</b>工位在车间网络里、可能关机、
/// 可能刚重启；推送要一台台连过去，任何一台不通报错就断了。让工位定时来问
/// "有没有新版本"最稳：关机的工位一开机就会自动补上，也不会被防火墙挡住。</para>
/// </summary>
public sealed class DeployPackage
{
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>包唯一号（也是模板池里的 Id）。</summary>
    public string PackageId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>模板名，例如"MIRROR_DEMO · 主机装配"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>这个包是从哪台工位汇总上来的（终端模板池里要看这个）。</summary>
    public string SourceStation { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>下发版本号：终端每下发一次 +1。工位拿它判断"我是不是已经是最新的"。</summary>
    public int Version { get; set; }

    /// <summary>配方（算法参数、ROI 坐标、底图路径）。</summary>
    public Recipe? Recipe { get; set; }

    /// <summary>SOP（工序顺序、名称、指引、超时、是否锁线）。</summary>
    public SopDefinition? Sop { get; set; }

    /// <summary>过程层规则（K 值、时间窗、是否强制顺序、置信度阈值…）。</summary>
    public ProcessRuleConfig? ProcessRule { get; set; }

    /// <summary>各工序标准工时。</summary>
    public List<StepTimeStandard> TimingStandards { get; set; } = new();

    /// <summary>
    /// 教示样本（可选）。
    ///
    /// <para>默认<b>不</b>下发：样本和这台工位的相机机位、光照强相关，
    /// 拿别的工位的样本过来只会误判。只有在"所有工位机位完全一致"、
    /// 或者想把某台调好的工位当"样板工位"批量铺开时才勾上。</para>
    /// </summary>
    public List<RoiSample>? RoiSamples { get; set; }

    public string Remark { get; set; } = string.Empty;

    /// <summary>界面上显示的一行摘要。</summary>
    public string Summary =>
        $"配方 {(string.IsNullOrWhiteSpace(Recipe?.Name) ? "—" : Recipe!.Name)} · " +
        $"{Recipe?.Rois.Count ?? 0} 个框 · 工序 {Sop?.Steps.Count ?? 0} 道 · " +
        $"标准工时 {TimingStandards.Count} 项" +
        (RoiSamples is { Count: > 0 } ? $" · 样本 {RoiSamples.Count} 条" : "");

    public string VersionText => Version <= 0 ? "未下发" : $"v{Version}";
}

/// <summary>模板池里的一个条目（列表用，不带完整负载）。</summary>
public sealed class DeployPoolItem
{
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceStation { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public int Version { get; set; }
}

/// <summary>下发状态：版本、已应用工位数、每个工位的情况。</summary>
public sealed class DeployStatus
{
    public int Version { get; set; }
    public string PublishedAt { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int AppliedCount { get; set; }
    public int KnownStations { get; set; }
    public List<DeployStationStatus> Stations { get; set; } = new();
}

/// <summary>某个工位对当前下发版本的应用情况。</summary>
public sealed class DeployStationStatus
{
    public string StationCode { get; set; } = string.Empty;
    public int AppliedVersion { get; set; }
    public string AppliedAt { get; set; } = string.Empty;
    public bool UpToDate { get; set; }
}
