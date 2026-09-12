using VisionForge.Core.Models;

namespace VisionForge.Core.Services;

/// <summary>
/// 手部动作合规规则引擎（方案 v1.3 落地）。
///
/// 输入：逐帧的手部 21 点关键点（掌心中心 + 置信度），可选的目标数量。
/// 输出：每帧一个结论 —— 进行中 / 完成一道工序 / 整件完成 / 违规 / 关键点丢失。
///
/// 三条从方案里继承、必须原样落地的设计：
///   1. K 按时间不按帧（v1.1）：停留用毫秒累计，换相机、改帧率都不用重新标定阈值。
///   2. 低置信帧跳过而非清零（v1.1）：手被零件挡一瞬间不等于"手离开了"。
///      看不清的帧既不加也不减；连续看不清超过上限就报警请人确认，
///      绝不把它当成违规去拦线 —— 拦错一次的代价远大于漏一次。
///   3. 动作内无序、工序间有序（v1.3）：单步内部目标到过即算，
///      步骤之间由 OrderEnforced 开关控制是否检跳步 / 错序。
/// </summary>
public sealed class HandActionRuleEngine
{
    private readonly ProcessRuleConfig _config;

    /// <summary>
    /// 违规记录（内存里只留最近这些条）。
    ///
    /// <para>为什么要有上限：这套系统是 7×24 常驻的，一个工位一天攒几百条违规很正常，
    /// 无限增长的 List 就是一条慢性内存泄漏。完整记录本来就会写日志、落历史库，
    /// 内存里这份只是给界面和报告用，留最近 500 条足够。</para>
    /// </summary>
    private const int MaxViolationHistory = 500;

    private readonly List<ProcessViolation> _violations = new();

    /// <summary>统一的入口：追加 + 裁剪。所有产生违规的地方都走这里，避免漏一处就白做。</summary>
    private void AddViolations(IEnumerable<ProcessViolation> items)
    {
        _violations.AddRange(items);

        int overflow = _violations.Count - MaxViolationHistory;
        if (overflow > 0) _violations.RemoveRange(0, overflow);
    }

    /// <summary>各目标在当前步骤窗口内的累计停留（ms）。</summary>
    private readonly Dictionary<string, double> _holdMs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本件已覆盖（停留达标过）的目标。</summary>
    private readonly HashSet<string> _covered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已报过超时的步骤序号，避免每帧刷屏。</summary>
    private readonly HashSet<int> _timeoutReported = new();

    /// <summary>已报过错序的"步骤:后续步骤"组合，避免重复计数。</summary>
    private readonly HashSet<string> _orderReported = new(StringComparer.OrdinalIgnoreCase);

    private int _stepIndex;
    private double _stepElapsedMs;
    private double _unreliableMs;
    private bool _unreliableRaised;
    private bool _completed;

    public HandActionRuleEngine(ProcessRuleConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Reset();
    }

    public ProcessRuleConfig Config => _config;

    public IReadOnlyList<ProcessViolation> AllViolations => _violations;

    public bool IsCompleted => _completed;

    public int CurrentStepIndex => _stepIndex;

    public ProcessStepDefinition? CurrentStep =>
        _stepIndex >= 0 && _stepIndex < _config.Steps.Count ? _config.Steps[_stepIndex] : null;

    // ==================================================================
    /// <summary>回到"新一件产品"的状态。换产品、复位流程时调用。</summary>
    public void Reset()
    {
        _stepIndex = 0;
        _stepElapsedMs = 0;
        _unreliableMs = 0;
        _unreliableRaised = false;
        _completed = _config.Steps.Count == 0;
        _holdMs.Clear();
        _covered.Clear();
        _timeoutReported.Clear();
        _orderReported.Clear();
    }

    /// <summary>清空违规记录（界面上的"清空异常列表"用）。</summary>
    public void ClearViolations() => _violations.Clear();

