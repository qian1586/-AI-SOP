using System.Collections.ObjectModel;
using System.ComponentModel;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Models;

namespace VisionForge.Core.Services;

/// <summary>步骤状态。</summary>
public enum SopStepState
{
    /// <summary>还没轮到。</summary>
    Pending,

    /// <summary>当前正在做的步骤，界面高亮。</summary>
    Active,

    /// <summary>已通过。</summary>
    Passed,

    /// <summary>检测不通过，等待返工重试。</summary>
    Failed,
}

/// <summary>一次检测结果带来的流程变化。</summary>
public enum AdvanceOutcome
{
    /// <summary>结果被忽略（流程已锁死 / 已完成 / 无当前步骤）。</summary>
    Ignored,

    /// <summary>本步骤通过，已推进到下一步。</summary>
    Advanced,

    /// <summary>本步骤不通过，停留在原步骤（可能已锁线）。</summary>
    StepFailed,

    /// <summary>最后一步通过，整件完成。</summary>
    ProcessCompleted,
}

/// <summary>运行时的步骤对象，可绑定到界面。</summary>
public sealed class SopStep : ObservableObject
{
    private SopStepState _state = SopStepState.Pending;
    private string _lastMessage = string.Empty;
    private int _failCount;
    private DateTime _enteredAt = DateTime.Now;
    private string _name = string.Empty;
    private string _hint = string.Empty;
    private bool _blockNextOnFail;
    private double _timeoutSec;

    public int Seq { get; init; }
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 步骤名称。运行时可以改（界面上右键小方框 → 改名称），
    /// 所以不是 init-only：改完步骤条上的字要立刻跟着变。
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>作业指引（给操作员看的一句话）。同样可运行时修改。</summary>
    public string Hint
    {
        get => _hint;
        set => SetProperty(ref _hint, value);
    }

    public string RecipeId { get; init; } = string.Empty;

    /// <summary>NG 时是否锁死流程。可运行时改（关键工序锁、辅助工序放行）。</summary>
    public bool BlockNextOnFail
    {
        get => _blockNextOnFail;
        set => SetProperty(ref _blockNextOnFail, value);
    }

    /// <summary>允许的超时秒数，0 = 不检查。可运行时改。</summary>
    public double TimeoutSec
    {
        get => _timeoutSec;
        set => SetProperty(ref _timeoutSec, value);
    }

    public SopStepState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateKey));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    /// <summary>最近一次判定理由，直接显示给操作员。</summary>
    public string LastMessage
    {
        get => _lastMessage;
        set => SetProperty(ref _lastMessage, value);
    }

    /// <summary>本步骤累计失败次数。同一工位反复出错的信号。</summary>
    public int FailCount
    {
        get => _failCount;
        set => SetProperty(ref _failCount, value);
    }

    /// <summary>进入本步骤的时刻，用于超时计时。</summary>
    public DateTime EnteredAt
    {
        get => _enteredAt;
        set => SetProperty(ref _enteredAt, value);
    }

    public bool IsFailed => State == SopStepState.Failed;

    /// <summary>界面据此上色。刻意用字符串而不是 Brush —— Core 层不依赖 WPF。</summary>
    public string StateKey => State switch
    {
        SopStepState.Passed => "ok",
        SopStepState.Failed => "ng",
        SopStepState.Active => "active",
        _ => "pending",
    };

    public string StateText => State switch
    {
        SopStepState.Passed => "已通过",
        SopStepState.Failed => "不合格",
        SopStepState.Active => "进行中",
        _ => "待执行",
    };

    public string Display => $"{Seq}. {Name}";
}

/// <summary>
/// SOP 流程状态机。
///
/// <para><b>它解决的问题：</b>光有检测还不够，产线真正需要的是"顺序锁死"。</para>
/// 没有状态机的时候：员工跳过 S2 直接做 S3，检测照样通过，因为每一步都是独立的。
/// 有状态机之后：S2 没通过就推进不到 S3，第一步 NG 后面全部走不通。
/// 这就是"过程合规"和"结果合格"的区别 —— 产品最后可能是好的，
/// 但过程错了，下次就可能出坏品。状态机管的是过程。
///
/// <para><b>三条设计原则（都是现场逼出来的）：</b></para>
/// <list type="number">
///   <item>NG 后<b>停留在原步骤</b>，不自动前进。员工修正后重新触发复检，通过了才走下一步。</item>
///   <item>是否锁死由配方里的 <c>BlockNextOnFail</c> 决定。安规/关键工序锁死，
///         辅助工序只告警不锁线 —— 全锁会让产线动不动就停，反而会被绕过。</item>
///   <item>提供<b>带权限的手动放行</b>。<b>必须留这个口子</b>：
///         视觉会误判，产线不能因为一次误判就停到下班。手动放行的记录会被标记出来，
///         事后统计"人工干预率"，如果某工位长期靠手动放行，说明算法该调了。</item>
/// </list>
/// </summary>
public sealed class SopProcess : ObservableObject
{
    private SopStep? _currentStep;
    private bool _isLocked;
    private bool _isCompleted;
    private string _processMessage = "未开始";
    private int _manualOverrideCount;

