using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using VisionForge.Core.Services;
using VisionForge.Main.ViewModels;

namespace VisionForge.Main;

/// <summary>
/// 主窗口。
///
/// View 层只做"界面才该做的事"：
///   1. 订阅 ViewModel 抛出的报警事件，弹窗 + 响警
///   2. 转发用户交互（按钮走 Command 绑定，画面鼠标画框在这里转发坐标）
///
/// <b>为什么 ROI 鼠标画框的坐标转发放在这里：</b>
/// 鼠标事件（MouseDown/Move/Up）是纯 View 概念，ViewModel 不该依赖 WPF 的输入事件。
/// 这里只做一件事：把 e.GetPosition 得到的基准画布坐标转发给 ViewModel，
/// 命中检测、归一化换算、写回配方这些"有逻辑"的部分都在 ViewModel 里。
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _boundViewModel;
    private bool _isDrawingRoi;
    private bool _dragMoved;      // 本次拖拽是否收到过移动事件（只影响"拖动时看不看得到预览"）
    private Point _draftStart;    // 本次按下的起点（基准画布坐标）

    /// <summary>
    /// 判定"这是拖出来的框"还是"点了一下"的距离门槛（基准画布像素）。
    ///
    /// <para>为什么按起点→终点的距离判，而不是等 MouseMove 事件：
    /// 现场出现过"按住拖、松手什么都没有"——按下和松开事件都收到了，中间的移动事件丢了。
    /// 按下点和松开点的坐标是可靠拿到的，用这两点重建矩形，框就一定画得出来。</para>
    /// </summary>
    private const double DragThresholdPx = 6;

    /// <summary>单击（没有拖动）时生成的默认框大小，基准画布像素。</summary>
    private const double ClickDefaultBoxPx = 160;

    // ---- 画面缩放 / 平移（鼠标滚轮 + 左键拖动）----
    private bool _isPanning;
    private MouseButton _panButton;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    /// <summary>滚轮每格的缩放倍数（乘/除）。1.15 手感比较顺，不飘。</summary>
    private const double WheelZoomFactor = 1.15;

    public MainWindow()
    {
        InitializeComponent();

        // 窗口图标单独在这里加载，并且包一层 try/catch。
        // 教训：图标文件格式不被 WPF 接受时，XAML 里的 Icon="..." 会让
        // InitializeComponent() 直接抛异常 —— 窗口根本构造不出来，
        // 表现出来就是"弹一句界面异常，然后没有界面"。
        // 图标只是装饰，绝不该有能力拖垮窗口。
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/logo.ico", UriKind.Absolute));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("窗口图标加载失败（忽略）：" + ex.Message);
        }

        DataContextChanged += OnDataContextChanged;

        // ROI 标定：在基准画布上挂鼠标事件。
        //
        // 用 Preview（隧道）而不是普通冒泡事件：隧道从根往下走，先于任何子控件触发，
        // 不会被画面上的浮层（提示条、工具条、叠加框）吃掉。
        // 之前用冒泡事件时现场反馈"拖拽画不出框"，就是事件被上层控件截走了。
        RoiCanvasHost.PreviewMouseLeftButtonDown += OnRoiMouseDown;
        RoiCanvasHost.PreviewMouseMove += OnRoiMouseMove;
        RoiCanvasHost.PreviewMouseLeftButtonUp += OnRoiMouseUp;
        RoiCanvasHost.PreviewMouseRightButtonDown += OnRoiRightClick;

        // 移动事件再挂一层到窗口（隧道，从根往下走）。
        // 画布上的那份只是"看得见的预览"；即使它收不到，下面 OnRoiMouseUp 也照样能把框画出来。
        PreviewMouseMove += OnRoiMouseMove;

        // 兜底：鼠标在画面外松开时也要结束画框 ——
        // 否则 _isDrawingRoi 会一直为 true，下一次拖拽就乱了。
        // 这是隧道事件，会在画布自己的处理之前先跑；两处都判 _isDrawingRoi，不会重复提交。
        PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (_isDrawingRoi) OnRoiMouseUp(s, e);
        };

        // 第二层保险：把同一套处理也挂在"画面区域"根容器上（冒泡路径）。
        // 场景：拖拽起点落在画面上的浮层（标定提示条、工具条）上时，
        // 事件不会经过画布，只挂画布就会"怎么拖都没反应"。
        // OnRoiMouseDown 里有 _isDrawingRoi 判重，两处都挂不会重复开框。
        PreviewHost.MouseLeftButtonDown += OnRoiMouseDown;
        PreviewHost.MouseMove += OnRoiMouseMove;
        PreviewHost.MouseLeftButtonUp += OnRoiMouseUp;
        PreviewHost.MouseRightButtonDown += OnRoiRightClick;

        // ---- 画面缩放 / 平移 ----
        // 滚轮缩放挂在"画面区"根容器上：整个画面框里随便哪个位置滚都缩放，
        // 包括鼠标压在工具条 / 提示条上的时候（现场操作员不会去瞄准"图像"才滚）。
        PreviewHost.PreviewMouseWheel += OnPreviewWheel;

        // 按住左键拖 = 挪画面；按住滚轮拖 = 任何时候都能挪（标定模式下左键要用来画框）。
        PreviewViewport.PreviewMouseDown += OnPreviewMouseDown;
        PreviewViewport.PreviewMouseMove += OnPreviewMouseMove;
        PreviewViewport.PreviewMouseUp += OnPreviewMouseUp;

        // 视口尺寸变了要告诉 ViewModel：「＋/－」按钮是以画面中心为锚点缩放的
        PreviewViewport.SizeChanged += (_, e) =>
            _boundViewModel?.SetViewportSize(e.NewSize.Width, e.NewSize.Height);

        // 说明：框位格子的右键现在走各自的 ContextMenu（见 MainWindow.xaml 里的
        // "步骤设置 / 删除这个框"），不再用窗口级的右键事件 —— 两者同时挂会导致
        // "菜单刚弹出、设置窗口也弹出来"这种重复响应。
    }

    /// <summary>
    /// 框位格子右键 →「步骤设置」：把这一格对应的工序打开来改名称/参数。
    /// </summary>
    private void OnRoiTileSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null) return;

        if ((sender as FrameworkElement)?.DataContext is not RoiTargetOption option) return;

        var step = _boundViewModel.Sop.Steps.FirstOrDefault(s => s.Seq == option.Index);
        if (step is null)
        {
            _boundViewModel.NoteRoiAction($"第 {option.Index} 个框还没有对应工序，先点「⇄ 同步为判定区域」");
            return;
        }

        var model = _boundViewModel.CreateStepEdit(step);
        if (model is null) return;

        var dialog = new StepEditWindow { Owner = this, DataContext = model };

        bool accepted;
        try
        {
            accepted = dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            // 对话框只是"改参数"，它要是自己出了问题，也不能把主界面拖垮
            _boundViewModel.NoteRoiAction("步骤设置窗口打开失败：" + ex.Message);
            return;
        }

        if (!accepted) return;
        if (!_boundViewModel.ApplyStepEdit(model)) return;

        if (model.RequestReframe)
            _boundViewModel.GoRoiEditCommand.Execute(null);
    }

    /// <summary>
    /// 框位格子右键 →「删除这个框」。
    ///
    /// <para>删除是不可逆的（框、这一格的样本、对应的工序、判定区域都会一起删），
    /// 所以先弹一次确认 —— 现场误删一个标好的框要重画、重教，代价不小。</para>
    /// </summary>
    private void OnRoiTileDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not RoiTargetOption option) return;

        var answer = MessageBox.Show(this,
            $"确定删除第 {option.Index} 个框「{option.Name}」？\n\n" +
            "会一起删掉：这个框、它的学习样本、对应的工序、以及判定区域里的作业位。\n" +
            "（框的位置需要重新框，学习样本需要重新教）",
            "删除框位",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        _boundViewModel.DeleteRoiByIndex(option.Index);
    }

    /// <summary>
    /// 框位格子右键 →「标记误判：其实是对的 / 其实是错的」。
    ///
    /// <para>这就是"误检修正"的入口：人不用切页面、不用找下拉框，
    /// 直接在这个框上右键说一句"你判错了"，当时那一帧立刻变成纠错样本。
    /// 学的是基恩士那套 Adjustment Navigation 的思路 ——
    /// 把工程师调参的经验固化成现场能点的按钮。</para>
    /// </summary>
    private void OnRoiTileMarkOkClick(object sender, RoutedEventArgs e)
        => MarkRoiTile(sender, isOk: true);

    private void OnRoiTileMarkNgClick(object sender, RoutedEventArgs e)
        => MarkRoiTile(sender, isOk: false);

    private void MarkRoiTile(object sender, bool isOk)
    {
        if (_boundViewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not RoiTargetOption option) return;

        // 先把这个框选中，纠错命令作用于"当前选中的框"
        _boundViewModel.SelectRoiTargetCommand.Execute(option);

        var command = isOk
            ? _boundViewModel.MarkRoiCorrectOkCommand
            : _boundViewModel.MarkRoiCorrectNgCommand;

        if (command.CanExecute(null)) command.Execute(null);
        else _boundViewModel.NoteRoiAction("先点「启动监测」让相机出图，再标记误判");
    }

    /// <summary>
    /// 配方页切换检测算法。参数面板的内容由算法自己声明，
    /// 所以换算法必须重建面板（这是"插件化 + 动态参数"能成立的前提）。
    /// </summary>
    private void OnAlgorithmChanged(object sender, SelectionChangedEventArgs e)
    {
        _boundViewModel?.OnAlgorithmChanged();
    }

    // ==================================================================
    // 动作时间：设置窗口 / 回看窗口
    // ==================================================================
    private void OnOpenTimingSettings(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null) return;

        var model = _boundViewModel.CreateTimingSettings();
        var dialog = new TimingSettingsWindow { Owner = this, DataContext = model };

        if (dialog.ShowDialog() == true)
            _boundViewModel.ApplyTimingSettings(model);
    }

    private void OnOpenTimingReview(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null) return;

        // 数据改了不用重开窗口：窗口自己订阅了 TimingRecords 的集合变化
        var window = new TimingReviewWindow { Owner = this, DataContext = _boundViewModel };
        window.Show();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 解绑旧的，避免重复订阅导致弹两次窗
        if (_boundViewModel is not null)
        {
            _boundViewModel.Alarm -= OnAlarm;
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundViewModel = e.NewValue as MainViewModel;

        if (_boundViewModel is not null)
        {
            _boundViewModel.Alarm += OnAlarm;
            _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        // 按配置里的界面缩放倍数调整窗口大小（放大后超出屏幕就最大化）
        ApplyScaleToWindow();

        // 换 DataContext 时也要把当前的缩放/平移落到画面上（否则视图会"看起来复位了、其实没有"）
        ApplyViewportTransform();
    }

    /// <summary>
    /// 界面缩放变化时同步调整窗口。
    ///
    /// <para>放大内容后如果窗口不变，右侧/下侧会被裁掉 —— 这是缩放功能最容易踩的坑。
    /// 所以这里按倍数放大窗口；一旦超出屏幕可用区域就直接最大化（工控机上最大化本来就是常态）。</para>
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UiScale))
            ApplyScaleToWindow();
        else if (e.PropertyName == nameof(MainViewModel.ViewportScale) ||
                 e.PropertyName == nameof(MainViewModel.ViewportPanX) ||
                 e.PropertyName == nameof(MainViewModel.ViewportPanY))
            ApplyViewportTransform();
    }

    /// <summary>
    /// 把 ViewModel 里的缩放倍数 / 平移量落到画面上。
    ///
    /// <para>变换加在内层 <c>ZoomHost</c>（Viewbox）上，不是加在 PreviewViewport 上 ——
    /// PreviewViewport 负责裁剪，裁剪矩形不能被放大，否则画面会盖到别的区域。</para>
    ///
    /// <para>用 MatrixTransform 现算现建，而不是在 XAML 里给 ScaleTransform/TranslateTransform
    /// 设 x:Name 再改属性 —— 后者在部分环境下变换对象会被冻结（Freezable），
    /// 一改就抛"不能修改冻结对象"，整块界面跟着挂。新建一个矩阵最省心。</para>
    /// </summary>
    private void ApplyViewportTransform()
    {
        if (_boundViewModel is null) return;

        // 顺手把画面框的实际尺寸报给 ViewModel（「＋/－」以中心为锚点，需要它）
        _boundViewModel.SetViewportSize(PreviewViewport.ActualWidth, PreviewViewport.ActualHeight);

        double s = _boundViewModel.ViewportScale;
        ZoomHost.RenderTransform = new System.Windows.Media.MatrixTransform(
            new System.Windows.Media.Matrix(s, 0, 0, s,
                                            _boundViewModel.ViewportPanX,
                                            _boundViewModel.ViewportPanY));
    }

    private void ApplyScaleToWindow()
    {
        double scale = _boundViewModel?.UiScale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        double targetWidth = 1440 * scale;
        double targetHeight = 900 * scale;
        var workArea = SystemParameters.WorkArea;

        if (targetWidth > workArea.Width || targetHeight > workArea.Height)
        {
            // 放不下就最大化，保证不裁切
            WindowState = WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        Width = targetWidth;
        Height = targetHeight;
    }

    // ==================================================================
    // ROI 标定：鼠标画框
    // ==================================================================

    // ------------------------------------------------------------------
    // 画面缩放 / 平移
    // ------------------------------------------------------------------

    /// <summary>滚轮缩放：以鼠标位置为中心（鼠标指着哪，放大后那里还在鼠标底下）。</summary>
    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (_boundViewModel is null) return;
        if (e.Delta == 0) return;

        var anchor = e.GetPosition(PreviewHost);   // PreviewHost 没被变换，坐标可直接当锚点

        // 鼠标压在底部步骤条上滚动时，锚点会落到画面区域之外 —— 夹回画面内，
        // 否则画面会按一个"画面外的点"缩放，看着像乱飞。
        if (PreviewViewport.ActualHeight > 0)
            anchor.Y = Math.Min(anchor.Y, PreviewViewport.ActualHeight);

        double factor = e.Delta > 0 ? WheelZoomFactor : 1.0 / WheelZoomFactor;
        _boundViewModel.ZoomAt(anchor.X, anchor.Y, factor);

        e.Handled = true;   // 别让外层滚动区跟着滚
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool isMiddle = e.ChangedButton == MouseButton.Middle;
        bool isLeft = e.ChangedButton == MouseButton.Left;
        if (!isMiddle && !isLeft) return;

        // 标定模式下左键是"画框"，这时挪画面请按住滚轮拖 —— 两条路不能抢同一个按键
        if (isLeft && _boundViewModel?.IsRoiEditMode == true) return;

        if (_isPanning) return;
        _isPanning = true;
        _panButton = e.ChangedButton;
        _panStart = e.GetPosition(PreviewHost);
        _panStartX = _boundViewModel?.ViewportPanX ?? 0;
        _panStartY = _boundViewModel?.ViewportPanY ?? 0;

        PreviewViewport.CaptureMouse();
        PreviewViewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;

        var p = e.GetPosition(PreviewHost);
        _boundViewModel?.SetViewportPan(_panStartX + (p.X - _panStart.X),
                                        _panStartY + (p.Y - _panStart.Y));
        e.Handled = true;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning || e.ChangedButton != _panButton) return;

        var p = e.GetPosition(PreviewHost);
        bool moved = Math.Abs(p.X - _panStart.X) + Math.Abs(p.Y - _panStart.Y) > 4;

        _isPanning = false;
        PreviewViewport.ReleaseMouseCapture();
        PreviewViewport.Cursor = null;   // 恢复默认光标（标定模式的十字在画布自己身上）

        // 只是点了一下（没拖动）= 选中点到的那个框，准备教 OK / NG。
        // 现场更自然：指着画面上的框就能教，不用去下拉里找第几个。
        if (!moved && e.ChangedButton == MouseButton.Left && _boundViewModel is not null)
        {
            var canvasPoint = e.GetPosition(RoiCanvasHost);
            _boundViewModel.SelectRoiAtCanvasPoint(canvasPoint.X, canvasPoint.Y);
        }

        e.Handled = true;
    }

    private void OnRoiMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_boundViewModel is null || !_boundViewModel.IsRoiEditMode) return;

        var p = e.GetPosition(RoiCanvasHost);   // 得到 960×720 基准画布坐标

        if (_isDrawingRoi) return;   // 画布与容器两处都挂了处理，这里判重

        _isDrawingRoi = true;
        _dragMoved = false;
        _draftStart = p;
        _boundViewModel.BeginRoiDraft(p.X, p.Y);

        // 捕获鼠标：拖到画面外面也要能收到 Move/Up
        bool captured = RoiCanvasHost.CaptureMouse();
        _boundViewModel.NoteRoiAction(
            $"按下 ({p.X:F0},{p.Y:F0})  画布 {RoiCanvasHost.ActualWidth:F0}×{RoiCanvasHost.ActualHeight:F0}  捕获={captured}");
        e.Handled = true;
    }

    private void OnRoiMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawingRoi) return;

        _dragMoved = true;
        var p = e.GetPosition(RoiCanvasHost);
        _boundViewModel?.UpdateRoiDraft(p.X, p.Y);
    }

    private void OnRoiMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawingRoi) return;

        var p = e.GetPosition(RoiCanvasHost);

        _isDrawingRoi = false;
        RoiCanvasHost.ReleaseMouseCapture();

        // 关键：用"按下的起点 + 松开时的终点"定框。
        // 拖动过程中的 MouseMove 丢不丢都不影响结果 —— 这是这次修"画不出框"的核心。
        _boundViewModel?.UpdateRoiDraft(p.X, p.Y);

        double dx = Math.Abs(p.X - _draftStart.X);
        double dy = Math.Abs(p.Y - _draftStart.Y);

        // 只看"按下点 → 松开点"的距离，不看有没有收到移动事件：
        // 鼠标是手，点一下也常带着 1~2 像素抖动，会收到移动事件；
        // 若把"收到过移动事件"也算成拖拽，单击就会被当成 0×0 的框给丢掉（日志里出现过）。
        bool dragged = Math.Max(dx, dy) >= DragThresholdPx;

        if (!dragged)
        {
            // 只是点了一下：以点击点为中心生成一个默认大小的框（现场"点一下就有框"）
            _boundViewModel?.SetRoiDraftCentered(_draftStart.X, _draftStart.Y, ClickDefaultBoxPx);
            _boundViewModel?.NoteRoiAction(
                $"单击 ({_draftStart.X:F0},{_draftStart.Y:F0}) → 生成默认 {ClickDefaultBoxPx:F0}px 框");
        }
        else
        {
            _boundViewModel?.NoteRoiAction(
                $"松开 ({p.X:F0},{p.Y:F0})  起点 ({_draftStart.X:F0},{_draftStart.Y:F0})  " +
                $"→ 提交 {dx:F0}×{dy:F0} 框（收到过移动事件={_dragMoved}）");
        }

        _boundViewModel?.CommitRoiDraft();

        e.Handled = true;
    }

    private void OnRoiRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_boundViewModel is null || !_boundViewModel.IsRoiEditMode) return;

        var p = e.GetPosition(RoiCanvasHost);
        _boundViewModel.DeleteRoiAt(p.X, p.Y);
        e.Handled = true;
    }

    // ==================================================================
    // 报警
    // ==================================================================

    private void OnAlarm(object? sender, AlarmEventArgs e)
    {
        // 报警声音：NG 锁线必须让整个车间听见，不能只靠屏幕变红
        try
        {
            SystemSounds.Exclamation.Play();
        }
        catch
        {
            // 有些工控机被禁用了音频设备，响不了也不能让程序崩
        }

        var result = e.Result;
        var message =
            $"步骤：{e.Step.Seq}. {e.Step.Name}\n\n" +
            $"判定：{result.VerdictText}\n\n" +
            $"理由：{result.JudgementReason}\n\n" +
            (result.Measurements.Count > 0
                ? "测量值：\n  " + string.Join("\n  ", result.MeasurementSummary())
                : string.Empty);

        if (!string.IsNullOrEmpty(result.EvidenceImagePath))
            message += $"\n\n取证图片已保存：\n{result.EvidenceImagePath}";

        MessageBox.Show(this, message, "检测不合格 · 流程已锁死",
                        MessageBoxButton.OK, MessageBoxImage.Warning);

        // 复位判定显示，避免操作员修好后还看到旧的 NG 大字
        _boundViewModel?.Sop.Reset();
        _boundViewModel?.Sop.Load(_boundViewModel.Sop.Definition!);
    }
}
