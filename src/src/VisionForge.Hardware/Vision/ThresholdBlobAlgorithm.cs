using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Vision;

/// <summary>
/// 阈值 + 连通域（Blob）计数算法。
///
/// 适用场景：
///   · 清点工件个数（多料 / 少料 / 异物混入）
///   · 有无检测（孔位、丝印、标签存在性）
///   · 简单的面积筛选
///
/// 这是工业视觉里最"土"但最可靠的一类算法。很多看上去需要深度学习的活，
/// 在光照受控的产线上用阈值 + Blob 就能稳定解决，而且：
///   · 不需要标注数据
///   · 不需要 GPU
///   · 单帧耗时 5~20ms
///   · 现场工程师能看懂、能自己调阈值
/// <b>先想能不能用传统算法解决，再考虑上模型。</b>
/// </summary>
public sealed class ThresholdBlobAlgorithm : IInspectionAlgorithm
{
    public string Key => "threshold-blob";

    public string DisplayName => "阈值斑点计数";

    public string Description =>
        "对指定区域做二值化后统计连通域数量与面积，用于计数、有无检测、异物判定。";

    // ------------------------------------------------------------------
    // 参数声明 —— 界面据此自动生成控件，配方据此存储取值
    // ------------------------------------------------------------------
    public IReadOnlyList<ParameterDefinition> Parameters { get; } = new[]
    {
        new ParameterDefinition
        {
            Key = "Threshold", DisplayName = "二值化阈值", Type = ParameterType.Number,
            Min = 0, Max = 255, Default = 128, Unit = "灰度",
            Description = "把画面分成「有料」和「背景」的那条分界线：亮度≥这个值的像素算有料。" +
                          "现场怎么调：料明显比背景亮就取 100~160；车间光变亮往上调，变暗往下调。" +
                          "调太低会把灰尘算成料（误判 NG），调太高会把暗一点的料漏掉（漏检）。",
        },
        new ParameterDefinition
        {
            Key = "MinAreaPx", DisplayName = "最小面积", Type = ParameterType.Number,
            Min = 1, Max = 1000000, Default = 200, Unit = "px²",
            Description = "比这个面积还小的亮点，直接当成灰尘/噪点丢掉，不参与判定。" +
                          "这是过滤干扰的第一道闸门：画面噪点多就调大（200→500）；" +
                          "小零件被误丢掉就调小。",
        },
        new ParameterDefinition
        {
            Key = "MaxBlobCount", DisplayName = "允许最大个数", Type = ParameterType.Number,
            Min = 0, Max = 999, Default = 1, Unit = "个",
            Description = "画面里最多允许出现几个「料」，超过就判 NG——用来抓多料、异物、连料。" +
                          "例：工装本来就只放 1 个零件，这里就填 1。",
        },
        new ParameterDefinition
        {
            Key = "MinBlobCount", DisplayName = "要求最小个数", Type = ParameterType.Number,
            Min = 0, Max = 999, Default = 1, Unit = "个",
            Description = "至少要出现几个「料」，少于就判 NG——用来抓缺件、漏装。" +
                          "和上一项配合：两项都填 1，表示「必须有且只有一个」。",
        },
        new ParameterDefinition
        {
            Key = "InvertPolarity", DisplayName = "反相（亮底暗物）", Type = ParameterType.Bool,
            Default = 0,
            Description = "默认是「亮的是料」。如果工件是暗的、背景是亮的（背光板、暗场轮廓），" +
                          "就勾上它，改成「暗的是料」。",
        },
        new ParameterDefinition
        {
            Key = "RegionName", DisplayName = "检测区域名", Type = ParameterType.Text,
            Default = 0,
            Description = "只在这个 ROI 里判「有没有料」。填的名字要和配方里 ROI 的名称一致。" +
                          "留空 = 看整幅画面（不推荐，容易受画面其他地方干扰）。",
        },
    };

    // ------------------------------------------------------------------
    public InspectionResult Run(CameraFrame frame, Recipe recipe)
    {
        var result = new InspectionResult
        {
            RecipeId = recipe.Id,
            RecipeName = recipe.Name,
            ProductModel = recipe.ProductModel,
            Timestamp = DateTime.Now,
        };

        var p = new ParameterReader(recipe, Parameters);
        int threshold = (int)p.GetDouble("Threshold", 128);
        int minArea = (int)p.GetDouble("MinAreaPx", 200);
        int maxCount = (int)p.GetDouble("MaxBlobCount", 1);
        int minCount = (int)p.GetDouble("MinBlobCount", 0);
        bool invert = p.GetBool("InvertPolarity");
        string regionName = p.GetString("RegionName");

        // ---- 确定检测区域 ----
        int x0 = 0, y0 = 0, w = frame.Width, h = frame.Height;
        if (!string.IsNullOrWhiteSpace(regionName))
        {
            var roi = recipe.Rois.FirstOrDefault(
                r => r.Enabled && string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));

            if (roi is null)
            {
                return InspectionResult.Error(
                    $"配方中找不到名为「{regionName}」的 ROI 区域，请检查配方配置");
            }
            var (rx, ry, rw, rh) = roi.ToPixels(frame.Width, frame.Height);
            x0 = Math.Max(0, rx); y0 = Math.Max(0, ry);
            w = Math.Min(rw, frame.Width - x0);
            h = Math.Min(rh, frame.Height - y0);
        }

