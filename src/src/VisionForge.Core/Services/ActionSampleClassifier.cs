using VisionForge.Core.Models;

namespace VisionForge.Core.Services;

/// <summary>
/// 一条"教示样本"：某一刻的画面 + 人工标的 OK/NG。
///
/// <para>这是"边做边教"的最小单位 —— 操作员在镜头下正常作业，
/// 工程师看到标准动作点一下「OK」，看到违规动作点一下「NG」，
/// 系统把这些画面连同标签记下来，之后靠它们自动识别。</para>
/// </summary>
public sealed class ActionSample : ISample
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>true = 人工标为 OK（标准动作），false = 标为 NG（违规动作）。</summary>
    public bool IsOk { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>样本抓帧图的路径（data\action-samples\ 下）。</summary>
    public string? ImagePath { get; set; }

    /// <summary>特征向量（由 FrameFeature 提取），识别时比对这个。</summary>
    public float[] Feature { get; set; } = Array.Empty<float>();

    public string Label => IsOk ? "OK" : "NG";

    public string Display => $"{Label} · {Timestamp:HH:mm:ss}";
}

/// <summary>
/// 画面特征提取 —— 把一帧图压成一个固定长度向量，用来比对"这两下动作像不像"。
///
/// <para><b>为什么是降采样灰度而不是关键点：</b>用户要的是"不画框、直接教"。
/// 关键点模型要等实拍样本和 ONNX 环境，先用降采样灰度做<b>可用的第一版</b>：
/// 免标定、免训练、纯 CPU、可解释（相似度多少一目了然）。
/// 将来接了手部关键点，只要把这个向量换成关键点坐标即可，识别与界面都不用改。</para>
///
/// <para>做了两处工程处理：① 减去均值 → 抵消整体亮度变化；② 单位化 → 比的是"形状"
/// 不是"亮度"，车间光照轻微漂移不会导致误判。</para>
/// </summary>
public static class FrameFeature
{
    public const int GridWidth = 16;
    public const int GridHeight = 12;
    public const int Dimension = GridWidth * GridHeight;

    /// <summary>
    /// 末尾额外一维：整体亮度的权重。
    ///
    /// <para>为什么需要它：整幅/整框的"图案"是<b>去均值</b>后算的（抗光照漂移），
    /// 于是"框里有个亮零件"和"框里空空的"如果都是均匀的，去均值后就都变成全 0，
    /// 根本分不开。可现场的用法恰恰是"有东西 = OK、被拿走了 = NG"——
    /// 所以补一维带权重的平均亮度进去：图案为主、亮度为辅，两边都能用。</para>
    /// </summary>
    public const float BrightnessWeight = 0.35f;

    /// <summary>整幅画面的特征。</summary>
    public static float[] Extract(CameraFrame frame) => Extract(frame, null);

    /// <summary>
    /// 取特征。传了 <paramref name="roi"/> 就只看这个框里的区域 ——
    /// "按框学习"就是靠这个把每个框单独看。
    /// </summary>
    public static float[] Extract(CameraFrame frame, RoiRegion? roi)
    {
        var feature = new float[Dimension + 1];
        if (frame is null || frame.Width <= 0 || frame.Height <= 0) return feature;

        int regionX0 = 0, regionY0 = 0, regionX1 = frame.Width, regionY1 = frame.Height;
        if (roi is not null)
        {
            regionX0 = Math.Clamp((int)(roi.X * frame.Width), 0, frame.Width - 1);
            regionY0 = Math.Clamp((int)(roi.Y * frame.Height), 0, frame.Height - 1);
            regionX1 = Math.Clamp((int)((roi.X + roi.Width) * frame.Width), regionX0 + 1, frame.Width);
            regionY1 = Math.Clamp((int)((roi.Y + roi.Height) * frame.Height), regionY0 + 1, frame.Height);
        }

        int regionW = regionX1 - regionX0;
        int regionH = regionY1 - regionY0;

        // 分块取平均：每个格子代表区域里的一小块，天然抗噪、抗轻微位移
        long total = 0;
        int totalCount = 0;
        for (int gy = 0; gy < GridHeight; gy++)
        {
            int y0 = regionY0 + regionH * gy / GridHeight;
            int y1 = Math.Max(y0 + 1, regionY0 + regionH * (gy + 1) / GridHeight);

            for (int gx = 0; gx < GridWidth; gx++)
            {
                int x0 = regionX0 + regionW * gx / GridWidth;
                int x1 = Math.Max(x0 + 1, regionX0 + regionW * (gx + 1) / GridWidth);

                long sum = 0;
                int count = 0;
                for (int y = y0; y < y1 && y < regionY1; y += 2)
                {
                    for (int x = x0; x < x1 && x < regionX1; x += 2)
                    {
                        sum += frame.GetLuminance(x, y);
                        count++;
                    }
                }

                feature[gy * GridWidth + gx] = count == 0 ? 0f : (float)sum / count / 255f;
                total += sum;
                totalCount += count;
            }
        }

        Normalize(feature);

        // 平均亮度放在最后（放在图案归一化之后，才不会被去均值抹掉）
        feature[Dimension] = totalCount == 0
            ? 0f
            : (float)total / totalCount / 255f * BrightnessWeight;

        // 补上亮度维之后必须再整体单位化一次。
        //
        // 为什么：Similarity 走的是点积（假设向量单位长度）。而"均匀画面"
        // （空框、平白墙）的图案维几乎是 0，之后把亮度维从 -1 覆盖成 0.048，
        // 向量长度就只剩 0.087 —— 结果它跟自己都比不像（相似度 1%）。
        // 现场表现就是：明明教过"空框 = NG"，识别却一直说「无法判定」。
        UnitNormalize(feature);

        return feature;
    }

