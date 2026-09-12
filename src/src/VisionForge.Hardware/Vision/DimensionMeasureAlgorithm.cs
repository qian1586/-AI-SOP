using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Vision;

/// <summary>
/// 尺寸测量算法（扫描线边缘对）。
///
/// 适用场景：外径、宽度、间隙、台阶高（配合标定）。
/// 原理：在 ROI 内取一条扫描线，找灰度跨越阈值的两个边沿，边沿间距即像素宽度，
/// 乘以标定系数就是实际尺寸。
///
/// <para><b>标定系数怎么来（现场必做）：</b></para>
/// 放一个已知尺寸的标准件（标定块 / 量块）在视野里，测出它的像素宽度，
/// <c>PixelsPerMm = 像素宽度 ÷ 实际宽度mm</c>。
/// 注意标定必须在<b>工作距离和镜头焦距不变</b>的前提下做，动了机位就得重标。
///
/// <para><b>为什么工业现场大量用这类算法而不是深度学习：</b></para>
/// 尺寸测量要的是微米级重复性，模型给不了这个确定性。
/// 只要光照稳定、边缘清晰，扫描线法的重复性可以做到 0.5 像素以内。
/// </summary>
public sealed class DimensionMeasureAlgorithm : IInspectionAlgorithm
{
    public string Key => "dimension-measure";

    public string DisplayName => "尺寸测量（扫描线）";

    public string Description =>
        "在指定 ROI 的某一行扫描灰度边沿，测量两侧边沿间距并换算为实际尺寸，与上下限比对。";

    public IReadOnlyList<ParameterDefinition> Parameters { get; } = new[]
    {
        new ParameterDefinition
        {
            Key = "RegionName", DisplayName = "测量区域名", Type = ParameterType.Text,
            Default = 0,
            Description = "在哪个 ROI 里量尺寸（填 ROI 的名称，要和配方里一致）。必填 —— " +
                          "不填它不知道量哪儿。",
        },
        new ParameterDefinition
        {
            Key = "Threshold", DisplayName = "边沿阈值", Type = ParameterType.Number,
            Min = 0, Max = 255, Default = 128, Unit = "灰度",
            Description = "找边缘用的分界线：亮度跨过这条线的位置就是工件边缘。" +
                          "取「料」和「背景」亮度的中间值最稳，例如料 200、背景 40 就取 120。",
        },
        new ParameterDefinition
        {
            Key = "EdgePolarity", DisplayName = "边沿方向", Type = ParameterType.Enum,
            Default = 0, Options = new[] { "暗到亮", "亮到暗" },
            Description = "工件边缘是从暗变亮（暗到亮），还是从亮变暗（亮到暗）？" +
                          "选错会量到工件的另一侧，尺寸直接差一整块。拿不准就把两个都试一次，看哪个数值合理。",
        },
        new ParameterDefinition
        {
            Key = "ScanLineRatio", DisplayName = "扫描线位置", Type = ParameterType.Number,
            Min = 0, Max = 1, Default = 0.5,
            Description = "在这条水平线上量宽度：0 = ROI 顶边，1 = 底边，0.5 = 正中。" +
                          "要避开倒角、毛刺、缺口的位置，选形状最饱满的地方量。",
        },
        new ParameterDefinition
        {
            Key = "PixelsPerMm", DisplayName = "标定系数", Type = ParameterType.Number,
            Min = 0.001, Max = 10000, Default = 10, Unit = "px/mm",
            Description = "1 毫米等于多少个像素 —— 用标准件量出来的换算系数。" +
                          "换镜头、挪机位、改焦距之后必须重新标定，否则所有尺寸都是错的（这是最容易踩的坑）。",
        },
        new ParameterDefinition
        {
            Key = "LowerLimitMm", DisplayName = "下限", Type = ParameterType.Number,
            Min = 0, Max = 100000, Default = 0, Unit = "mm",
            Description = "合格尺寸范围的下限（毫米）。测量值小于它就判 NG。",
        },
        new ParameterDefinition
        {
            Key = "UpperLimitMm", DisplayName = "上限", Type = ParameterType.Number,
            Min = 0, Max = 100000, Default = 100, Unit = "mm",
            Description = "合格尺寸范围的上限（毫米）。测量值大于它就判 NG。" +
                          "上下限之间 = OK，超出一侧就是 NG，判定理由里会写明实测值和超了多少。",
        },
        new ParameterDefinition
        {
            Key = "SubPixel", DisplayName = "亚像素插值", Type = ParameterType.Bool,
            Default = 1,
            Description = "勾上后用灰度插值把边缘位置算到小数点后，精度大约提升 3 倍，耗时可以忽略。" +
                          "建议一直勾着。",
        },
    };

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
        string regionName = p.GetString("RegionName");
        int threshold = p.GetInt("Threshold", 128);
        bool brightToDark = p.GetEnum("EdgePolarity", "暗到亮") == "亮到暗";
        double scanRatio = Math.Clamp(p.GetDouble("ScanLineRatio", 0.5), 0, 1);
        double pxPerMm = p.GetDouble("PixelsPerMm", 10);
        double lower = p.GetDouble("LowerLimitMm", 0);
        double upper = p.GetDouble("UpperLimitMm", 100);
        bool subPixel = p.GetBool("SubPixel", true);

