using VisionForge.Core.Models;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 首页最底下那一行"一眼看全场"的状态条：
/// 工单号、产品型号、SOP 版本、步骤进度、合规率、节拍时间、工位状态、今日告警、今日漏步。
///
/// <para><b>为什么要有这一条：</b>上面几块面板各讲一件事（识别、计时、判定），
/// 但班组长走过来只想知道"今天这条线行不行" —— 那就要把最关键的九个数字放在同一行里，
/// 不点任何按钮、不切任何页面就能看到。</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>本件开始时刻（算节拍用）。</summary>
    private DateTime _pieceStartedAt = DateTime.Now;

    /// <summary>最近几件的节拍，用来算平均（只看最后一件容易被偶然情况带偏）。</summary>
    private readonly List<double> _recentCycleSec = new();

    // ------------------------------------------------------------------
    // 九个字段
    // ------------------------------------------------------------------

    /// <summary>工单号：按批次自动生成（一个批次一个工单）。</summary>
    public string WorkOrderText =>
        string.IsNullOrWhiteSpace(BatchNo) ? "—" : $"WO-{BatchNo}";

    public string ModelText => string.IsNullOrWhiteSpace(ProductModel) ? "—" : ProductModel;

    /// <summary>SOP 版本：来自 sop.json（换工艺文件就换版本）。</summary>
    public string SopVersionText => "SOP-" + (Sop.Definition?.Version ?? "1.0.0");

    /// <summary>步骤进度：已完成 / 总步骤（总步骤 = 画面上的框数）。</summary>
    public string StepProgressText => $"{Sop.PassedCount} / {Sop.TotalSteps}";

    /// <summary>合规率（按工序计）：通过数 / (通过+不合格)。</summary>
    public string ComplianceRateText
    {
        get
        {
            int total = _session.OkCount + _session.NgCount;
            return total == 0 ? "—" : $"{_session.PassRate:F1}%";
        }
    }

    /// <summary>最近一件的节拍时间（整件做完用了多久）。</summary>
    public string CycleTimeText =>
        _recentCycleSec.Count == 0 ? "0s" : $"{_recentCycleSec[^1]:F1}s";

    public string CycleTimeHint
    {
        get
        {
            if (_recentCycleSec.Count == 0) return "还没有完成整件";
            double avg = _recentCycleSec.Average();
            return $"最近 {_recentCycleSec.Count} 件平均 {avg:F1}s";
        }
    }

    /// <summary>工位状态：运行中 / 待机 / 已锁线 / 未连接。</summary>
    public string StationStateText
    {
        get
        {
            if (_camera?.IsConnected != true) return "未连接";
            if (Sop.IsLocked) return "已锁线";
            if (IsRoiLearning || IsProcessRunning || IsRecognizing) return "运行中";
            return "待机";
        }
    }

    /// <summary>状态对应的颜色键（界面按它上色）。</summary>
    public string StationStateKey
    {
        get
        {
            if (_camera?.IsConnected != true) return "pending";
            if (Sop.IsLocked) return "ng";
            if (IsRoiLearning || IsProcessRunning || IsRecognizing) return "ok";
            return "active";
        }
    }

    /// <summary>今日告警：今天落库的不合格记录条数（重启也算得出来，因为查的是历史库）。</summary>
    public string TodayAlarmCountText =>
        HistoryRecords.Count(r => r.Timestamp.Date == DateTime.Today &&
                                  r.Verdict == InspectionVerdict.Fail).ToString();

    /// <summary>今日漏步：今天判定说明里带"跳步"的记录条数。</summary>
    public string TodaySkippedCountText =>
        HistoryRecords.Count(r => r.Timestamp.Date == DateTime.Today &&
                                  r.JudgementReason.Contains("跳步")).ToString();

    /// <summary>
    /// 整件做完了：算这一件的节拍，然后复位计时基准。
    /// 由 <c>FeedProcessToSop</c> 在流程走到"整件完成"时调用。
    /// </summary>
    private void NotePieceCompleted(bool isOk, string reason)
    {
        double cycleSec = Math.Max(0, (DateTime.Now - _pieceStartedAt).TotalSeconds);

        // 先把这一件推给 MES（这里读的还是"本件的开始时刻"），再把计时基准翻到下一件
        QueuePieceToMes(isOk, reason);
        _pieceStartedAt = DateTime.Now;

        _recentCycleSec.Add(cycleSec);
        while (_recentCycleSec.Count > 5) _recentCycleSec.RemoveAt(0);

        RaiseStatusBarProps();
    }

    /// <summary>状态条上的派生值刷新（步骤一变就要刷）。</summary>
    private void RaiseStatusBarProps()
    {
        OnPropertyChanged(nameof(WorkOrderText));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(SopVersionText));
        OnPropertyChanged(nameof(StepProgressText));
        OnPropertyChanged(nameof(ComplianceRateText));
        OnPropertyChanged(nameof(CycleTimeText));
        OnPropertyChanged(nameof(CycleTimeHint));
        OnPropertyChanged(nameof(StationStateText));
        OnPropertyChanged(nameof(StationStateKey));
        OnPropertyChanged(nameof(TodayAlarmCountText));
        OnPropertyChanged(nameof(TodaySkippedCountText));
    }
}