    /// <summary>整体单位化（不去均值）—— 保证任意两个特征的点积就是余弦相似度。</summary>
    private static void UnitNormalize(float[] v)
    {
        double norm = 0;
        for (int i = 0; i < v.Length; i++) norm += v[i] * (double)v[i];

        norm = Math.Sqrt(norm);
        if (norm < 1e-6) return;

        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }

    /// <summary>去均值 + 单位化。之后两个向量的点积就是余弦相似度。</summary>
    private static void Normalize(float[] v)
    {
        double mean = 0;
        for (int i = 0; i < v.Length; i++) mean += v[i];
        mean /= v.Length;

        double norm = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] -= (float)mean;
            norm += v[i] * (double)v[i];
        }

        norm = Math.Sqrt(norm);
        if (norm < 1e-6) return;

        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }

    /// <summary>
    /// 余弦相似度（-1~1）。1 = 完全一样。
    ///
    /// <para>这里<b>显式除以两个向量的长度</b>，而不是直接返回点积 ——
    /// 这样即使遇到历史样本（旧版本存的、长度不统一的向量）也照样能比，
    /// 不会因为软件升级就让现场教过的样本全部失效。</para>
    /// </summary>
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;

        // 长度不一致时按较短的那段比：旧版本存的样本是 192 维（没有亮度那一维），
        // 不能因为软件升级就让现场教过的样本全部失效 —— 比前 192 维足够判断像不像。
        int n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < n; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        if (na < 1e-12 || nb < 1e-12) return 0;
        return Math.Clamp(dot / Math.Sqrt(na * nb), -1, 1);
    }
}

/// <summary>识别结论。</summary>
public sealed class ActionRecognition
{
    /// <summary>null = 无法判定（没有足够像的样本）。这是"防误拦"的关键：不确定就说不知道。</summary>
    public bool? IsOk { get; set; }

    /// <summary>与最像样本的相似度（0~1）。</summary>
    public double Confidence { get; set; }

    public ISample? Match { get; set; }

    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 最近邻识别器 —— 拿样本库跟当前画面比，最像的那条是什么标签就判什么。
///
/// <para>为什么用最近邻而不是训练一个模型：样本只有几条到几十条，
/// 最近邻<b>立刻可用、可解释</b>（"跟 12:31:07 标为 NG 的那次很像，相似度 0.93"），
/// 现场工程师能马上判断该不该信它。样本攒多了再换模型也不迟。</para>
///
/// <para>关键设计：<b>相似度不够就判"无法判定"</b>，绝不硬猜。
/// 工控软件里"我不确定"是一个合法且必须存在的结论 —— 硬猜的代价是误拦产线。</para>
/// </summary>
public sealed class ActionSampleClassifier
{
    // 用 ISample 而不是 ActionSample：整幅画面的样本和"某一个框内"的样本共用这一个识别器
    private readonly List<ISample> _samples;

    public ActionSampleClassifier(IEnumerable<ISample> samples)
    {
        _samples = new List<ISample>(samples ?? Array.Empty<ISample>());
    }

    public int Count => _samples.Count;

    public int OkCount => _samples.Count(s => s.IsOk);

    public int NgCount => _samples.Count(s => !s.IsOk);

    /// <summary>相似度低于它就判"无法判定"。默认 0.75，界面可调。</summary>
    public double MinSimilarity { get; set; } = 0.75;

    public ActionRecognition Recognize(float[] feature)
    {
        if (_samples.Count == 0)
        {
            return new ActionRecognition
            {
                IsOk = null,
                Confidence = 0,
                Message = "还没有教示样本：先对着标准动作点「记 OK」、对着违规动作点「记 NG」",
            };
        }

        ISample? best = null;
        double bestSimilarity = -1;

        foreach (var sample in _samples)
        {
            double similarity = FrameFeature.Similarity(feature, sample.Feature);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = sample;
            }
        }

        if (best is null || bestSimilarity < MinSimilarity)
        {
            return new ActionRecognition
            {
                IsOk = null,
                Confidence = Math.Max(0, bestSimilarity),
                Match = best,
                Message = $"无法判定：最像的样本（{best?.Display}）相似度只有 {bestSimilarity:P0}，" +
                          $"低于阈值 {MinSimilarity:P0} —— 请人工确认或补教示样本",
            };
        }

        return new ActionRecognition
        {
            IsOk = best.IsOk,
            Confidence = bestSimilarity,
            Match = best,
            Message = $"识别为 {best.Label}：与 {best.Timestamp:HH:mm:ss} 的 {best.Label} 样本相似度 {bestSimilarity:P0}",
        };
    }
}