        if (pxPerMm <= 0)
            return InspectionResult.Error("标定系数 PixelsPerMm 非法（必须 > 0），请先用标准件标定");

        // ---- 定位 ROI ----
        var roi = recipe.Rois.FirstOrDefault(
            r => r.Enabled && string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));

        if (roi is null)
            return InspectionResult.Error(
                string.IsNullOrWhiteSpace(regionName)
                    ? "未指定测量区域名（RegionName 参数为空）"
                    : $"配方中找不到名为「{regionName}」的 ROI 区域");

        var (x0, y0, w, h) = roi.ToPixels(frame.Width, frame.Height);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        w = Math.Min(w, frame.Width - x0);
        h = Math.Min(h, frame.Height - y0);

        if (w < 4 || h < 2)
            return InspectionResult.Error($"ROI「{regionName}」太小（{w}×{h}），无法测量");

        int scanY = y0 + (int)(h * scanRatio);
        scanY = Math.Clamp(scanY, y0, y0 + h - 1);

        // ---- 找边沿 ----
        var edges = FindEdgesOnLine(frame, x0, scanY, w, threshold, brightToDark, subPixel);

        if (edges.Count < 2)
        {
            return InspectionResult.Error(
                $"在 ROI「{regionName}」第 {scanY} 行未找到 2 个边沿（只找到 {edges.Count} 个）。" +
                $"请检查：阈值 {threshold} 是否合适、边沿方向是否选反、工件是否在视野内");
        }

        // 取最外侧的一对边沿作为宽度
        double leftEdge = edges.First();
        double rightEdge = edges.Last();
        double widthPx = rightEdge - leftEdge;
        double widthMm = widthPx / pxPerMm;

        result.Measurements.Add(new MeasurementItem
        {
            Name = "测量宽度", Value = Math.Round(widthMm, 4), Unit = "mm",
            LowerLimit = lower, UpperLimit = upper,
        });
        result.Measurements.Add(new MeasurementItem
        {
            Name = "像素宽度", Value = Math.Round(widthPx, 2), Unit = "px",
        });
        result.Measurements.Add(new MeasurementItem
        {
            Name = "边沿数量", Value = edges.Count, Unit = "个",
        });

        // ---- 判定 ----
        if (widthMm < lower)
        {
            result.Verdict = InspectionVerdict.Fail;
            result.JudgementReason =
                $"测量宽度 {widthMm:F3}mm，小于下限 {lower:F3}mm（像素宽度 {widthPx:F1}px，标定 {pxPerMm:F2}px/mm）";
        }
        else if (widthMm > upper)
        {
            result.Verdict = InspectionVerdict.Fail;
            result.JudgementReason =
                $"测量宽度 {widthMm:F3}mm，超过上限 {upper:F3}mm（像素宽度 {widthPx:F1}px，标定 {pxPerMm:F2}px/mm）";
        }
        else
        {
            result.Verdict = InspectionVerdict.Pass;
            result.JudgementReason =
                $"测量宽度 {widthMm:F3}mm，在规格 {lower:F3}~{upper:F3}mm 内";
        }

        return result;
    }

    // ==================================================================
    /// <summary>
    /// 在一条水平扫描线上找灰度跳变点。
    ///
    /// 亚像素插值的原理：真实边沿落在两个像素之间，
    /// 用 (threshold - 前一点灰度) / (后一点灰度 - 前一点灰度) 求比例，
    /// 就能把边沿定位到 0.1 像素级别。测量类应用必开。
    /// </summary>
    private static List<double> FindEdgesOnLine(
        CameraFrame frame, int startX, int y, int length,
        int threshold, bool brightToDark, bool subPixel)
    {
        var edges = new List<double>();

        byte prev = ReadLum(frame, startX, y);

        for (int i = 1; i < length; i++)
        {
            int x = startX + i;
            byte cur = ReadLum(frame, x, y);

            bool crossed = brightToDark
                ? prev >= threshold && cur < threshold
                : prev < threshold && cur >= threshold;

            if (crossed)
            {
                double pos = x;
                if (subPixel)
                {
                    double denom = cur - prev;
                    if (Math.Abs(denom) > 1e-6)
                    {
                        // 线性插值：边沿在 prev 和 cur 之间的相对位置
                        double ratio = (threshold - prev) / denom;
                        pos = (x - 1) + Math.Clamp(ratio, 0, 1);
                    }
                }
                edges.Add(pos);
            }

            prev = cur;
        }

        return edges;
    }

    private static byte ReadLum(CameraFrame frame, int x, int y)
    {
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return 0;
        return frame.Format == PixelFormat.Mono8 ? frame.GetGray(x, y) : frame.GetLuminance(x, y);
    }
}
