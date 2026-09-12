namespace VisionForge.Core.Services;

/// <summary>
/// 样本是从哪来的。这个字段决定"这条样本能不能被自动淘汰"。
///
/// <para>用字符串常量而不是 enum：样本文件是现场资产，JSON 里写成 "Auto" / "Manual"
/// 比写 1 / 2 直观，工程师用记事本打开也能看懂，改起来不怕改错。</para>
/// </summary>
public static class SampleSource
{
    /// <summary>人工教的（现场点「记 OK」「记 NG」）。<b>永远不淘汰</b> —— 那是人花时间标的。</summary>
    public const string Manual = "Manual";

    /// <summary>系统自己在有把握时收的（"边干边学"）。多了先淘汰这一类。</summary>
    public const string Auto = "Auto";

    /// <summary>人工纠错来的（系统判错了，人点了"其实是这样"）。分量比人工教更重，不淘汰。</summary>
    public const string Corrected = "Corrected";

    public static string Describe(string? source) => source switch
    {
        Auto => "自学",
        Corrected => "纠错",
        _ => "人工",
    };
}

/// <summary>
/// 自主学习的配置。
///
/// <para><b>为什么默认"只自动学 OK、NG 必须人工确认"：</b>
/// 这是工业现场和研究院最大的区别。系统自己误报了一次 NG，
/// 如果它把那一帧当成 NG 样本收进去，下次看到同样的正常画面就会更坚信"这是违规"——
/// 越学越错，而且错得越来越自信。OK 侧相反：正常生产占了绝大多数时间，
/// 自动收 OK 只是把"人本来就一直在做的事"自动化，风险低、收益直接。</para>
/// </summary>
public sealed class SelfLearningOptions
{
    /// <summary>总开关。关掉之后一帧都不收，行为和旧版本完全一致。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>系统判 OK 且很有把握时，自动把这一帧收成 OK 样本（对应基恩士 Auto-Teach 的"只跑良品"）。</summary>
    public bool AutoCollectOk { get; set; } = true;

    /// <summary>
    /// 系统判 NG 且很有把握时要不要自动收成 NG 样本。<b>默认关</b>。
    ///
    /// <para>打开它的前提是现场确认过"这一批 NG 是真的 NG"。默认关是因为
    /// 误报一旦被学进去就再也洗不掉。</para>
    /// </summary>
    public bool AutoCollectNg { get; set; }

    /// <summary>自动收录的置信度门槛。低于它一律不收 —— 只从"有把握"的帧里学。</summary>
    public double MinConfidence { get; set; } = 0.90;

    /// <summary>和已有同类样本相似度超过它就算"不过是同一张"，不再新增、只把命中次数 +1。</summary>
    public double DedupSimilarity { get; set; } = 0.985;

    /// <summary>每个框、每类最多留多少条。到顶后优先淘汰"最冗余"的自学样本。</summary>
    public int MaxPerClass { get; set; } = 40;

    /// <summary>OK 样本只在"这一步确实正常完成"时才收（防止把人还没做完的半成品当标准动作学进去）。</summary>
    public bool RequireStepCompleted { get; set; } = true;

    public SelfLearningOptions Clone() => new()
    {
        Enabled = Enabled,
        AutoCollectOk = AutoCollectOk,
        AutoCollectNg = AutoCollectNg,
        MinConfidence = MinConfidence,
        DedupSimilarity = DedupSimilarity,
        MaxPerClass = MaxPerClass,
        RequireStepCompleted = RequireStepCompleted,
    };
}

/// <summary>喂给自学引擎的一次观察：某一刻某个框的样子 + 系统自己怎么判的。</summary>
public sealed class SelfLearningObservation
{
    public string RecipeId { get; set; } = string.Empty;

    public int RoiIndex { get; set; }

    public string RoiName { get; set; } = string.Empty;

    /// <summary>框内区域的特征向量（没有它就学不了）。</summary>
    public float[] Feature { get; set; } = Array.Empty<float>();

    /// <summary>系统自己的判定：true=OK / false=NG / null=无法判定。null 一律不学。</summary>
    public bool? Judge { get; set; }

    /// <summary>系统对自己这一判的把握。</summary>
    public double Confidence { get; set; }

    /// <summary>这一下是不是伴随"这道工序正常完成"。</summary>
    public bool StepCompleted { get; set; }

    /// <summary>来源（Auto / Corrected）。人工教的样本走另一条路（现场点按钮），不经过这里。</summary>
    public string Source { get; set; } = SampleSource.Auto;

