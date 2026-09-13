using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionForge.Common.Events;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Hardware.Camera;
using VisionForge.Hardware.Simulation;
using VisionForge.Hardware.Vision;
using VisionForge.Infrastructure.Config;
using VisionForge.Infrastructure.Storage;
using VisionForge.Main.Imaging;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 主界面 ViewModel —— 整个上位机的中枢。
///
/// <para><b>它编排的这条链路，就是这套系统的全部价值所在：</b></para>
/// <code>
///   PLC 给到位信号 ──┐
///                    ↓
///   相机软触发拍照 ──→ 取一帧 CameraFrame
///                    ↓
///   算法插件 Run() ──→ InspectionResult（OK/NG + 理由 + 测量值）
///                    ↓
///   SOP 状态机 ──────→ 允许流转 / 锁死流程
///                    ↓
///   ┌────────────────┼────────────────┐
///   ↓                ↓                ↓
/// 写回 PLC       存 NG 图 + 历史     界面提示 + 报警
/// </code>
///
/// <b>注意 ViewModel 里没有一行视觉算法代码。</b>
/// 它只跟 IInspectionAlgorithm / ICamera / IPlcClient 这些接口打交道。
/// 这就是"硬件抽象层 + 插件化"带来的收益：换相机、换算法、换 PLC，
/// 这个文件都不用改。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILogger _log;
    private BitmapSource? _previewImage;
    private string _batchNo = DateTime.Now.ToString("yyyyMMdd");
    private string _currentOperator = "操作员";
    /// <summary>
    /// 启动时的角色。
    ///
    /// <para><b>必须是操作员</b>：以前默认是工程师，等于开机就有全部权限 ——
    /// 现场反馈"这个权限没有设计好"，最主要的就是这一条。
    /// 想改配方/参数，开机后自己去切角色、验口令。</para>
    /// </summary>
    private string _currentUser = Core.Services.Roles.Operator;

    private string _lastActionText = "尚未执行模拟动作";
    private string _outcomeText = "尚未执行检测";

    private DispatcherTimer? _autoResetTimer;
    private IPoseSource? _poseSource;
    private bool _isProcessRunning;
    private string _currentActionName = "—";
    private string _currentActionHint = string.Empty;

    // ---- 页面导航 ----
    private int _selectedPageIndex;

    // ---- 历史查询页 ----
    private DateTime? _queryFrom = DateTime.Today.AddDays(-7);
    private DateTime? _queryTo = DateTime.Today;
    private string _queryBatch = string.Empty;
    private string _queryModel = string.Empty;

    // ---- 判定状态 / NG 保持图（借鉴基恩士 operator 层）----
    private string _verdictStateKey = "pending";

    // ---- 动作教示 / 自动识别（用户的现场用法：不画框，直接教 OK/NG）----
    private readonly List<ActionSample> _actionSamples = new();
    private bool _isRecognizing;
    private bool? _lastRecognizedOk;
    private string _recognizedHint = "对着标准动作点「记 OK」，对着违规动作点「记 NG」，之后点「开始自动识别」";
    // 画面上的 ROI 框默认"显示"。
    // 这里踩过一个坑：这个字段以前是 false 且全工程没有任何地方把它置 true，
    // 结果就是"能框、框也存进配方了，但画面上永远看不见"——现场只能理解为"画不出框"。
    private bool _showRoiBoxes = true;
    private string _validationDetail = string.Empty;

    // ---- 底图：相机实时画面 / 导入的现场照片 ----
    // 导入现场照片后，如果不冻结相机渲染，下一帧就会把照片顶掉，
    // 表现成"导进去了、一眨眼又没了"。所以相机帧到达时先看这个开关。
    private bool _useStaticSceneImage;

    // ---- ROI 鼠标编辑状态 ----
    private bool _isRoiEditMode;

    public MainViewModel(
        IAlgorithmRegistry algorithms,
        IRecipeRepository recipes,
        IInspectionHistoryStore history,
        IPlcClient plc,
        IAlarmDevice alarm,
        ILogger logger,
        EventAggregator events,
        AppSettingsProvider settings)
    {
        _algorithms = algorithms;
        _recipes = recipes;
        _history = history;
        _plc = plc;
        _alarm = alarm;
        _log = logger;
        _events = events;
        _settings = settings;

        Sop = new SopProcess();
        Sop.Alarm += OnSopAlarm;
        Sop.StateChanged += (_, _) => RaiseMonitorProps();

        _session = new MonitoringSession(Sop);

        ParameterFields = new ObservableCollection<ParameterFieldViewModel>();
        RecipeList = new ObservableCollection<Recipe>();
        Cameras = new ObservableCollection<CameraInfo>(settings.Current.Cameras);
        AvailableAlgorithms = new ObservableCollection<IInspectionAlgorithm>(algorithms.All);
        HistoryRecords = new ObservableCollection<InspectionRecord>();
        RoiOverlays = new ObservableCollection<RoiOverlayViewModel>();
        ProcessViolations = new ObservableCollection<ProcessViolation>();
        ProcessTargetStates = new ObservableCollection<ProcessTargetStateViewModel>();
        QueryResults = new ObservableCollection<InspectionRecord>();
        EvidenceImages = new ObservableCollection<EvidenceItem>();

        // 顶栏显示用的运行批次号 / 操作员 / 产品型号 —— 默认值可在界面修改并写回当前批次
        _productModel = settings.Current.LastProductModel ?? "";

        // ---------------- 命令 ----------------
        ConnectCameraCommand = new AsyncRelayCommand(ConnectCameraAsync, () => !IsBusy);
        DisconnectCameraCommand = new AsyncRelayCommand(DisconnectCameraAsync, () => _camera?.IsConnected == true);
        InspectCommand = new AsyncRelayCommand(InspectOnceAsync,
            () => _camera?.IsConnected == true && ActiveRecipe is not null && !IsBusy);
        // 权限：配方类操作按角色放开（操作员只能作业，不能改配置）
        SaveRecipeCommand = new AsyncRelayCommand(SaveRecipeAsync, () => ActiveRecipe is not null && CanTuneRecipe);
        NewRecipeCommand = new RelayCommand(NewRecipe, () => !IsBusy && CanEditRecipe);
        ResetSopCommand = new RelayCommand(ResetProcess);
        ForceAdvanceCommand = new RelayCommand(ForceAdvance, () => Sop.IsLocked && CanForceAdvance);
        ConnectPlcCommand = new AsyncRelayCommand(ConnectPlcAsync, () => _plc.State != PlcConnectionState.Connected);
        ExportHistoryCommand = new AsyncRelayCommand(ExportHistoryAsync);
        OpenBoardCommand = new RelayCommand(OpenBoard);
        SilenceBuzzerCommand = new RelayCommand(SilenceBuzzer, () => _alarm.IsBuzzerOn);
        ToggleRoiEditCommand = new RelayCommand(ToggleRoiEdit, () => CanTuneRecipe);
        ClearRoisCommand = new RelayCommand(ClearRois, () => IsRoiEditMode && RoiOverlays.Count > 0);

        // ---- 测试逻辑：模拟操作员动作（仅模拟相机可用）----
        SimulateCorrectStepCommand = new AsyncRelayCommand(
            () => SimulateAsync(SimulatedAction.CorrectStep), CanSimulate);
        SimulateSkipStepCommand = new AsyncRelayCommand(
            () => SimulateAsync(SimulatedAction.SkipStep), CanSimulate);
        SimulateNoActionCommand = new AsyncRelayCommand(
            () => SimulateAsync(SimulatedAction.NoAction), CanSimulate);
        SimulateRepeatCommand = new AsyncRelayCommand(
            () => SimulateAsync(SimulatedAction.RepeatTrigger), CanSimulate);

        // ---- 过程层（手部动作合规）----
        ToggleProcessMonitorCommand = new RelayCommand(ToggleProcessMonitor, () => _processConfig is not null);
        ClearProcessViolationsCommand = new RelayCommand(ClearProcessViolations);
        ExportProcessRuleCommand = new RelayCommand(ExportProcessRule, () => _processConfig is not null);

        // ---- 其它页面 ----
        QueryHistoryCommand = new AsyncRelayCommand(QueryHistoryAsync);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ReloadSettingsCommand = new RelayCommand(ReloadSettings);
        SaveProcessRuleCommand = new RelayCommand(SaveProcessRule, () => _processConfig is not null);
        DeleteRecipeCommand = new AsyncRelayCommand(DeleteRecipeAsync, () => ActiveRecipe is not null && CanEditRecipe);
        GoRoiEditCommand = new RelayCommand(GoRoiEdit);

        // 相机看门狗：5 秒一次检查连接状态，掉线自动重连。
        // 工控现场最常见的故障就是网线松动 / 相机意外重启 ——
        // 靠人发现往往要等下一件产品检测失败，自动重连能省下这段停机时间。
        _cameraWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _cameraWatchdog.Tick += OnCameraWatchdogTick;

        // ---- 动作教示 / 自动识别（不画框，直接教 OK / NG）----
        CaptureOkSampleCommand = new RelayCommand(() => CaptureSample(isOk: true));
        CaptureNgSampleCommand = new RelayCommand(() => CaptureSample(isOk: false));
        ClearSamplesCommand = new RelayCommand(ClearActionSamples);
        ToggleRecognizeCommand = new RelayCommand(ToggleRecognize);
        RunBatchValidationCommand = new RelayCommand(RunBatchValidation);
        SnapshotCommand = new RelayCommand(TakeSnapshot);

        // 导航兜底通路：RadioButton 除了 IsChecked 双向绑定，再挂一条命令。
        // 万一某个环境下 IsChecked 的写回不生效，点一下也能切页。
        SwitchPageCommand = new RelayCommand<string>(SwitchPage);

        // 一键演示：自动建图 → 模拟抓两个东西 → 跑判定 → 反例（漏抓）
        RunDemoCommand = new RelayCommand(() => _ = RunAutoDemoAsync(), () => !IsDemoRunning);

        // 建图与步骤：导入现场照片当底图 / 把框同步成判定区域 / 点步骤小框去调参数
        ImportSceneImageCommand = new RelayCommand(ImportSceneImage);
        SyncRoisToTargetsCommand = new RelayCommand(SyncRoisToTargets, () => ActiveRecipe is not null);
        SelectStepCommand = new RelayCommand<SopStep>(SelectStepForEdit);

        // 画面上的框一键显隐 / 从现场照片切回相机画面
        ToggleShowRoiBoxesCommand = new RelayCommand(ToggleShowRoiBoxes);
        UseCameraPreviewCommand = new RelayCommand(UseCameraPreview);

        // 画面缩放 / 拖动（滚轮放大缩小、左键按住拖画面）
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ResetViewportCommand = new RelayCommand(ResetViewport, () => !IsViewDefault);

        // 区域学习：按框教 OK / NG（基恩士抓取设定那套用法）
        CaptureRoiOkCommand = new RelayCommand(() => CaptureRoiSample(isOk: true), () => CanUseRoiLearning);
        CaptureRoiNgCommand = new RelayCommand(() => CaptureRoiSample(isOk: false), () => CanUseRoiLearning);
        CaptureRoiClassCommand = new RelayCommand(CaptureRoiClass, () => CanUseRoiLearning);
        ClearRoiTargetSamplesCommand = new RelayCommand(ClearRoiTargetSamples, () => CanUseRoiLearning);
        ToggleRoiLearnCommand = new RelayCommand(ToggleRoiLearn,
            () => IsRoiLearning || (ActiveRecipe is not null && RoiTargets.Count > 0));
        SelectRoiTargetCommand = new RelayCommand<RoiTargetOption>(SelectRoiTarget);
        SimulateObjectPresentCommand = new RelayCommand(() => SimulateRoiObject(present: true),
            () => IsMockSceneAvailable && CanUseRoiLearning);
        SimulateObjectGoneCommand = new RelayCommand(() => SimulateRoiObject(present: false),
            () => IsMockSceneAvailable && CanUseRoiLearning);

        // AI 自主学习：边生产边学（有把握才收 / 人工纠错立即生效 / 数量封顶可回滚）
        ToggleSelfLearningCommand = new RelayCommand(ToggleSelfLearning);
        UndoLastAutoSampleCommand = new RelayCommand(UndoLastAutoSample, () => HasAutoSamples);
        ClearAutoSamplesCommand = new RelayCommand(ClearAutoSamples, () => HasAutoSamples);
        FreezeAutoSamplesCommand = new RelayCommand(FreezeAutoSamples, () => HasAutoSamples);
        MarkRoiCorrectOkCommand = new RelayCommand(() => CaptureRoiSample(isOk: true, source: SampleSource.Corrected),
            () => CanUseRoiLearning);
        MarkRoiCorrectNgCommand = new RelayCommand(() => CaptureRoiSample(isOk: false, source: SampleSource.Corrected),
            () => CanUseRoiLearning);
        OpenLearningFolderCommand = new RelayCommand(OpenLearningFolder);

        // 动作计时：回看与导出
        QueryTimingCommand = new AsyncRelayCommand(QueryTimingAsync);
        ExportTimingCsvCommand = new RelayCommand(ExportTimingCsv);

        // 录像当相机（没有现场硬件也能把整套流程跑一遍）
        ImportVideoCommand = new AsyncRelayCommand(ImportVideoAsync, () => !IsBusy);
        ToggleVideoPlayCommand = new RelayCommand(ToggleVideoPlay, () => _videoCamera is not null);
        RestartVideoCommand = new RelayCommand(RestartVideo, () => _videoCamera is not null);
        StopVideoCommand = new RelayCommand(StopVideoPlayback, () => _videoCamera is not null);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        // 多工位联网 + MES 对接
        OpenWallboardCommand = new RelayCommand(OpenWallboard);
        CopyWallboardUrlCommand = new RelayCommand(CopyWallboardUrl);
        TestMesConnectionCommand = new AsyncRelayCommand(TestMesAsync);
        FlushMesQueueCommand = new AsyncRelayCommand(FlushMesQueueAsync);

        // 模板池与一键下发
        UploadTemplateCommand = new AsyncRelayCommand(UploadTemplateAsync);
        PublishTemplateCommand = new AsyncRelayCommand(PublishTemplateAsync,
            () => _reportClient is not null && !string.IsNullOrWhiteSpace(_settings.Current.Station?.HubUrl));
        RefreshDeployStatusCommand = new AsyncRelayCommand(RefreshDeployStatusAsync);

        // 关键点叠加显示（当前来自模拟手轨迹，接入手部模型后即为真实关键点）
        TogglePosePointsCommand = new RelayCommand(TogglePosePoints);
        InitPosePoints();

        // 计时显示要"活着"：200ms 刷一次本步用时
        StartTimingTimer();

        _log.Info("主界面 ViewModel 初始化完成");
    }

    // ==================================================================
    // 属性
    // ==================================================================
    public SopProcess Sop { get; }

    public ObservableCollection<ParameterFieldViewModel> ParameterFields { get; }

    public ObservableCollection<Recipe> RecipeList { get; }

    public ObservableCollection<CameraInfo> Cameras { get; }

    public ObservableCollection<IInspectionAlgorithm> AvailableAlgorithms { get; }

    /// <summary>当前操作员。顶部"用户"位会显示。</summary>
    public string CurrentOperator
    {
        get => _currentOperator;
        set
        {
            if (!SetProperty(ref _currentOperator, value)) return;

            // 顶栏改的操作员要写进配置，否则检测记录里记的还是旧名字
            _settings.Current.OperatorName = value;
            _settings.Save();
        }
    }

    /// <summary>
    /// 当前角色（界面下拉框绑的就是它）。
    ///
    /// <para><b>注意它现在不是"直接赋值"</b>：赋值会走
    /// <c>RequestRoleChange</c> —— 降级直接放行，升级必须先过口令，
    /// 口令不对时下拉框会自动弹回原值。真正的校验与生效在
    /// <c>MainViewModel.Security.cs</c>。</para>
    /// </summary>
    public string CurrentUser
    {
        get => _currentUser;
        set => RequestRoleChange(value);
    }

    /// <summary>可选角色三档。</summary>
    public IReadOnlyList<string> UserRoles { get; } = new[] { "工程师", "技术员", "操作员" };

    public bool IsEngineer => string.Equals(_currentUser, "工程师", StringComparison.Ordinal);
    public bool IsTechnician => string.Equals(_currentUser, "技术员", StringComparison.Ordinal);
    public bool IsOperator => string.Equals(_currentUser, "操作员", StringComparison.Ordinal);

    /// <summary>能否新建 / 删除配方（只有工程师）。</summary>
    public bool CanEditRecipe => IsEngineer;

    /// <summary>能否调参数、标定 ROI（工程师与技术员）。</summary>
    public bool CanTuneRecipe => !IsOperator;

    /// <summary>能否人工放行（工程师与技术员）。操作员只能按流程作业。</summary>
    public bool CanForceAdvance => !IsOperator;

    /// <summary>当前角色的权限说明，界面上直接给操作员看。</summary>
    public string RolePermissionText => IsOperator
        ? "仅作业：启动监测 / 触发检测 / 消音，不能改配方或放行"
        : IsTechnician
            ? "可调参数与标定 ROI、可人工放行；不能新建或删除配方"
            : "全部权限：配方增删改、标定、放行、系统设置";

    // ---- 三级界面可见性（对应方案十一：操作员 / 管理员 / 运维）----
    /// <summary>配方管理页：只有工程师（运维）能进。</summary>
    public bool CanSeeRecipePage => IsEngineer;

    /// <summary>历史查询页：工程师与技术员（管理员/质检）能进，操作员不能。</summary>
    public bool CanSeeHistoryPage => !IsOperator;

    /// <summary>系统设置页：只有工程师能进。</summary>
    public bool CanSeeSettingsPage => IsEngineer;

    private void RaiseRoleProps()
    {
        OnPropertyChanged(nameof(IsEngineer));
        OnPropertyChanged(nameof(IsTechnician));
        OnPropertyChanged(nameof(IsOperator));
        OnPropertyChanged(nameof(CanEditRecipe));
        OnPropertyChanged(nameof(CanTuneRecipe));
        OnPropertyChanged(nameof(CanForceAdvance));
        OnPropertyChanged(nameof(RolePermissionText));
        OnPropertyChanged(nameof(PermissionHintText));
        OnPropertyChanged(nameof(HasPermissionHint));
        OnPropertyChanged(nameof(CanSeeRecipePage));
        OnPropertyChanged(nameof(CanSeeHistoryPage));
        OnPropertyChanged(nameof(CanSeeSettingsPage));

        // 角色切小之后，当前停着的页面可能已经无权查看 —— 拉回首页，
        // 否则会停在"页签都没了但内容还在"的怪状态
        if ((_selectedPageIndex == 1 && !CanSeeRecipePage)
            || (_selectedPageIndex == 2 && !CanSeeHistoryPage)
            || (_selectedPageIndex == 3 && !CanSeeSettingsPage))
        {
            SelectedPageIndex = 0;
        }
    }

    /// <summary>底部历史表格数据源。</summary>
    public ObservableCollection<InspectionRecord> HistoryRecords { get; }

    /// <summary>左侧画面 ROI 叠加框集合（从 ActiveRecipe.Rois 换算而来）。</summary>
    public ObservableCollection<RoiOverlayViewModel> RoiOverlays { get; }

    /// <summary>编辑模式按钮文字。</summary>
    public string RoiEditButtonText => IsRoiEditMode ? "✅ 完成标定" : "🎯 标定 ROI";

    /// <summary>系统是否在运行（相机已连 + 已采集）。右下角运行灯据此显示。</summary>
    public bool SystemRunning => _camera?.IsConnected == true && _camera.IsGrabbing;

    /// <summary>当前工序大标题，如"第 3 步 / 共 6 步"。</summary>
    public string StepTitle => Sop.CurrentStep is not null
        ? $"第 {Sop.CurrentStep.Seq} 步 / 共 {Sop.TotalSteps} 步"
        : (Sop.IsCompleted ? "本件已完成" : "—");

    /// <summary>当前工序名称（大字显示）。</summary>
    public string StepName => Sop.CurrentStep?.Name ?? (Sop.IsCompleted ? "等待下一件" : "—");

    /// <summary>最近一次模拟动作的说明（测试用，界面上单独一块）。</summary>
    public string LastActionText
    {
        get => _lastActionText;
        private set => SetProperty(ref _lastActionText, value);
    }

    /// <summary>最近一次触发的流程结论。</summary>
    public string OutcomeText
    {
        get => _outcomeText;
        private set => SetProperty(ref _outcomeText, value);
    }

    /// <summary>模拟进度文案。</summary>
    public string SimulatorProgressText
    {
        get
        {
            if (_simulator is null || ActiveRecipe is null) return "模拟动作未启用";
            int total = ActiveRecipe.Rois.Count(r => r.Enabled);
            return $"模拟作业进度 {_simulator.CompletedCount}/{total}";
        }
    }

    /// <summary>是否可做模拟动作：相机得是"可编程场景相机"，且选了配方。</summary>
    public bool IsSimulationAvailable => _simulator is not null && ActiveRecipe is not null;

    /// <summary>检测过程数据（每行一条），界面上直接列出来。</summary>
    public ObservableCollection<string> MeasurementLines { get; } = new();

    // ==================================================================
    // 过程层看板（方案 v9：手部动作合规 + 实时拦截）
    // ==================================================================
    /// <summary>违规记录列表（规则码 + 时间 + 说明），界面右栏显示。</summary>
    public ObservableCollection<ProcessViolation> ProcessViolations { get; }

    /// <summary>各作业位的三态块（绿=已到位 / 橙=等待中 / 灰=未执行）。</summary>
    public ObservableCollection<ProcessTargetStateViewModel> ProcessTargetStates { get; }

    public bool IsProcessRunning
    {
        get => _isProcessRunning;
        private set
        {
            if (SetProperty(ref _isProcessRunning, value))
            {
                OnPropertyChanged(nameof(ProcessMonitorButtonText));
                (ToggleProcessMonitorCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 这个按钮跑的是<b>模拟手部轨迹</b>演示：一只能"标准作业"的假手会依次经过每个作业位，
    /// 所以一开就全部判 OK。它演示的是规则引擎（跳步/漏装能不能拦住），<b>不是</b>真实识别；
    /// 真实判定请看画面下方的「区域学习」。按钮文字必须写明这一点，
    /// 否则现场会以为"我明明教了 NG，软件却全判 OK"。
    /// </summary>
    public string ProcessMonitorButtonText => IsProcessRunning ? "⏹ 停止模拟" : "▶ 模拟手轨迹";

    /// <summary>当前动作名（教示时命名）。方案 v7 补丁 B：界面要显示"动作名 + 置信度"。</summary>
    public string CurrentActionName
    {
        get => _currentActionName;
        private set => SetProperty(ref _currentActionName, value);
    }

    /// <summary>当前动作完成度 0~1（MVP 用"停留 / K"近似）。</summary>
    public double ActionConfidence
    {
        get => _actionConfidence;
        private set
        {
            if (SetProperty(ref _actionConfidence, value))
                OnPropertyChanged(nameof(ActionConfidenceText));
        }
    }

    public string ActionConfidenceText =>
        _actionConfidence <= 0 ? "—" : _actionConfidence.ToString("P0");

    public string CurrentActionHint
    {
        get => _currentActionHint;
        private set => SetProperty(ref _currentActionHint, value);
    }

    public string ProcessProgressText => _processProgress <= 0 ? "—" : _processProgress.ToString("P0");

    /// <summary>规则摘要，界面上显示"这套规则是什么"。</summary>
    public string ProcessRuleSummary => _processConfig is null
        ? "未加载规则"
        : $"{_processConfig.Targets.Count} 个作业位 / {_processConfig.Steps.Count} 道工序 · " +
          $"顺序{(_processConfig.OrderEnforced ? "强制" : "自由")} · K={_processConfig.Kms:F0}ms · " +
          $"置信度阈值={_processConfig.MinConfidence:F2}";

    // ==================================================================
    // 动作教示 / 自动识别（现场用法：不画框，直接教 OK / NG）
    // ==================================================================
    /// <summary>已教示的 OK 样本数。</summary>
    public int OkSampleCount => _actionSamples.Count(s => s.IsOk);

    /// <summary>已教示的 NG 样本数。</summary>
    public int NgSampleCount => _actionSamples.Count(s => !s.IsOk);

    public bool IsRecognizing
    {
        get => _isRecognizing;
        private set
        {
            if (SetProperty(ref _isRecognizing, value))
                OnPropertyChanged(nameof(RecognitionButtonText));
        }
    }

    public string RecognitionButtonText => IsRecognizing ? "停止自动识别" : "开始自动识别";

    /// <summary>识别状态的键（ok / ng / active / pending），全界面统一上色。</summary>
    public string RecognizedStateKey { get; private set; } = "pending";

    /// <summary>最近 3 张 NG 保持图（基恩士式：最多留几张，够复盘就行）。</summary>
    public ObservableCollection<EvidenceItem> EvidenceImages { get; }

    public bool HasEvidenceImage => _selectedEvidenceImage is not null;

    // ==================================================================
    // 页面导航
    // ==================================================================
    /// <summary>当前页：0=首页 1=配方管理 2=历史查询 3=系统设置。</summary>
    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (SetProperty(ref _selectedPageIndex, value))
                OnPropertyChanged(nameof(PageTitle));
        }
    }

    public string PageTitle => _selectedPageIndex switch
    {
        0 => "首页 · 实时监测",
        1 => "配方管理",
        2 => "历史查询",
        3 => "系统设置",
        _ => string.Empty,
    };

    // ==================================================================
    // 历史查询页
    // ==================================================================
    public ObservableCollection<InspectionRecord> QueryResults { get; }

    // ==================================================================
    // 系统设置页
    // ==================================================================
    /// <summary>全局配置（相机 / PLC / 报警 / 存储）。改完点「保存设置」写回 appsettings.json。</summary>
    public AppSettings CurrentSettings => _settings.Current;

    /// <summary>存 NG 图 / 保留天数等存储项。</summary>
    public IReadOnlyList<string> VerdictFilterOptions { get; } =
        new[] { "全部", "仅 OK", "仅 NG" };

    public string CameraStatus => _camera?.IsConnected == true
        ? $"{_camera.Info.DisplayName} 已连接"
        : "相机未连接";

    public string PlcStatus => _plc.State switch
    {
        PlcConnectionState.Connected => $"PLC 已连接 ({_plc.Name})",
        PlcConnectionState.Connecting => "PLC 连接中…",
        PlcConnectionState.Faulted => "PLC 通信异常",
        _ => "PLC 未连接",
    };

    /// <summary>当前声光报警状态，界面上显示。</summary>
    public string AlarmStatusText => _alarm.CurrentSignal switch
    {
        AlarmSignal.Ok => "🟢 正常",
        AlarmSignal.Warning => "🟡 警示",
        AlarmSignal.Fault => "🔴 故障 · 停线",
        _ => "⚪ 未运行",
    };

    /// <summary>蜂鸣器是否在响。界面据此显示「消音」按钮是否可用。</summary>
    public bool IsBuzzerSounding => _alarm.IsBuzzerOn;

    /// <summary>报警状态的键，界面用它触发上色（Core 层不依赖 WPF，所以传字符串不传 Brush）。</summary>
    public string AlarmSignalKey => _alarm.CurrentSignal switch
    {
        AlarmSignal.Ok => "ok",
        AlarmSignal.Warning => "warning",
        AlarmSignal.Fault => "fault",
        _ => "none",
    };

    // ==================================================================
    // 命令
    // ==================================================================
    public ICommand ConnectCameraCommand { get; }
    public ICommand DisconnectCameraCommand { get; }
    public ICommand InspectCommand { get; }
    public ICommand SaveRecipeCommand { get; }
    public ICommand NewRecipeCommand { get; }
    public ICommand ResetSopCommand { get; }
    public ICommand ForceAdvanceCommand { get; }
    public ICommand ConnectPlcCommand { get; }
    public ICommand ExportHistoryCommand { get; }
    public ICommand OpenBoardCommand { get; }
    public ICommand SilenceBuzzerCommand { get; }
    public ICommand ToggleRoiEditCommand { get; }
    public ICommand ClearRoisCommand { get; }

    /// <summary>测试逻辑：模拟"规范完成当前工序"。</summary>
    public ICommand SimulateCorrectStepCommand { get; }

    /// <summary>测试逻辑：模拟"跳步"。</summary>
    public ICommand SimulateSkipStepCommand { get; }

    /// <summary>测试逻辑：模拟"漏装/未动作"。</summary>
    public ICommand SimulateNoActionCommand { get; }

    /// <summary>测试逻辑：画面不变，重复触发（验证幂等）。</summary>
    public ICommand SimulateRepeatCommand { get; }

    /// <summary>过程层：启动 / 停止手部动作合规监测。</summary>
    public ICommand ToggleProcessMonitorCommand { get; }

    /// <summary>过程层：清空违规列表。</summary>
    public ICommand ClearProcessViolationsCommand { get; }

    /// <summary>过程层：导出规则 YAML（给现场交接 / 手工微调用）。</summary>
    public ICommand ExportProcessRuleCommand { get; }

    /// <summary>配方管理页：删除当前配方（移入 _deleted，可找回）。</summary>
    public ICommand DeleteRecipeCommand { get; }

    /// <summary>配方管理页：跳到首页并进入 ROI 标定模式。</summary>
    public ICommand GoRoiEditCommand { get; }

    /// <summary>历史查询页：按条件查询。</summary>
    public ICommand QueryHistoryCommand { get; }

    /// <summary>系统设置页：保存 / 重新载入配置。</summary>
    public ICommand SaveSettingsCommand { get; }
    public ICommand ReloadSettingsCommand { get; }

    /// <summary>系统设置页：保存过程层规则（JSON + YAML）。</summary>
    public ICommand SaveProcessRuleCommand { get; }

    /// <summary>教示：把当前画面记为 OK 样本。</summary>
    public ICommand CaptureOkSampleCommand { get; }

    /// <summary>教示：把当前画面记为 NG 样本。</summary>
    public ICommand CaptureNgSampleCommand { get; }

    /// <summary>清空全部教示样本。</summary>
    public ICommand ClearSamplesCommand { get; }

    /// <summary>开始 / 停止自动识别。</summary>
    public ICommand ToggleRecognizeCommand { get; }

    /// <summary>批量仿真：用留一法把教示样本考一遍，出准确率/漏检/误检。</summary>
    public ICommand RunBatchValidationCommand { get; }

    /// <summary>画面抓拍：把当前画面存成一张图（现场取证用）。</summary>
    public ICommand SnapshotCommand { get; }

    /// <summary>顶栏导航：直接切到指定页（参数是页号字符串）。</summary>
    public ICommand SwitchPageCommand { get; }

    private void SwitchPage(string? page)
    {
        if (int.TryParse(page, out int index)) SelectedPageIndex = index;
    }

    /// <summary>
    /// 刷新监测看板相关的绑定属性。
    /// 步骤列表用 ItemsControl 直接绑 Sop.Steps（它们自己是 ObservableObject），
    /// 但"当前步骤"这块汇总信息是计算属性，得手动通知。
    /// </summary>
    private void RaiseMonitorProps()
    {
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepName));
        OnPropertyChanged(nameof(StepHintText));
        OnPropertyChanged(nameof(StepStateKey));
        OnPropertyChanged(nameof(CompletedPieces));
        OnPropertyChanged(nameof(YieldText));
        OnPropertyChanged(nameof(SimulatorProgressText));
        OnPropertyChanged(nameof(IsSimulationAvailable));

        (SimulateCorrectStepCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SimulateSkipStepCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SimulateNoActionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SimulateRepeatCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeleteRecipeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveProcessRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleRoiEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ==================================================================
    private void ForceAdvance()
    {
        // 生产环境这里应该弹窗要求输入工号或主管密码，让放行有责任人
        if (Sop.ForceAdvance("操作员手动确认"))
        {
            StatusMessage = $"⚠️ 已人工放行（累计 {Sop.ManualOverrideCount} 次）";
            _log.Warn($"人工放行第 {Sop.ManualOverrideCount} 次");
            _ = UpdateAlarmFromSopAsync();
        }
    }

    // ==================================================================
    /// <summary>
    /// 重置流程状态。
    ///
    /// <b>必须同时清掉算法插件内部的记忆，这是容易漏的一步。</b>
    /// 像 SOP 序列检测这类插件是跨帧有状态的 —— 只复位界面上的步骤列表，
    /// 插件内部还记着上一件的进度，下一件一开工就会凭空报跳步，
    /// 而且这种 bug 极难复现（要"连续做两件"才触发）。
    /// </summary>
    private void ResetProcess()
    {
        Sop.Reset();
        _session.ResetForNewPiece();

        // 模拟器也要跟着回到"新一件"的起点，否则画面里还留着上一件的料
        if (_simulator is not null && ActiveRecipe is not null)
            _simulator.Reset(ActiveRecipe);
        LastActionText = "已复位，等待第一道工序";
        OnPropertyChanged(nameof(SimulatorProgressText));

        // 过程层的规则引擎同样要复位（跨件有状态，忘了清就会凭空报跳步）
        _processEngine?.Reset();
        ResetProcessTargetStates();
        ClearRoiStates();

        // 区域学习跨件也有记忆（每个框最近判成什么），不清就会"上一件是 OK，
        // 这一件一开局就少推一步"。同时把框的颜色收回未判定。
        _roiLastStates.Clear();
        _handTracker.ResetAll();      // 手部轨迹也是"这一件"的，换件要重新开始
        HandPathText = "手的动作轨迹：（还没有动作）";
        HandActionText = "手柄动作判定：—";
        RoiLearnStateKey = "pending";
        RoiLearnResultText = "—";
        RoiLearnConfidence = 0;

        // 新的一件：换件号，本步计时从零开始（上一件的时间记录已经在库里了）
        StartNewPieceTiming();

        VerdictStateKey = "pending";
        SelectedViolation = null;
        ProcessProgress = 0;
        ActionConfidence = 0;
        CurrentActionName = _processConfig?.Steps.FirstOrDefault()?.Name ?? "—";
        RaiseProcessProps();

        RaiseMonitorProps();

        int cleared = 0;
        foreach (var algo in _algorithms.All)
        {
            if (algo is SopSequenceAlgorithm seq)
            {
                seq.ResetAll();
                cleared++;
            }
        }

        Log(cleared > 0
            ? $"流程已重置（含 {cleared} 个有状态算法插件）"
            : "流程已重置");

        _ = UpdateAlarmFromSopAsync();
    }

    // ==================================================================
    private SopBoardWindow? _boardWindow;

    /// <summary>打开车间大屏看板。只开一个实例，重复点就激活已有窗口。</summary>
    private void OpenBoard()
    {
        if (_boardWindow is { IsLoaded: true })
        {
            _boardWindow.Activate();
            return;
        }

        _boardWindow = new SopBoardWindow { DataContext = this };
        _boardWindow.Closed += (_, _) => _boardWindow = null;
        _boardWindow.Show();

        _log.Info("已打开 SOP 作业看板");
        StatusMessage = "大屏看板已打开（ESC 退出全屏）";
    }

    // ==================================================================
    private void Log(string message)
    {
        _log.Info(message);
        StatusMessage = message;
    }

    private void RefreshCommands()
    {
        (ConnectCameraCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisconnectCameraCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (InspectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SaveRecipeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (NewRecipeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ForceAdvanceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ConnectPlcCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SimulateCorrectStepCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SimulateSkipStepCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SimulateNoActionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SimulateRepeatCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SyncRoisToTargetsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleRoiEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _autoResetTimer?.Stop();
        _autoResetTimer = null;

        // 计时刷新定时器同样要停：窗口都关了还在跳没有意义
        _timingTimer?.Stop();
        _timingTimer = null;

        // 蜂鸣器自动消音定时器也必须停：它每一跳都会去写报警设备，
        // 窗关了还继续跑，就会摸到已经释放的设备（长时间运行的隐患）
        _buzzerTimer?.Stop();
        _buzzerTimer = null;

        // 相机看门狗同理：停机后不该再尝试重连
        _cameraWatchdog?.Stop();
        _cameraWatchdog = null;

        // 自学的样本是"攒够 5 秒才写盘"的（见 SaveRoiSamplesThrottled）：
        // 关软件前必须补写一次，否则刚刚学到的最后几条会随进程一起消失，
        // 而现场看到的现象是"明明学了，重开就少了几条"。
        FlushRoiSamples();

        // 手部关键点：定时器停掉、ONNX 会话释放（原生资源，不释放会留到进程退出）
        StopHandPose();
        _handEstimator?.Dispose();
        _handEstimator = null;

        // 解绑状态机事件，避免停机过程中回调进来碰已释放的界面状态
        Sop.Alarm -= OnSopAlarm;

        if (_poseSource is not null)
        {
            _poseSource.PoseArrived -= OnPoseArrived;
            _poseSource.Finished -= OnPoseSourceFinished;
            _poseSource.Dispose();
            _poseSource = null;
        }

        // 注意：这里不用 _camera?.FrameArrived -= ... 写法 ——
        // "null 条件复合赋值" 是 C# 14 才有的语法，本项目按 net8.0（C# 12）编译，
        // 换台机器用旧 SDK 打开会直接编译不过。显式判空最保险。
        if (_camera is not null)
            _camera.FrameArrived -= OnFrameArrived;

        // 录像源要单独关（它是 MediaPlayer，关的时候必须回到创建它的 UI 线程）
        ClearVideoSource();

        // 联网相关：停掉看板服务、上报定时器、MES 补发定时器
        StopNetwork();

        _camera?.Dispose();
        _plc.Dispose();
    }
}

/// <summary>检测完成事件（走事件聚合器，供历史页面等订阅）。</summary>
public sealed record InspectionCompletedEvent(InspectionResult Result);
