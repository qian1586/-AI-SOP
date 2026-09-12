namespace VisionForge.Core.Models;

/// <summary>像素格式。先只支持工业现场最常用的两种。</summary>
public enum PixelFormat
{
    /// <summary>8 位灰度 —— 尺寸测量、Blob 分析首选。</summary>
    Mono8 = 0,

    /// <summary>24 位彩色，BGR 排列（海康/OpenCV 的默认顺序）。</summary>
    Bgr24 = 1,
}

/// <summary>
/// 一帧图像。
///
/// 设计要点：<b>不依赖任何图像库</b>（不用 OpenCV 的 Mat，也不用 GDI 的 Bitmap）。
/// 只保留最原始的字节缓冲 + 元数据。好处：
///   1. Core / Hardware 层不被迫引入图像库，编译依赖干净
///   2. 换算法库（OpenCV / Halcon / 自研）时不用改数据契约
///   3. WPF 层可以把它转成 BitmapSource，Web 层可以转成 PNG 流，各取所需
/// 这就是"硬件抽象层"该有的样子。
/// </summary>
public sealed class CameraFrame
{
    public CameraFrame(int width, int height, int stride, PixelFormat format, byte[] data)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        Data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat Format { get; }
    public byte[] Data { get; }

    /// <summary>采集时间戳。用于节拍统计与"取离触发最近的一帧"。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>帧号，便于排查丢帧。</summary>
    public long FrameNumber { get; init; }

    /// <summary>取指定像素的灰度值（Mono8）。</summary>
    public byte GetGray(int x, int y) => Data[y * Stride + x];

    /// <summary>取指定像素的 BGR（Bgr24）。</summary>
    public (byte B, byte G, byte R) GetBgr(int x, int y)
    {
        int offset = y * Stride + x * 3;
        return (Data[offset], Data[offset + 1], Data[offset + 2]);
    }

    /// <summary>按亮度加权转灰度。Bgr24 转 Mono8 时用，权重取 ITU-R BT.601。</summary>
    public byte GetLuminance(int x, int y)
    {
        if (Format == PixelFormat.Mono8) return GetGray(x, y);
        var (b, g, r) = GetBgr(x, y);
        return (byte)Math.Clamp((r * 299 + g * 587 + b * 114) / 1000, 0, 255);
    }
}

/// <summary>相机的静态描述信息。UI 上显示、配置里持久化都用它。</summary>
public sealed class CameraInfo
{
    public string Id { get; set; } = string.Empty;

    /// <summary>界面显示名，如"工位1-海康MV-CS060"。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>品牌，如 Hikvision / Daheng / Basler / Mock。</summary>
    public string Vendor { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>序列号。多相机时用于唯一绑定，避免接错工位。</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>网口相机 IP（GigE）或空（USB3）。</summary>
    public string? IpAddress { get; set; }

    public override string ToString() => $"{DisplayName} ({Model})";
}