        if (w <= 0 || h <= 0)
            return InspectionResult.Error("检测区域尺寸非法（宽或高为 0）");

        // ---- 连通域分析 ----
        var areas = FindBlobAreas(frame, x0, y0, w, h, threshold, invert, minArea);

        int totalArea = areas.Sum();
        int maxArea = areas.Count > 0 ? areas.Max() : 0;

        result.Measurements.Add(new MeasurementItem
        {
            Name = "有效斑点数", Value = areas.Count, Unit = "个",
            LowerLimit = minCount > 0 ? minCount : null,
            UpperLimit = maxCount,
        });
        result.Measurements.Add(new MeasurementItem
        {
            Name = "斑点总面积", Value = totalArea, Unit = "px²",
        });
        result.Measurements.Add(new MeasurementItem
        {
            Name = "最大斑点面积", Value = maxArea, Unit = "px²",
        });

        // ---- 判定 ----（理由必须写成人话，现场班长要能直接懂）
        bool tooMany = areas.Count > maxCount;
        bool tooFew = minCount > 0 && areas.Count < minCount;

        if (tooMany)
        {
            result.Verdict = InspectionVerdict.Fail;
            result.JudgementReason =
                $"检测到 {areas.Count} 个斑点，超过上限 {maxCount} 个（最大斑面积 {maxArea}px²），疑似多料或异物混入";
            for (int i = 0; i < Math.Min(areas.Count, 20); i++)
                result.Defects.Add(new DefectItem { Type = "多余斑点", Confidence = 1.0, Area = $"{areas[i]}px²" });
        }
        else if (tooFew)
        {
            result.Verdict = InspectionVerdict.Fail;
            result.JudgementReason =
                $"仅检测到 {areas.Count} 个斑点，低于要求下限 {minCount} 个，疑似缺件";
        }
        else
        {
            result.Verdict = InspectionVerdict.Pass;
            result.JudgementReason =
                $"检测到 {areas.Count} 个斑点，在规定范围 {minCount}~{maxCount} 个内，总面积 {totalArea}px²";
        }

        return result;
    }

    // ==================================================================
    // 连通域分析（4 邻域，迭代式，避免深递归爆栈）
    // ==================================================================
    private static List<int> FindBlobAreas(
        CameraFrame frame, int x0, int y0, int w, int h,
        int threshold, bool invert, int minArea)
    {
        var areas = new List<int>();
        var visited = new bool[w * h];

        // 显式栈：1280×1024 的大连通域用递归必爆栈
        var stack = new int[Math.Max(1024, w * h / 4)];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                if (visited[idx]) continue;

                bool isForeground = IsForeground(frame, x0 + x, y0 + y, threshold, invert);
                if (!isForeground) { visited[idx] = true; continue; }

                // ---- BFS 洪水填充 ----
                int area = 0;
                int sp = 0;
                stack[sp++] = idx;
                visited[idx] = true;

                while (sp > 0)
                {
                    int cur = stack[--sp];
                    area++;

                    int cx = cur % w;
                    int cy = cur / w;

                    // 4 邻域
                    if (cx > 0) TryPush(cur - 1, cx - 1, cy);
                    if (cx < w - 1) TryPush(cur + 1, cx + 1, cy);
                    if (cy > 0) TryPush(cur - w, cx, cy - 1);
                    if (cy < h - 1) TryPush(cur + w, cx, cy + 1);

                    void TryPush(int nIdx, int nx, int ny)
                    {
                        if (visited[nIdx]) return;
                        if (!IsForeground(frame, x0 + nx, y0 + ny, threshold, invert)) return;

                        visited[nIdx] = true;
                        // 栈溢出保护：正常场景不会触发，异常图像（如全白）下兜底
                        if (sp < stack.Length) stack[sp++] = nIdx;
                    }
                }

                if (area >= minArea) areas.Add(area);
            }
        }

        return areas;
    }

    private static bool IsForeground(CameraFrame frame, int x, int y, int threshold, bool invert)
    {
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return false;

        byte lum = frame.Format == PixelFormat.Mono8
            ? frame.GetGray(x, y)                       // 快速路径
            : frame.GetLuminance(x, y);

        return invert ? lum < threshold : lum >= threshold;
    }
}
