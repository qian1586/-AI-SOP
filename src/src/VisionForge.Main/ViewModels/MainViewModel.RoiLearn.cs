using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Infrastructure.Storage;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 区域学习：按框教 OK / NG，像基恩士抓取设定那样"教几条就会判"。
///
/// <para>现场三步走：先框出抓取位置；再选一个框，零件放好时点「记 OK」、
/// 零件拿走（空框）时点「记 NG」，各教几条；最后点「开始识别」，
/// 每个框自己判"有 / 没有"，绿=有（OK）、红=没有（NG）。</para>
///
/// <para>判定怎么接进流程：框 N 判 OK 就等于"第 N 步做到了"。
/// 只推动流程里当前该做的那一步（顺序不会乱），已经 OK 的步骤会被连续补上，
/// 所以现场先放 A 再放 B、或者同时放，都能跟上。</para>
/// </summary>
public sealed partial class MainViewModel
{
    private readonly List<RoiSample> _roiSamples = new();
    private RoiSampleLibrary _roiLibrary = new();

    /// <summary>每个框最近一次的结论（true=OK / false=NG / null=无法判定），只在"变化时"驱动流程。</summary>
    private readonly Dictionary<int, bool?> _roiLastStates = new();

    private RoiTargetOption? _selectedRoiTarget;
    private bool _isRoiLearning;
    private string _roiLearnHint = "先在画面上框出抓取位置，再选一个框教 OK / NG";
    private string _roiLearnResultText = "—";
    private string _roiLearnStateKey = "pending";
    private double _roiLearnConfidence;

    /// <summary>可选的框（跟着当前配方的框自动更新）。</summary>
    public ObservableCollection<RoiTargetOption> RoiTargets { get; } = new();

    /// <summary>当前选中的那个框教过的样本（缩略图列表）。</summary>
    public ObservableCollection<RoiSample> SelectedRoiSamples { get; } = new();

    /// <summary>动作置信度 Top3（学了参考界面：让现场一眼看出"系统认为人在做哪一步"）。</summary>
    public ObservableCollection<ActionRankItem> ActionRanking { get; } = new();

    /// <summary>每个框最近一次的识别结论（算 Top3、留证据帧都用它）。</summary>
    private readonly Dictionary<int, ActionRecognition> _lastRecognition = new();