    public SopProcess()
    {
        Steps = new ObservableCollection<SopStep>();
    }

    public ObservableCollection<SopStep> Steps { get; }

    public SopDefinition? Definition { get; private set; }

    public SopStep? CurrentStep
    {
        get => _currentStep;
        private set
        {
            if (SetProperty(ref _currentStep, value))
            {
                OnPropertyChanged(nameof(CurrentStepText));
                OnPropertyChanged(nameof(CurrentHint));
            }
        }
    }

    /// <summary>流程被锁死 —— 界面应弹出禁用/报警提示。</summary>
    public bool IsLocked
    {
        get => _isLocked;
        private set => SetProperty(ref _isLocked, value);
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        private set => SetProperty(ref _isCompleted, value);
    }

    public string ProcessMessage
    {
        get => _processMessage;
        private set => SetProperty(ref _processMessage, value);
    }

    /// <summary>人工放行次数。这个数字持续增长是算法失效的信号。</summary>
    public int ManualOverrideCount
    {
        get => _manualOverrideCount;
        private set => SetProperty(ref _manualOverrideCount, value);
    }

    public string CurrentStepText => CurrentStep is null
        ? "—"
        : $"{CurrentStep.Seq}/{Steps.Count}  {CurrentStep.Name}";

    public string CurrentHint => CurrentStep?.Hint ?? string.Empty;

    public int PassedCount => Steps.Count(s => s.State == SopStepState.Passed);

    /// <summary>总步骤数。</summary>
    public int TotalSteps => Steps.Count;

    /// <summary>进度文案，如"2 / 6 完成"。右侧步骤列表标题用。</summary>
    public string ProgressText => $"{PassedCount} / {TotalSteps} 完成";

    /// <summary>整件是否全部按序通过。</summary>
    public bool AllPassed => Steps.Count > 0 && Steps.All(s => s.State == SopStepState.Passed);

    // ------------------------------------------------------------------
    /// <summary>步骤判定结果出来时触发。界面据此弹窗、响警。</summary>
    public event EventHandler<AlarmEventArgs>? Alarm;

    /// <summary>流程推进时触发（可用来刷界面、写日志）。</summary>
    public event EventHandler? StateChanged;

