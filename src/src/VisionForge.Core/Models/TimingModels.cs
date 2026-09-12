namespace VisionForge.Core.Models;

/// <summary>
/// 一道工序的"标准工时"（秒）。
///
/// <para>现场配一次，之后每一件都能拿实际用时跟它比 ——
/// 不用人去掐表，也不用事后翻视频找"这一下花了几秒"。</para>
/// </summary>
public sealed class StepTimeStandard
{
    public int StepSeq { get; set; }
    public string StepName { get; set; } = string.Empty;

    /// <summary>标准工时（秒）。0 = 没设，不参与快慢判定。</summary>
    public double StandardSec { get; set; }

    /// <summary>允许偏差（%）。超出才判"慢了/快了"，避免把正常抖动也报出来。</summary>
    public double TolerancePercent { get; set; } = 10;
}

/// <summary>
/// 一次"动作节点"的计时记录 —— 这是"看人快了还是慢了"的数据基础。
///
/// <para><b>记什么：</b>哪一件、第几道工序（哪个框）、什么时候开始、什么时候结束、
/// 花了多久、上一次同工序用了多久、标准工时多少、这一步判定 OK 还是 NG。</para>
///
/// <para><b>为什么要存"上一次用时"：</b>现场最常问的是"这次比上次快还是慢"。
/// 用相邻两件对比（而不是只跟标准比），能看出人的状态波动；
/// 跟标准比则能看出"这个工位整体是不是偏慢"。两个基准都要有。</para>
/// </summary>
public sealed class ActionTimingRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>件号：一件产品的整条流程共用同一个号，用来看"这一件的各节点"。</summary>
    public string PieceId { get; set; } = string.Empty;

    public string RecipeId { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string BatchNo { get; set; } = string.Empty;
    public string ProductModel { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;

    /// <summary>第几道工序（第 N 个框图 = 第 N 道工序）。</summary>
    public int StepSeq { get; set; }

    public string StepName { get; set; } = string.Empty;

    /// <summary>本动作开始时刻（= 进入这一步的时刻）。</summary>
    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>本动作结束时刻（= 这一步判定出来的时刻）。</summary>
    public DateTime FinishedAt { get; set; } = DateTime.Now;

    /// <summary>本动作用时（毫秒）。</summary>
    public double DurationMs { get; set; }

    /// <summary>上一次同一工序的用时（毫秒）。null = 这是第一件，没有可比对象。</summary>
    public double? PreviousDurationMs { get; set; }

    /// <summary>标准工时（毫秒）。null / 0 = 没设。</summary>
    public double? StandardMs { get; set; }

    /// <summary>这一步的判定（true=OK，false=NG）。</summary>
    public bool IsOk { get; set; } = true;

    public string Note { get; set; } = string.Empty;

    // ------------------------------------------------------------------
    // 界面直接绑这些（免得 XAML 里写算不出来的表达式）
    // ------------------------------------------------------------------

    public double DurationSec => DurationMs / 1000.0;

    public string DurationText => $"{DurationSec:F1}s";

    public string TimeText => FinishedAt.ToString("HH:mm:ss");

    public string DayText => FinishedAt.ToString("MM-dd");

    public string PreviousText => PreviousDurationMs is null or <= 0
        ? "—"
        : $"{PreviousDurationMs.Value / 1000.0:F1}s";

    public string StandardText => StandardMs is null or <= 0
        ? "—"
        : $"{StandardMs.Value / 1000.0:F1}s";

    /// <summary>基准：优先和"上一次同一动作"比，没有上次才和标准比。</summary>
    public double? BaselineMs => PreviousDurationMs is > 0 ? PreviousDurationMs
                                : (StandardMs is > 0 ? StandardMs : null);

    /// <summary>相对基准的差值（毫秒）。正 = 慢了，负 = 快了。</summary>
    public double? DeltaMs => BaselineMs is null ? null : DurationMs - BaselineMs.Value;

    /// <summary>和标准工时的差值（毫秒）。正 = 超过标准。</summary>
    public double? DeltaVsStandardMs => StandardMs is > 0 ? DurationMs - StandardMs.Value : null;

    /// <summary>相对基准的变化率（0.1 = 慢 10%）。</summary>
    public double? DeltaRatio => BaselineMs is > 0 ? (DurationMs - BaselineMs.Value) / BaselineMs.Value : null;

    /// <summary>
    /// 快慢文案，直接显示给操作员。
    ///
    /// <para>没有基准（第一件且没设标准）时明确写"首次"，
    /// 而不是显示"持平" —— 那会让人以为它比过了。</para>
    /// </summary>
    public string PaceText
    {
        get
        {
            if (DeltaMs is null) return "首次（无对比）";

            double delta = DeltaMs.Value;
            if (Math.Abs(delta) < 200) return "与基准持平";

            string arrow = delta > 0 ? "慢了" : "快了";
            string basis = PreviousDurationMs is > 0 ? "比上次" : "比标准";
            return $"{basis}{arrow} {Math.Abs(delta) / 1000.0:F1}s（{Math.Abs(delta) / Math.Max(1, BaselineMs!.Value):P0}）";
        }
    }

    /// <summary>颜色键：ng=明显慢 / ok=明显快 / active=正常波动。</summary>
    public string PaceStateKey
    {
        get
        {
            if (DeltaRatio is null) return "pending";
            if (DeltaRatio.Value > 0.30) return "ng";         // 慢 30% 以上：要管
            if (DeltaRatio.Value > 0.10) return "active";     // 慢 10~30%：留意
            if (DeltaRatio.Value < -0.10) return "ok";        // 明显更快
            return "pending";
        }
    }

    public string VerdictText => IsOk ? "OK" : "NG";

    /// <summary>后台存的时候用这一行（CSV / 人工核对都方便）。</summary>
    public string Display =>
        $"{FinishedAt:HH:mm:ss} {PieceId} 第{StepSeq}步 {StepName} 用时{DurationText} " +
        $"上次{PreviousText} 标准{StandardText} {PaceText} {VerdictText}";
}
