using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Infrastructure.Config;
using VisionForge.Infrastructure.Storage;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 动作计时：每个节点什么时候开始、什么时候结束、花了多久、比上次快还是慢。
///
/// <para><b>数据从哪来：</b>不用额外接线 —— 每一步的"开始时刻"就是它变成当前步骤的时刻
/// （<see cref="SopStep.EnteredAt"/>），"结束时刻"就是这一步判定出来的时刻。
/// 所以区域学习、过程监测、保底视觉三条路都自动带计时。</para>
///
/// <para><b>存哪：</b>一天一个文件 <c>data\timings\yyyy-MM-dd.jsonl</c>，追加写、断电最多丢一行。</para>
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<int, double> _lastDurationMs = new();        // 工序号 → 上一次用时
    private readonly Dictionary<int, ActionTimingRecord> _lastRecordOfStep = new();

    private DispatcherTimer? _timingTimer;
    private string _currentPieceId = string.Empty;
    private int _pieceSerial;

    private string _currentActionElapsedText = "—";
    private string _currentActionBaselineText = "—";
    private string _lastActionTimeText = "—";
    private string _lastActionPaceText = "尚无记录";
    private string _lastActionPaceStateKey = "pending";
    private string _pieceText = "—";

    // ---- 回看窗口用 ----
    private DateTime? _timingFrom = DateTime.Today;
    private DateTime? _timingTo = DateTime.Today;
    private int _timingStepFilterIndex;
    private bool _timingOnlySlow;
    private string _timingSummaryText = "点「查询」载入动作时间记录";

    /// <summary>查出来的计时记录（回看窗口的表格 + 趋势图都用它）。</summary>
    public ObservableCollection<ActionTimingRecord> TimingRecords { get; } = new();

    /// <summary>查询计时记录（回看窗口）。</summary>
    public ICommand QueryTimingCommand { get; }

    /// <summary>把查询结果导出成 CSV（给工艺/IE 用 Excel 分析）。</summary>
    public ICommand ExportTimingCsvCommand { get; }

    /// <summary>工序筛选项（"全部" + 每道工序）。</summary>
    public ObservableCollection<string> TimingStepOptions { get; } = new();

    public DateTime? TimingFrom
    {
        get => _timingFrom;
        set => SetProperty(ref _timingFrom, value);
    }

    public DateTime? TimingTo
    {
        get => _timingTo;
        set => SetProperty(ref _timingTo, value);
    }

    public int TimingStepFilterIndex
    {
        get => _timingStepFilterIndex;
        set => SetProperty(ref _timingStepFilterIndex, value);
    }

    /// <summary>只看"明显慢"的那些（找问题节点用）。</summary>
    public bool TimingOnlySlow
    {
        get => _timingOnlySlow;
        set => SetProperty(ref _timingOnlySlow, value);
    }

    public string TimingSummaryText
    {
        get => _timingSummaryText;
        private set => SetProperty(ref _timingSummaryText, value);
    }

    // ==================================================================
    // 界面上实时显示的时间
    // ==================================================================
    /// <summary>当前这一步已经用了多久（每 200ms 刷一次）。</summary>
    public string CurrentActionElapsedText
    {
        get => _currentActionElapsedText;
        private set => SetProperty(ref _currentActionElapsedText, value);
    }

    /// <summary>当前这一步的基准（上一次用时或标准工时），用于"还差多少"。</summary>
    public string CurrentActionBaselineText
    {
        get => _currentActionBaselineText;
        private set => SetProperty(ref _currentActionBaselineText, value);
    }

    /// <summary>上一次同一个动作用了多久。</summary>
    public string LastActionTimeText
    {
        get => _lastActionTimeText;
        private set => SetProperty(ref _lastActionTimeText, value);
    }

    /// <summary>"比上次慢了 0.6s（13%）"这句话。</summary>
    public string LastActionPaceText
    {
        get => _lastActionPaceText;
        private set => SetProperty(ref _lastActionPaceText, value);
    }

    public string LastActionPaceStateKey
    {
        get => _lastActionPaceStateKey;
        private set => SetProperty(ref _lastActionPaceStateKey, value);
    }

    /// <summary>当前件号（一件产品的所有节点共用）。</summary>
    public string PieceText
    {
        get => _pieceText;
        private set => SetProperty(ref _pieceText, value);
    }

    /// <summary>计时是否开启（设置窗口里的开关）。</summary>
    public bool IsTimingEnabled => _settings.Current.Timing?.Enabled == true;

    private void StartTimingTimer()
    {
        if (_timingTimer is not null) return;

        _timingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timingTimer.Tick += (_, _) => RefreshLiveTiming();
        _timingTimer.Start();
    }

    /// <summary>每 200ms 刷一次"本步已用时"——计时必须看起来是活的，否则现场以为没在计。</summary>
    private void RefreshLiveTiming()
    {
        // 顺带刷录像进度：录像工具条上的时间/进度也要动
        RefreshVideoProgress();

        // 没在监测时不要跳秒：否则软件一打开"本步已用时"就自己往上走，
        // 现场会以为计时坏了（它计的是"人在做这一步"的时间，不是开机时间）。
        bool monitoring = _camera?.IsConnected == true || IsProcessRunning || IsRoiLearning || IsRecognizing;
        if (!monitoring)
        {
            CurrentActionElapsedText = "—";
            CurrentActionBaselineText = "未在监测（先「启动监测」）";
            return;
        }

        var step = Sop.CurrentStep;
        if (step is null)
        {
            CurrentActionElapsedText = "—";
            CurrentActionBaselineText = "—";
            return;
        }

        double elapsed = Math.Max(0, (DateTime.Now - step.EnteredAt).TotalSeconds);
        CurrentActionElapsedText = $"{elapsed:F1}s";

        double? baseline = BaselineMsOf(step.Seq);
        if (baseline is null or <= 0)
        {
            CurrentActionBaselineText = "基准未设（可在「⏱ 时间设置」里填标准工时）";
            return;
        }

        double baseSec = baseline.Value / 1000.0;
        double diff = elapsed - baseSec;
        CurrentActionBaselineText = diff >= 0
            ? $"基准 {baseSec:F1}s · 已超 {diff:F1}s"
            : $"基准 {baseSec:F1}s · 还差 {-diff:F1}s";
    }

    /// <summary>某道工序的对比基准：优先上一次用时，其次标准工时。</summary>
    private double? BaselineMsOf(int stepSeq)
    {
        if (_lastDurationMs.TryGetValue(stepSeq, out double last) && last > 0) return last;

        return _settings.Current.Timing?.FindStandardMs(stepSeq);
    }

    // ==================================================================
    // 记录一条
    // ==================================================================
    /// <summary>
    /// 一道工序判定出来时记一条。由 <c>FeedProcessToSop</c> 调用 ——
    /// 三条识别通路（区域学习 / 过程监测 / 保底视觉）都汇到那里，所以都自动带计时。
    /// </summary>
    private void RecordStepTiming(SopStep step, bool isOk, string note)
    {
        var options = _settings.Current.Timing;
        if (options is null || !options.Enabled) return;
        if (!isOk && !options.RecordNg) return;

        try
        {
            var now = DateTime.Now;
            double durationMs = Math.Max(0, (now - step.EnteredAt).TotalMilliseconds);

            double? previous = _lastDurationMs.TryGetValue(step.Seq, out double prev) && prev > 0 ? prev : null;

            var record = new ActionTimingRecord
            {
                PieceId = EnsurePieceId(),
                RecipeId = ActiveRecipe?.Id ?? string.Empty,
                RecipeName = ActiveRecipe?.Name ?? string.Empty,
                BatchNo = BatchNo,
                ProductModel = ProductModel,
                Operator = _settings.Current.OperatorName,
                StepSeq = step.Seq,
                StepName = step.Name,
                StartedAt = step.EnteredAt,
                FinishedAt = now,
                DurationMs = durationMs,
                PreviousDurationMs = previous,
                StandardMs = options.FindStandardMs(step.Seq),
                IsOk = isOk,
                Note = note,
            };

            _lastDurationMs[step.Seq] = durationMs;
            _lastRecordOfStep[step.Seq] = record;

            // 本件的工序明细（整件做完时随件数据一起上传 MES / 落盘）
            _currentPieceSteps.RemoveAll(s => s.Seq == step.Seq);
            _currentPieceSteps.Add(new PieceStepReport
            {
                Seq = step.Seq,
                Name = step.Name,
                StartedAt = step.EnteredAt,
                FinishedAt = now,
                DurationSec = Math.Round(durationMs / 1000.0, 2),
                StandardSec = record.StandardMs is > 0 ? Math.Round(record.StandardMs.Value / 1000.0, 2) : null,
                IsOk = isOk,
            });

            // 界面上"上次用时 / 快慢"
            LastActionTimeText = record.DurationText;
            LastActionPaceText = record.PaceText;
            LastActionPaceStateKey = record.PaceStateKey;

            // 落盘放后台：这里在 UI 线程上，同步写盘会卡界面
            string dir = ActionTimingStore.DirectoryOf(_settings.Current.DataRoot);
            _ = Task.Run(() =>
            {
                if (!ActionTimingStore.Append(record, dir))
                    _log.Warn("动作计时写盘失败（不影响生产）：" + record.Display);
            });

            _log.Info("动作计时：" + record.Display);
        }
        catch (Exception ex)
        {
            // 计时是"附加价值"，绝不能因为它把生产流程搞崩
            _log.Warn("动作计时记录失败：" + ex.Message);
        }
    }

    /// <summary>取当前件号；没有就开一件新的。</summary>
    private string EnsurePieceId()
    {
        if (!string.IsNullOrEmpty(_currentPieceId)) return _currentPieceId;

        _pieceSerial++;
        string batch = string.IsNullOrWhiteSpace(BatchNo) ? DateTime.Now.ToString("yyyyMMdd") : BatchNo;
        _currentPieceId = $"{batch}-{_pieceSerial:D4}";
        PieceText = _currentPieceId;
        return _currentPieceId;
    }

    /// <summary>一件做完 / 手动复位：下一件换新件号。</summary>
    private void StartNewPieceTiming()
    {
        _currentPieceId = string.Empty;
        PieceText = EnsurePieceId();
        _pieceStartedAt = DateTime.Now;      // 节拍时间从这一刻算起
        _currentPieceSteps.Clear();          // 新的一件：工序明细从零开始
        CurrentActionElapsedText = "—";
        CurrentActionBaselineText = "—";
        RaiseStatusBarProps();
    }

    // ==================================================================
    // 回看
    // ==================================================================
    private async Task QueryTimingAsync()
    {
        var from = TimingFrom ?? DateTime.Today;
        var to = TimingTo ?? DateTime.Today;
        if (to < from) (from, to) = (to, from);

        string dir = ActionTimingStore.DirectoryOf(_settings.Current.DataRoot);

        List<ActionTimingRecord> all;
        try
        {
            all = await Task.Run(() => ActionTimingStore.Load(dir, from, to));
        }
        catch (Exception ex)
        {
            TimingSummaryText = "读取失败：" + ex.Message;
            return;
        }

        // 工序筛选：第 0 项是"全部"
        int stepFilter = TimingStepFilterIndex;   // 1 = 第 1 步 …
        var filtered = all
            .Where(r => stepFilter <= 0 || r.StepSeq == stepFilter)
            .Where(r => !TimingOnlySlow || r.PaceStateKey == "ng")
            .OrderByDescending(r => r.FinishedAt)
            .ToList();

        TimingRecords.Clear();
        foreach (var r in filtered) TimingRecords.Add(r);

        RefreshTimingStepOptions();

        if (all.Count == 0)
        {
            TimingSummaryText = $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}：没有计时记录" +
                                "（先「启动监测」，再做几个动作就有了）";
            return;
        }

        double avg = all.Average(r => r.DurationMs) / 1000.0;
        var slowest = all.OrderByDescending(r => r.DurationMs).First();
        int slowCount = all.Count(r => r.PaceStateKey == "ng");

        TimingSummaryText =
            $"共 {all.Count} 条（显示 {filtered.Count} 条）· 平均 {avg:F1}s · " +
            $"最慢 {slowest.StepName} {slowest.DurationSec:F1}s · 明显偏慢 {slowCount} 次" +
            (TimingOnlySlow ? "（当前只看偏慢）" : "");
    }

    /// <summary>下拉里的工序列表按"当前配方有几个框"来生成。</summary>
    private void RefreshTimingStepOptions()
    {
        int keep = TimingStepFilterIndex;

        TimingStepOptions.Clear();
        TimingStepOptions.Add("全部工序");

        var rois = ActiveRecipe?.Rois.Where(r => r.Enabled).ToList() ?? new List<RoiRegion>();
        for (int i = 0; i < rois.Count; i++)
            TimingStepOptions.Add($"第 {i + 1} 步 · {rois[i].Name}");

        TimingStepFilterIndex = keep < TimingStepOptions.Count ? keep : 0;
    }

    private void ExportTimingCsv()
    {
        if (TimingRecords.Count == 0)
        {
            TimingSummaryText = "没有可导出的记录：先查询出数据";
            return;
        }

        try
        {
            string dir = _settings.Current.DataRoot;
            string path = Path.Combine(dir, $"动作时间-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            ActionTimingStore.ExportCsv(TimingRecords, path);
            TimingSummaryText = "已导出：" + path;
            _log.Info("动作时间已导出 CSV：" + path);
        }
        catch (Exception ex)
        {
            TimingSummaryText = "导出失败：" + ex.Message;
        }
    }

    // ==================================================================
    // 时间设置窗口
    // ==================================================================
    /// <summary>按"当前配方有几个框"生成设置项（框 = 工序 = 要计时的节点）。</summary>
    public TimingSettingsModel CreateTimingSettings()
    {
        var options = _settings.Current.Timing ??= new TimingOptions();
        var rois = ActiveRecipe?.Rois.Where(r => r.Enabled).ToList() ?? new List<RoiRegion>();

        if (rois.Count == 0)
        {
            // 还没画框：把配置里已有的标准工时列出来，别让人以为数据丢了
            var existing = options.Standards
                .OrderBy(s => s.StepSeq)
                .Select(s => new TimingStandardItem(s.StepSeq, s.StepName, s.StandardSec))
                .ToList();
            return new TimingSettingsModel(options, existing, ActionTimingStore.DirectoryOf(_settings.Current.DataRoot));
        }

        var items = new List<TimingStandardItem>();
        for (int i = 0; i < rois.Count; i++)
        {
            int seq = i + 1;
            var std = options.Ensure(seq, rois[i].Name);
            items.Add(new TimingStandardItem(seq, rois[i].Name, std.StandardSec));
        }

        return new TimingSettingsModel(options, items, ActionTimingStore.DirectoryOf(_settings.Current.DataRoot));
    }

    /// <summary>保存时间设置。返回 false 表示校验没过（窗口不关）。</summary>
    public bool ApplyTimingSettings(TimingSettingsModel model)
    {
        if (model is null || !model.Validate()) return false;

        var options = _settings.Current.Timing ??= new TimingOptions();
        options.Enabled = model.Enabled;
        options.RecordNg = model.RecordNg;
        options.SlowAlertPercent = model.SlowAlertPercent;

        foreach (var item in model.Items)
        {
            var std = options.Ensure(item.StepSeq, item.StepName);
            std.StandardSec = item.StandardSec;
        }

        _settings.Save();

        OnPropertyChanged(nameof(IsTimingEnabled));
        RefreshLiveTiming();
        RefreshTimingStepOptions();

        StatusMessage = model.Enabled
            ? $"时间设置已保存：{model.Items.Count} 道工序的标准工时（数据存到 {model.DataFolder}）"
            : "时间设置已保存：动作计时已关闭";
        _log.Info("时间设置已保存：" + StatusMessage);
        return true;
    }
}
