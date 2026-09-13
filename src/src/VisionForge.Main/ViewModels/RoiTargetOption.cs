using System.Windows.Media.Imaging;
using VisionForge.Common.Mvvm;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 「区域学习」里可选的框（一个框 = 一道工序的作业区）。
///
/// <para>下拉里直接写清是第几个框、教过几条样本 ——
/// 现场要能一眼看出"这个框我到底教了没有"，而不是点进去才发现是空的。</para>
/// </summary>
public sealed class RoiTargetOption : ObservableObject
{
    private string _name;
    private int _okCount;
    private int _ngCount;
    private string _stateKey = "pending";

    public RoiTargetOption(int index, string name)
    {
        Index = index;
        _name = string.IsNullOrWhiteSpace(name) ? $"区域{index}" : name;
    }

    /// <summary>第几个框（从 1 开始）。</summary>
    public int Index { get; }

    public string Name
    {
        get => _name;
        private set
        {
            if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(Label));
        }
    }

    public string Label => $"{Index}. {Name}";

    public int OkCount
    {
        get => _okCount;
        private set
        {
            if (SetProperty(ref _okCount, value)) OnPropertyChanged(nameof(SampleText));
        }
    }

    public int NgCount
    {
        get => _ngCount;
        private set
        {
            if (SetProperty(ref _ngCount, value)) OnPropertyChanged(nameof(SampleText));
        }
    }

    public bool HasSamples => _okCount + _ngCount > 0;

    public string SampleText => HasSamples ? $"OK {_okCount} / NG {_ngCount}" : "未教";

    /// <summary>
    /// 这个框现在的判定状态：ok（有东西）/ ng（东西被拿走）/ active（拿不准）/ pending（还没判或没教）。
    /// 步骤条上的小方框按它上色 —— 画一个框就多一格，格子跟着识别结果变绿变红。
    /// </summary>
    public string StateKey
    {
        get => _stateKey;
        private set
        {
            if (SetProperty(ref _stateKey, value)) OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => _stateKey switch
    {
        // 颜色表达的是"这一步做到没有"，所以文案也用它 ——
        // 拿走型工序里"框空了"才是完成，写 OK/NG 反而看不懂。
        "ok" => "完成",
        "ng" => "未完成",
        "active" => "拿不准",
        _ => HasSamples ? "待识别" : "未教",
    };

    private bool _doneWhenAbsent;

    /// <summary>这个框的完成条件：true = 东西被拿走才算完成（取件类工序）。</summary>
    public bool DoneWhenAbsent
    {
        get => _doneWhenAbsent;
        private set
        {
            if (!SetProperty(ref _doneWhenAbsent, value)) return;
            OnPropertyChanged(nameof(DoneModeText));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(TileHint));
        }
    }

    public string DoneModeText => _doneWhenAbsent ? "完成条件：被拿走" : "完成条件：有东西";

    internal void SetDoneMode(bool absentMeansDone) => DoneWhenAbsent = absentMeansDone;

    internal void SetStateKey(string key)
    {
        if (!string.Equals(_stateKey, key, StringComparison.Ordinal)) StateKey = key;
    }

    // ------------------------------------------------------------------
    // 证据帧：这个框最近一次判定时画面长什么样
    // ------------------------------------------------------------------
    private BitmapSource? _evidenceImage;
    private string _evidenceTimeText = string.Empty;

    /// <summary>
    /// 最近一张证据帧（判定那一刻的整幅画面）。
    /// 直接当格子背景显示 —— 参考界面把证据铺在最下面一排，我们把它嵌进格子里，
    /// 不额外占地方，而且"第几格 = 第几个框"比一排独立的卡片更好对。
    /// </summary>
    public BitmapSource? EvidenceImage
    {
        get => _evidenceImage;
        private set
        {
            if (SetProperty(ref _evidenceImage, value)) OnPropertyChanged(nameof(HasEvidence));
        }
    }

    public bool HasEvidence => _evidenceImage is not null;

    /// <summary>证据帧的时间（鼠标悬停看）。</summary>
    public string EvidenceTimeText
    {
        get => _evidenceTimeText;
        private set => SetProperty(ref _evidenceTimeText, value);
    }

    public string EvidenceHint => HasEvidence
        ? $"证据帧 {EvidenceTimeText}（判定那一刻的画面）"
        : "还没有证据帧";

    /// <summary>
    /// 格子上的一句话提示（鼠标悬停显示）。
    ///
    /// <para>刻意在 ViewModel 里拼好整句，而不是在 XAML 里"绑定 + 文字"混着写：
    /// XAML 的属性值里只要出现 <c>{Binding …}</c>，后面就不能再接普通文字
    /// （编译期直接报 MC3044 —— 这个坑踩过一次）。</para>
    /// </summary>
    public string TileHint =>
        $"{DoneModeText}　{EvidenceHint}　左键：选中去教 OK / NG；右键：改完成条件 / 名称 / 删除";

    internal void SetEvidence(BitmapSource? image, DateTime time)
    {
        EvidenceImage = image;
        EvidenceTimeText = time.ToString("HH:mm:ss");
        OnPropertyChanged(nameof(EvidenceHint));
    }

    internal void SetCounts(int okCount, int ngCount)
    {
        OkCount = okCount;
        NgCount = ngCount;
        OnPropertyChanged(nameof(HasSamples));
        OnPropertyChanged(nameof(StateText));   // "未教 / 待识别" 取决于教没教过
    }

    internal void Rename(string name)
        => Name = string.IsNullOrWhiteSpace(name) ? $"区域{Index}" : name;
}

/// <summary>
/// 「动作置信度 Top3」里的一行：这一次各个动作谁最像。
///
/// <para>单个框的 OK/NG 是"结论"，置信度是"有多确定"。
/// 排名能让现场一眼看出"系统现在认为人在做哪一步"——</para>
/// </summary>
public sealed class ActionRankItem
{
    public ActionRankItem(int rank, string name, double confidence)
    {
        Rank = rank;
        Name = string.IsNullOrWhiteSpace(name) ? $"区域{rank}" : name;
        Confidence = Math.Clamp(confidence, 0, 1);
    }

    /// <summary>第几名（1 最像）。</summary>
    public int Rank { get; }

    public string Name { get; }

    public double Confidence { get; }

    public string ConfidenceText => Confidence <= 0 ? "—" : $"{Confidence:P0}";
}
