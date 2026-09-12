using System.Windows.Input;
using VisionForge.Common.Mvvm;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 画面区域的缩放与平移（鼠标滚轮放大缩小、左键按住拖动挪画面）。
///
/// <para><b>为什么放在 ViewModel 而不是全写在 View 的代码里：</b>
/// 缩放是"围绕鼠标位置缩放"的数学问题 —— 鼠标指着的那个点，放大后还得在鼠标底下。
/// 这段算术最容易写错，放在 ViewModel 里就能被自检脚本直接验证（UI-04/UI-05），
/// View 层只负责把鼠标事件和数值搬来搬去。</para>
///
/// <para><b>坐标约定：</b>画面内容坐标 u 与屏幕（画面框内）坐标 c 的关系是
/// <c>c = pan + scale × u</c>。scale=1、pan=0 就是"图片正好铺满框"的初始状态。</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>最小缩放倍数。缩太小看不清，再小也没意义。</summary>
    public const double MinViewportScale = 0.2;

    /// <summary>最大缩放倍数。工位照片放到 12 倍，连螺丝都能看清楚。</summary>
    public const double MaxViewportScale = 12.0;

    /// <summary>点一次「＋/－」的倍数。</summary>
    private const double ZoomStep = 1.25;

    private double _viewportScale = 1.0;
    private double _viewportPanX;
    private double _viewportPanY;

    // 画面框的当前尺寸（由 View 在 SizeChanged 时同步过来）。
    // 「＋/－」按钮没有鼠标位置，就以画面中心为锚点缩放。
    private double _viewportWidth = 960;
    private double _viewportHeight = 720;

    /// <summary>放大（以画面中心为锚点）。</summary>
    public ICommand ZoomInCommand { get; }

    /// <summary>缩小（以画面中心为锚点）。</summary>
    public ICommand ZoomOutCommand { get; }

    /// <summary>复位视图：回到 100%、居中。</summary>
    public ICommand ResetViewportCommand { get; }

    /// <summary>当前缩放倍数（1.0 = 正好铺满）。</summary>
    public double ViewportScale
    {
        get => _viewportScale;
        private set
        {
            if (!SetProperty(ref _viewportScale, value)) return;
            OnPropertyChanged(nameof(ZoomText));
        }
    }

    public double ViewportPanX
    {
        get => _viewportPanX;
        private set => SetProperty(ref _viewportPanX, value);
    }

    public double ViewportPanY
    {
        get => _viewportPanY;
        private set => SetProperty(ref _viewportPanY, value);
    }

    /// <summary>界面上显示的缩放百分比。</summary>
    public string ZoomText => $"{_viewportScale * 100:F0}%";


    /// <summary>是否已经不在初始视图（用于"复位"按钮的可用状态）。</summary>
    public bool IsViewDefault =>
        Math.Abs(_viewportScale - 1.0) < 0.001 &&
        Math.Abs(_viewportPanX) < 0.5 &&
        Math.Abs(_viewportPanY) < 0.5;

    /// <summary>View 在尺寸变化时把画面框的实际尺寸报进来。</summary>
    public void SetViewportSize(double width, double height)
    {
        if (width > 0) _viewportWidth = width;
        if (height > 0) _viewportHeight = height;
        ClampViewport();   // 窗口变大了，画面位置要跟着重新夹一次
    }

    /// <summary>
    /// 围绕某个点缩放。
    ///
    /// <para>锚点不变式：鼠标下面那个"内容点"缩放前后必须还在鼠标下面。
    /// 推导：设 <c>k = 新倍数 / 旧倍数</c>，则 <c>pan' = anchor − k × (anchor − pan)</c>。
    /// 少了这一步，缩放就会"跑偏"——放大的同时画面往一边飞，现场很难用。</para>
    /// </summary>
    public void ZoomAt(double anchorX, double anchorY, double factor)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0) return;

        double target = Math.Clamp(_viewportScale * factor, MinViewportScale, MaxViewportScale);
        if (Math.Abs(target - _viewportScale) < 1e-9) return;   // 已到上下限

        double k = target / _viewportScale;
        double newPanX = anchorX - k * (anchorX - _viewportPanX);
        double newPanY = anchorY - k * (anchorY - _viewportPanY);

        ViewportPanX = newPanX;
        ViewportPanY = newPanY;
        ViewportScale = target;
        ClampViewport();
        RaiseViewportProps();
    }

    /// <summary>拖动画面：把平移量设成指定位移（绝对值，避免累积误差）。</summary>
    public void SetViewportPan(double x, double y)
    {
        ViewportPanX = x;
        ViewportPanY = y;
        ClampViewport();
        RaiseViewportProps();
    }

    /// <summary>回到 100%、居中。</summary>
    public void ResetViewport()
    {
        ViewportScale = 1.0;
        ViewportPanX = 0;
        ViewportPanY = 0;
        ClampViewport();
        RaiseViewportProps();
    }

    /// <summary>
    /// 把画面夹在画面框内 —— 这是"只能在对应的框里面动，不覆盖其他地方"的保证。
    ///
    /// <para>规则和看图软件一致：</para>
    /// <list type="bullet">
    ///   <item>放大后（≥100%）：画面必须始终盖满整个框，不允许拖出空白边</item>
    ///   <item>缩小时（&lt;100%）：画面居中，不允许拖到一边去</item>
    /// </list>
    ///
    /// <para>注意画面框和图片的宽高比不一定一致（预览画布固定 960×720）：
    /// 框比图"宽"时，Viewbox 会在左右留边再居中。夹取范围要把这段留边算进去，
    /// 否则放大后仍然能拖出一块空白 —— 那就等于没夹。</para>
    /// </summary>
    private void ClampViewport()
    {
        if (_viewportWidth <= 0 || _viewportHeight <= 0) return;

        // Viewbox 等比缩放后的"铺满尺寸"与居中留边
        double aspect = RoiCanvasWidth / RoiCanvasHeight;
        double fitW = Math.Min(_viewportWidth, _viewportHeight * aspect);
        double fitH = fitW / aspect;

        double offsetX = (_viewportWidth - fitW) / 2.0;
        double offsetY = (_viewportHeight - fitH) / 2.0;

        double scaledW = fitW * _viewportScale;
        double scaledH = fitH * _viewportScale;

        // 整幅都看得见 -> 居中，不允许拖；放大到超出 -> 只能拖到"边缘贴住框"
        ViewportPanX = scaledW <= _viewportWidth
            ? 0
            : Math.Clamp(_viewportPanX,
                         _viewportWidth - offsetX * _viewportScale - scaledW,
                         -offsetX * _viewportScale);

        ViewportPanY = scaledH <= _viewportHeight
            ? 0
            : Math.Clamp(_viewportPanY,
                         _viewportHeight - offsetY * _viewportScale - scaledH,
                         -offsetY * _viewportScale);
    }

    /// <summary>缩放/平移变化后刷新派生属性与命令可用状态。</summary>
    private void RaiseViewportProps()
    {
        OnPropertyChanged(nameof(IsViewDefault));
        (ResetViewportCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ZoomIn() => ZoomAt(_viewportWidth / 2.0, _viewportHeight / 2.0, ZoomStep);

    private void ZoomOut() => ZoomAt(_viewportWidth / 2.0, _viewportHeight / 2.0, 1.0 / ZoomStep);
}