    public string? ImagePath { get; set; }
}

/// <summary>引擎对一次观察的处理结果。</summary>
public enum SelfLearningDecision
{
    /// <summary>没学（并在 Reason 里说清为什么）。</summary>
    Rejected = 0,

    /// <summary>新增了一条样本。</summary>
    Added = 1,

    /// <summary>和已有样本几乎一样，不新增，只把那条的命中次数 +1（强化）。</summary>
    Reinforced = 2,
}

/// <summary>一次自学处理的结果（界面/日志要显示"学了还是没学、为什么"）。</summary>
public sealed class SelfLearningResult
{
    public SelfLearningDecision Decision { get; init; }

    public string Reason { get; init; } = string.Empty;

    /// <summary>新增或被强化的那条样本。</summary>
    public RoiSample? Sample { get; init; }

    /// <summary>为了让位而被淘汰的旧样本（没有就是 null）。</summary>
    public RoiSample? Evicted { get; init; }

    public bool Accepted => Decision != SelfLearningDecision.Rejected;

    public bool LearnedSomething => Decision == SelfLearningDecision.Added;
}

/// <summary>
/// 自主学习引擎 —— 让软件"越用越准"，同时保证不会越学越错。
///
/// <para><b>它做什么：</b>现场正常作业时，系统在<b>有把握</b>的前提下把自己看到的画面
/// 自动收成样本；人觉得它判错了，点一下"其实是这样"，那条立刻变成最权威的样本。
/// 于是样本库随着生产自己长大，不需要工程师再手工教一遍。</para>
///
/// <para><b>为什么要有护栏（这是本类存在的真正理由）：</b>
/// 一个不加限制的自学习系统会在现场悄悄跑偏，而且跑偏了没人看得出来。
/// 所以这里定了五条硬规矩：</para>
/// <list type="number">
///   <item><b>拿不准就不学</b>：系统自己都判"无法判定"（或置信度低于门槛）的帧，一条都不收。</item>
///   <item><b>NG 默认不自动学</b>：误报被学进去就洗不掉了，所以 NG 必须人工确认。</item>
///   <item><b>几乎一样的只算强化不新增</b>：不然对着一个静止画面跑一分钟就能把样本库灌满，
///         而且全是同一张，识别反而变差。</item>
///   <item><b>只淘汰自学的</b>：人工教的、人工纠错的样本永远保留，淘汰永远从自学样本里挑，
///         并且优先淘汰"和别人最像、最没信息量"的那条 —— 保多样性，保识别率。</item>
///   <item><b>数量封顶</b>：每个框每类最多 MaxPerClass 条，样本库不会无限长大、内存不会漏。</item>
/// </list>
///
/// <para>引擎是纯函数式的：给一批样本 + 一次观察，返回结果并就地改这批样本。
/// 不碰文件、不碰界面，所以自检里可以反复跑、随时断言。</para>
/// </summary>
public static class SelfLearningEngine
{
    /// <summary>最小特征长度：低于这个长度的向量是坏的（比如没取到画面），不能当样本。</summary>
    private const int MinFeatureLength = 16;

