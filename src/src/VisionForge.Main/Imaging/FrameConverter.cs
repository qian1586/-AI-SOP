using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionForge.Core.Models;

namespace VisionForge.Main.Imaging;

/// <summary>
/// 把 Core 层的 <see cref="CameraFrame"/> 转成 WPF 能显示的位图。
///
/// 放在 View 层而不是 Core/Infrastructure，是有意的：
///   Core 定义"帧"长什么样，但不该知道 WPF 的存在。
///   将来做 Web 版，这段换成 JPEG 编码；做 WinForms 版，换成 Bitmap。
///   转换逻辑跟着界面框架走，这才是分层的意义。
/// </summary>
public static class FrameConverter
{
    /// <summary>
    /// 转换成 BitmapSource。
    ///
    /// 注意 Freeze()：冻结后位图变成不可变对象，
    /// 可以跨线程访问，也能被 WPF 缓存复用，避免每次刷新都重新上传纹理。
    /// 相机回调在后台线程，不 Freeze 的话在 UI 线程用会直接抛异常。
    /// </summary>
    public static BitmapSource ToBitmapSource(CameraFrame frame)
    {
        var format = frame.Format == VisionForge.Core.Models.PixelFormat.Mono8
            ? PixelFormats.Gray8
            : PixelFormats.Bgr24;

        var bmp = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96, 96,
            format,
            null,
            frame.Data,
            frame.Stride);

        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// 生成一张占位图（无相机时显示），避免界面上是一片纯黑让人以为程序挂了。
    /// </summary>
    public static BitmapSource CreatePlaceholder(int width = 640, int height = 480, string? text = null)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 28, 30)), null,
                             new System.Windows.Rect(0, 0, width, height));

            var brush = new SolidColorBrush(Color.FromRgb(90, 90, 95));
            var typeface = new Typeface("Microsoft YaHei");
            var line = text ?? "未连接相机";
            var ft = new FormattedText(line,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 22, brush, 96);

            dc.DrawText(ft, new System.Windows.Point(
                (width - ft.Width) / 2, (height - ft.Height) / 2));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
