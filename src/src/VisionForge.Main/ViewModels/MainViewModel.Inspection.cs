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

public sealed partial class MainViewModel
{
    private readonly IPlcClient _plc;
    private readonly EventAggregator _events;

    private ICamera? _camera;
    private string _statusMessage = "就绪";
    private string _verdictText = "—";
    private string _judgementReason = "尚未执行检测";
    private string _productModel = "";
    private bool _isBusy;
    private int _okCount;
    private int _ngCount;

    /// <summary>监测会话：一次触发 → 判定 → 流程流转 → 计数，只在这一处实现。</summary>
    private readonly MonitoringSession _session;

    /// <summary>可编程场景相机（模拟相机才实现它）。测试逻辑靠它构造画面。</summary>
    private ISceneCamera? _sceneCamera;

    /// <summary>操作员动作模拟器：把"做了哪个动作"翻译成"画面里哪个工位有料"。</summary>
    private OperatorActionSimulator? _simulator;
    private CameraFrame? _lastFrame;

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string VerdictText
    {
        get => _verdictText;
        private set => SetProperty(ref _verdictText, value);
    }

    public string JudgementReason
    {
        get => _judgementReason;
        private set => SetProperty(ref _judgementReason, value);
    }

    public string BatchNo
    {
        get => _batchNo;
        set => SetProperty(ref _batchNo, value);
    }

    /// <summary>当前产品型号（顶栏批次旁显示）。</summary>
    public string ProductModel
    {
        get => _productModel;
        set
        {
            if (SetProperty(ref _productModel, value))
                _settings.Current.LastProductModel = value;
        }
    }

