using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VisionForge.Main.Imaging;

/// <summary>手部关键点结果：一个手的外接框 + 21 个关节（坐标已换算回整幅画面）。</summary>
public sealed class HandPoseResult
{
    /// <summary>这一只手的整体置信度。</summary>
    public double Score { get; init; }

    /// <summary>手的外接框（画面坐标）。</summary>
    public (double X, double Y, double W, double H) Box { get; init; }

    /// <summary>21 个关节：画面坐标 + 各自的置信度。</summary>
    public IReadOnlyList<(double X, double Y, double Confidence)> Points { get; init; }
        = Array.Empty<(double, double, double)>();

    /// <summary>关节的重心（判断"手在哪个区域"用它最稳）。</summary>
    public (double X, double Y) Center
    {
        get
        {
            if (Points.Count == 0) return (0, 0);
            double sx = 0, sy = 0;
            foreach (var p in Points) { sx += p.X; sy += p.Y; }
            return (sx / Points.Count, sy / Points.Count);
        }
    }
}

/// <summary>
/// 手部 21 关节估计 —— 真正的姿势模型（ONNX Runtime + YOLOv8-pose 手部模型）。
///
/// <para><b>模型规格（实测确认的，不是猜的）：</b>
/// 输入 <c>[1,3,224,224]</c> float32，NCHW，取值 0~1；
/// 输出 <c>[1,68,1029]</c>：68 = 4（框）+ 1（置信度）+ 21×3（关节 x,y,置信度），
/// 1029 = 28²+14²+7² 个锚点。取分数最高的那个锚点即可。</para>
///
/// <para><b>为什么必须喂彩色：</b>实测同一张画面，彩色输入最高分 0.83，
/// 灰度复制成三通道只有 0.004 —— 等于完全认不出。
/// 所以这里吃的是"界面上显示的那张预览图"（录像/彩色相机是彩色），
/// 而产品内部的检测帧是灰度、不能直接拿来跑这个模型。</para>
/// </summary>
public sealed class HandPoseEstimator : IDisposable
{
    private const int InputSize = 224;
    private const int Keypoints = 21;

    /// <summary>相邻锚点之间的偏移（YOLO 的 cx,cy,w,h 是相对格点的，不是绝对值）。</summary>
    private static readonly int[] Strides = { 8, 16, 32 };

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public HandPoseEstimator(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("找不到手部模型", modelPath);

        // 单线程足够（224×224 一次推理几毫秒），也避免和相机线程抢 CPU
        var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public static string DefaultModelPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "models", "hand_pose.onnx");

    /// <summary>
    /// 在给定的画面区域内找一只手。
    /// </summary>
    /// <param name="image">彩色预览图（Bgr32 / Bgra32 / Rgb24 都行）。</param>
    /// <param name="region">要搜索的区域（画面像素）；传 null = 整幅。</param>
    /// <param name="minScore">低于它直接返回 null（宁可不说，也不乱标）。</param>
    public HandPoseResult? Estimate(BitmapSource image, Int32Rect? region = null, double minScore = 0.5)
    {
        var area = region ?? new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight);
        area = Clamp(area, image.PixelWidth, image.PixelHeight);
        if (area.Width < 16 || area.Height < 16) return null;

        var tensor = BuildInput(image, area);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

        float[] raw;
        int anchors;

        using (var results = _session.Run(inputs))
        {
            var output = results.First().AsTensor<float>();
            var dims = output.Dimensions.ToArray();      // [1, 68, 1029]
            anchors = dims[^1];
            raw = output.ToArray();
        }

        int stride = anchors == 0 ? 0 : raw.Length / anchors;   // 68
        if (stride < 5 + Keypoints * 3) return null;

        // 找分数最高的锚点（一只手就够了；多手以后要加 NMS，这里先不做）
        int best = 0;
        float bestScore = float.MinValue;
        for (int a = 0; a < anchors; a++)
        {
            float s = raw[4 * anchors + a];              // 第 4 行 = 置信度（列优先）
            if (s > bestScore) { bestScore = s; best = a; }
        }

        if (bestScore < minScore) return null;

        // YOLOv8：cx,cy,w,h 都是"格点偏移 × stride"
        int anchor = best;
        double gridX = 0, gridY = 0;
        int consumed = 0;
        foreach (int s in Strides)
        {
            int count = (InputSize / s) * (InputSize / s);
            if (anchor < consumed + count)
            {
                int idx = anchor - consumed;
                int side = InputSize / s;
                gridX = idx % side;
                gridY = idx / side;
                stride = s;
                break;
            }
            consumed += count;
        }

        double cx = (raw[0 * anchors + best] * 2 + gridX) * stride;
        double cy = (raw[1 * anchors + best] * 2 + gridY) * stride;
        double bw = raw[2 * anchors + best] * 2 * stride;
        double bh = raw[3 * anchors + best] * 2 * stride;

        // 从 224 的模型坐标换算回画面坐标
        double sx = area.Width / (double)InputSize;
        double sy = area.Height / (double)InputSize;
        double ox = area.X, oy = area.Y;

        var points = new List<(double, double, double)>(Keypoints);
        for (int k = 0; k < Keypoints; k++)
        {
            float px = raw[(5 + k * 3 + 0) * anchors + best];
            float py = raw[(5 + k * 3 + 1) * anchors + best];
            float pc = raw[(5 + k * 3 + 2) * anchors + best];

            points.Add((ox + px * sx, oy + py * sy, pc));
        }

        return new HandPoseResult
        {
            Score = bestScore,
            Box = (ox + (cx - bw / 2) * sx, oy + (cy - bh / 2) * sy, bw * sx, bh * sy),
            Points = points,
        };
    }

    /// <summary>把画面区域缩放到 224×224 并归一化成 NCHW。</summary>
    private static DenseTensor<float> BuildInput(BitmapSource image, Int32Rect area)
    {
        // 统一成 Bgr32 再取像素：源图可能是 Bgr24/Bgra32/Rgb24，逐个分支太容易漏
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;

        int strideBytes = w * 4;
        var pixels = new byte[strideBytes * h];
        converted.CopyPixels(new Int32Rect(0, 0, w, h), pixels, strideBytes, 0);

        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });

        for (int y = 0; y < InputSize; y++)
        {
            int srcY = area.Y + (int)((double)y * area.Height / InputSize);
            if (srcY >= h) srcY = h - 1;

            for (int x = 0; x < InputSize; x++)
            {
                int srcX = area.X + (int)((double)x * area.Width / InputSize);
                if (srcX >= w) srcX = w - 1;

                int i = srcY * strideBytes + srcX * 4;

                // Bgr32 → 模型的 RGB
                tensor[0, 0, y, x] = pixels[i + 2] / 255f;
                tensor[0, 1, y, x] = pixels[i + 1] / 255f;
                tensor[0, 2, y, x] = pixels[i] / 255f;
            }
        }

        return tensor;
    }

    private static Int32Rect Clamp(Int32Rect r, int width, int height)
    {
        int x = Math.Clamp(r.X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(r.Y, 0, Math.Max(0, height - 1));
        int w = Math.Clamp(r.Width, 1, width - x);
        int h = Math.Clamp(r.Height, 1, height - y);
        return new Int32Rect(x, y, w, h);
    }

    public void Dispose() => _session.Dispose();
}