    // ------------------------------------------------------------------
    public void Load(SopDefinition sop)
    {
        Definition = sop;
        Steps.Clear();

        foreach (var def in sop.OrderedSteps)
        {
            var step = new SopStep
            {
                Seq = def.Seq,
                Id = def.Id,
                Name = def.Name,
                Hint = def.Hint,
                RecipeId = def.RecipeId,
                BlockNextOnFail = def.BlockNextOnFail,
                TimeoutSec = def.TimeoutSec,
            };

            // 步骤被改名/改指引后，页面上"当前步骤"那两行也要跟着变 ——
            // 否则会出现"步骤条上叫新名字、上面还写着旧名字"的自相矛盾。
            step.PropertyChanged += OnStepPropertyChanged;
            Steps.Add(step);
        }

        Reset();
        RaiseProgressProps();
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, CurrentStep)) return;

        OnPropertyChanged(nameof(CurrentStepText));
        OnPropertyChanged(nameof(CurrentHint));
        OnPropertyChanged(nameof(ProgressText));
    }

    private void RaiseProgressProps()
    {
        OnPropertyChanged(nameof(PassedCount));
        OnPropertyChanged(nameof(TotalSteps));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(AllPassed));
    }

    public void Reset()
    {
        foreach (var s in Steps)
        {
            s.State = SopStepState.Pending;
            s.LastMessage = string.Empty;
            s.FailCount = 0;
        }

        IsLocked = false;
        IsCompleted = false;
        ManualOverrideCount = 0;
        CurrentStep = Steps.FirstOrDefault();

        if (CurrentStep is not null)
        {
            CurrentStep.State = SopStepState.Active;
            CurrentStep.EnteredAt = DateTime.Now;
            ProcessMessage = $"等待执行：{CurrentStep.Name}";
        }
        else
        {
            ProcessMessage = "未配置任何步骤";
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// 把一次检测结果喂给状态机。
    /// 这是整个上位机的中枢 —— Halcon 只管给结果，怎么流转由它决定。
    /// </summary>
    public AdvanceOutcome ApplyResult(InspectionResult result)
    {
        if (CurrentStep is null || IsCompleted) return AdvanceOutcome.Ignored;

        // None = 序列算法报的"还在进行中，本帧没有新结论"。
        // 它不是不良，也不是系统故障，绝不能当成 Error 报警，更不能推进流程。
        if (result.Verdict == InspectionVerdict.None) return AdvanceOutcome.Ignored;

        var step = CurrentStep;

        if (result.Verdict == InspectionVerdict.Pass)
        {
            // 重试成功的情况也要处理：之前是 Failed，现在过了
            step.FailCount = step.FailCount;   // 保留历史失败次数，用于统计
            step.State = SopStepState.Passed;
            step.LastMessage = result.JudgementReason;

            // 复检通过必须解锁。
            // 漏掉这一句的症状很典型：NG 后返工做对了，流程却还是锁着，
            // 红灯不灭、还得再点一次「人工放行」——现场会认为软件坏了。
            IsLocked = false;
            RaiseProgressProps();

            var idx = Steps.IndexOf(step);
            if (idx >= Steps.Count - 1)
            {
                // 最后一步通过
                IsCompleted = true;
                CurrentStep = null;
                ProcessMessage = "✅ 整件完成，全部步骤按序通过";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return AdvanceOutcome.ProcessCompleted;
            }

            var next = Steps[idx + 1];
            next.State = SopStepState.Active;
            next.EnteredAt = DateTime.Now;
            CurrentStep = next;
            ProcessMessage = $"下一步：{next.Name} —— {next.Hint}";

            StateChanged?.Invoke(this, EventArgs.Empty);
            return AdvanceOutcome.Advanced;
        }

        if (result.Verdict == InspectionVerdict.Fail)
        {
            step.State = SopStepState.Failed;
            step.FailCount++;
            step.LastMessage = result.JudgementReason;

            // 关键：NG 后停留在原步骤，不前进
            if (step.BlockNextOnFail)
            {
                IsLocked = true;
                ProcessMessage = $"❌ {step.Name} 不合格，流程已锁死。修正后重新触发复检。";
                Alarm?.Invoke(this, new AlarmEventArgs(step, result, isBlocking: true));
            }
            else
            {
                IsLocked = false;
                ProcessMessage = $"⚠️ {step.Name} 不合格（非关键工序，允许继续）。{result.JudgementReason}";
                Alarm?.Invoke(this, new AlarmEventArgs(step, result, isBlocking: false));
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            return AdvanceOutcome.StepFailed;
        }

        // Error：系统故障，不算产品不良，也不该锁线把人困住
        step.LastMessage = $"检测异常：{result.JudgementReason}";
        ProcessMessage = $"⚠️ 检测未完成 —— {result.JudgementReason}";
        Alarm?.Invoke(this, new AlarmEventArgs(step, result, isBlocking: false));
        StateChanged?.Invoke(this, EventArgs.Empty);
        return AdvanceOutcome.Ignored;
    }

    /// <summary>
    /// 人工强制放行。
    ///
    /// <b>这个口子必须有</b>，否则第一个误判就能把产线卡到下班。
    /// 但每一次调用都会被计数 —— 人工干预率是评估视觉算法是否合格的核心指标。
    /// 建议在界面上要求输入工号或密码，让放行有责任人。
    /// </summary>
    public bool ForceAdvance(string reason)
    {
        if (CurrentStep is null || IsCompleted) return false;

        var step = CurrentStep;
        step.State = SopStepState.Passed;
        step.LastMessage = $"[人工放行] {reason}";
        ManualOverrideCount++;

        var idx = Steps.IndexOf(step);
        if (idx >= Steps.Count - 1)
        {
            IsCompleted = true;
            CurrentStep = null;
            IsLocked = false;
            ProcessMessage = "整件完成（含人工放行）";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        var next = Steps[idx + 1];
        next.State = SopStepState.Active;
        next.EnteredAt = DateTime.Now;
        CurrentStep = next;
        IsLocked = false;
        ProcessMessage = $"⚠️ 人工放行 {step.Name}（累计 {ManualOverrideCount} 次）→ 下一步：{next.Name}";

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>检查当前步骤是否超时。由界面定时器调用。</summary>
    public string? CheckTimeout()
    {
        var step = CurrentStep;
        if (step is null || step.TimeoutSec <= 0) return null;

        var elapsed = (DateTime.Now - step.EnteredAt).TotalSeconds;
        if (elapsed <= step.TimeoutSec) return null;

        return $"{step.Name} 已耗时 {elapsed:F0}s，超过规定 {step.TimeoutSec:F0}s";
    }

    /// <summary>累计各步骤的失败次数，用于找"哪个工位最容易出错"。</summary>
    public IReadOnlyList<(string Step, int FailCount)> FailRanking =>
        Steps.Where(s => s.FailCount > 0)
             .OrderByDescending(s => s.FailCount)
             .Select(s => ($"{s.Id} {s.Name}", s.FailCount))
             .ToList();
}

/// <summary>报警事件参数。</summary>
public sealed class AlarmEventArgs : EventArgs
{
    public AlarmEventArgs(SopStep step, InspectionResult result, bool isBlocking)
    {
        Step = step;
        Result = result;
        IsBlocking = isBlocking;
    }

    public SopStep Step { get; }
    public InspectionResult Result { get; }

    /// <summary>是否锁线。界面据此决定是"红色弹窗+声音"还是"黄色提示"。</summary>
    public bool IsBlocking { get; }
}
