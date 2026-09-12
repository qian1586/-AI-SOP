using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VisionForge.Core.Models;
using VisionForge.Main.ViewModels;

namespace VisionForge.Main;

/// <summary>
/// 「动作时间回看」窗口：查询 + 趋势图 + 工序耗时排行（帕累托）+ 明细。
///
/// <para><b>为什么自己画图而不是引第三方图表库：</b>这套软件要能在没有外网的工控机上
/// 部署和重建，多一个 NuGet 依赖就多一个装不上的风险。这里只有柱、折线、文字三种元素，
/// 用 Canvas 画不到一百行，反而更稳、启动更快。</para>
/// </summary>
public partial class TimingReviewWindow : Window
{
    private MainViewModel? _vm;
    private bool _hooked;

    /// <summary>趋势图最多显示多少次（再多就看不清了）。</summary>
    private const int TrendPointLimit = 30;

    public TimingReviewWindow()
    {
        InitializeComponent();

        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/logo.ico", UriKind.Absolute));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("动作时间窗口图标加载失败（忽略）：" + ex.Message);
        }

        // DataContext 是"窗口构造完之后"由调用方赋的，所以不能在构造函数里取 ——
        // 这里拖到 Loaded：那时 DataContext 一定就位，顺便把当天数据先查出来。
        Loaded += (_, _) =>
        {
            HookViewModel();
            _vm?.QueryTimingCommand.Execute(null);
            Redraw();
        };

        SizeChanged += (_, _) => Redraw();