    /// <summary>
    /// 处理一次观察。会就地修改 <paramref name="samples"/>（新增 / 强化 / 淘汰+新增）。
    /// </summary>
    public static SelfLearningResult Apply(
        IList<RoiSample> samples,
        SelfLearningObservation observation,
        SelfLearningOptions options,
        DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(options);

        var stamp = now ?? DateTime.Now;
        bool isCorrection = observation.Source == SampleSource.Corrected;

        var gate = CheckGate(observation, options, isCorrection);
        if (gate is not null) return gate;

        bool isOk = observation.Judge!.Value;

        // ---- 去重：和同类已有样本几乎一样，就只强化、不新增 ----
        var sameClass = samples
            .Where(s => s.RoiIndex == observation.RoiIndex && s.IsOk == isOk)
            .ToList();

        foreach (var existing in sameClass)
        {
            double similarity = FrameFeature.Similarity(observation.Feature, existing.Feature);
            if (similarity >= options.DedupSimilarity)
            {
                // 强化：命中次数 +1。时间戳只在"隔了一会儿"才刷新 ——
                // 静止画面每秒 25 帧都命中，每帧都改时间戳会让界面和落盘一直动。
                existing.Hits++;
                if ((stamp - existing.Timestamp).TotalSeconds >= 5) existing.Timestamp = stamp;
                if (existing.Confidence < observation.Confidence) existing.Confidence = observation.Confidence;

                return new SelfLearningResult
                {
                    Decision = SelfLearningDecision.Reinforced,
                    Sample = existing,
                    Reason = $"和已有 {existing.Label} 样本（{existing.Display}）相似度 {similarity:P1}，" +
                             $"视为同一种情况，只强化不新增（累计命中 {existing.Hits} 次）",
                };
            }
        }

        // ---- 数量封顶：先腾位置，再放新的 ----
        RoiSample? evicted = null;
        if (sameClass.Count >= Math.Max(2, options.MaxPerClass))
        {
            evicted = PickEvictionCandidate(sameClass);
            if (evicted is null)
            {
                return new SelfLearningResult
                {
                    Decision = SelfLearningDecision.Rejected,
                    Reason = $"这个框的 {SampleClass(isOk)} 样本已有 {sameClass.Count} 条（上限 {options.MaxPerClass}），" +
                             "而且都是人工教的 —— 请先人工清理，系统不会动人工样本",
                };
            }

            samples.Remove(evicted);
        }

        var sample = new RoiSample
        {
            RecipeId = observation.RecipeId,
            RoiIndex = observation.RoiIndex,
            RoiName = observation.RoiName,
            IsOk = isOk,
            Timestamp = stamp,
            ImagePath = observation.ImagePath,
            Feature = observation.Feature,
            Source = isCorrection ? SampleSource.Corrected : SampleSource.Auto,
            Hits = 1,
            Confidence = observation.Confidence,
            Note = isCorrection ? "人工纠错：系统判错了，这是正确答案" : "自学：系统判定有把握时自动收录",
        };

        samples.Add(sample);

        return new SelfLearningResult
        {
            Decision = SelfLearningDecision.Added,
            Sample = sample,
            Evicted = evicted,
            Reason = evicted is null
                ? $"收录一条 {sample.Label} 样本（{SampleSource.Describe(sample.Source)}，置信度 {observation.Confidence:P0}）"
                : $"收录一条 {sample.Label} 样本，并淘汰最冗余的旧自学样本（{evicted.Display}）以腾出位置",
        };
    }

    /// <summary>
    /// 撤销最近一次"新学进来"的样本（只认自学的；人工/纠错样本不撤）。
    /// 返回被撤掉的那条，没有就返回 null。
    /// </summary>
    public static RoiSample? UndoLastAuto(IList<RoiSample> samples, int? roiIndex = null)
    {
        var last = samples
            .Where(s => s.Source == SampleSource.Auto && (roiIndex is null || s.RoiIndex == roiIndex))
            .OrderByDescending(s => s.Timestamp)
            .FirstOrDefault();

        if (last is null) return null;

        samples.Remove(last);
        return last;
    }

    /// <summary>把自学的样本"固化"成人工样本：之后系统再也不会淘汰它们。</summary>
    public static int FreezeAutoSamples(IList<RoiSample> samples, int? roiIndex = null)
    {
        int count = 0;
        foreach (var sample in samples)
        {
            if (sample.Source != SampleSource.Auto) continue;
            if (roiIndex is not null && sample.RoiIndex != roiIndex) continue;

            sample.Source = SampleSource.Manual;
            sample.Note = string.IsNullOrWhiteSpace(sample.Note)
                ? "已固化为人工样本"
                : sample.Note + "（已固化为人工样本）";
            count++;
        }

        return count;
    }

