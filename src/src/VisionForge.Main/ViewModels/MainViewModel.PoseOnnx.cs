using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VisionForge.Main.Imaging;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 真实手部关键点：把 ONNX 手部模型跑在预览画面上，喂给现有的 21 点显示。
///
/// <para><b>为什么喂"预览图"而不是检测帧：</b>产品内部的检测帧是灰度（Mono8），
/// 而这个模型实测吃灰度完全认不出（同一张图：彩色 0.83 / 灰度 0.004）。
/// 预览图（录像、彩色相机）是彩色的，正好。</para>
///
/// <para><b>为什么隔 250ms 跑一次而不是每帧：</b>单次推理约 30ms，
/// 30fps 全跑会吃掉一个核；手的位置变化没那么快，4fps 的骨架看起来已经是连贯的。</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>手部 21 关节的连线（画绿线用）。下标含义见模型文档：0 手腕、4 拇指尖、8 食指尖…</summary>
    private static readonly (int A, int B)[] HandEdges =
    {
        (0, 1), (1, 2), (2, 3), (3, 4),        // 拇指
        (0, 5), (5, 6), (6, 7), (7, 8),        // 食指
        (5, 9), (9, 10), (10, 11), (11, 12),   // 中指
        (9, 13), (13, 14), (14, 15), (15, 16), // 无名指
        (13, 17), (17, 18), (18, 19), (19, 20),// 小指
        (0, 17),                               // 掌根闭合
    };

    private HandPoseEstimator? _handEstimator;
    private DispatcherTimer? _handTimer;
    private int _handBusy;
    private string _poseStatusText = "手部关键点：未启动";
    private string _handZoneText = "手部位置：—";

    /// <summary>骨架连线（画布坐标）。</summary>
    public PointCollection HandSkeleton { get; } = new();

    /// <summary>手的外接框（画布坐标）。</summary>
    public Rect HandBox { get; private set; } = Rect.Empty;

    public bool HasHandBox => !HandBox.IsEmpty;

    /// <summary>一行状态：认没认出手、分数多少、耗时多少。</summary>
    public string PoseStatusText
    {
        get => _poseStatusText;
        private set => SetProperty(ref _poseStatusText, value);
    }

    /// <summary>手现在在哪个框里（这就是"轨迹 / 工位判断"的第一步）。</summary>
    public string HandZoneText
    {
        get => _handZoneText;
        private set => SetProperty(ref _handZoneText, value);
    }

    /// <summary>开关打开/关闭时启停手部估计。</summary>
    private void ApplyPoseToggle(bool enabled)
    {
        if (enabled) StartHandPose();
        else StopHandPose();
    }

    private void StartHandPose()
    {
        if (_handTimer is not null) return;

        _handTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _handTimer.Tick += (_, _) => RunHandPoseOnce();
        _handTimer.Start();

        RunHandPoseOnce();
    }

    private void StopHandPose()
    {
        _handTimer?.Stop();
        _handTimer = null;

        foreach (var point in PosePoints) point.Hide();
        HandSkeleton.Clear();
        HandBox = Rect.Empty;
        OnPropertyChanged(nameof(HasHandBox));
        PoseStatusText = "手部关键点：已关闭";
    }

    /// <summary>跑一次手部估计（在后台线程推理，回到 UI 线程更新点）。</summary>
    private void RunHandPoseOnce()
    {
        if (Interlocked.CompareExchange(ref _handBusy, 1, 0) != 0) return;

        var preview = PreviewImage;
        if (preview is null)
        {
            Interlocked.Exchange(ref _handBusy, 0);
            return;
        }

        int width = preview.PixelWidth;
        int height = preview.PixelHeight;

        _ = Task.Run(() =>
        {
            try
            {
                _handEstimator ??= new HandPoseEstimator(HandPoseEstimator.DefaultModelPath);

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var hand = _handEstimator.Estimate(preview, null, minScore: 0.5);
                watch.Stop();

                if (hand is null)
                {
                    ApplyHandPose(null, watch.ElapsedMilliseconds, width, height);
                    return;
                }

                ApplyHandPose(hand, watch.ElapsedMilliseconds, width, height);
            }
            catch (Exception ex)
            {
                // 模型缺失/原生库加载失败都不该让界面崩：写一行状态就够了
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                dispatcher?.BeginInvoke(new Action(() =>
                    PoseStatusText = "手部关键点不可用：" + ex.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _handBusy, 0);
            }
        });
    }

    /// <summary>把手部结果画到画布上（像素坐标 → 960×720 画布）。</summary>
    private void ApplyHandPose(HandPoseResult? hand, long elapsedMs, int previewWidth, int previewHeight)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(new Action(() =>
        {
            if (hand is null || hand.Points.Count < HandPointCount)
            {
                foreach (var point in PosePoints) point.Hide();
                HandSkeleton.Clear();
                HandBox = Rect.Empty;
                OnPropertyChanged(nameof(HasHandBox));
                PoseStatusText = $"手部关键点：这一帧没认出手（{elapsedMs}ms）";
                HandZoneText = "手部位置：—";
                return;
            }

            double sx = RoiCanvasWidth / (double)Math.Max(1, previewWidth);
            double sy = RoiCanvasHeight / (double)Math.Max(1, previewHeight);

            for (int i = 0; i < HandPointCount; i++)
            {
                var p = hand.Points[i];
                PosePoints[i].Move(p.X * sx, p.Y * sy, p.Confidence >= 0.5);
            }

            HandSkeleton.Clear();
            foreach (var (a, b) in HandEdges)
            {
                HandSkeleton.Add(new Point(hand.Points[a].X * sx, hand.Points[a].Y * sy));
                HandSkeleton.Add(new Point(hand.Points[b].X * sx, hand.Points[b].Y * sy));
            }

            HandBox = new Rect(hand.Box.X * sx, hand.Box.Y * sy, hand.Box.W * sx, hand.Box.H * sy);
            OnPropertyChanged(nameof(HasHandBox));

            PoseStatusText = $"手部关键点：21 关节 · 置信度 {hand.Score:P0} · {elapsedMs}ms";

            // 手在哪个框 → 交给轨迹跟踪器（它会判停留、顺序，并更新界面上的位置与轨迹）
            int roiIndex = ResolveHandRoi(hand, previewWidth, previewHeight);
            TrackHandAction(DateTime.Now, roiIndex, hand.Score);
        }));
    }

    /// <summary>
    /// 手的重心落在第几个框里（归一化坐标判断）。不落在任何框里就返回 -1。
    /// </summary>
    private int ResolveHandRoi(HandPoseResult hand, int previewWidth, int previewHeight)
    {
        var recipe = ActiveRecipe;
        if (recipe is null || previewWidth <= 0 || previewHeight <= 0) return -1;

        var center = hand.Center;
        double nx = center.X / previewWidth;
        double ny = center.Y / previewHeight;

        var rois = recipe.Rois.Where(r => r.Enabled).ToList();
        for (int i = 0; i < rois.Count; i++)
        {
            if (rois[i].Contains(nx, ny))
                return i + 1;
        }

        return -1;
    }
}