        Closed += (_, _) =>
        {
            if (_vm is not null) _vm.TimingRecords.CollectionChanged -= OnRecordsChanged;
            _hooked = false;
        };
    }

    private void HookViewModel()
    {
        _vm = DataContext as MainViewModel;
        if (_vm is null || _hooked) return;

        _vm.TimingRecords.CollectionChanged += OnRecordsChanged;
        _hooked = true;
    }

    private void OnRecordsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    // ==================================================================
    // 画图
    // ==================================================================
    private void Redraw()
    {
        DrawTrend();
        DrawPareto();
    }

    /// <summary>动作时间趋势：一次一根柱（用时），虚线是平均，点线是标准工时。</summary>
    private void DrawTrend()
    {
        TrendCanvas.Children.Clear();

        double w = TrendCanvas.ActualWidth;
        double h = TrendCanvas.ActualHeight;
        if (w < 60 || h < 60) return;

        var records = _vm?.TimingRecords;
        if (records is null || records.Count == 0)
        {
            DrawCenterText(TrendCanvas, "没有数据：先选日期范围点「查询」；生产时每次动作都会自动记录");
            return;
        }

        // 明细是倒序的，取最近 N 条再翻正，横轴就是时间顺序
        var list = records.Take(TrendPointLimit).Reverse().ToList();

        double max = list.Max(r => r.DurationSec);
        if (max <= 0) max = 1;
        max *= 1.15;                       // 顶上留一点空间给数字

        double plotTop = 16;
        double plotH = h - plotTop - 22;
        if (plotH <= 10) return;

        double avg = list.Average(r => r.DurationSec);
        double stdSec = list.Select(r => r.StandardMs ?? 0).FirstOrDefault(v => v > 0) / 1000.0;

        double slot = w / list.Count;
        double barW = Math.Max(3, Math.Min(38, slot - 3));

        // Y 轴参考
        DrawAxisText(TrendCanvas, $"{max:F1}s", 2, 2);
        DrawAxisText(TrendCanvas, "0s", 2, plotTop + plotH - 14);

        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            double bh = Math.Max(2, r.DurationSec / max * plotH);
            double x = i * slot + (slot - barW) / 2;

            var bar = new Rectangle
            {
                Width = barW,
                Height = bh,
                Fill = PaceBrush(r),
                RadiusX = 2,
                RadiusY = 2,
                ToolTip = r.Display,
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, plotTop + plotH - bh);
            TrendCanvas.Children.Add(bar);

            // 柱太多就不标数字了，否则糊成一片
            if (list.Count <= 16)
                DrawAxisText(TrendCanvas, $"{r.DurationSec:F1}", x, plotTop + plotH - bh - 13);
        }

        DrawDashedLine(TrendCanvas, plotTop + plotH - avg / max * plotH, w,
                       Brushes.Gray, "平均");

        if (stdSec > 0)
            DrawDashedLine(TrendCanvas, plotTop + plotH - stdSec / max * plotH, w,
                           (Brush)FindResource("StateWarn"), "标准");
    }

    /// <summary>工序耗时排行（帕累托）：柱 = 平均用时（降序），折线 = 累计占比。</summary>
    private void DrawPareto()
    {
        ParetoCanvas.Children.Clear();

        double w = ParetoCanvas.ActualWidth;
        double h = ParetoCanvas.ActualHeight;
        if (w < 60 || h < 60) return;

        var records = _vm?.TimingRecords;
        if (records is null || records.Count == 0)
        {
            DrawCenterText(ParetoCanvas, "没有数据");
            return;
        }

        var groups = records
            .GroupBy(r => r.StepSeq)
            .Select(g => new
            {
                StepSeq = g.Key,
                Name = g.OrderByDescending(r => r.FinishedAt).First().StepName,
                AvgSec = g.Average(r => r.DurationSec),
                Count = g.Count(),
            })
            .OrderByDescending(g => g.AvgSec)
            .ToList();

        double total = groups.Sum(g => g.AvgSec);
        double max = groups.Max(g => g.AvgSec);
        if (max <= 0) max = 1;
        max *= 1.2;

        double plotTop = 16;
        double plotH = h - plotTop - 24;
        if (plotH <= 10) return;

        double slot = w / groups.Count;
        double barW = Math.Max(6, Math.Min(46, slot - 8));

        DrawAxisText(ParetoCanvas, $"{max:F1}s", 2, 2);

        var cumPoints = new PointCollection();
        double cumulative = 0;

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            double bh = Math.Max(2, g.AvgSec / max * plotH);
            double x = i * slot + (slot - barW) / 2;

            var bar = new Rectangle
            {
                Width = barW,
                Height = bh,
                Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xD4)),
                RadiusX = 2,
                RadiusY = 2,
                ToolTip = $"第 {g.StepSeq} 步 {g.Name}：平均 {g.AvgSec:F1}s（{g.Count} 次）",
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, plotTop + plotH - bh);
            ParetoCanvas.Children.Add(bar);

            DrawAxisText(ParetoCanvas, $"第{g.StepSeq}步", i * slot + (slot - 42) / 2, plotTop + plotH + 4);

            cumulative += g.AvgSec / Math.Max(0.001, total);
            double y = plotTop + plotH - cumulative * plotH;
            cumPoints.Add(new Point(i * slot + slot / 2, y));
        }

        if (cumPoints.Count >= 2)
        {
            ParetoCanvas.Children.Add(new Polyline
            {
                Points = cumPoints,
                Stroke = (Brush)FindResource("StateWarn"),
                StrokeThickness = 2,
            });
        }
    }

    private Brush PaceBrush(ActionTimingRecord r) => r.PaceStateKey switch
    {
        "ng" => (Brush)FindResource("StateNg"),
        "active" => (Brush)FindResource("StateWarn"),
        "ok" => (Brush)FindResource("StateOk"),
        _ => new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xD4)),
    };

    private static void DrawDashedLine(Canvas canvas, double y, double width, Brush brush, string label)
    {
        if (double.IsNaN(y) || y < 0) return;

        canvas.Children.Add(new Line
        {
            X1 = 0, X2 = width, Y1 = y, Y2 = y,
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        });

        var text = new TextBlock { Text = label, Foreground = brush, FontSize = 10 };
        Canvas.SetLeft(text, width - 34);
        Canvas.SetTop(text, Math.Max(0, y - 13));
        canvas.Children.Add(text);
    }

    private static void DrawAxisText(Canvas canvas, string text, double left, double top)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA5, 0xBD)),
            FontSize = 10,
        };
        Canvas.SetLeft(block, Math.Max(0, left));
        Canvas.SetTop(block, Math.Max(0, top));
        canvas.Children.Add(block);
    }

    private static void DrawCenterText(Canvas canvas, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA5, 0xBD)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(100, canvas.ActualWidth - 20),
        };
        Canvas.SetLeft(block, 12);
        Canvas.SetTop(block, Math.Max(10, canvas.ActualHeight / 2 - 12));
        canvas.Children.Add(block);
    }
}