    // ==================================================================
    /// <summary>喂一帧姿态，返回本帧结论。</summary>
    public ProcessEvaluation Feed(PoseObservation observation)
    {
        if (_completed || _config.Steps.Count == 0)
        {
            return new ProcessEvaluation
            {
                Verdict = ProcessVerdict.AllCompleted,
                Progress = 1,
                Message = "整件工序已全部完成",
                CoveredTargets = _covered.ToList(),
            };
        }

        var step = _config.Steps[_stepIndex];
        var required = RequiredTargetsOf(step);
        double frameMs = _config.FrameMs;
        _stepElapsedMs += frameMs;

        // ---- 1) 置信度：看不清的帧跳过，既不加也不清零 ----
        double minConf = observation.HandVisible ? observation.MinConfidence : 0;
        if (minConf < _config.MinConfidence)
        {
            _unreliableMs += frameMs;
            if (_unreliableMs >= _config.MaxUnreliableMs && !_unreliableRaised)
            {
                _unreliableRaised = true;
                var alarm = new ProcessViolation
                {
                    Code = ViolationCode.Unreliable,
                    StepSeq = step.Seq,
                    Message = $"关键点连续丢失 {_unreliableMs:F0}ms，请人工确认（不判违规）",
                };
        AddViolations(new[] { alarm });
                return new ProcessEvaluation
                {
                    Verdict = ProcessVerdict.Unreliable,
                    StepSeq = step.Seq,
                    StepName = step.Name,
                    Message = alarm.Message,
                    Progress = ProgressOf(),
                    NewViolations = new[] { alarm },
                    CoveredTargets = _covered.ToList(),
                    ActiveTargets = required,
                };
            }

            return new ProcessEvaluation
            {
                Verdict = ProcessVerdict.InProgress,
                StepSeq = step.Seq,
                StepName = step.Name,
                Message = "关键点不可信，本帧跳过（不影响已累计的停留）",
                Progress = ProgressOf(),
                CoveredTargets = _covered.ToList(),
                ActiveTargets = required,
            };
        }
        _unreliableMs = 0;
        _unreliableRaised = false;

        // ---- 2) 掌心中心（不是腕点：腕点抖动大）----
        if (!observation.TryGetPalmCenter(out double handX, out double handY))
        {
            return new ProcessEvaluation
            {
                Verdict = ProcessVerdict.InProgress,
                StepSeq = step.Seq,
                StepName = step.Name,
                Message = "本帧没有可用的手部关键点",
                Progress = ProgressOf(),
                CoveredTargets = _covered.ToList(),
                ActiveTargets = required,
            };
        }

        // ---- 3) 累计每个目标的停留（时间基准）----
        foreach (var target in _config.Targets)
        {
            if (target.Contains(handX, handY))
            {
                _holdMs.TryGetValue(target.Id, out double current);
                _holdMs[target.Id] = current + frameMs;
            }
            else
            {
                _holdMs[target.Id] = 0;   // 手离开就清零
            }
        }

        // ---- 4) 当前步骤是否达成（集合覆盖：本步的目标都到过且停留够）----
        double k = step.Kms > 0 ? step.Kms : _config.Kms;
        bool stepDone = required.Count > 0 && required.All(id => HoldOf(id) >= k);
        double confidence = required.Count == 0
            ? 0
            : Math.Min(0.99, required.Min(id => Math.Min(1.0, HoldOf(id) / k)));

        foreach (var target in _config.Targets)
        {
            if (HoldOf(target.Id) >= k) _covered.Add(target.Id);
        }

        // ---- 5) 对象状态轨：数量校验（MVP 支持计数）----
        if (stepDone && _config.ObjectState.Count && observation.TargetCounts is { } counts)
        {
            foreach (var id in required)
            {
                var target = _config.FindTarget(id);
                if (target is null || target.ExpectedCount <= 0) continue;

                counts.TryGetValue(id, out int actual);
                if (actual < target.ExpectedCount)
                {
                    var violation = new ProcessViolation
                    {
                        Code = ViolationCode.CountMismatch,
                        StepSeq = step.Seq,
                        TargetId = id,
                        Message = $"{target.Name}（{id}）期望 {target.ExpectedCount} 个，实际 {actual} 个",
                    };
        AddViolations(new[] { violation });
                    return new ProcessEvaluation
                    {
                        Verdict = ProcessVerdict.Violation,
                        StepSeq = step.Seq,
                        StepName = step.Name,
                        Confidence = confidence,
                        Progress = ProgressOf(),
                        Message = violation.Message,
                        NewViolations = new[] { violation },
                        CoveredTargets = _covered.ToList(),
                        ActiveTargets = required,
                    };
                }
            }
        }

        // ---- 6) 顺序检查：还没做当前工序，就先做了后面的 ----
        if (_config.OrderEnforced)
        {
            for (int i = _stepIndex + 1; i < _config.Steps.Count; i++)
            {
                var later = _config.Steps[i];
                var laterTargets = RequiredTargetsOf(later);
                if (laterTargets.Count == 0) continue;

                double laterK = later.Kms > 0 ? later.Kms : _config.Kms;
                bool laterDone = laterTargets.All(id => HoldOf(id) >= laterK);
                if (!laterDone) continue;

                string key = step.Seq + ":" + later.Seq;
                if (_orderReported.Contains(key)) break;
                _orderReported.Add(key);

                var list = new List<ProcessViolation>
                {
                    new()
                    {
                        Code = ViolationCode.StepOutOfOrder,
                        StepSeq = later.Seq,
                        Message = $"还没做第 {step.Seq} 道「{step.Name}」，就先做了第 {later.Seq} 道「{later.Name}」",
                    },
                };

                for (int skippedIndex = _stepIndex; skippedIndex < i; skippedIndex++)
                {
                    var skipped = _config.Steps[skippedIndex];
                    list.Add(new ProcessViolation
                    {
                        Code = ViolationCode.StepSkipped,
                        StepSeq = skipped.Seq,
                        Message = $"第 {skipped.Seq} 道「{skipped.Name}」被跳过",
                    });
                }

        AddViolations(list);
                return new ProcessEvaluation
                {
                    Verdict = ProcessVerdict.Violation,
                    StepSeq = step.Seq,
                    StepName = step.Name,
                    Confidence = confidence,
                    Progress = ProgressOf(),
                    Message = list[0].Message,
                    NewViolations = list,
                    CoveredTargets = _covered.ToList(),
                    ActiveTargets = required,
                };
            }
        }

        // ---- 7) 本步完成 → 推进 ----
        if (stepDone)
        {
            _stepIndex++;
            _stepElapsedMs = 0;
            _holdMs.Clear();

            bool allDone = _stepIndex >= _config.Steps.Count;
            _completed = allDone;

            return new ProcessEvaluation
            {
                Verdict = allDone ? ProcessVerdict.AllCompleted : ProcessVerdict.StepCompleted,
                StepSeq = step.Seq,
                StepName = step.Name,
                Confidence = confidence,
                Progress = ProgressOf(),
                Message = allDone
                    ? $"整件完成：{_config.Steps.Count} 道工序全部合规"
                    : $"第 {step.Seq} 道「{step.Name}」合规完成 → 下一步：{_config.Steps[_stepIndex].Name}",
                CoveredTargets = _covered.ToList(),
                ActiveTargets = allDone
                    ? Array.Empty<string>()
                    : RequiredTargetsOf(_config.Steps[_stepIndex]),
            };
        }

        // ---- 8) 超时：目标未完成 ----
        if (_stepElapsedMs > _config.TimeWindowMs && !_timeoutReported.Contains(step.Seq))
        {
            _timeoutReported.Add(step.Seq);

            var missed = required.Where(id => HoldOf(id) < k).ToList();
            var names = missed.Select(id => _config.FindTarget(id)?.Name ?? id).ToList();

            var violation = new ProcessViolation
            {
                Code = ViolationCode.StepNotDone,
                StepSeq = step.Seq,
                TargetId = missed.FirstOrDefault() ?? string.Empty,
                Message = $"第 {step.Seq} 道「{step.Name}」超时（{_config.TimeWindowMs:F0}ms）：" +
                          (names.Count > 0 ? "未达标 " + string.Join("、", names) : "目标未完成"),
            };
        AddViolations(new[] { violation });

            return new ProcessEvaluation
            {
                Verdict = ProcessVerdict.Violation,
                StepSeq = step.Seq,
                StepName = step.Name,
                Confidence = confidence,
                Progress = ProgressOf(),
                Message = violation.Message,
                NewViolations = new[] { violation },
                CoveredTargets = _covered.ToList(),
                ActiveTargets = required,
            };
        }

        // ---- 9) 进行中 ----
        return new ProcessEvaluation
        {
            Verdict = ProcessVerdict.InProgress,
            StepSeq = step.Seq,
            StepName = step.Name,
            Confidence = confidence,
            Progress = ProgressOf(),
            Message = $"进行中：{step.Name}（{confidence:P0}）",
            CoveredTargets = _covered.ToList(),
            ActiveTargets = required,
        };
    }

    // ==================================================================
    private double HoldOf(string targetId) =>
        _holdMs.TryGetValue(targetId, out double value) ? value : 0;

    private double ProgressOf() =>
        _config.Steps.Count == 0 ? 0 : Math.Min(1.0, (double)_stepIndex / _config.Steps.Count);

    /// <summary>取某步的目标集合：单步模式下允许用全局 Required 覆盖。</summary>
    private List<string> RequiredTargetsOf(ProcessStepDefinition step)
    {
        if (_config.Steps.Count == 1 && _config.Required.Count > 0)
            return _config.Required.ToList();

        return step.Targets.ToList();
    }
}