    /// <summary>选中哪个框去教。点画面上的框也会切到这里。</summary>
    public RoiTargetOption? SelectedRoiTarget
    {
        get => _selectedRoiTarget;
        set
        {
            if (!SetProperty(ref _selectedRoiTarget, value)) return;
            RefreshSelectedRoiSamples();
            OnPropertyChanged(nameof(RoiTargetSampleText));
            OnPropertyChanged(nameof(CanUseRoiLearning));
            (CaptureRoiOkCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CaptureRoiNgCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ClearRoiTargetSamplesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SimulateObjectPresentCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SimulateObjectGoneCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>教 OK：框里有这个东西（基恩士那套"有 = 良品"）。</summary>
    public ICommand CaptureRoiOkCommand { get; }

    /// <summary>教 NG：东西被拿走（空框）。</summary>
    public ICommand CaptureRoiNgCommand { get; }

    /// <summary>清空当前框的样本（教错了要能一键重来）。</summary>
    public ICommand ClearRoiTargetSamplesCommand { get; }

    /// <summary>开始 / 停止按框识别。</summary>
    public ICommand ToggleRoiLearnCommand { get; }

    /// <summary>点步骤条上的小方框 = 选中这个框去教它（一个框就一格）。</summary>
    public ICommand SelectRoiTargetCommand { get; }

    /// <summary>
    /// 模拟相机才有：「往选中的框里放一块料」。
    ///
    /// <para>没有真相机时，靠它就能把整套 OK / NG 学习走一遍：
    /// 点「放上一块」→ 记 OK；点「拿走了」→ 记 NG；再开始识别，
    /// 框会跟着变绿变红。真实相机上这两个按钮不显示。</para>
    /// </summary>
    public ICommand SimulateObjectPresentCommand { get; }

    /// <summary>模拟相机才有：把选中的框里那块料拿走（画面变成空框）。</summary>
    public ICommand SimulateObjectGoneCommand { get; }

    /// <summary>当前相机是不是"可编程场景"的模拟相机（决定要不要显示模拟按钮）。</summary>
    public bool IsMockSceneAvailable => _sceneCamera is not null;

    /// <summary>相机连上/断开时刷新"有没有模拟场景可用"，并同步按钮可用状态。</summary>
    private void RaiseMockSceneAvailability()
    {
        OnPropertyChanged(nameof(IsMockSceneAvailable));
        (SimulateObjectPresentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SimulateObjectGoneCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void SimulateRoiObject(bool present)
    {
        var target = _selectedRoiTarget;
        var sceneCamera = _sceneCamera;
        var recipe = ActiveRecipe;

        if (target is null || sceneCamera is null || recipe is null)
        {
            RoiLearnHint = "先连上（模拟）相机、并选中一个框";
            return;
        }

        var rois = recipe.Rois.Where(r => r.Enabled).ToList();
        if (target.Index - 1 >= rois.Count)
        {
            RefreshRoiTargetOptions();
            return;
        }

        var roi = rois[target.Index - 1];
        var blobs = new List<SceneBlob>();

        if (present)
        {
            // 在框内放一块亮料（留点边距，避免压到框线）
            blobs.Add(new SceneBlob
            {
                X = roi.X + roi.Width * 0.2,
                Y = roi.Y + roi.Height * 0.2,
                Width = roi.Width * 0.6,
                Height = roi.Height * 0.6,
            });
        }

        sceneCamera.SetScene(blobs);

        RoiLearnHint = present
            ? $"已模拟：第 {target.Index} 个框里放上了一块料 —— 现在点「✔ 记 OK」"
            : $"已模拟：第 {target.Index} 个框里的料被拿走了 —— 现在点「✘ 记 NG」";

        _log.Info($"区域学习（模拟场景）：第 {target.Index} 个框 {(present ? "放料" : "空框")}");
    }

    private void SelectRoiTarget(RoiTargetOption? option)
    {
        if (option is null) return;
        SelectedRoiTarget = option;
        StatusMessage = $"已选中第 {option.Index} 个框「{option.Name}」—— 可以教 OK / NG 了";
    }

    public bool IsRoiLearning
    {
        get => _isRoiLearning;
        private set
        {
            if (!SetProperty(ref _isRoiLearning, value)) return;
            OnPropertyChanged(nameof(RoiLearnButtonText));
            (ToggleRoiLearnCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string RoiLearnButtonText => IsRoiLearning ? "■ 停止识别" : "▶ 开始识别";

    /// <summary>操作提示（教了几条、为什么判不了）。</summary>
    public string RoiLearnHint
    {
        get => _roiLearnHint;
        private set => SetProperty(ref _roiLearnHint, value);
    }

    /// <summary>当前框的识别结论大字。</summary>
    public string RoiLearnResultText
    {
        get => _roiLearnResultText;
        private set => SetProperty(ref _roiLearnResultText, value);
    }

    /// <summary>结论对应的颜色键（ok / ng / active / pending），界面按它上色。</summary>
    public string RoiLearnStateKey
    {
        get => _roiLearnStateKey;
        private set => SetProperty(ref _roiLearnStateKey, value);
    }

    public double RoiLearnConfidence
    {
        get => _roiLearnConfidence;
        private set
        {
            if (!SetProperty(ref _roiLearnConfidence, value)) return;
            OnPropertyChanged(nameof(RoiLearnConfidenceText));
        }
    }

    public string RoiLearnConfidenceText =>
        _roiLearnConfidence <= 0 ? "—" : _roiLearnConfidence.ToString("P0");

    /// <summary>"OK 3 / NG 2"这种计数文案。</summary>
    public string RoiTargetSampleText => _selectedRoiTarget?.SampleText ?? "未选择";

    /// <summary>步骤条右边的框数量（"7 个框"）。画一个多一个，现场一眼确认自己画了几个。</summary>
    public string RoiBoxCountText => RoiTargets.Count == 0
        ? "还没有框"
        : $"{RoiTargets.Count} 个框";

    /// <summary>选中了框才谈得上教样本。</summary>
    public bool CanUseRoiLearning => _selectedRoiTarget is not null;

    // ==================================================================
    // 载入 / 保存
    // ==================================================================
    private void LoadRoiSamples()
    {
        try
        {
            _roiSamples.Clear();
            _roiSamples.AddRange(RoiSampleStore.Load(
                Path.Combine(_settings.Current.DataRoot, "roi-samples.json")));

            RebuildRoiLibrary();
            _log.Info($"区域学习样本已载入：{_roiSamples.Count} 条");
        }
        catch (Exception ex)
        {
            _log.Warn("区域学习样本载入失败：" + ex.Message);
        }
    }

    private void SaveRoiSamples()
    {
        try
        {
            RoiSampleStore.Save(_roiSamples,
                Path.Combine(_settings.Current.DataRoot, "roi-samples.json"));

            // 记下"刚写过盘"，自学那边的节流据此决定要不要再写（见 SaveRoiSamplesThrottled）
            _lastRoiSampleSave = DateTime.Now;
            _roiSamplesDirty = false;
        }
        catch (Exception ex)
        {
            // 不静默吞掉：写盘失败意味着"重启后样本又没了"，现场会认为教了没用
            _log.Warn("区域学习样本保存失败：" + ex.Message);
        }
    }

    /// <summary>当前配方下的样本（别的配方教的不能拿来判这个配方）。</summary>
    private List<RoiSample> CurrentRoiSamples()
    {
        var recipeId = ActiveRecipe?.Id;
        return string.IsNullOrEmpty(recipeId)
            ? new List<RoiSample>()
            : _roiSamples.Where(s => s.RecipeId == recipeId).ToList();
    }

    private void RebuildRoiLibrary()
        => _roiLibrary.Rebuild(CurrentRoiSamples(), _similarityThreshold);

    /// <summary>配方换了 / 框增删了，就重建下拉与识别器。</summary>
    private void RefreshRoiTargetOptions()
    {
        int previous = _selectedRoiTarget?.Index ?? 1;

        RoiTargets.Clear();
        var rois = ActiveRecipe?.Rois.Where(r => r.Enabled).ToList() ?? new List<RoiRegion>();

        for (int i = 0; i < rois.Count; i++)
            RoiTargets.Add(new RoiTargetOption(i + 1, rois[i].Name));

        RebuildRoiLibrary();
        RefreshRoiTargetCounts();

        // 尽量保持原来选中的那个框；少了就退到第一个
        SelectedRoiTarget = RoiTargets.FirstOrDefault(t => t.Index == previous) ?? RoiTargets.FirstOrDefault();

        OnPropertyChanged(nameof(CanUseRoiLearning));
        OnPropertyChanged(nameof(RoiBoxCountText));
        RefreshTimingStepOptions();          // 框变了，时间回看里的工序下拉也跟着变
        (ToggleRoiLearnCommand as RelayCommand)?.RaiseCanExecuteChanged();

        // 换配方 / 增删框之后，"有没有自学样本"这件事可能变了（撤销/清空/固化按钮的可用状态）
        RaiseSelfLearningProps();
    }

    private void RefreshRoiTargetCounts()
    {
        var samples = CurrentRoiSamples();

        // 名字可能被改过（右键改步骤名会同步到框名），这里跟着更新
        var rois = ActiveRecipe?.Rois.Where(r => r.Enabled).ToList() ?? new List<RoiRegion>();

        foreach (var option in RoiTargets)
        {
            if (option.Index - 1 < rois.Count) option.Rename(rois[option.Index - 1].Name);

            option.SetCounts(
                samples.Count(s => s.RoiIndex == option.Index && s.IsOk),
                samples.Count(s => s.RoiIndex == option.Index && !s.IsOk));
        }

        OnPropertyChanged(nameof(RoiTargetSampleText));
        OnPropertyChanged(nameof(CanUseRoiLearning));
        (ClearRoiTargetSamplesCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RefreshSelectedRoiSamples()
    {
        SelectedRoiSamples.Clear();
        if (_selectedRoiTarget is null) return;

        foreach (var sample in CurrentRoiSamples()
                     .Where(s => s.RoiIndex == _selectedRoiTarget.Index)
                     .OrderByDescending(s => s.Timestamp))
            SelectedRoiSamples.Add(sample);
    }

    // ==================================================================
    // 教学
    // ==================================================================
    /// <summary>
    /// 抓一帧当作样本。
    /// </summary>
    /// <param name="isOk">人工/纠错指定的标签。</param>
    /// <param name="source">
    /// 这条样本算什么来源：手动点「记 OK / 记 NG」是 Manual；
    /// 点「其实是对的 / 其实是错的」（纠正系统误判）是 Corrected。
    ///
    /// <para>两者抓的都是"此刻这一帧"，区别只在身份：Corrected 是人工对系统的一次纠错，
    /// 信息量更高，所以永远不会被自学淘汰逻辑清掉。</para>
    /// </param>
    private void CaptureRoiSample(bool isOk, string source = SampleSource.Manual)
    {
        var target = _selectedRoiTarget;
        if (target is null)
        {
            RoiLearnHint = "先在上面选一个框（或者在画面上点一下那个框）";
            return;
        }

        var frame = _lastFrame;
        if (frame is null)
        {
            RoiLearnHint = "还没有画面：先点「启动监测」连上相机，对着框里的东西再教";
            return;
        }

        var recipe = ActiveRecipe;
        if (recipe is null) return;

        var rois = recipe.Rois.Where(r => r.Enabled).ToList();
        if (target.Index - 1 >= rois.Count)
        {
            RoiLearnHint = "这个框已经不存在了，请重新选择";
            RefreshRoiTargetOptions();
            return;
        }

        var roi = rois[target.Index - 1];

        try
        {
            string dir = Path.Combine(_settings.Current.DataRoot, "roi-samples");
            Directory.CreateDirectory(dir);
            string imagePath = Path.Combine(dir,
                $"ROI{target.Index}-{(isOk ? "OK" : "NG")}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            var preview = PreviewImage;
            if (preview is not null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(preview));
                using var fs = File.Create(imagePath);
                encoder.Save(fs);
            }

            var sample = new RoiSample
            {
                RecipeId = recipe.Id,
                RoiIndex = target.Index,
                RoiName = roi.Name,
                IsOk = isOk,
                ImagePath = preview is null ? null : imagePath,
                Feature = FrameFeature.Extract(frame, roi),   // 只取这个框里的特征
                Source = source,
                Confidence = 1,
                Note = source == SampleSource.Corrected
                    ? "人工纠错：纠正一次误判"
                    : "人工教示",
            };

            _roiSamples.Add(sample);
            RebuildRoiLibrary();
            SaveRoiSamples();
            RefreshRoiTargetCounts();
            RefreshSelectedRoiSamples();

            bool corrected = source == SampleSource.Corrected;
            RoiLearnHint = corrected
                ? $"已按你的纠正记下第 {target.Index} 个框的 {sample.Label} 样本（纠错样本不会被自动淘汰）"
                : $"已记下第 {target.Index} 个框的 {sample.Label} 样本（本框 {target.SampleText}）。" +
                  "多教几条（换个光照、换个摆放角度）识别更稳";

            if (corrected)
            {
                string was = _lastRecognition.TryGetValue(target.Index, out var last)
                    ? last.IsOk switch { true => "OK", false => "NG", _ => "无法判定" }
                    : "（还没判过）";

                WriteJournal(new LearningJournalEntry
                {
                    Timestamp = DateTime.Now,
                    Source = SampleSource.Corrected,
                    Decision = "Corrected",
                    RoiIndex = target.Index,
                    RoiName = roi.Name,
                    Label = sample.Label,
                    Confidence = 1,
                    Reason = $"人工纠错：系统原判 {was}，人改成 {sample.Label}（这是最值钱的样本，不会被自动淘汰）",
                });

                RaiseSelfLearningProps();
            }

            _log.Info($"区域学习：第 {target.Index} 个框「{roi.Name}」记 {sample.Label} 样本" +
                      $"（来源 {SampleSource.Describe(source)}），特征 {sample.Feature.Length} 维");
        }
        catch (Exception ex)
        {
            RoiLearnHint = "记样本失败：" + ex.Message;
            _log.Error("区域学习记样本失败", ex);
        }
    }

    private void ClearRoiTargetSamples()
    {
        var target = _selectedRoiTarget;
        var recipeId = ActiveRecipe?.Id;
        if (target is null || string.IsNullOrEmpty(recipeId)) return;

        int removed = _roiSamples.RemoveAll(s => s.RecipeId == recipeId && s.RoiIndex == target.Index);
        RebuildRoiLibrary();
        SaveRoiSamples();
        RefreshRoiTargetCounts();
        RefreshSelectedRoiSamples();

        RoiLearnHint = $"已清空第 {target.Index} 个框的 {removed} 条样本";
        _log.Warn($"区域学习：清空第 {target.Index} 个框的样本 {removed} 条");
    }

    // ==================================================================
    // 识别
    // ==================================================================
    private void ToggleRoiLearn()
    {
        if (IsRoiLearning)
        {
            StopRoiLearning("已停止区域识别");
            return;
        }

        if (ActiveRecipe is null || RoiTargets.Count == 0)
        {
            RoiLearnHint = "画面上还没有框：先点「🎯 标定 ROI」把抓取位置框出来";
            return;
        }

        if (_roiLibrary.TotalSamples == 0)
        {
            RoiLearnHint = "还没有教过样本：选一个框，点「记 OK」「记 NG」各教几条再开始";
            return;
        }

        if (_camera?.IsConnected != true)
        {
            RoiLearnHint = "没有画面：先点「启动监测」连上相机";
            return;
        }

        // 三条识别通路不能同时驱动流程（会重复计数、互相打架）：开这个就关掉另外两个
        if (IsRecognizing) ToggleRecognize();
        if (IsProcessRunning) StopProcessMonitor();

        _roiLastStates.Clear();
        RebuildRoiLibrary();

        // 关键：把跨件的进度基准清零。
        //
        // 会话按"进度有没有增加"来判断是不是真的完成了一道工序（防重复计数）。
        // 如果之前跑过过程监测，_processEngine 里还留着上次的步骤下标，
        // 那么按框识别连续判 OK 时，第 2 步之后会被当成"没有新进展"而被丢掉 ——
        // 表现就是"第一个框变绿了，后面几个死活不推进"。区域学习不经过那个引擎，
        // 所以这里直接把它放掉，让每一步都按"新进展"计数。
        _processEngine = null;
        _session.ResetForNewPiece();

        IsRoiLearning = true;
        RoiLearnHint = $"区域识别中：{RoiTargets.Count} 个框，" +
                       $"相似度阈值 {_similarityThreshold:P0}（低于它就判「无法判定」，不硬猜）";
        _log.Info($"区域学习识别已启动：{RoiTargets.Count} 个框 / {_roiLibrary.TotalSamples} 条样本");
    }

    private void StopRoiLearning(string reason)
    {
        if (!IsRoiLearning) return;

        IsRoiLearning = false;
        RoiLearnHint = reason;
        RoiLearnStateKey = "pending";
        RoiLearnResultText = "—";
        RoiLearnConfidence = 0;

        // 自学是攒着写盘的（见 SaveRoiSamplesThrottled），停止时必须补写一次，
        // 否则"刚学到的最后两条"会在断电/关软件时丢掉。
        FlushRoiSamples();
        _uncertainStreak.Clear();
        _lastLearnAt.Clear();          // 下次开始识别时立刻就能学（不用等满一秒）

        _log.Info("区域学习识别已停止：" + reason);
    }

    /// <summary>
    /// 每来一帧：把每个"教过"的框单独拿来比一次。
    /// 没教过的框直接跳过（不判 = 不误拦）。
    /// </summary>
    private void RecognizeRoiFrame(CameraFrame frame)
    {
        var recipe = ActiveRecipe;
        if (recipe is null) return;

        // 标定模式下不判：那会儿人正在画框/改框，判出来的结论没有意义，
        // 还会把步骤状态搅乱（现场会觉得"我什么都没干，怎么红了"）。
        if (IsRoiEditMode) return;

        var rois = recipe.Rois.Where(r => r.Enabled).ToList();

        for (int i = 0; i < rois.Count; i++)
        {
            int seq = i + 1;
            if (!_roiLibrary.CanJudge(seq)) continue;

            var learner = _roiLibrary.Get(seq);
            if (learner is null) continue;

            var feature = FrameFeature.Extract(frame, rois[i]);
            var recognition = learner.Recognize(feature);
            ApplyRoiRecognition(seq, recognition);

            // 自主学习：这一帧有没有资格进样本库（拿不准、没做完、太常见的一律不收）。
            // 放在判定之后、且只学不改判 —— 自学永远不会影响当前这一帧的结论。
            LearnFromRoiObservation(seq, rois[i].Name, feature, recognition);
        }

        RefreshActionRanking();
    }

    /// <summary>
    /// 算出"动作置信度 Top3"：每个教过的框取它最近一次的相似度，排前三。
    ///
    /// <para>参考界面右上角就是这个 —— 它回答的是"系统现在认为人在做哪一步"，
    /// 比单个 OK/NG 更能让人判断"识别到底靠不靠谱"。</para>
    /// </summary>
    private void RefreshActionRanking()
    {
        var top = _lastRecognition
            .Select(kv => new
            {
                Seq = kv.Key,
                Name = RoiTargets.FirstOrDefault(t => t.Index == kv.Key)?.Name ?? $"区域{kv.Key}",
                Confidence = kv.Value.Confidence,
            })
            .OrderByDescending(x => x.Confidence)
            .Take(3)
            .ToList();

        // 数据没变就不动集合：每帧都 Clear+Add 会让界面一直重画
        if (top.Count == ActionRanking.Count &&
            top.Select((t, i) => Math.Abs(t.Confidence - ActionRanking[i].Confidence) < 0.005 &&
                                 t.Name == ActionRanking[i].Name).All(same => same))
            return;

        ActionRanking.Clear();
        for (int i = 0; i < top.Count; i++)
            ActionRanking.Add(new ActionRankItem(i + 1, top[i].Name, top[i].Confidence));
    }

    /// <summary>
    /// 给这一步留一张证据帧：判定那一刻的整幅画面。
    ///
    /// <para>参考界面的"步骤证据（近 5 步）"就是这个意思 —— 只有结论没有画面，
    /// 事后谁也说不清"当时到底什么样"。这里把证据直接挂到框位格子上。
    /// 落盘放后台：证据是附加价值，不能拖慢判定。</para>
    /// </summary>
    private void CaptureStepEvidence(int seq, string stepName, bool isOk)
    {
        try
        {
            var image = PreviewImage;
            if (image is null) return;

            var option = RoiTargets.FirstOrDefault(t => t.Index == seq);
            option?.SetEvidence(image, DateTime.Now);

            string dir = Path.Combine(_settings.Current.DataRoot, "step-evidence");
            string path = Path.Combine(dir,
                $"第{seq}步-{(isOk ? "OK" : "NG")}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var fs = File.Create(path);
                    encoder.Save(fs);
                }
                catch (Exception ex)
                {
                    _log.Warn("步骤证据帧保存失败：" + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            _log.Warn("步骤证据帧记录失败：" + ex.Message);
        }
    }

    private void ApplyRoiRecognition(int seq, ActionRecognition recognition)
    {
        string stateKey = recognition.IsOk switch
        {
            true => "ok",
            false => "ng",
            _ => "active",
        };

        // 画面上的框跟着变色（绿=有东西 / 红=被拿走 / 橙=拿不准）
        if (seq - 1 >= 0 && seq - 1 < RoiOverlays.Count)
            RoiOverlays[seq - 1].StateKey = stateKey;

        // 步骤条上的小方框（一个框一格）跟着变色
        var chip = RoiTargets.FirstOrDefault(t => t.Index == seq);
        if (chip is not null) chip.SetStateKey(stateKey);

        // 步骤小方框也跟着变（第 N 个框 = 第 N 步）
        var step = Sop.Steps.FirstOrDefault(s => s.Seq == seq);
        if (step is not null && recognition.IsOk is not null)
            step.State = recognition.IsOk == true ? SopStepState.Passed : SopStepState.Failed;

        // 面板上只显示"当前选中的那个框"的结论，避免一堆数字互相打架
        if (_selectedRoiTarget is not null && _selectedRoiTarget.Index == seq)
        {
            RoiLearnResultText = recognition.IsOk switch
            {
                true => "OK",
                false => "NG",
                _ => "无法判定",
            };
            RoiLearnStateKey = stateKey;
            RoiLearnConfidence = recognition.Confidence;
            RoiLearnHint = recognition.Message;
        }

        bool first = !_roiLastStates.TryGetValue(seq, out var previous);
        bool changed = first || previous != recognition.IsOk;
        _roiLastStates[seq] = recognition.IsOk;
        _lastRecognition[seq] = recognition;

        if (!changed || recognition.IsOk is null) return;

        if (recognition.IsOk == true) DriveFlowFromRoiStates(seq, recognition);
        else FeedRoiViolation(seq, recognition);
    }

    /// <summary>
    /// 框判 OK 等于"这一步做到了"：只推动流程里当前该做的那一步；
    /// 已经 OK 的步骤会连续补上（现场先放 A 再放 B 也能跟上）。
    /// </summary>
    private void DriveFlowFromRoiStates(int seq, ActionRecognition recognition)
    {
        int guard = 0;
        while (Sop.CurrentStep is { } current && guard++ <= Sop.Steps.Count + 1)
        {
            if (!_roiLastStates.TryGetValue(current.Seq, out var state) || state != true) break;

            FeedProcessToSop(new ProcessEvaluation
            {
                Verdict = ProcessVerdict.StepCompleted,
                StepSeq = current.Seq,
                StepName = current.Name,
                Confidence = recognition.Confidence,
                Progress = 1,
                Message = $"第 {current.Seq} 个框识别为 OK：{recognition.Message}",
            }, passed: true);
        }

        StatusMessage = $"区域学习：第 {seq} 个框 OK";
    }

    /// <summary>框判 NG：留一张当时的画面 + 一条违规记录 + 写 PLC（如果正是当前该做的那一步）。</summary>
    private void FeedRoiViolation(int seq, ActionRecognition recognition)
    {
        var violation = new ProcessViolation
        {
            Code = ViolationCode.StepNotDone,
            StepSeq = seq,
            TargetId = "P" + seq,
            Message = $"第 {seq} 个框识别为 NG：{recognition.Message}",
        };

        var evidence = CaptureEvidence($"第 {seq} 个框 NG · {recognition.Match?.Display ?? "无匹配样本"}");
        if (evidence is not null) violation.EvidenceImagePath = evidence.Path;

        ProcessViolations.Insert(0, violation);
        _ = WriteRuleCodeToPlcAsync(violation);

        if (Sop.CurrentStep?.Seq == seq)
        {
            FeedProcessToSop(new ProcessEvaluation
            {
                Verdict = ProcessVerdict.Violation,
                StepSeq = seq,
                StepName = Sop.CurrentStep.Name,
                Confidence = recognition.Confidence,
                Message = violation.Message,
            }, passed: false);
        }
        else
        {
            StatusMessage = violation.Message;
            _log.Warn(violation.Message);
        }
    }

    /// <summary>
    /// 让工序数量<b>双向</b>跟上框的数量：画一个框就多一道工序，删一个框就少一道。
    ///
    /// <para>现场的理解是"一个框 = 一道工序"，所以两者数量必须永远一致。
    /// 早先只做了"增"没做"减"，结果出现"框全删了，画面上还写着 2/9 步"这种自相矛盾 ——
    /// 界面上只要有两处显示同一个东西，就必须一起变。</para>
    ///
    /// <para>多出来的工序直接删掉（名称/指引/超时不再保留）：框都没了，
    /// 留着一道看不见的工序只会让人以为流程还在。</para>
    /// </summary>
    private void SyncSopStepsWithRois()
    {
        var definition = Sop.Definition;
        var recipe = ActiveRecipe;
        if (definition is null || recipe is null) return;

        var rois = recipe.Rois.Where(r => r.Enabled).ToList();
        bool changed = false;

        // ① 多了就删：删掉没有对应框的那些工序
        int removed = definition.Steps.RemoveAll(s => s.Seq > rois.Count);
        if (removed > 0) changed = true;

        // ② 少了就补：每个框都要有一道工序（第 N 个框 = 第 N 道工序）
        for (int i = 0; i < rois.Count; i++)
        {
            int seq = i + 1;
            string boxName = string.IsNullOrWhiteSpace(rois[i].Name) ? $"区域{seq}" : rois[i].Name;

            var existing = definition.Steps.FirstOrDefault(s => s.Seq == seq);
            if (existing is not null)
            {
                // 已经有这道工序：名字跟着框走。
                // 底部格子显示的是框名、右上角显示的是工序名，两边不一致现场一定会问"到底叫什么"。
                if (existing.Name != boxName)
                {
                    existing.Name = boxName;
                    changed = true;
                }
                continue;
            }

            definition.Steps.Add(new SopStepDefinition
            {
                Seq = seq,
                Id = "S" + seq,
                Name = boxName,
                RecipeId = recipe.Id,
                Hint = "在画面上框出来的作业位（第 " + seq + " 个框）",
                BlockNextOnFail = true,
                TimeoutSec = 0,
            });
            changed = true;
        }

        if (!changed) return;

        definition.UpdatedAt = DateTime.Now;
        Sop.Load(definition);      // 重建运行时步骤列表（标定期间做，复位可接受）
        SaveSopDefinition();       // 落盘 sop.json

        _log.Info($"框数变化：工序已同步为 {definition.Steps.Count} 道" +
                  $"（当前 {rois.Count} 个框；第 N 个框 = 第 N 道工序）");
    }

    /// <summary>
    /// 按序号删掉一个框（框位格子右键"删除这个框"走这里）。
    ///
    /// <para><b>删一个框牵动四处，少做一处就会留下不一致：</b></para>
    /// <list type="number">
    ///   <item>配方里的 ROI（框本身）</item>
    ///   <item>画面上的叠加框</item>
    ///   <item>工序列表（第 N 个框 = 第 N 道工序，删了要跟着减）</item>
    ///   <item>过程层判定区域（作业位坐标就来自框）</item>
    /// </list>
    ///
    /// <para>还有一处最容易漏：<b>学习样本是按"第几个框"存的</b>。
    /// 删掉中间的框后，后面的框序号整体前移，样本不跟着挪就会"认成别人的东西"。</para>
    /// </summary>
    public void DeleteRoiByIndex(int index)
    {
        var recipe = ActiveRecipe;
        if (recipe is null) return;

        var enabled = recipe.Rois.Where(r => r.Enabled).ToList();
        if (index < 1 || index > enabled.Count) return;

        var roi = enabled[index - 1];
        recipe.Rois.Remove(roi);

        // ① 学习样本：删掉这个框的；排在它后面的整体前移一位
        int removedSamples = _roiSamples.RemoveAll(s => s.RecipeId == recipe.Id && s.RoiIndex == index);
        foreach (var sample in _roiSamples.Where(s => s.RecipeId == recipe.Id && s.RoiIndex > index))
            sample.RoiIndex--;
        SaveRoiSamples();

        // ② 配方、画面框、工序
        RebuildRoiOverlays();
        SyncSopStepsWithRois();
        RefreshRoiTargetOptions();

        // ③ 过程层判定区域：还有框就按剩下的重建；一个都不剩就清空（否则会留着刚才那个框的坐标）
        if (recipe.Rois.Any(r => r.Enabled)) SyncRoisToTargets();
        else ClearProcessTargets();

        // ④ 落盘（框是配置，断电丢了要重画）
        SaveRecipeInBackground(recipe);

        StatusMessage = $"已删除第 {index} 个框「{roi.Name}」" +
                        (removedSamples > 0 ? $"（连同它的 {removedSamples} 条学习样本）" : "");
        _log.Warn($"删除 ROI 第 {index} 个「{roi.Name}」，同时清掉样本 {removedSamples} 条；" +
                  $"剩余框 {recipe.Rois.Count(r => r.Enabled)} 个，工序 {Sop.Steps.Count} 道");
    }

    /// <summary>一个框都不剩时，把过程层规则里的作业位与工序也清空并落盘。</summary>
    private void ClearProcessTargets()
    {
        var config = _processConfig;
        if (config is null) return;

        config.Targets.Clear();
        config.Steps.Clear();
        config.Required.Clear();

        try
        {
            ProcessConfigStore.SaveJson(config, Path.Combine(_settings.Current.DataRoot, "process-rule.json"));
        }
        catch (Exception ex)
        {
            _log.Warn("清空判定区域失败：" + ex.Message);
        }

        _processEngine = null;
        BuildProcessTargetStates();
        OnPropertyChanged(nameof(ProcessRuleSummary));
    }

    /// <summary>
    /// 在画面上点一下选中某个框（现场更自然：指着哪个框就教哪个）。
    /// 点在框外则不动选择。
    /// </summary>
    public void SelectRoiAtCanvasPoint(double canvasX, double canvasY)
    {
        if (IsRoiEditMode) return;   // 标定模式下左键是画框，不抢它的点击

        var recipe = ActiveRecipe;
        if (recipe is null) return;

        var rois = recipe.Rois.Where(r => r.Enabled).ToList();
        double nx = canvasX / RoiCanvasWidth;
        double ny = canvasY / RoiCanvasHeight;

        for (int i = rois.Count - 1; i >= 0; i--)
        {
            if (!rois[i].Contains(nx, ny)) continue;

            var option = RoiTargets.FirstOrDefault(t => t.Index == i + 1);
            if (option is not null && !ReferenceEquals(option, _selectedRoiTarget))
            {
                SelectedRoiTarget = option;
                StatusMessage = $"已选中第 {option.Index} 个框「{option.Name}」—— 可以教 OK / NG 了";
            }
            return;
        }
    }
}
