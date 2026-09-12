using System.IO;
using System.Windows.Media.Imaging;
using VisionForge.Core.Models;

namespace VisionForge.Main.Imaging;

/// <summary>
/// 画面文件编解码 —— 把落盘的保持图/样本图重新读成 <see cref="CameraFrame"/>。
///
/// <para><b>为什么需要它：</b>方案 10.3 要求"历史回放测试" ——
/// 调取过往图像、重跑一遍判定，验证"算法改版前后结果一致"。
/// 回放的前提就是能把存下来的 PNG 读回成算法认识的帧格式。</para>
///
/// <para>实现放在 Main 层（而不是 Core/Infrastructure）：解码用的是 WPF 的图像解码器，
/// 只有这层依赖它。Core 依然不认识任何图像库。</para>
/// </summary>
public static class FrameFileCodec
{
    /// <summary>读一张图，转成 Mono8 的 <see cref="CameraFrame"/>。读不了返回 null。</summary>
    public static CameraFrame? LoadGrayFrame(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 读完就释放文件，不占句柄
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();

            // 统一转成 8 位灰度，和相机出来的 Mono8 对齐
            var gray = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Gray8, null, 0);
            gray.Freeze();

            int width = gray.PixelWidth;
            int height = gray.PixelHeight;
            int stride = width;
            var data = new byte[stride * height];
            gray.CopyPixels(data, stride, 0);

            return new CameraFrame(width, height, stride, PixelFormat.Mono8, data)
            {
                Timestamp = File.GetLastWriteTime(path),
            };
        }
        catch
        {
            return null;
        }
    }
}