    /// <summary>清空自学样本（人工 / 纠错的保留）。返回清掉的条数。</summary>
    public static int ClearAutoSamples(IList<RoiSample> samples, int? roiIndex = null)
    {
        var doomed = samples
            .Where(s => s.Source == SampleSource.Auto && (roiIndex is null || s.RoiIndex == roiIndex))
            .ToList();

        foreach (var sample in doomed) samples.Remove(sample);
        return doomed.Count;
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>收录前的所有"否决理由"。返回 null = 可以学。</summary>
    private static SelfLearningResult? CheckGate(
        SelfLearningObservation observation,
        SelfLearningOptions options,
        bool isCorrection)
    {
        if (observation.Feature is null || observation.Feature.Length < MinFeatureLength)
        {
            return Reject("没有拿到画面特征（可能是相机还没出图），这一帧不学");
        }

        if (observation.Judge is null)
        {
            return Reject("系统自己都判「无法判定」，不学 —— 只从有把握的帧里学，否则等于把噪声当标准");
        }

        // 人工纠错：人说了算，不受开关和门槛限制（这是最权威的学习信号）
        if (isCorrection)
        {
            return null;
        }

        if (!options.Enabled)
        {
            return Reject("自主学习已关闭");
        }

        bool isOk = observation.Judge.Value;

        if (isOk && !options.AutoCollectOk)
        {
            return Reject("未开启「自动收录 OK 样本」");
        }

        if (!isOk && !options.AutoCollectNg)
        {
            return Reject("NG 不自动收录（默认关，防越学越错）—— 确认这确实是违规时，点「判错了 / 记 NG」由人工收录");
        }

        if (observation.Confidence < options.MinConfidence)
        {
            return Reject($"置信度 {observation.Confidence:P0} 低于自学门槛 {options.MinConfidence:P0}，这一帧不学");
        }

        if (isOk && options.RequireStepCompleted && !observation.StepCompleted)
        {
            return Reject("这一步还没正常完成，先不把当前画面当标准动作学进去");
        }

        return null;
    }

    private static SelfLearningResult Reject(string reason) =>
        new() { Decision = SelfLearningDecision.Rejected, Reason = reason };

    private static string SampleClass(bool isOk) => isOk ? "OK" : "NG";

    /// <summary>
    /// 从同一框、同一类的样本里挑一条"最该淘汰"的。
    ///
    /// <para>只挑自学的；一条自学样本都没有就返回 null（人工样本一律不动）。
    /// 挑法是"最冗余优先"：算每条和同组其它样本的最大相似度，
    /// 谁和别人最像谁最没信息量，先淘汰谁；并列时淘汰更早的那条。</para>
    /// </summary>
    private static RoiSample? PickEvictionCandidate(IReadOnlyList<RoiSample> sameClass)
    {
        RoiSample? worst = null;
        double worstScore = double.NegativeInfinity;

        foreach (var candidate in sameClass)
        {
            if (candidate.Source != SampleSource.Auto) continue;
            if (candidate.Feature.Length == 0) return candidate;   // 坏样本优先清掉

            double maxSimilarity = 0;
            foreach (var other in sameClass)
            {
                if (ReferenceEquals(other, candidate)) continue;
                double similarity = FrameFeature.Similarity(candidate.Feature, other.Feature);
                if (similarity > maxSimilarity) maxSimilarity = similarity;
            }

            bool better =
                worst is null
                || maxSimilarity > worstScore + 1e-9
                || (Math.Abs(maxSimilarity - worstScore) <= 1e-9 && candidate.Timestamp < worst.Timestamp);

            if (better)
            {
                worst = candidate;
                worstScore = maxSimilarity;
            }
        }

        return worst;
    }
}

/// <summary>自学统计（界面显示"学了多少、拒了多少、为什么拒"）。</summary>
public sealed class SelfLearningStats
{
    /// <summary>新收录的样本条数（本次运行累计）。</summary>
    public int Added { get; set; }

    /// <summary>被强化的次数（同一情况反复出现）。</summary>
    public int Reinforced { get; set; }

    /// <summary>被否决的次数。</summary>
    public int Rejected { get; set; }

    /// <summary>因为样本到顶而淘汰的条数。</summary>
    public int Evicted { get; set; }

    /// <summary>人工纠错的次数（这个数字越大，说明系统原先错得越多，值得看）。</summary>
    public int Corrected { get; set; }

    /// <summary>最近一次处理结果（界面直接显示这句话）。</summary>
    public string LastReason { get; set; } = "还没有开始自学";

    public void Record(SelfLearningResult result)
    {
        LastReason = result.Reason;

        if (result.Decision == SelfLearningDecision.Rejected)
        {
            Rejected++;
            return;
        }

        if (result.Decision == SelfLearningDecision.Reinforced)
        {
            Reinforced++;
            return;
        }

        Added++;
        if (result.Sample?.Source == SampleSource.Corrected) Corrected++;
        if (result.Evicted is not null) Evicted++;
    }

    public string Summary(int totalSamples, int okSamples, int ngSamples) =>
        $"样本 {totalSamples} 条（OK {okSamples} / NG {ngSamples}）· " +
        $"本次自学 {Added} 条 · 强化 {Reinforced} · 纠错 {Corrected} · 拒绝 {Rejected} · 淘汰 {Evicted}";
}
