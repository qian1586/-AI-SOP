using System.Buffers.Binary;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// 把 <see cref="CameraFrame"/> 编码成 24 位 BMP 并落盘。
///
/// 为什么自己写编码器，不用 WPF 的 PngBitmapEncoder 或 System.Drawing：
///   1. 存储层不该依赖 UI 框架。用了 WPF 编码器，Infrastructure 就得引用 WPF，
///      那这套"分层"就破了（将来做 Web 版 / 控制台版会很难受）
///   2. System.Drawing.Common 在 .NET 6+ 已被限制为 Windows 专用，且要额外引包
///   3. BMP 格式简单到"一个下午能写完并测透"，而 PNG 要处理 zlib 压缩、CRC，
///      自己写不值得引依赖换来的那点体积收益
///
/// 代价：文件比 PNG 大（1280×1024 灰度约 3.9MB）。
/// 对策：NG 图只存灰度 + 按保留天数自动清理，实际占用可控。真嫌大再引 SkiaSharp 不迟。
/// </summary>
public static class BmpEncoder
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PixelOffset = FileHeaderSize + InfoHeaderSize;   // 54

    public static void Save(CameraFrame frame, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, Encode(frame));
    }

    public static byte[] Encode(CameraFrame frame)
    {
        int width = frame.Width;
        int height = frame.Height;

        // BMP 每行必须是 4 字节的整数倍
        int rowStride = (width * 3 + 3) / 4 * 4;
        int pixelDataSize = rowStride * height;
        int fileSize = PixelOffset + pixelDataSize;

        var buffer = new byte[fileSize];

        // ---------- 文件头（14 字节） ----------
        buffer[0] = (byte)'B';
        buffer[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(2), fileSize);
        // 4..8 reserved，保持 0
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(10), PixelOffset);

        // ---------- 信息头（40 字节） ----------
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(14), InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(22), height);   // 正值 = 自下而上
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(26), 1);        // planes
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(28), 24);       // 24 位
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(30), 0);        // 不压缩
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(34), pixelDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(38), 2835);     // 72 DPI
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(42), 2835);

        // ---------- 像素数据（BGR，自下而上） ----------
        for (int y = 0; y < height; y++)
        {
            // BMP 是自下而上存储，所以源图第 y 行要写到目标第 (height-1-y) 行
            int dstRow = PixelOffset + (height - 1 - y) * rowStride;

            for (int x = 0; x < width; x++)
            {
                int dst = dstRow + x * 3;

                if (frame.Format == PixelFormat.Mono8)
                {
                    byte g = frame.GetGray(x, y);
                    buffer[dst] = g;        // B
                    buffer[dst + 1] = g;    // G
                    buffer[dst + 2] = g;    // R
                }
                else
                {
                    var (b, g, r) = frame.GetBgr(x, y);
                    buffer[dst] = b;
                    buffer[dst + 1] = g;
                    buffer[dst + 2] = r;
                }
            }
            // 行末 padding 已是 0，无需处理
        }

        return buffer;
    }

    /// <summary>
    /// 按日期分目录生成 NG 图路径。
    /// 分目录是必须的 —— 一条线一天可能几十张 NG 图，全堆一个目录里，
    /// 半年后资源管理器打开都会卡。
    /// </summary>
    public static string BuildNgImagePath(string rootDirectory, DateTime timestamp, string recipeName, string reason)
    {
        var dayDir = Path.Combine(rootDirectory, timestamp.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayDir);

        var safeRecipe = SanitizeFileName(recipeName);
        var safeReason = SanitizeFileName(reason);
        if (safeReason.Length > 24) safeReason = safeReason[..24];

        var name = $"{timestamp:HHmmss_fff}_{safeRecipe}_{safeReason}.bmp";
        return Path.Combine(dayDir, name);
    }

    /// <summary>清掉非法文件名字符。现场配方名里带 / : * 是常有的事。</summary>
    private static string SanitizeFileName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var result = new string(chars).Trim('_');
        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }
}
