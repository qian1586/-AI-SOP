namespace VisionForge.Core.Models;

/// <summary>检测结论。</summary>
public enum InspectionVerdict
{
    /// <summary>未执行。</summary>
    None = 0,
    Pass = 1,
    Fail = 2,

    /// <summary>流程本身出错（没图、算法抛异常）。必须和 Fail 区分开 —— Fail 是产品不良，Error 是系统故障，处理方式完全不同。</summary>
    Error = 3,
}

/// <summary>一项测量值。文章里"测量数据汇总"的条目。</summary>
public sealed class MeasurementItem
{
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;

    /// <summary>规格上下限，可为空表示不做判定。</summary>
    public double? LowerLimit { get; set; }
    public double? UpperLimit { get; set; }

    public bool? InSpec
    {
        get
        {
            if (LowerLimit is null && UpperLimit is null) return null;
            if (LowerLimit is { } lo && Value < lo) return false;
            if (UpperLimit is { } hi && Value > hi) return false;
            return true;
        }
    }

    public string Display => InSpec switch
    {
        true => $"{Name} {Value:F3}{Unit} ✓",
        false => $"{Name} {Value:F3}{Unit} ✗ (限 {LowerLimit:F3}~{UpperLimit:F3})",
        _ => $"{Name} {Value:F3}{Unit}",
    };
}

/// <summary>一处缺陷。</summary>
public sealed class DefectItem
{
    public string Type { get; set; } = string.Empty;
    public double Confidence { get; set; }

    /// <summary>缺陷包围盒（像素坐标）。</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public string Area { get; set; } = string.Empty;

    public override string ToString() =>
        $"{Type} (conf={Confidence:F2}, {Width}×{Height})";
}

/// <summary>
/// 一次检测的完整结果。
///
/// <see cref="JudgementReason"/> 这个字段看着不起眼，但它是现场能否接受系统的关键。
/// 只显示"NG"，班长一定会问"凭什么？"。
/// 必须给出："外径 12.42mm 超出上限 12.40mm" 这样人话级别的理由。
/// </summary>
public sealed class InspectionResult
{
    public string RecipeId { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string ProductModel { get; set; } = string.Empty;
    public string BatchNo { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;

    public InspectionVerdict Verdict { get; set; } = InspectionVerdict.None;

    /// <summary>判断理由 —— 必须是现场人一眼看懂的句子。</summary>
    public string JudgementReason { get; set; } = string.Empty;

    public List<MeasurementItem> Measurements { get; set; } = new();
    public List<DefectItem> Defects { get; set; } = new();

    /// <summary>检测耗时（毫秒）。节拍分析要用。</summary>
    public double ElapsedMs { get; set; }

    /// <summary>
    /// 过程进度：本件已按序完成的工序数。
    ///
    /// <para>为什么单独立一个字段：序列类算法（<c>sop-sequence</c>）是跨帧有状态的，
    /// 一次触发不一定产生"结论"——画面没变化时它只能回答"还在进行中"。
    /// 如果只看 Verdict，上位机就分不清"这一步做完了"和"什么都没发生"，
    /// 结果是每触发一次都推进一道工序（早期版本的真实 bug）。</para>
    ///
    /// <para>约定：只有<b>进度增加</b>才算一次有效的工序完成；无状态算法（阈值/尺寸/Halcon）
    /// 保持 0，上位机按"Pass 即本步通过"处理。</para>
    /// </summary>
    public int ProgressSteps { get; set; }

    /// <summary>
    /// 取证图片路径。
    /// 只在判定非 Pass 时写入 —— 全存的话磁盘撑不住，而且良品图也没人看。
    /// </summary>
    public string? EvidenceImagePath { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public bool IsPass => Verdict == InspectionVerdict.Pass;

    public string VerdictText => Verdict switch
    {
        InspectionVerdict.Pass => "OK",
        InspectionVerdict.Fail => "NG",
        InspectionVerdict.Error => "ERROR",
        _ => "—",
    };

    /// <summary>测量数据汇总，一行一条，直接用于界面显示。</summary>
    public IEnumerable<string> MeasurementSummary()
    {
        foreach (var m in Measurements) yield return m.Display;
    }

    public static InspectionResult Error(string reason) => new()
    {
        Verdict = InspectionVerdict.Error,
        JudgementReason = reason,
    };
}

/// <summary>
/// 入库用的历史记录。刻意<b>不</b>含原图，只含 NG 图的磁盘路径 ——
/// 否则数据库会被图片撑爆（一张 500 万像素原图约 15MB，一天几千条就是几十 GB）。
/// </summary>
public sealed class InspectionRecord
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string RecipeId { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string ProductModel { get; set; } = string.Empty;
    public string BatchNo { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;

    public InspectionVerdict Verdict { get; set; }
    public string JudgementReason { get; set; } = string.Empty;
    public double ElapsedMs { get; set; }

    public List<MeasurementItem> Measurements { get; set; } = new();
    public List<DefectItem> Defects { get; set; } = new();

    /// <summary>NG 图存储路径。只在 Verdict != Pass 时写入。</summary>
    public string? NgImagePath { get; set; }

    public static InspectionRecord FromResult(InspectionResult r) => new()
    {
        Timestamp = r.Timestamp,
        RecipeId = r.RecipeId,
        RecipeName = r.RecipeName,
        ProductModel = r.ProductModel,
        BatchNo = r.BatchNo,
        Operator = r.Operator,
        Verdict = r.Verdict,
        JudgementReason = r.JudgementReason,
        ElapsedMs = r.ElapsedMs,
        Measurements = r.Measurements,
        Defects = r.Defects,
    };
}

/// <summary>历史查询条件。</summary>
public sealed class HistoryQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? BatchNo { get; set; }
    public string? ProductModel { get; set; }
    public string? RecipeId { get; set; }
    public InspectionVerdict? Verdict { get; set; }
    public int Limit { get; set; } = 500;
}
