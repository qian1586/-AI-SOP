namespace VisionForge.Core.Services;

/// <summary>
/// 样本的公共形态。
///
/// <para>有了它，"最近邻识别器"既能识别整幅画面的样本（<see cref="ActionSample"/>），
/// 也能识别某一个框里的样本（<see cref="RoiSample"/>）——
/// 两套学习共用同一份识别代码，行为不会跑偏，也不用写第二遍。</para>
/// </summary>
public interface ISample
{
    bool IsOk { get; }
    float[] Feature { get; }
    DateTime Timestamp { get; }
    string Label { get; }

    /// <summary>给人看的一行字（识别理由里会引用它）。</summary>
    string Display { get; }
}

/// <summary>
/// 在某个框上教出来的样本：这一刻"框里的样子" + 人工标的 OK/NG。
///
/// <para><b>现场用法（和基恩士抓取设定一个思路）：</b>
/// 框住零件该在的位置 → 零件放好时点「记 OK」→ 零件被拿走（空框）时点「记 NG」→
/// 各教几条，之后系统自己判"框里有 / 没有"，框变绿还是变红一眼就看到。</para>
/// </summary>
public sealed class RoiSample : ISample
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>属于哪个配方。</summary>
    public string RecipeId { get; set; } = string.Empty;

    /// <summary>第几个框（从 1 开始，与"第 N 步 = 第 N 个框"同序）。</summary>
    public int RoiIndex { get; set; } = 1;

    /// <summary>教的时候这个框叫什么名字（框改名后旧样本仍能看出是哪个框教的）。</summary>
    public string RoiName { get; set; } = string.Empty;

    public bool IsOk { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>教这一条时的抓帧图（data\roi-samples\ 下）。</summary>
    public string? ImagePath { get; set; }

    /// <summary>框内区域的特征向量。</summary>
    public float[] Feature { get; set; } = Array.Empty<float>();

    /// <summary>
    /// 这条样本是谁放进来的：Manual（人工教）/ Auto（系统自学）/ Corrected（人工纠错）。
    ///
    /// <para>老版本存的样本没有这个字段，反序列化时就是默认值 Manual ——
    /// 语义正好也对："以前能存在的样本都是人教的"。</para>
    /// </summary>
    public string Source { get; set; } = SampleSource.Manual;

    /// <summary>这条样本被"再次看到"过多少次（同一个情况反复出现就累加，不重复存图）。</summary>
    public int Hits { get; set; } = 1;

    /// <summary>收录它时系统自己的把握（人工教的就是 1）。</summary>
    public double Confidence { get; set; } = 1;

    /// <summary>给人看的一句话：为什么会有这条样本。</summary>
    public string Note { get; set; } = string.Empty;

    public string Label => IsOk ? "OK" : "NG";

    /// <summary>来源的短标签（界面缩略图下面显示）。</summary>
    public string SourceText => SampleSource.Describe(Source);

    /// <summary>自学的样本在界面上要能一眼认出来（不然人会以为是自己教的）。</summary>
    public bool IsAutoLearned => Source == SampleSource.Auto;

    public string Display => $"{Label} · {SourceText} · {Timestamp:HH:mm:ss}";
}

/// <summary>
/// 一个框的学习情况：样本 + 识别器 + 计数。
/// </summary>
public sealed class RoiTargetLearner
{
    private ActionSampleClassifier _classifier;

    public RoiTargetLearner(int roiIndex, string roiName,
                            IEnumerable<RoiSample> samples, double minSimilarity)
    {
        RoiIndex = roiIndex;
        RoiName = roiName ?? string.Empty;
        Samples = new List<RoiSample>(samples ?? Array.Empty<RoiSample>());
        _classifier = new ActionSampleClassifier(Samples) { MinSimilarity = minSimilarity };
    }

    public int RoiIndex { get; }

    public string RoiName { get; set; }

    public List<RoiSample> Samples { get; }

    public int OkCount => Samples.Count(s => s.IsOk);

    public int NgCount => Samples.Count(s => !s.IsOk);

    public bool HasSamples => Samples.Count > 0;

    /// <summary>样本变了就重建识别器（最近邻是一条条比的，重建很便宜）。</summary>
    public void Rebuild(double minSimilarity)
        => _classifier = new ActionSampleClassifier(Samples) { MinSimilarity = minSimilarity };

    public ActionRecognition Recognize(float[] feature) => _classifier.Recognize(feature);
}

/// <summary>
/// 整个配方的"按框学习"样本库：按框号分组，每个框一个识别器。
///
/// <para>为什么按框分开而不是一个大样本库：一个工位有 6 个框，
/// 每个框看的是不同的东西（零件、螺丝、线束）。混在一起比，识别必定乱套。</para>
/// </summary>
public sealed class RoiSampleLibrary
{
    private readonly Dictionary<int, RoiTargetLearner> _learners = new();

    /// <summary>相似度阈值。低于它就判"无法判定"，不硬猜。</summary>
    public double MinSimilarity { get; private set; } = 0.75;

    public int TotalSamples => _learners.Values.Sum(l => l.Samples.Count);

    public IReadOnlyCollection<RoiTargetLearner> All => _learners.Values;

    /// <summary>用当前所有样本重建整库。参数变了（阈值调整、框增删）就调它。</summary>
    public void Rebuild(IEnumerable<RoiSample> samples, double minSimilarity)
    {
        MinSimilarity = minSimilarity;
        _learners.Clear();

        if (samples is null) return;

        foreach (var group in samples.GroupBy(s => s.RoiIndex))
        {
            string name = group.OrderByDescending(s => s.Timestamp)
                               .Select(s => s.RoiName)
                               .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;

            _learners[group.Key] = new RoiTargetLearner(group.Key, name, group, minSimilarity);
        }
    }

    public RoiTargetLearner? Get(int roiIndex)
        => _learners.TryGetValue(roiIndex, out var learner) ? learner : null;

    /// <summary>
    /// 这个框有没有"能用来判"的样本。
    ///
    /// <para>只有一边（比如只教了 OK）时仍然可用：那样只能判"像不像 OK"，
    /// 不像则返回无法判定 —— 这比硬判成 NG 安全。</para>
    /// </summary>
    public bool CanJudge(int roiIndex)
        => _learners.TryGetValue(roiIndex, out var learner) && learner.HasSamples;
}
