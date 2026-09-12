using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Infrastructure.Storage;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// AI 自主学习：让识别率随着生产自己往上涨。
///
/// <para><b>它由两条腿走路：</b></para>
/// <list type="number">
///   <item><b>自动收录（无人工介入）</b>：识别开着、系统判某个框 OK 而且很有把握、
///         这一步又确实正常完成了 —— 这一帧就自动变成 OK 样本。
///         正常生产占掉了绝大部分时间，等于"不用人再教一遍"。</item>
///   <item><b>人工纠错（点一下）</b>：系统判错了，人点「其实是对的 / 其实是错的」，
///         当时那一帧立刻以人工指定的标签入样本库，并且标记为"纠错样本"永不被淘汰。
///         这是最值钱的一类样本 —— 它专门补的正是模型最薄弱的那些画面。</item>
/// </list>
///
/// <para><b>为什么还要能"停"和"退"：</b>现场换产品、换工装、换光照的时候，
/// 老样本会过期，自学也可能学歪。所以这里有三个开关给班组长用：
/// 关自学（立刻不再收）、撤销上次（撤掉刚学的那条）、
/// 固化（把自学样本升级成人工样本，之后系统再也不动它）。
/// 所有动作都写进 data\learning\ 的账本，事后可查。</para>
/// </summary>
public sealed partial class MainViewModel
{
    // ------------------------------------------------------------------ 状态

    /// <summary>本次运行的统计（学了多少、拒了多少、为什么拒）。</summary>
    private readonly SelfLearningStats _selfLearnStats = new();

    /// <summary>每个框连续多少帧"拿不准"。用来告诉现场"该给哪个框补样本了"。</summary>
    private readonly Dictionary<int, int> _uncertainStreak = new();

    /// <summary>
    /// 日志节流：同一个理由（同一个框 + 同一种结论）60 秒最多记一条。
    ///
    /// <para>不加这个，一帧一条"无法判定"，跑一分钟就是 1500 条日志，
    /// 真正有用的信息全被冲掉了。</para>
    /// </summary>
    private readonly Dictionary<string, DateTime> _journalThrottle = new();

    /// <summary>
    /// 每个框最近一次"送去自学"的时刻 —— 给自学限速用（见 <see cref="SelfLearningInterval"/>）。
    /// </summary>
    private readonly Dictionary<int, DateTime> _lastLearnAt = new();

    /// <summary>
    /// 同一个框最多多久送一次自学。
    ///
    /// <para><b>为什么要限速：</b>自学是逐帧跑的，而相邻两帧的画面几乎一模一样 ——
    /// 送进去也只会被判成"同一个情况又见了一次"（强化），功能上零收益。
    /// 但每一帧都要付出：复制一份样本列表 + 跟几十条样本逐一算 193 维相似度 + 一堆 LINQ 临时对象。
    /// 一个工位 9 个框、25 帧/秒，就是每秒两百多次这种计算，全是白烧的 CPU 和 GC。
    /// 7×24 跑下来，垃圾回收压力会实实在在拖慢界面。</para>
    ///
    /// <para>1 秒一次对"边生产边学"完全够用：一个动作持续几秒到几十秒，
    /// 而真正有区别的画面（零件放上去 / 被拿走）一定会跨越多个 1 秒窗口。</para>
    /// </summary>
    private static readonly TimeSpan SelfLearningInterval = TimeSpan.FromSeconds(1);

    private DateTime _lastRoiSampleSave = DateTime.MinValue;
    private bool _roiSamplesDirty;

    /// <summary>自学账本（界面上的"自学记录"列表，最近 200 条）。</summary>
    public ObservableCollection<LearningJournalEntry> LearningJournal { get; } = new();

    // ------------------------------------------------------------------ 命令

    /// <summary>一键开/关自学。</summary>
    public ICommand ToggleSelfLearningCommand { get; }

    /// <summary>撤销最近学进来的一条（只撤自学的，人工样本不动）。</summary>
    public ICommand UndoLastAutoSampleCommand { get; }

