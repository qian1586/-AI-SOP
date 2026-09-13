using VisionForge.Core.Models;
using VisionForge.Core.Services;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 手部轨迹 → 自动判工序。
///
/// <para><b>为什么要这一步：</b>只靠零件判定，只能知道"东西装得对不对"；
/// 加上手的轨迹才知道"人有没有动手、先做的哪一步"。
/// 文章里的 3.2（手部位姿判断操作区域、跟踪轨迹）+ 3.5（顺序校验）就是这两件事。</para>
///
/// <para><b>分寸（很重要）：</b>手的动作**只用来补充判断，不用来代替零件判定**。
/// 手伸进框里不等于零件装好了 —— 所以这里做的是：
///   · 手去了「当前该做的那一步」→ 记一笔"这一步有动作"，配合零件结果把结论说得更准
///     （零件没到位就说"做了但没装上"，而不是笼统的"NG"）；
///   · 手先去了「后面才该做的那一步」→ 判**错序**（以前只有零件判定能发现，手被遮挡时就漏了）；
///   · 手回到已经做完的框 → 当作"复检/补做"，不报警。</para>
/// </summary>
public sealed partial class MainViewModel
{
    private readonly HandActionTracker _handTracker = new();

    private string _handPathText = "手的动作轨迹：（还没有动作）";
    private string _handActionText = "手柄动作判定：—";

    /// <summary>手走过的路径（只在"停留够时间"时才记），例如 第1框→第2框。</summary>
    public string HandPathText
    {
        get => _handPathText;
        private set => SetProperty(ref _handPathText, value);
    }

    /// <summary>最近一次手部动作的判定说明。</summary>
    public string HandActionText
    {
        get => _handActionText;
        private set => SetProperty(ref _handActionText, value);
    }

    /// <summary>
    /// 喂一帧手部结果（在 ApplyHandPose 里调）。
    /// </summary>
    private void TrackHandAction(DateTime time, int roiIndex, double score)
    {
        var events = _handTracker.Observe(time, roiIndex, score);

        if (events.Count == 0)
        {
            // 没事件也要把"当前停留多久"显示出来，现场才知道系统看没看见手
            if (_handTracker.CurrentRoi > 0)
            {
                HandZoneText = $"手部位置：第 {_handTracker.CurrentRoi} 个框" +
                               $"（已停留 {_handTracker.CurrentDwellMs:F0}ms）";
            }
            return;
        }

        foreach (var e in events)
        {
            _log.Debug("手部动作：" + e.Describe(e.RoiIndex));

            if (e.Kind == "Dwell") ReportHandAction(e.RoiIndex, e.DwellMs);
        }

        if (_handTracker.CurrentRoi > 0)
        {
            HandZoneText = $"手部位置：第 {_handTracker.CurrentRoi} 个框" +
                           $"（已停留 {_handTracker.CurrentDwellMs:F0}ms）";
        }
        else
        {
            HandZoneText = "手部位置：框外";
        }

        HandPathText = "手的动作轨迹：" + _handTracker.PathText;
    }

    /// <summary>
    /// 手在某个框里停留够时间了（做了个动作）—— 判它是哪一步、顺序对不对。
    ///
    /// <para>公开出来是因为"手部动作"这个输入将来可能来自不同的源
    /// （现在的 ONNX 模型、以后的其它姿态源、甚至外部设备送进来的信号），
    /// 判定逻辑应该只有这一份；自检也直接调它来验证错序判定。</para>
    /// </summary>
    public void ReportHandAction(int roiIndex, double dwellMs)
    {
        int currentSeq = Sop.CurrentStep?.Seq ?? 0;
        string name = RoiTargets.FirstOrDefault(t => t.Index == roiIndex)?.Name
                      ?? $"第 {roiIndex} 个框";

        if (currentSeq == 0)
        {
            HandActionText = $"手在「{name}」做了动作（流程还没开始）";
            return;
        }

        if (roiIndex == currentSeq)
        {
            // 正该做这一步：记一笔"有动作"。这一步最终通过与否仍由零件判定说了算。
            HandActionText = $"手在「{name}」做了动作（停留 {dwellMs:F0}ms）—— 正是当前该做的第 {currentSeq} 步";
            StatusMessage = HandActionText;
            return;
        }

        if (roiIndex < currentSeq)
        {
            HandActionText = $"手回到「{name}」—— 已完成过的步骤，按复检处理（不报警）";
            return;
        }

        // 手先去了后面才该做的那一步：错序。
        // 以前只有零件判定才能发现错序；手被工具/身体挡住时那条路会漏，这条补上。
        HandActionText = $"⚠ 错序：手先去了第 {roiIndex} 步「{name}」，当前应该做第 {currentSeq} 步";

        var violation = new ProcessViolation
        {
            Code = ViolationCode.StepSkipped,
            StepSeq = roiIndex,
            TargetId = "H" + roiIndex,
            Message = $"手部动作错序：先动了第 {roiIndex} 步「{name}」，" +
                      $"当前应该做第 {currentSeq} 步「{Sop.CurrentStep?.Name}」",
        };

        var evidence = CaptureEvidence($"手部错序 · 第{roiIndex}步");
        if (evidence is not null) violation.EvidenceImagePath = evidence.Path;

        ProcessViolations.Insert(0, violation);
        _ = WriteRuleCodeToPlcAsync(violation);

        StatusMessage = violation.Message;
        _log.Warn(violation.Message);
    }
}
