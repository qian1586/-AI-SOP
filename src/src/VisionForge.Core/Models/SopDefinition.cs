namespace VisionForge.Core.Models;

/// <summary>
/// SOP 里的一步。
///
/// 注意它和 <see cref="Recipe"/> 的分工：
///   Recipe（配方）  = 一个检测点怎么做（ROI、模板、阈值）—— 管"怎么看"
///   SopStep（步骤） = 什么时候该看、看完放不放行      —— 管"流程"
/// 两者分开，同一个检测配方可以被多个步骤复用，换产品时只换 SOP 不改配方。
/// </summary>
public sealed class SopStepDefinition
{
    /// <summary>步骤序号，从 1 开始。决定执行顺序。</summary>
    public int Seq { get; set; }

    /// <summary>步骤标识，如 S1。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>步骤名称，如"安装电机并锁付"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本步骤用哪个检测配方。对应 <see cref="Recipe.Id"/>。</summary>
    public string RecipeId { get; set; } = string.Empty;

    /// <summary>给操作员看的提示语，显示在界面上引导作业。</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>
    /// NG 时是否锁死流程。
    /// true  = 必须返工合格才能进入下一步（安规/关键工序用）
    /// false = 记录告警但放行（辅助工序用，避免频繁锁线影响节拍）
    /// </summary>
    public bool BlockNextOnFail { get; set; } = true;

    /// <summary>允许的超时秒数。0 表示不检查。超时会提示但默认不锁线。</summary>
    public double TimeoutSec { get; set; }

    public override string ToString() => $"{Id} {Name}";
}

/// <summary>
/// 一个产品的完整作业规程。
/// </summary>
public sealed class SopDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string ProductModel { get; set; } = string.Empty;

    public string Version { get; set; } = "1.0.0";

    public string Remark { get; set; } = string.Empty;

    public List<SopStepDefinition> Steps { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>按 Seq 排序后的步骤。</summary>
    public IReadOnlyList<SopStepDefinition> OrderedSteps =>
        Steps.OrderBy(s => s.Seq).ToList();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name)) errors.Add("SOP 名称为空");
        if (Steps.Count == 0) errors.Add("SOP 至少要有一个步骤");

        var seqs = Steps.Select(s => s.Seq).ToList();
        if (seqs.Distinct().Count() != seqs.Count)
            errors.Add($"步骤序号有重复：{string.Join(",", seqs)}");
        if (seqs.Any(s => s <= 0))
            errors.Add("步骤序号必须从 1 开始");

        foreach (var s in Steps)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) errors.Add($"步骤 Seq={s.Seq} 的 Id 为空");
            if (string.IsNullOrWhiteSpace(s.RecipeId))
                errors.Add($"步骤 {s.Id} 未绑定检测配方");
        }

        return errors;
    }

    /// <summary>生成一份示例 SOP，方便第一次使用时有东西可看。</summary>
    public static SopDefinition CreateSample() => new()
    {
        Name = "洗地机主机装配 SOP（示例）",
        ProductModel = "WM-XXX",
        Steps = new List<SopStepDefinition>
        {
            new() { Seq = 1, Id = "S1", Name = "取件并放入工装", Hint = "从物料区取壳体，放入定位工装", BlockNextOnFail = true,  TimeoutSec = 25 },
            new() { Seq = 2, Id = "S2", Name = "安装电机",       Hint = "装入电机并预定位",           BlockNextOnFail = true,  TimeoutSec = 30 },
            new() { Seq = 3, Id = "S3", Name = "电批锁付螺丝",   Hint = "电批锁付 4 颗螺丝，用完归位", BlockNextOnFail = true,  TimeoutSec = 45 },
            new() { Seq = 4, Id = "S4", Name = "连接电源线束",   Hint = "接入电源线束",               BlockNextOnFail = true,  TimeoutSec = 30 },
            new() { Seq = 5, Id = "S5", Name = "安装清水箱",     Hint = "装配清水箱组件",             BlockNextOnFail = false, TimeoutSec = 35 },
            new() { Seq = 6, Id = "S6", Name = "成品下料",       Hint = "确认合格后取下成品",         BlockNextOnFail = false, TimeoutSec = 25 },
        },
    };
}