    /// <summary>系统运行文字（"系统运行中" / "系统未启动"）。</summary>
    public string SystemStatusText => SystemRunning ? "系统运行中" : "系统未启动";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RefreshCommands();
        }
    }

    public int OkCount
    {
        get => _okCount;
        private set => SetProperty(ref _okCount, value);
    }

    public int NgCount
    {
        get => _ngCount;
        private set => SetProperty(ref _ngCount, value);
    }

    // ==================================================================
    // 监测看板属性（操作员首页用）
    // ==================================================================
    /// <summary>累计完成的整件数。</summary>
    public int CompletedPieces => _session.CompletedPieces;

    /// <summary>合格率（按工序计）。没做过检测时显示"—"。</summary>
    public string YieldText =>
        (OkCount + NgCount) == 0 ? "—" : $"{_session.PassRate:F1}%";

    /// <summary>当前工序状态键（ok / ng / active / pending），界面据此上色。</summary>
    public string StepStateKey =>
        Sop.IsLocked ? "ng" : (Sop.CurrentStep?.StateKey ?? (Sop.IsCompleted ? "ok" : "pending"));

    private bool CanSimulate() => IsSimulationAvailable && !IsBusy;

    // ==================================================================
    // 初始化
    // ==================================================================
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var list = await _recipes.LoadAllAsync();
            RecipeList.Clear();
            foreach (var r in list) RecipeList.Add(r);

            // 恢复上次用的配方
            if (!string.IsNullOrEmpty(_settings.Current.LastRecipeId))
                ActiveRecipe = RecipeList.FirstOrDefault(r => r.Id == _settings.Current.LastRecipeId);

            ActiveRecipe ??= RecipeList.FirstOrDefault();

            await LoadSopAsync();

            // 过程层规则（手部动作合规）：没有就生成一份示例，开箱可跑
            LoadProcessConfig();

            // 教示样本（现场"记 OK / 记 NG"采出来的资产）
            LoadActionSamples();

            // 区域学习样本（按框教的，现场最常用的那条路）
            LoadRoiSamples();
            SyncSopStepsWithRois();       // 启动就按实际框数对齐工序（多了删、少了补）
            RefreshRoiTargetOptions();

            // AI 自主学习：读回上一次的自学账本（现场要能回看"它什么时候学了什么"）
            LoadSelfLearning();

            // 加载最近的历史记录到表格
            try
            {
                var recent = await _history.QueryAsync(new HistoryQuery { Limit = HistoryDisplayLimit });
                HistoryRecords.Clear();
                foreach (var r in recent.OrderByDescending(r => r.Timestamp))
                    HistoryRecords.Add(r);
            }
            catch (Exception ex)
            {
                _log.Warn("加载历史失败：" + ex.Message);
            }

            StatusMessage = RecipeList.Count == 0
                ? "还没有配方，点「新建配方」开始"
                : $"已加载 {RecipeList.Count} 个配方";

            // 网络相关（汇总终端 / 工位上报 / MES）在数据加载完之后启动
            StartNetwork();
        }
        catch (Exception ex)
        {
            _log.Error("初始化失败", ex);
            StatusMessage = "初始化失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSopAsync()
    {
        var sopPath = Path.Combine(_settings.Current.DataRoot, "sop.json");
        var sop = SopDefinitionStore.Load(sopPath);
        Sop.Load(sop);
        _log.Info($"已加载 SOP：{sop.Name}，共 {sop.Steps.Count} 步");
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    private async Task ConnectCameraAsync()
    {
        var info = Cameras.FirstOrDefault();
        if (info is null)
        {
            StatusMessage = "没有配置任何相机";
            return;
        }

        IsBusy = true;
        try
        {
            // 切回真相机：先把"录像当相机"那一路关掉，否则两路都会往界面上推帧
            ClearVideoSource();

            _camera?.Dispose();
            _camera = CameraFactory.Create(info);

            StatusMessage = $"正在连接 {info.DisplayName} …";
            if (!await _camera.ConnectAsync())
            {
                StatusMessage = "相机连接失败（无硬件时请确认相机配置里的 Vendor 为 Mock）";
                // 已导入现场照片时保留照片：连不上相机照样能标位置、能判定
                if (!_useStaticSceneImage)
                    PreviewImage = FrameConverter.CreatePlaceholder(text: "相机连接失败");
                return;
            }

            // 把配方里的相机参数下发给相机
            if (ActiveRecipe is not null && ActiveRecipe.CameraParameters.Count > 0)
                await _camera.ApplyParametersAsync(ActiveRecipe.CameraParameters);

            // 模拟相机实现了 ISceneCamera —— 接上动作模拟器，
            // 于是没有产线也能把"人做对 / 做错"这类场景一条条跑出来
            _sceneCamera = _camera as ISceneCamera;
            _simulator = _sceneCamera is null ? null : new OperatorActionSimulator(_sceneCamera);
            RaiseMockSceneAvailability();       // 模拟相机连上后，"模拟放料/拿走"按钮才显示
            if (_simulator is not null && ActiveRecipe is not null)
            {
                _simulator.Reset(ActiveRecipe);
                LastActionText = "模拟器就绪：点「模拟：规范动作」开始演示";
            }

            _camera.FrameArrived += OnFrameArrived;
            await _camera.StartGrabbingAsync();

            // 连接成功：复位重连计数并启动看门狗
            _cameraReconnectAttempts = 0;
            _cameraWatchdog?.Start();

            StatusMessage = CameraStatus;
            OnPropertyChanged(nameof(CameraStatus));
            OnPropertyChanged(nameof(SystemRunning));
            OnPropertyChanged(nameof(SystemStatusText));
            RaiseMonitorProps();
            _log.Info($"相机已连接：{info.DisplayName}");
        }
        catch (Exception ex)
        {
            _log.Error("连接相机失败", ex);
            StatusMessage = "连接相机失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectCameraAsync()
    {
        if (_camera is null) return;

        _camera.FrameArrived -= OnFrameArrived;
        await _camera.DisconnectAsync();
        _camera.Dispose();
        _camera = null;

        _simulator = null;
        _sceneCamera = null;

        // 录像源也要一起关掉（它同样是"相机"）
        ClearVideoSource();

        RaiseMockSceneAvailability();

        _cameraWatchdog?.Stop();
        _cameraReconnectAttempts = 0;

        // 同上：断开相机不该把已经导入的现场照片抹掉
        if (!_useStaticSceneImage)
            PreviewImage = FrameConverter.CreatePlaceholder();
        StatusMessage = "相机已断开";
        OnPropertyChanged(nameof(CameraStatus));
        OnPropertyChanged(nameof(SystemRunning));
        OnPropertyChanged(nameof(SystemStatusText));
        RaiseMonitorProps();
        RefreshCommands();
    }

    private void OnFrameArrived(object? sender, CameraFrame frame)
    {
        // 原始帧留一份：提特征做动作识别要用像素，不是 BitmapSource
        _lastFrame = frame;

        // 相机回调在 SDK 线程上，必须切回 UI 线程再碰控件
        var app = Application.Current;
        if (app is null) return;

        // 与姿态帧同理：退出瞬间的 BeginInvoke 可能抛异常，
        // 而这里是相机采集线程，异常逃出去会污染取流任务
        try
        {
            app.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    // 导入过现场照片时保持照片不动（标定要一直看着同一张底图）；
                    // 判定与识别照常跑在实时帧上，两者互不影响。
                    if (!_useStaticSceneImage)
                        PreviewImage = FrameConverter.ToBitmapSource(frame);

                    // 自动识别开着就逐帧判定（结论只在变化时驱动流程）
                    if (IsRecognizing) RecognizeFrame(frame);

                    // 区域学习开着就逐框判定（每个框单独比它自己的样本）
                    if (IsRoiLearning) RecognizeRoiFrame(frame);
                }
                catch (Exception ex) { _log.Debug("渲染帧失败：" + ex.Message); }
            });
        }
        catch (Exception ex)
        {
            _log.Debug("画面帧投递失败（通常是正在退出）：" + ex.Message);
        }
    }

    private async Task ConnectPlcAsync()
    {
        IsBusy = true;
        try
        {
            await _plc.ConnectAsync();
            StatusMessage = PlcStatus;
            OnPropertyChanged(nameof(PlcStatus));
        }
        finally { IsBusy = false; }
    }

    // ==================================================================
    // 核心：执行一次检测
    // ==================================================================
    private async Task InspectOnceAsync()
    {
        var recipe = ActiveRecipe;
        var camera = _camera;

        if (recipe is null || camera is null) return;

        IsBusy = true;
        var sw = Stopwatch.StartNew();
        try
        {
            // ---- 1. 取图 ----
            // 用软触发单帧，而不是拿连续采集的最后一帧 ——
            // 连续采集的帧可能包含手部遮挡，触发的帧才是"作业刚完成"的瞬间
            var frame = await camera.GrabOnceAsync(timeoutMs: 3000);
            if (frame is null)
            {
                StatusMessage = "取图超时，检查相机触发模式与光电信号";
                return;
            }

            // ---- 2. 找算法插件 ----
            var algo = _algorithms.Find(recipe.AlgorithmKey);
            if (algo is null)
            {
                VerdictText = "ERROR";
                JudgementReason = $"找不到算法插件「{recipe.AlgorithmKey}」，可用：" +
                                  string.Join(", ", _algorithms.All.Select(a => a.Key));
                StatusMessage = "算法未注册";
                return;
            }

            // ---- 3. 把界面上的参数写回配方，再执行 ----
            SyncParametersToRecipe(recipe);

            // 序列类算法是"跨帧有状态"的：单帧往往只能判出"进行中"。
            // 真实现场就是光电触发后连拍几帧取稳定判定，这里按配方里的去抖帧数照做，
            // 于是模拟环境和现场走的是同一条路径。
            int settleFrames = Math.Clamp(
                new ParameterReader(recipe, algo.Parameters).GetInt("DebounceFrames", 1), 1, 8);

            // 算法是 CPU 密集的，扔到线程池避免卡界面
            var result = await Task.Run(() =>
            {
                InspectionResult? last = null;
                for (int i = 0; i < settleFrames; i++) last = algo.Run(frame, recipe);
                return last!;
            });

            sw.Stop();
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            result.BatchNo = BatchNo;
            result.Operator = _settings.Current.OperatorName;

            // ---- 4. 并入监测会话：判定 / 流程流转 / 计数只在这一处发生 ----
            // 以前是"这里判一次、那里推进一次"，界面逻辑和自检逻辑各写一份，
            // 结果就是"测过的"和"现场跑的"不是同一段代码。现在统一走 MonitoringSession。
            var outcome = _session.Process(result);

            VerdictText = result.Verdict switch
            {
                InspectionVerdict.Pass => "OK",
                InspectionVerdict.Fail => "NG",
                InspectionVerdict.Error => "ERROR",
                _ => "进行中",
            };
            JudgementReason = result.JudgementReason;
            OutcomeText = DescribeOutcome(outcome);
            OkCount = _session.OkCount;
            NgCount = _session.NgCount;
            RefreshMeasurements(result);
            RaiseMonitorProps();

            // ---- 5. 存 NG 图 ----
            if (result.Verdict != InspectionVerdict.Pass && _settings.Current.SaveNgImage)
            {
                try
                {
                    var path = BmpEncoder.BuildNgImagePath(
                        _history.NgImageDirectory, result.Timestamp, recipe.Name, result.VerdictText);
                    BmpEncoder.Save(frame, path);
                    result.EvidenceImagePath = path;
                }
                catch (Exception ex)
                {
                    _log.Warn("NG 图保存失败：" + ex.Message);
                }
            }

            // ---- 6. 写历史：只记"有结论"的触发 ----
            // "进行中"（人手还没到位就触发了）不是判定结论，记进去只会把历史表刷成噪音。
            // 要追溯的是 OK / NG 这些真结论。
            if (result.Verdict == InspectionVerdict.Pass || result.Verdict == InspectionVerdict.Fail)
            {
                var record = InspectionRecord.FromResult(result);
                record.NgImagePath = result.EvidenceImagePath;
                await _history.SaveAsync(record);
                AppendHistoryRecord(record);
            }

            // ---- 7. 流程走向已经在监测会话里算完了（Sop.ApplyResult 由它调用）----
            Log($"检测结果 {VerdictText}：{result.JudgementReason} → 流程 {outcome.Advance}");

            // 整件完成 → 稍后自动开始下一件（现场是连件生产，不该等人去点复位）
            if (outcome.Advance == AdvanceOutcome.ProcessCompleted)
            {
                _log.Info($"整件完成（累计 {_session.CompletedPieces} 件），2.5 秒后自动开始下一件");
                ScheduleAutoReset();
            }

            // ---- 8. 写回 PLC ----
            if (_plc.State == PlcConnectionState.Connected)
            {
                var addr = _settings.Current.PlcAddresses.FirstOrDefault(a => a.Name == "InspectResult");
                if (addr is not null)
                    await _plc.WriteInspectionVerdictAsync(addr, result.Verdict);

                var passAddr = _settings.Current.PlcAddresses.FirstOrDefault(a => a.Name == "AllowPass");
                if (passAddr is not null)
                    await _plc.WriteAsync(passAddr, result.Verdict == InspectionVerdict.Pass);
            }

            // ---- 9. 声光报警 ----
            // 放在最后统一驱动，而不是在几个分支里各写一遍 ——
            // 状态机已经算出了"现在是什么局面"，照它映射到灯就行，逻辑只有一处
            await UpdateAlarmFromSopAsync();

            StatusMessage = $"检测完成（{result.ElapsedMs:F0} ms）";
            _events.Publish(new InspectionCompletedEvent(result));
        }
        catch (Exception ex)
        {
            _log.Error("执行检测失败", ex);
            VerdictText = "ERROR";
            JudgementReason = "检测异常：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    // ==================================================================
    // 测试逻辑：模拟人工动作
    // ==================================================================
    /// <summary>
    /// 模拟一次操作员动作，然后立刻触发一次检测。
    ///
    /// <para>为什么把"做动作"和"触发检测"合成一个按钮：现场就是
    /// "人做完 → 手离开 → 光电触发"一气呵成，中间没有第二次人工干预。
    /// 拆成两个按钮，演示的人很容易只点第一个，然后以为系统没反应。</para>
    /// </summary>
    private async Task SimulateAsync(SimulatedAction action)
    {
        var simulator = _simulator;
        var recipe = ActiveRecipe;

        if (simulator is null || recipe is null)
        {
            StatusMessage = "当前相机不支持模拟动作（只有模拟相机可以）";
            return;
        }

        LastActionText = simulator.Perform(recipe, action);
        OnPropertyChanged(nameof(SimulatorProgressText));
        _log.Info("【模拟动作】" + LastActionText);

        await InspectOnceAsync();
    }

    /// <summary>把流程走向翻译成一句操作员能看懂的话。</summary>
    private static string DescribeOutcome(MonitoringOutcome outcome)
    {
        string flow = outcome.Advance switch
        {
            AdvanceOutcome.Advanced => "已推进到下一道工序",
            AdvanceOutcome.ProcessCompleted => "本件全部工序完成",
            AdvanceOutcome.StepFailed => "不合格，停留在原工序等待返工",
            _ => "流程保持不变",
        };
        return string.IsNullOrWhiteSpace(outcome.Message) ? flow : $"{flow}｜{outcome.Message}";
    }

    /// <summary>把测量值 / 缺陷刷到界面上。</summary>
    private void RefreshMeasurements(InspectionResult result)
    {
        MeasurementLines.Clear();
        foreach (var m in result.Measurements) MeasurementLines.Add(m.Display);
        foreach (var d in result.Defects) MeasurementLines.Add("缺陷：" + d);
    }

    /// <summary>整件完成后延时自动复位，开始下一件（连件生产，不该等人点按钮）。</summary>
    private void ScheduleAutoReset()
    {
        if (_autoResetTimer is null)
        {
            _autoResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _autoResetTimer.Tick += (_, _) =>
            {
                _autoResetTimer?.Stop();
                ResetProcess();
                _log.Info("已自动开始下一件");
            };
        }

        _autoResetTimer.Stop();
        _autoResetTimer.Start();
    }
}