    /// <summary>清空自学样本（人工 / 纠错的保留）。</summary>
    public ICommand ClearAutoSamplesCommand { get; }

    /// <summary>把自学样本固化成人工样本（之后永不被自动淘汰）。</summary>
    public ICommand FreezeAutoSamplesCommand { get; }

    /// <summary>人工纠错：刚才这一下其实是对的（记成 OK 纠错样本）。</summary>
    public ICommand MarkRoiCorrectOkCommand { get; }

    /// <summary>人工纠错：刚才这一下其实是错的（记成 NG 纠错样本）。</summary>
    public ICommand MarkRoiCorrectNgCommand { get; }

    /// <summary>打开自学账本目录（现场要看"它到底学了什么"）。</summary>
    public ICommand OpenLearningFolderCommand { get; }

    // ------------------------------------------------------------------ 开关（直接读写配置）

    private SelfLearningOptions LearningOptions => _settings.Current.SelfLearning;

    /// <summary>自学总开关。</summary>
    public bool SelfLearningEnabled
    {
        get => LearningOptions.Enabled;
        set => SetLearningOption(() => LearningOptions.Enabled = value,
                                  () => LearningOptions.Enabled, value);
    }

    /// <summary>自动收录 OK 样本。</summary>
    public bool SelfLearningAutoOkEnabled
    {
        get => LearningOptions.AutoCollectOk;
        set => SetLearningOption(() => LearningOptions.AutoCollectOk = value,
                                  () => LearningOptions.AutoCollectOk, value);
    }

    /// <summary>自动收录 NG 样本（默认关：误报学进去就洗不掉了）。</summary>
    public bool SelfLearningAutoNgEnabled
    {
        get => LearningOptions.AutoCollectNg;
        set => SetLearningOption(() => LearningOptions.AutoCollectNg = value,
                                  () => LearningOptions.AutoCollectNg, value);
    }

