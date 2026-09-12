using VisionForge.Core.Models;

namespace VisionForge.Core.Services;

/// <summary>一次触发检测之后的完整结论：检测结果 + 流程走向 + 该不该计数。</summary>
public sealed class MonitoringOutcome
{
    public MonitoringOutcome(
        InspectionResult result,
        AdvanceOutcome advance,
        bool countsAsOk,
        bool countsAsNg,
        string message)
    {
        Result = result;
        Advance = advance;
        CountsAsOk = countsAsOk;
        CountsAsNg = countsAsNg;
        Message = message;
    }

    public InspectionResult Result { get; }
    public AdvanceOutcome Advance { get; }
    public bool CountsAsOk { get; }
    public bool CountsAsNg { get; }
    public string Message { get; }
}

/// <summary>
/// 监测会话 —— "一次触发 → 判定 → 流程流转 → 计数"这条链路的<b>唯一实现</b>。
///
/// <para><b>为什么单独抽出来：</b>这段逻辑以前写在主界面 ViewModel 里，
/// 结果就是"界面跑的逻辑"和"测试跑的逻辑"是两份代码，测过不等于现场对。
/// 抽成一个不依赖 WPF 的类之后，界面和自动化自检走的是同一条路径 ——
/// 自检通过，界面上的行为就是通过的那个行为。</para>
///
/// <para><b>它解决的一个真实缺陷：</b>跨帧有状态的序列算法，一次触发不一定产生结论
/// （画面没变化时就是"进行中"）。早期版本不看进度、只看判定，
/// 于是每触发一次都推进一步 —— 员工什么都没做，进度条也在往前走。
/// 现在只有<b>进度确实增加</b>才算完成一道工序。</para>
/// </summary>
public sealed class MonitoringSession
{
    private readonly SopProcess _sop;
    private int _lastProgress;

    public MonitoringSession(SopProcess sop)
    {
        _sop = sop ?? throw new ArgumentNullException(nameof(sop));
    }

    /// <summary>累计通过的工序数（不是件数）。</summary>
    public int OkCount { get; private set; }

    /// <summary>累计不合格次数。</summary>
    public int NgCount { get; private set; }

    /// <summary>累计完成的整件数。</summary>
    public int CompletedPieces { get; private set; }

    /// <summary>当前这件已完成的工序数。</summary>
    public int LastProgress => _lastProgress;

    /// <summary>合格率（按工序计）。</summary>
    public double PassRate =>
        (OkCount + NgCount) == 0 ? 0 : (double)OkCount / (OkCount + NgCount) * 100.0;

    /// <summary>开始新一件时清零进度（计数保持累计）。</summary>
    public void ResetForNewPiece() => _lastProgress = 0;

    /// <summary>把一次检测结果并入监测会话，返回该做什么。</summary>
    public MonitoringOutcome Process(InspectionResult result)
    {
        if (result.Verdict == InspectionVerdict.Fail)
        {
            NgCount++;
            var advance = _sop.ApplyResult(result);
            return new MonitoringOutcome(result, advance, countsAsOk: false, countsAsNg: true,
                                         message: result.JudgementReason);
        }

        if (result.Verdict == InspectionVerdict.Pass)
        {
            // ProgressSteps == 0 表示"这个算法没有过程进度概念"（阈值/尺寸/Halcon），
            // 对这种算法，Pass 就是本步通过。
            bool progressed = result.ProgressSteps <= 0 || result.ProgressSteps > _lastProgress;
            if (!progressed)
            {
                return new MonitoringOutcome(result, AdvanceOutcome.Ignored, false, false,
                    message: $"本步没有新的进展（进度仍为 {_lastProgress}），流程保持不变");
            }

            if (result.ProgressSteps > _lastProgress) _lastProgress = result.ProgressSteps;
            OkCount++;

            var advance = _sop.ApplyResult(result);
            if (advance == AdvanceOutcome.ProcessCompleted)
            {
                CompletedPieces++;
                _lastProgress = 0;     // 下一件从零开始
            }

            return new MonitoringOutcome(result, advance, countsAsOk: true, countsAsNg: false,
                                         message: result.JudgementReason);
        }

        // None：进行中 / 无结论；Error：系统故障。都不计数、不推进流程。
        return new MonitoringOutcome(result, AdvanceOutcome.Ignored, false, false,
                                     message: result.JudgementReason);
    }
}
