using System.Diagnostics;

namespace VisionForge.Core.Services;

/// <summary>
/// 批量仿真 / 验收指标报告（对应方案十：测试逻辑与验收标准）。
/// </summary>
public sealed class ValidationReport
{
    public int Total { get; set; }
    public int OkSamples { get; set; }
    public int NgSamples { get; set; }

    /// <summary>判定与人工标签一致的条数。</summary>
    public int Correct { get; set; }

    /// <summary>漏检：人工标 NG，系统却没报 NG（判 OK 或无法判定）。</summary>
    public int Missed { get; set; }

    /// <summary>误检：人工标 OK，系统却报了 NG。</summary>
    public int FalseAlarm { get; set; }

    /// <summary>无法判定（相似度不够，转人工确认）—— 不计入漏检也不计入误检，单独统计。</summary>
    public int Unknown { get; set; }

    public double Accuracy { get; set; }
    public double MissRate { get; set; }
    public double FalseAlarmRate { get; set; }
    public double AverageMs { get; set; }

    /// <summary>被截断的样本数（超过批量上限的部分）。</summary>
    public int Skipped { get; set; }

    public List<string> Failures { get; } = new();

    /// <summary>是否达到方案 10.6 的验收线（准确率≥98%、漏检≤1%、误检≤1%）。</summary>
    public bool MeetsAcceptance => Total > 0 && Accuracy >= 0.98 && MissRate <= 0.01 && FalseAlarmRate <= 0.01;

    public string Summary =>
        $"样本 {Total} 条（OK {OkSamples} / NG {NgSamples}）· " +
        $"准确率 {Accuracy:P1} · 漏检 {MissRate:P1} · 误检 {FalseAlarmRate:P1} · " +
        $"无法判定 {Unknown} · 平均 {AverageMs:F1}ms/条 · " +
        (MeetsAcceptance ? "达到验收线（≥98% / ≤1% / ≤1%）" : "未达验收线") +
        (Skipped > 0 ? $"（超出批量上限，跳过 {Skipped} 条）" : string.Empty);
}

/// <summary>
/// 教示样本的批量仿真校验 —— 用"留一法"把样本库自己当考题考一遍。
///
/// <para><b>为什么是留一法：</b>样本是现成的人工标注，不需要额外录数据。
/// 拿每条样本去问"用其余样本能不能认出你是 OK 还是 NG"：
/// 认得出，说明这条样本和同类像、和异类不像（特征有区分度）；
/// 认不出，说明样本不够、或者这两类动作本来就分不开 ——
/// 这比"我教了 10 条，感觉应该行"要可靠得多。</para>
///
/// <para>产出的准确率 / 漏检率 / 误检率，正好对应方案 10.6 的验收指标。
/// 批次上限 256 条（对应方案"支持 256 组样本批量仿真"）。</para>
/// </summary>
public static class ActionSampleValidator
{
    public const int MaxBatchSamples = 256;

    public static ValidationReport LeaveOneOut(
        IReadOnlyList<ActionSample> samples,
        double minSimilarity,
        int maxBatch = MaxBatchSamples)
    {
        var report = new ValidationReport();
        if (samples is null || samples.Count == 0) return report;

        var batch = samples.Take(maxBatch).ToList();
        report.Skipped = Math.Max(0, samples.Count - batch.Count);
        report.Total = batch.Count;
        report.OkSamples = batch.Count(s => s.IsOk);
        report.NgSamples = batch.Count(s => !s.IsOk);

        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < batch.Count; i++)
        {
            var sample = batch[i];

            // 用"除它以外"的样本当训练集
            var others = batch.Where((_, index) => index != i).ToList();
            if (others.Count == 0) break;

            var classifier = new ActionSampleClassifier(others) { MinSimilarity = minSimilarity };
            var result = classifier.Recognize(sample.Feature);

            if (result.IsOk is null)
            {
                report.Unknown++;
                report.Failures.Add($"{sample.Display} → 无法判定（最高相似度 {result.Confidence:P0}）");
            }
            else if (result.IsOk == sample.IsOk)
            {
                report.Correct++;
            }
            else if (!sample.IsOk && result.IsOk == true)
            {
                report.Missed++;
                report.Failures.Add($"{sample.Display} → 漏检：NG 被判成 OK（相似度 {result.Confidence:P0}）");
            }
            else
            {
                report.FalseAlarm++;
                report.Failures.Add($"{sample.Display} → 误检：OK 被判成 NG（相似度 {result.Confidence:P0}）");
            }
        }

        stopwatch.Stop();

        report.Accuracy = report.Total == 0 ? 0 : (double)report.Correct / report.Total;
        report.MissRate = report.NgSamples == 0 ? 0 : (double)report.Missed / report.NgSamples;
        report.FalseAlarmRate = report.OkSamples == 0 ? 0 : (double)report.FalseAlarm / report.OkSamples;
        report.AverageMs = report.Total == 0 ? 0 : stopwatch.Elapsed.TotalMilliseconds / report.Total;

        return report;
    }
}