    /// <summary>自学置信度门槛（0~1）。低于它的帧一条都不收。</summary>
    public double SelfLearningMinConfidence
    {
        get => LearningOptions.MinConfidence;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 2),
                                        SelfLearningOptions.MinConfidenceFloor,
                                        SelfLearningOptions.MinConfidenceCeiling);
            SetLearningOption(() => LearningOptions.MinConfidence = clamped,
                               () => LearningOptions.MinConfidence, clamped);
        }
    }

    /// <summary>每个框、每类最多留多少条自学样本。</summary>
    public int SelfLearningMaxPerClass
    {
        get => LearningOptions.MaxPerClass;
        set
        {
            int clamped = Math.Clamp(value,
                                     SelfLearningOptions.MaxPerClassFloor,
                                     SelfLearningOptions.MaxPerClassCeiling);
            SetLearningOption(() => LearningOptions.MaxPerClass = clamped,
                               () => LearningOptions.MaxPerClass, clamped);
        }
    }

    public string SelfLearningMinConfidenceText => LearningOptions.MinConfidence.ToString("P0");

    public string SelfLearningToggleText => SelfLearningEnabled ? "🤖 自学：开" : "🤖 自学：关";

    /// <summary>当前配方里有没有自学样本（决定"撤销/清空/固化"能不能点）。</summary>
    public bool HasAutoSamples =>
        _roiSamples.Any(s => s.Source == SampleSource.Auto && s.RecipeId == ActiveRecipe?.Id);

    private int AutoSampleCount =>
        _roiSamples.Count(s => s.Source == SampleSource.Auto && s.RecipeId == ActiveRecipe?.Id);

    private int CorrectedSampleCount =>
        _roiSamples.Count(s => s.Source == SampleSource.Corrected && s.RecipeId == ActiveRecipe?.Id);

    // ------------------------------------------------------------------ 文案

    public string SelfLearningStatsText
    {
        get
        {
            var samples = CurrentRoiSamples();
            string head = _selfLearnStats.Summary(
                samples.Count,
                samples.Count(s => s.IsOk),
                samples.Count(s => !s.IsOk));

            return head + $"（其中自学 {AutoSampleCount} / 纠错 {CorrectedSampleCount}）";
        }
    }

    public string SelfLearningLastReasonText => _selfLearnStats.LastReason;

    /// <summary>
    /// 给现场的一句话建议（这是自学最实用的输出）：
    /// 它直接回答"我该去教哪个框"。
    /// </summary>
    public string SelfLearningAdviceText
    {
        get
        {
            if (!SelfLearningEnabled) return "自学已关闭：现在只按已有样本判，不会自己收样本";
            if (_roiLibrary.TotalSamples == 0) return "还没有样本：先教几条 OK / NG，自学才有起点";

            var worst = _uncertainStreak
                .Where(kv => kv.Value >= 30)
                .OrderByDescending(kv => kv.Value)
                .FirstOrDefault();

            if (worst.Value >= 30)
                return $"⚠️ 第 {worst.Key} 个框已经连续 {worst.Value} 帧拿不准 —— 建议对着它补 2~3 条「记 OK」「记 NG」";

            return HasAutoSamples
                ? "自学正常工作中：只在有把握时收录，重复画面只强化不新增"
                : "自学待命：等系统有把握地判对一步就会自动收录";
        }
    }

    // ------------------------------------------------------------------ 生命周期

    /// <summary>启动时调用：把上一次的账本读回来（现场要能回看"它学了什么"）。</summary>
    private void LoadSelfLearning()
    {
        try
        {
            LearningJournal.Clear();
            string dir = LearningJournalStore.DirectoryOf(_settings.Current.DataRoot);

            foreach (var entry in LearningJournalStore.LoadRecent(dir, 200))
                LearningJournal.Add(entry);

            _log.Info($"自学账本已载入：{LearningJournal.Count} 条（目录 {dir}）");
        }
        catch (Exception ex)
        {
            _log.Warn("自学账本载入失败：" + ex.Message);
        }

        RaiseSelfLearningProps();
    }

    private void SetLearningOption(Action apply, Func<bool> readBool, bool value)
    {
        bool before = readBool();
        if (before == value) return;

        apply();
        _settings.Save();

        _log.Info($"自主学习设置变更：{(value ? "开启" : "关闭")}（{nameof(SetLearningOption)}）");
        RaiseSelfLearningProps();
    }

    /// <summary>double / int 版的重载（泛型会装箱，这里图个直白）。</summary>
    private void SetLearningOption(Action apply, Func<double> read, double value)
    {
        if (Math.Abs(read() - value) < 1e-9) return;

        apply();
        _settings.Save();
        RaiseSelfLearningProps();
    }

    private void SetLearningOption(Action apply, Func<int> read, int value)
    {
        if (read() == value) return;

        apply();
        _settings.Save();
        RaiseSelfLearningProps();
    }

    private void ToggleSelfLearning()
    {
        SelfLearningEnabled = !SelfLearningEnabled;
        StatusMessage = SelfLearningEnabled
            ? "AI 自主学习已开启：有把握时自动收样本，重复画面只强化不新增"
            : "AI 自主学习已关闭：不再自动收样本（已收的仍然参与识别）";
    }

    private void RaiseSelfLearningProps()
    {
        OnPropertyChanged(nameof(SelfLearningEnabled));
        OnPropertyChanged(nameof(SelfLearningAutoOkEnabled));
        OnPropertyChanged(nameof(SelfLearningAutoNgEnabled));
        OnPropertyChanged(nameof(SelfLearningMinConfidence));
        OnPropertyChanged(nameof(SelfLearningMinConfidenceText));
        OnPropertyChanged(nameof(SelfLearningMaxPerClass));
        OnPropertyChanged(nameof(SelfLearningToggleText));
        OnPropertyChanged(nameof(SelfLearningStatsText));
        OnPropertyChanged(nameof(SelfLearningLastReasonText));
        OnPropertyChanged(nameof(SelfLearningAdviceText));
        OnPropertyChanged(nameof(HasAutoSamples));

        (UndoLastAutoSampleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearAutoSamplesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (FreezeAutoSamplesCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ 核心：逐帧学习

    /// <summary>
    /// 每帧调一次（只在"区域学习识别"开着的时候）。
    ///
    /// <para>注意它<b>只学不判</b>：判定结果由识别器给出，这里只负责把
    /// "有把握的帧"沉淀成样本。所以自学出问题也绝不会改变当前这一帧的判定，
    /// 最坏情况只是样本库多了几条，随时可以撤销。</para>
    /// </summary>
    private void LearnFromRoiObservation(int seq, string roiName, float[] feature, ActionRecognition recognition)
    {
        var options = LearningOptions;

        // 拿不准的帧：记一笔（用于"该教哪个框"的提示），但绝不入库
        if (recognition.IsOk is null)
        {
            int streak = Math.Min(99_999, _uncertainStreak.GetValueOrDefault(seq) + 1);
            _uncertainStreak[seq] = streak;

            // 每攒够 30 帧刷新一次提示文案（"第 N 个框拿不准，建议补样本"）。
            // 逐帧刷新会白白重排界面，攒着刷新既省又足够及时。
            if (streak % 30 == 0) OnPropertyChanged(nameof(SelfLearningAdviceText));

            if (options.Enabled) TrackRejected(seq, roiName, recognition, "系统判「无法判定」，这一帧不学");
            return;
        }

        // 一旦判出了结论，"拿不准"的连击就断了。
        //
        // 这一步必须发生在"要不要收录"的判断之前：哪怕这一帧因为把握不够而没被收录，
        // 也说明系统已经能判了。否则那句「连续 N 帧拿不准，建议补样本」会一直挂在界面上 ——
        // 现场明明已经把样本补齐、系统也判对了，提示还在喊"拿不准"，人就再也不信这句话了。
        bool wasStuck = _uncertainStreak.TryGetValue(seq, out int previousStreak) && previousStreak >= 30;
        _uncertainStreak[seq] = 0;
        if (wasStuck) OnPropertyChanged(nameof(SelfLearningAdviceText));

        if (!options.Enabled)
        {
            return;
        }

        // 限速：同一个框一秒最多送一次自学。
        //
        // 注意位置 —— 必须在"拿不准"那段计数之后：那句「连续 N 帧拿不准」是按帧统计的，
        // 要如实反映现场，不能被限速改掉。
        var now = DateTime.Now;
        if (_lastLearnAt.TryGetValue(seq, out var lastLearn) && now - lastLearn < SelfLearningInterval)
            return;
        _lastLearnAt[seq] = now;

        // 这一步是不是"正常完成了"（OK 样本只在步骤确实完成时才收）
        var step = Sop.Steps.FirstOrDefault(s => s.Seq == seq);
        bool stepCompleted = step is null || step.State == SopStepState.Passed;

        var working = CurrentRoiSamples();      // 当前配方的样本（元素是共享引用）
        var observation = new SelfLearningObservation
        {
            RecipeId = ActiveRecipe?.Id ?? string.Empty,
            RoiIndex = seq,
            RoiName = roiName,
            Feature = feature,
            Judge = recognition.IsOk,
            Confidence = recognition.Confidence,
            StepCompleted = stepCompleted,
            Source = SampleSource.Auto,
        };

        var result = SelfLearningEngine.Apply(working, observation, options);
        _selfLearnStats.Record(result);

        if (result.Decision == SelfLearningDecision.Rejected)
        {
            TrackRejected(seq, roiName, recognition, result.Reason);
            return;
        }

        // 只是"强化"（同一个画面反复出现）时不做任何界面/结构改动 ——
        // 静止画面每秒 25 帧都会命中，这里必须保持零负担。
        if (result.Decision == SelfLearningDecision.Reinforced)
        {
            OnPropertyChanged(nameof(SelfLearningLastReasonText));

            WriteJournal(new LearningJournalEntry
            {
                Timestamp = DateTime.Now,
                Source = result.Sample?.Source ?? SampleSource.Auto,
                Decision = SelfLearningDecision.Reinforced.ToString(),
                RoiIndex = seq,
                RoiName = roiName,
                Label = result.Sample?.Label ?? string.Empty,
                Confidence = recognition.Confidence,
                Reason = result.Reason,
            }, throttleKey: $"{seq}|reinforce", throttleSeconds: 300);

            return;
        }

        // 新收录 / 淘汰：整份列表同步回主库
        var recipeId = ActiveRecipe?.Id;
        if (!string.IsNullOrEmpty(recipeId))
        {
            _roiSamples.RemoveAll(s => s.RecipeId == recipeId);
            _roiSamples.AddRange(working);
        }

        RebuildRoiLibrary();
        SaveRoiSamplesThrottled();
        RefreshRoiTargetCounts();

        // 样本条只显示"当前选中那个框"的样本，所以只有学到的正好是它时才重建。
        // 重建这个列表会让 WPF 把每个缩略图重新从磁盘解码一遍（样本最多 80 张），
        // 没必要的重建纯属白烧 UI 线程。
        if (_selectedRoiTarget?.Index == seq) RefreshSelectedRoiSamples();

        _log.Info($"自主学习：{result.Reason}");

        WriteJournal(new LearningJournalEntry
        {
            Timestamp = DateTime.Now,
            Source = result.Sample?.Source ?? SampleSource.Auto,
            Decision = result.Decision.ToString(),
            RoiIndex = seq,
            RoiName = roiName,
            Label = result.Sample?.Label ?? (recognition.IsOk == true ? "OK" : "NG"),
            Confidence = recognition.Confidence,
            Reason = result.Reason,
        });

        RaiseSelfLearningProps();
    }

    /// <summary>
    /// 被拒绝也记一笔 —— 但按"框 + 理由"节流 60 秒。
    /// 现场调阈值、判断"是不是样本太少"全靠这些记录。
    /// </summary>
    private void TrackRejected(int seq, string roiName, ActionRecognition recognition, string reason)
    {
        WriteJournal(new LearningJournalEntry
        {
            Timestamp = DateTime.Now,
            Source = SampleSource.Auto,
            Decision = SelfLearningDecision.Rejected.ToString(),
            RoiIndex = seq,
            RoiName = roiName,
            Label = recognition.IsOk switch { true => "OK", false => "NG", _ => "无法判定" },
            Confidence = recognition.Confidence,
            Reason = reason,
        }, throttleKey: $"{seq}|{reason}", throttleSeconds: 60);
    }

    private void WriteJournal(LearningJournalEntry entry, string? throttleKey = null, int throttleSeconds = 0)
    {
        if (throttleKey is not null && throttleSeconds > 0)
        {
            if (_journalThrottle.TryGetValue(throttleKey, out var last) &&
                (DateTime.Now - last).TotalSeconds < throttleSeconds)
                return;

            _journalThrottle[throttleKey] = DateTime.Now;
        }

        LearningJournal.Insert(0, entry);
        while (LearningJournal.Count > 200) LearningJournal.RemoveAt(LearningJournal.Count - 1);

        _selfLearnStats.LastReason = entry.Reason;
        OnPropertyChanged(nameof(SelfLearningLastReasonText));

        string dir = LearningJournalStore.DirectoryOf(_settings.Current.DataRoot);
        if (!LearningJournalStore.Append(entry, dir))
            _log.Warn("自学账本写入失败（不影响识别与样本，请检查磁盘权限）");
    }

    // ------------------------------------------------------------------ 回滚 / 固化

    private void UndoLastAutoSample()
    {
        var target = _selectedRoiTarget?.Index;

        var working = CurrentRoiSamples();
        var removed = SelfLearningEngine.UndoLastAuto(working, target)
                      ?? SelfLearningEngine.UndoLastAuto(working);

        if (removed is null)
        {
            StatusMessage = "没有可撤销的自学样本（人工教的样本不会被撤销）";
            return;
        }

        // CurrentRoiSamples() 是副本，撤销要把结果同步回主库
        var recipeId = ActiveRecipe?.Id;
        if (!string.IsNullOrEmpty(recipeId))
        {
            _roiSamples.RemoveAll(s => s.RecipeId == recipeId);
            _roiSamples.AddRange(working);
        }

        RebuildRoiLibrary();
        SaveRoiSamples();
        RefreshRoiTargetCounts();
        RefreshSelectedRoiSamples();
        RaiseSelfLearningProps();

        WriteJournal(new LearningJournalEntry
        {
            Timestamp = DateTime.Now,
            Source = SampleSource.Auto,
            Decision = "Undo",
            RoiIndex = removed.RoiIndex,
            RoiName = removed.RoiName,
            Label = removed.Label,
            Reason = $"撤销自学样本：第 {removed.RoiIndex} 个框的 {removed.Label}（{removed.Display}）",
        });

        StatusMessage = $"已撤销第 {removed.RoiIndex} 个框刚学到的 {removed.Label} 样本";
        _log.Warn(StatusMessage);
    }

    private void ClearAutoSamples()
    {
        var recipeId = ActiveRecipe?.Id;
        if (string.IsNullOrEmpty(recipeId)) return;

        var working = CurrentRoiSamples();
        int removed = SelfLearningEngine.ClearAutoSamples(working);
        if (removed == 0)
        {
            StatusMessage = "当前配方没有自学样本（人工 / 纠错样本不会被清掉）";
            return;
        }

        _roiSamples.RemoveAll(s => s.RecipeId == recipeId);
        _roiSamples.AddRange(working);

        RebuildRoiLibrary();
        SaveRoiSamples();
        RefreshRoiTargetCounts();
        RefreshSelectedRoiSamples();
        RaiseSelfLearningProps();

        WriteJournal(new LearningJournalEntry
        {
            Decision = "Cleared",
            Reason = $"清空自学样本 {removed} 条（人工 / 纠错样本保留）",
        });

        StatusMessage = $"已清空 {removed} 条自学样本，人工教的样本都还在";
        _log.Warn(StatusMessage);
    }

    private void FreezeAutoSamples()
    {
        var recipeId = ActiveRecipe?.Id;
        if (string.IsNullOrEmpty(recipeId)) return;

        var working = CurrentRoiSamples();
        int frozen = SelfLearningEngine.FreezeAutoSamples(working);
        if (frozen == 0)
        {
            StatusMessage = "当前没有自学样本可固化";
            return;
        }

        _roiSamples.RemoveAll(s => s.RecipeId == recipeId);
        _roiSamples.AddRange(working);

        RebuildRoiLibrary();
        SaveRoiSamples();
        RefreshRoiTargetCounts();
        RefreshSelectedRoiSamples();
        RaiseSelfLearningProps();

        WriteJournal(new LearningJournalEntry
        {
            Decision = "Frozen",
            Reason = $"固化自学样本 {frozen} 条（升级为人工样本，之后不再被自动淘汰）",
        });

        StatusMessage = $"已固化 {frozen} 条自学样本：它们现在是人工样本，不会被自动淘汰";
        _log.Info(StatusMessage);
    }

    private void OpenLearningFolder()
    {
        try
        {
            string dir = LearningJournalStore.DirectoryOf(_settings.Current.DataRoot);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            StatusMessage = "已打开自学账本目录：" + dir;
        }
        catch (Exception ex)
        {
            StatusMessage = "打开自学账本目录失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 落盘节流

    /// <summary>
    /// 样本落盘节流：5 秒最多写一次。
    ///
    /// <para>自学是逐帧判断的，一秒钟可能新收好几条样本。每条都整份重写 JSON，
    /// 样本攒到几百条之后就是每秒几 MB 的写盘量 —— 工位机的硬盘顶不住。
    /// 所以这里攒着写，停止识别 / 退出时再补一次（<see cref="FlushRoiSamples"/>）。</para>
    /// </summary>
    private void SaveRoiSamplesThrottled()
    {
        _roiSamplesDirty = true;
        if ((DateTime.Now - _lastRoiSampleSave).TotalSeconds < 5) return;
        SaveRoiSamples();
    }

    /// <summary>把还没落盘的自学样本立刻写下去（停止识别、关窗口时调）。</summary>
    private void FlushRoiSamples()
    {
        if (!_roiSamplesDirty) return;
        SaveRoiSamples();
    }
}
