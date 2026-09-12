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

    /// <summary>界面上保留的违规条目上限（完整记录在日志与历史库里，不会丢）。</summary>
    private const int MaxViolationEntries = 200;
    private HandActionRuleEngine? _processEngine;
    private string _processStatusText = "过程监测未启动";
    private double _actionConfidence;
    private double _processProgress;
    private ActionSampleClassifier? _classifier;
    private string _recognizedText = "尚未开始识别";
    private double _recognizedConfidence;
    private double _similarityThreshold = 0.75;
    private string _validationSummary = "尚未做批量仿真：教几条样本后点「批量仿真」";

    /// <summary>当前工序作业指引。</summary>
    public string StepHintText => Sop.CurrentStep?.Hint ?? Sop.ProcessMessage;

    public string ProcessStatusText
    {
        get => _processStatusText;
        private set => SetProperty(ref _processStatusText, value);
    }

    /// <summary>整件工序进度 0~1。</summary>
    public double ProcessProgress
    {
        get => _processProgress;
        private set
        {
            if (SetProperty(ref _processProgress, value))
                OnPropertyChanged(nameof(ProcessProgressText));
        }
    }

    /// <summary>识别结论文字（OK / NG / 无法判定）。</summary>
    public string RecognizedText
    {
        get => _recognizedText;
        private set => SetProperty(ref _recognizedText, value);
    }

    /// <summary>与最像样本的相似度（0~1）。</summary>
    public double RecognizedConfidence
    {
        get => _recognizedConfidence;
        private set
        {
            if (SetProperty(ref _recognizedConfidence, value))
                OnPropertyChanged(nameof(RecognizedConfidenceText));
        }
    }

    public string RecognizedConfidenceText =>
        _recognizedConfidence <= 0 ? "—" : _recognizedConfidence.ToString("P0");

    public string RecognizedHint
    {
        get => _recognizedHint;
        private set => SetProperty(ref _recognizedHint, value);
    }

    /// <summary>
    /// 相似度阈值：低于它就判"无法判定"，不硬猜。
    /// 这是防误拦的关键旋钮 —— 现场按实际情况调（宁可多让人确认，也别乱拦线）。
    /// </summary>
    public double SimilarityThreshold
    {
        get => _similarityThreshold;
        set
        {
            if (!SetProperty(ref _similarityThreshold, value)) return;
            if (_classifier is not null) _classifier.MinSimilarity = value;
            RebuildRoiLibrary();   // 区域学习用同一个阈值，改了一起重算
        }
    }

    /// <summary>
    /// 是否在画面上显示 ROI 框（默认显示）。
    ///
    /// <para>现场既要用框来标"抓取位置"，也要用框的颜色看每一步做到没有，
    /// 所以默认打开；觉得挡视线可以一键隐藏，判定逻辑不受影响。</para>
    /// </summary>
    public bool ShowRoiBoxes
    {
        get => _showRoiBoxes;
        set
        {
            if (!SetProperty(ref _showRoiBoxes, value)) return;
            OnPropertyChanged(nameof(ShowRoiBoxesButtonText));
        }
    }

    /// <summary>
    /// 批量仿真结果（对应方案 10.3 / 10.6）：把教示样本用留一法考一遍，
    /// 得到准确率 / 漏检率 / 误检率，直接对着验收线看。
    /// </summary>
    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public string ValidationDetail
    {
        get => _validationDetail;
        private set => SetProperty(ref _validationDetail, value);
    }

    private void RunBatchValidation()
    {
        var report = ActionSampleValidator.LeaveOneOut(_actionSamples, _similarityThreshold);

        ValidationSummary = report.Summary;
        ValidationDetail = report.Failures.Count == 0
            ? string.Empty
            : "未通过项：" + string.Join("；", report.Failures.Take(6));

        _log.Info("批量仿真（留一法）：" + report.Summary);
        if (report.Failures.Count > 0)
            _log.Warn("批量仿真未通过项：" + string.Join("；", report.Failures.Take(6)));

        if (report.Total == 0)
            ValidationSummary = "没有样本可仿真：先「记 OK」「记 NG」教几条";
    }

    // ==================================================================
    // 统一判定状态 + NG 保持图（借鉴基恩士 operator 层）
    // ==================================================================
    /// <summary>
    /// 判定状态键，全界面统一用它上色：ok=通过(绿) / active=报警(橙) / ng=违规(红) / pending=未判定(灰)。
    ///
    /// <para>ROI 框、判定结果区、步骤条、作业位状态块全部读这一个值 ——
    /// 避免"画面上是绿的、步骤条是红的"这种自相矛盾的显示。</para>
    /// </summary>
    public string VerdictStateKey
    {
        get => _verdictStateKey;
        private set => SetProperty(ref _verdictStateKey, value);
    }

    // ==================================================================
    // 过程层：手部动作合规监测（方案 v9）
    // ==================================================================
    /// <summary>加载教示样本（现场教出来的 OK/NG 样本是资产，必须持久化）。</summary>
    private void LoadActionSamples()
    {
        try
        {
            string path = Path.Combine(_settings.Current.DataRoot, "action-samples.json");
            _actionSamples.Clear();
            _actionSamples.AddRange(ActionSampleStore.Load(path));
            _classifier = new ActionSampleClassifier(_actionSamples) { MinSimilarity = _similarityThreshold };

            OnPropertyChanged(nameof(OkSampleCount));
            OnPropertyChanged(nameof(NgSampleCount));

            if (_actionSamples.Count > 0)
                RecognizedHint = $"已载入教示样本：{OkSampleCount} 个 OK / {NgSampleCount} 个 NG";
        }
        catch (Exception ex)
        {
            _log.Warn("教示样本载入失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 记一条教示样本：把当前画面连同人工标的 OK/NG 存下来。
    ///
    /// <para>这就是用户要的用法 —— 员工在镜头下作业，工程师看到标准动作点一下「记 OK」、
    /// 看到违规动作点一下「记 NG」，不用画任何框。</para>
    /// </summary>
    private void CaptureSample(bool isOk)
    {
        var frame = _lastFrame;
        if (frame is null)
        {
            RecognizedHint = "还没有画面：先点「连接相机」，再对着动作点「记 OK」/「记 NG」";
            return;
        }

        try
        {
            string dir = Path.Combine(_settings.Current.DataRoot, "action-samples");
            Directory.CreateDirectory(dir);
            string imagePath = Path.Combine(dir, $"{(isOk ? "OK" : "NG")}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            var preview = PreviewImage;
            if (preview is not null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(preview));
                using var fs = File.Create(imagePath);
                encoder.Save(fs);
            }

            var sample = new ActionSample
            {
                IsOk = isOk,
                ImagePath = preview is null ? null : imagePath,
                Feature = FrameFeature.Extract(frame),
            };
            _actionSamples.Add(sample);

            // 样本变了就重建识别器，并把样本落盘
            _classifier = new ActionSampleClassifier(_actionSamples) { MinSimilarity = _similarityThreshold };
            ActionSampleStore.Save(_actionSamples,
                Path.Combine(_settings.Current.DataRoot, "action-samples.json"));

            OnPropertyChanged(nameof(OkSampleCount));
            OnPropertyChanged(nameof(NgSampleCount));
            RecognizedHint = $"已记下 {sample.Label} 样本（累计 {OkSampleCount} OK / {NgSampleCount} NG）";
            _log.Info($"教示样本已记录：{sample.Label}，特征 {sample.Feature.Length} 维");
        }
        catch (Exception ex)
        {
            RecognizedHint = "记样本失败：" + ex.Message;
            _log.Error("记教示样本失败", ex);
        }
    }

    private void ClearActionSamples()
    {
        _actionSamples.Clear();
        _classifier = new ActionSampleClassifier(Array.Empty<ActionSample>()) { MinSimilarity = _similarityThreshold };

        try
        {
            ActionSampleStore.Save(_actionSamples,
                Path.Combine(_settings.Current.DataRoot, "action-samples.json"));
        }
        catch (Exception ex)
        {
            // 不静默吞掉：清空后写盘失败意味着"下次启动样本又回来了"，
            // 现场会觉得"清了没用"，日志里必须留下痕迹
            _log.Warn("清空教示样本后写盘失败：" + ex.Message);
        }

        OnPropertyChanged(nameof(OkSampleCount));
        OnPropertyChanged(nameof(NgSampleCount));
        RecognizedHint = "教示样本已清空";
        _log.Warn("教示样本已清空");
    }

    private void ToggleRecognize()
    {
        if (IsRecognizing)
        {
            IsRecognizing = false;
            RecognizedHint = "自动识别已停止";
            _log.Info("动作自动识别已停止");
            return;
        }

        if (_actionSamples.Count == 0)
        {
            RecognizedHint = "还没有教示样本：先对着标准动作点「记 OK」、对着违规动作点「记 NG」";
            return;
        }

        if (_camera?.IsConnected != true)
        {
            RecognizedHint = "没有画面：先点「连接相机」，再开始自动识别";
            return;
        }

        _classifier = new ActionSampleClassifier(_actionSamples) { MinSimilarity = _similarityThreshold };
        _lastRecognizedOk = null;

        // 三条识别通路不能同时驱动流程（会重复计数、互相打架）：
        // 开整幅画面的自动识别，就关掉按框的区域识别
        StopRoiLearning("已切换到整幅画面识别（区域学习识别已停止）");

        IsRecognizing = true;
        RecognizedHint = $"自动识别中：{OkSampleCount} 个 OK / {NgSampleCount} 个 NG 样本，阈值 {_similarityThreshold:P0}";
        _log.Info("动作自动识别已启动");
    }

    /// <summary>
    /// 识别一帧画面（UI 线程）。
    ///
    /// <para>只在结论<b>发生变化</b>时驱动流程 —— 否则 10fps 会每帧写一次 SOP 和历史。
    /// "无法判定"既不推进也不报警，只提示补样本，这是防误拦的基本功。</para>
    /// </summary>
    private void RecognizeFrame(CameraFrame frame)
    {
        var classifier = _classifier;
        if (classifier is null) return;

        var recognition = classifier.Recognize(FrameFeature.Extract(frame));

        RecognizedConfidence = recognition.Confidence;
        RecognizedHint = recognition.Message;

        string stateKey;
        if (recognition.IsOk is null)
        {
            RecognizedText = "无法判定";
            stateKey = "active";
        }
        else if (recognition.IsOk == true)
        {
            RecognizedText = "OK";
            stateKey = "ok";
        }
        else
        {
            RecognizedText = "NG";
            stateKey = "ng";
        }

        RecognizedStateKey = stateKey;
        VerdictStateKey = stateKey;
        OnPropertyChanged(nameof(RecognizedStateKey));

        if (_lastRecognizedOk == recognition.IsOk) return;   // 结论没变，不重复驱动
        _lastRecognizedOk = recognition.IsOk;

        if (recognition.IsOk is null) return;                // 无法判定：不推进、不报警

        if (recognition.IsOk == false)
        {
            var violation = new ProcessViolation
            {
                Code = ViolationCode.StepNotDone,
                StepSeq = Sop.CurrentStep?.Seq ?? 0,
                Message = "动作识别为 NG：" + recognition.Message,
            };

            var evidence = CaptureEvidence("识别 NG · " + (recognition.Match?.Display ?? "无匹配样本"));
            if (evidence is not null) violation.EvidenceImagePath = evidence.Path;

            ProcessViolations.Insert(0, violation);
            _ = WriteRuleCodeToPlcAsync(violation);

            FeedProcessToSop(new ProcessEvaluation
            {
                Verdict = ProcessVerdict.Violation,
                StepSeq = violation.StepSeq,
                StepName = CurrentActionName,
                Confidence = recognition.Confidence,
                Message = violation.Message,
            }, passed: false);
        }
        else
        {
            FeedProcessToSop(new ProcessEvaluation
            {
                Verdict = ProcessVerdict.StepCompleted,
                StepSeq = Sop.CurrentStep?.Seq ?? 0,
                StepName = CurrentActionName,
                Confidence = recognition.Confidence,
                Progress = 1,
                Message = "动作识别为 OK：" + recognition.Message,
            }, passed: true);
        }
    }

    private void ToggleProcessMonitor()
    {
        if (IsProcessRunning) StopProcessMonitor();
        else StartProcessMonitor();
    }

    private void StartProcessMonitor()
    {
        var config = _processConfig;
        if (config is null)
        {
            StatusMessage = "没有可用的过程层规则（检查 data\\process-rule.json）";
            return;
        }

        var errors = config.Validate();
        if (errors.Count > 0)
        {
            var violation = new ProcessViolation
            {
                Code = ViolationCode.ConfigInvalid,
                Message = "规则校验未通过：" + string.Join("；", errors),
            };
            ProcessViolations.Insert(0, violation);
            ProcessStatusText = violation.Message;
            _log.Warn(violation.Message);
            return;
        }

        // 规则引擎是纯逻辑，喂什么数据源都行。
        // 现在用"模拟手部轨迹"，等实拍样本与手部模型到位后换成模型数据源即可，引擎一行不改。
        _processEngine = new HandActionRuleEngine(config);

        // 同上：开过程监测就关掉区域学习识别
        StopRoiLearning("已切换到过程监测（区域学习识别已停止）");

        _poseSource?.Dispose();
        _poseSource = new SimulatedPoseSource(
            config,
            SimulatedPoseSource.BuildCorrectScript(config),
            "模拟手部轨迹（标准作业）");
        _poseSource.PoseArrived += OnPoseArrived;
        _poseSource.Finished += OnPoseSourceFinished;
        _poseSource.Start();

        ResetProcessTargetStates();
        IsProcessRunning = true;
        ProcessStatusText = "模拟演示运行中（假手依次经过每个作业位，所以会全绿）· " + _poseSource.Name +
                            " —— 真实判定请用画面下方的「区域学习」";
        _log.Info("过程监测已启动：" + ProcessRuleSummary);
    }

    private void StopProcessMonitor()
    {
        if (_poseSource is not null)
        {
            _poseSource.PoseArrived -= OnPoseArrived;
            _poseSource.Finished -= OnPoseSourceFinished;
            _poseSource.Stop();
            _poseSource.Dispose();
            _poseSource = null;
        }

        IsProcessRunning = false;
        ProcessStatusText = "过程监测已停止";
        VerdictStateKey = "pending";
        ClearRoiStates();
        UpdatePosePoints(null);   // 停了就把关键点收掉，别在画面上留个"上一帧的手"
        _log.Info("过程监测已停止");
    }

    /// <summary>轨迹播完：把按钮状态复位，避免界面一直显示"运行中"。</summary>
    private void OnPoseSourceFinished(object? sender, EventArgs e)
    {
        var app = Application.Current;
        if (app is null) return;

        app.Dispatcher.BeginInvoke(() =>
        {
            IsProcessRunning = false;
            ProcessStatusText = "本次动作轨迹已播放完毕（可再次点「启动过程监测」）";
        });
    }

    private void ResetProcessTargetStates()
    {
        foreach (var chip in ProcessTargetStates) chip.SetStateKey("pending");
    }

    /// <summary>数据源回调 —— 采集线程上，必须切回 UI 线程。</summary>
    private void OnPoseArrived(object? sender, PoseObservation observation)
    {
        var app = Application.Current;
        if (app is null) return;

        // 程序正在退出时 BeginInvoke 可能抛异常：关闭瞬间丢一帧姿态无所谓，
        // 但绝不能让退出流程因为这一下炸掉（日志与前程都会受影响）
        try
        {
            app.Dispatcher.BeginInvoke(() => EvaluatePoseFrame(observation));
        }
        catch (Exception ex)
        {
            _log.Debug("姿态帧投递失败（通常是正在退出）：" + ex.Message);
        }
    }

    private void EvaluatePoseFrame(PoseObservation observation)
    {
        // 顺手把这一帧的关键点画到画面上（参考界面那个绿色骨架）
        if (ShowPosePoints) UpdatePosePoints(observation);

        var engine = _processEngine;
        if (engine is null) return;

        try
        {
            ApplyProcessEvaluation(engine.Feed(observation));
        }
        catch (Exception ex)
        {
            _log.Error("过程层判定异常", ex);
        }
    }

    private void ApplyProcessEvaluation(ProcessEvaluation evaluation)
    {
        CurrentActionName = string.IsNullOrWhiteSpace(evaluation.StepName) ? "—" : evaluation.StepName;
        CurrentActionHint = evaluation.Message;
        ActionConfidence = evaluation.Confidence;
        ProcessProgress = evaluation.Progress;

        UpdateProcessTargetStates(evaluation);

        // 统一判定状态：绿=通过 / 橙=报警(进行中·关键点丢失) / 红=违规
        VerdictStateKey = evaluation.Verdict switch
        {
            ProcessVerdict.StepCompleted => "ok",
            ProcessVerdict.AllCompleted => "ok",
            ProcessVerdict.Violation => "ng",
            ProcessVerdict.Unreliable => "active",
            _ => "active",
        };

        foreach (var violation in evaluation.NewViolations)
        {
            // NG 保持图：违规那一刻必须留下画面，否则复盘只能看到一行规则码
            var evidence = CaptureEvidence(violation.CodeText + " · " + evaluation.StepName);
            if (evidence is not null) violation.EvidenceImagePath = evidence.Path;

            ProcessViolations.Insert(0, violation);
            _ = WriteRuleCodeToPlcAsync(violation);   // PLC 实时拦截契约

            // 步骤小方框那边：这一步"没监测到"就直接标红。
            // 不加这一句的话，被跳过的步骤会一直停在灰色，现场看不出是哪一步漏了。
            MarkStepNotDetected(violation);
        }

        switch (evaluation.Verdict)
        {
            case ProcessVerdict.StepCompleted:
            case ProcessVerdict.AllCompleted:
                FeedProcessToSop(evaluation, passed: true);
                break;

            case ProcessVerdict.Violation:
                FeedProcessToSop(evaluation, passed: false);
                break;

            case ProcessVerdict.Unreliable:
                // 「看不清」不等于「不合规」：只报警请人确认，不动流程、不拦线
                StatusMessage = "过程监测：" + evaluation.Message;
                _log.Warn("过程层：" + evaluation.Message);
                break;
        }

        RaiseProcessProps();
    }

    private void UpdateProcessTargetStates(ProcessEvaluation evaluation)
    {
        var covered = new HashSet<string>(evaluation.CoveredTargets, StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(evaluation.ActiveTargets, StringComparer.OrdinalIgnoreCase);

        foreach (var chip in ProcessTargetStates)
        {
            string key = covered.Contains(chip.Id) ? "ok"
                       : active.Contains(chip.Id) ? "active"
                       : "pending";
            chip.SetStateKey(key);
        }

        // 画面上的 ROI 框跟着一起变色（基恩士式"结果变色"）。
        // 过程层目标与配方 ROI 是同序的，按下标对齐即可 ——
        // 这也是当初让示例数据"6 个作业位坐标 = 6 个 ROI 坐标"的原因。
        for (int i = 0; i < RoiOverlays.Count; i++)
        {
            RoiOverlays[i].StateKey = i < ProcessTargetStates.Count
                ? ProcessTargetStates[i].StateKey
                : string.Empty;
        }
    }

    /// <summary>清掉 ROI 框上的判定状态，回到原来的调色板配色。</summary>
    private void ClearRoiStates()
    {
        foreach (var overlay in RoiOverlays) overlay.StateKey = string.Empty;
    }

    /// <summary>
    /// 把"这一步压根没监测到"的步骤小方框标成红色。
    ///
    /// <para>对应现场那句话："过了的动作框变绿，没监测到的框是红的"。
    /// 只处理"跳步 / 目标未完成"两类违规 —— 其它码（比如关键点丢失）不算做错，
    /// 不能把框染红，否则操作员会以为自己做错了。</para>
    /// </summary>
    private void MarkStepNotDetected(ProcessViolation violation)
    {
        if (violation.Code != ViolationCode.StepSkipped &&
            violation.Code != ViolationCode.StepNotDone) return;

        var step = Sop.Steps.FirstOrDefault(s => s.Seq == violation.StepSeq);
        if (step is null) return;

        if (step.State != SopStepState.Failed)
        {
            step.State = SopStepState.Failed;
            step.LastMessage = violation.Message;
            _log.Warn($"步骤 {step.Seq}「{step.Name}」未监测到，已标红（{violation.CodeText}）");
        }
    }

    /// <summary>
    /// PLC 实时拦截契约（P0）。
    ///
    /// 契约内容：违规发生时写三样东西 ——
    ///   ① InspectResult = 0（NG）  ② AllowPass = false（不放行）
    ///   ③ RuleCode = 违规码整数值（见 ViolationCode 枚举，配规则码地址 D201）
    /// 时间戳由上位机历史记录承担（PLC 侧不必存时间）。
    /// </summary>
    private async Task WriteRuleCodeToPlcAsync(ProcessViolation violation)
    {
        if (_plc.State != PlcConnectionState.Connected) return;

        try
        {
            var verdictAddr = _settings.Current.PlcAddresses.FirstOrDefault(a => a.Name == "InspectResult");
            if (verdictAddr is not null)
                await _plc.WriteAsync(verdictAddr, 0, CancellationToken.None);       // 0 = NG

            var passAddr = _settings.Current.PlcAddresses.FirstOrDefault(a => a.Name == "AllowPass");
            if (passAddr is not null)
                await _plc.WriteAsync(passAddr, false, CancellationToken.None);

            var ruleAddr = _settings.Current.PlcAddresses.FirstOrDefault(a => a.Name == "RuleCode");
            if (ruleAddr is not null)
                await _plc.WriteAsync(ruleAddr, (int)violation.Code, CancellationToken.None);

            _log.Info($"已写 PLC：NG + 不放行 + 规则码 {(int)violation.Code}({violation.CodeText}) " +
                      $"@ {violation.Timestamp:HH:mm:ss.fff}");
        }
        catch (Exception ex)
        {
            _log.Warn("PLC 拦截信号写入失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把过程层结论喂给同一套监测会话（SOP 状态机 + 计数 + 报警）。
    /// 过程层和保底层不各搞一套流程控制 —— 谁给的结论都走同一个状态机，
    /// 这样"跳步锁线""复检解锁""整件完成"这些行为天然一致。
    /// </summary>
    private void FeedProcessToSop(ProcessEvaluation evaluation, bool passed)
    {
        var config = _processConfig;
        if (config is null) return;

        // 判定之前先抓住"这一步"：判定之后 CurrentStep 就走到下一步了。
        // 动作计时就是在这里记的 —— 三条识别通路都汇到这一个函数，所以都自动带计时。
        var timedStep = Sop.CurrentStep;

        int doneSteps = evaluation.Verdict == ProcessVerdict.AllCompleted
            ? config.Steps.Count
            : (_processEngine?.CurrentStepIndex ?? 0);

        var result = new InspectionResult
        {
            RecipeId = ActiveRecipe?.Id ?? "process-layer",
            RecipeName = ActiveRecipe?.Name ?? "过程层（手部动作）",
            ProductModel = ProductModel,
            BatchNo = BatchNo,
            Operator = _settings.Current.OperatorName,
            Timestamp = DateTime.Now,
            Verdict = passed ? InspectionVerdict.Pass : InspectionVerdict.Fail,
            JudgementReason = evaluation.Message,
            ProgressSteps = passed ? doneSteps : 0,
        };
        result.Measurements.Add(new MeasurementItem
        {
            Name = "当前动作完成度",
            Value = Math.Round(evaluation.Confidence * 100, 1),
            Unit = "%",
        });
        result.Measurements.Add(new MeasurementItem
        {
            Name = "工序进度",
            Value = doneSteps,
            Unit = "步",
        });

        var outcome = _session.Process(result);

        VerdictText = passed ? "OK" : "NG";
        JudgementReason = evaluation.Message;
        OutcomeText = DescribeOutcome(outcome);
        OkCount = _session.OkCount;
        NgCount = _session.NgCount;
        RefreshMeasurements(result);
        RaiseMonitorProps();

        // 违规必落历史，整件完成也落一条；中间的"某道工序完成"不落，
        // 否则一件产品会刷出 6 条记录，把历史表淹掉
        if (!passed || evaluation.Verdict == ProcessVerdict.AllCompleted)
        {
            var record = InspectionRecord.FromResult(result);
            AppendHistoryRecord(record);

            // 写盘放后台线程：这里在 UI 线程上，直接同步等待会重演"异步死锁"
            _ = Task.Run(async () =>
            {
                try { await _history.SaveAsync(record); }
                catch (Exception ex) { _log.Warn("过程层历史写入失败：" + ex.Message); }
            });
        }

        if (outcome.Advance == AdvanceOutcome.ProcessCompleted)
        {
            _log.Info($"过程层：整件完成（累计 {_session.CompletedPieces} 件）");
            NotePieceCompleted(passed, evaluation.Message);   // 记节拍 + 推给 MES
            ScheduleAutoReset();
        }

        // 动作计时：这一步花了多久、比上次快还是慢（记录里会带上件号，便于按件回看）
        if (timedStep is not null)
        {
            RecordStepTiming(timedStep, passed, evaluation.Message);
            CaptureStepEvidence(timedStep.Seq, timedStep.Name, passed);   // 每步留一张"当时什么样"
        }

        // 违规要驱动声光报警（走的是和保底层同一套逻辑）
        _ = UpdateAlarmFromSopAsync();
    }

    private void ClearProcessViolations()
    {
        ProcessViolations.Clear();
        _processEngine?.ClearViolations();
        _log.Info("过程层违规列表已清空");
    }

    private void ExportProcessRule()
    {
        var config = _processConfig;
        if (config is null) return;

        try
        {
            string path = Path.Combine(_settings.Current.DataRoot, "process-rule.yaml");
            File.WriteAllText(path, ProcessConfigStore.ToYaml(config), new System.Text.UTF8Encoding(true));
            StatusMessage = "规则已导出：" + path;
            _log.Info("过程层规则已导出 YAML：" + path);
        }
        catch (Exception ex)
        {
            StatusMessage = "规则导出失败：" + ex.Message;
        }
    }

    private void RaiseProcessProps()
    {
        // 违规列表只保留最近 N 条：7×24 连跑时一个工位一天可能攒下几千条，
        // 无限增长的列表既吃内存也拖慢界面。完整记录在日志与历史库里，不会丢。
        while (ProcessViolations.Count > MaxViolationEntries)
            ProcessViolations.RemoveAt(ProcessViolations.Count - 1);

        OnPropertyChanged(nameof(ProcessMonitorButtonText));
        OnPropertyChanged(nameof(ProcessStatusText));
        OnPropertyChanged(nameof(CurrentActionName));
        OnPropertyChanged(nameof(ActionConfidence));
        OnPropertyChanged(nameof(ActionConfidenceText));
        OnPropertyChanged(nameof(CurrentActionHint));
        OnPropertyChanged(nameof(ProcessProgress));
        OnPropertyChanged(nameof(ProcessProgressText));
        OnPropertyChanged(nameof(ProcessRuleSummary));

        // 底部状态条上的进度/合规率/状态都跟这些变化有关，一起刷
        RaiseStatusBarProps();
    }
}
