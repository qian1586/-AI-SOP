using System.Collections.ObjectModel;
using System.Windows.Input;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Models;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 手部关键点叠加显示（参考界面里那个绿色骨架）。
///
/// <para><b>现在的数据是真的还是模拟的：</b>过程监测跑的是"模拟手部轨迹"，
/// 所以画面上的关键点来自那套模拟数据 —— 界面里也照实写明，不会让人误以为是真识别。
/// 等实拍样本和手部模型到位，同一段代码画的就是真实关键点，界面一行都不用改。</para>
///
/// <para><b>为什么用 21 个固定点子而不是每帧重建集合：</b>
/// 关键点每帧都在动，如果每帧都 Clear+Add，界面会被反复重画。
/// 这里固定 21 个对象、只改坐标，帧率再高也不卡。</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>关键点数量（手部 21 点模型）。</summary>
    private const int HandPointCount = 21;

    /// <summary>画面上的关键点（坐标已换算到 960×720 预览画布）。</summary>
    public ObservableCollection<PosePoint> PosePoints { get; } = new();

    /// <summary>是否显示关键点。</summary>
    public bool ShowPosePoints
    {
        get => _showPosePoints;
        set
        {
            if (!SetProperty(ref _showPosePoints, value)) return;
            OnPropertyChanged(nameof(PoseButtonText));
            ApplyPoseToggle(value);      // 打开就开始跑手部模型，关掉就停（见 PoseOnnx 分部）
        }
    }

    public string PoseButtonText => _showPosePoints ? "⊙ 关键点：开" : "⊙ 关键点：关";

    /// <summary>关键点是否"有数据可显示"（没跑过程监测时没有）。</summary>
    public bool HasPoseData => _poseFrameCount > 0;

    public ICommand TogglePosePointsCommand { get; }

    private void TogglePosePoints()
    {
        ShowPosePoints = !ShowPosePoints;
        StatusMessage = ShowPosePoints
            ? "已显示手部关键点（真实模型：ONNX 手部 21 关节，跑在彩色预览画面上）"
            : "已隐藏手部关键点";
    }

    /// <summary>建好 21 个点对象，坐标先都放在画面外（避免开局在左上角堆一堆点）。</summary>
    private void InitPosePoints()
    {
        PosePoints.Clear();
        for (int i = 0; i < HandPointCount; i++)
            PosePoints.Add(new PosePoint { X = -10, Y = -10 });
    }

    /// <summary>把一帧姿态观测画到画布上（归一化坐标 × 预览画布尺寸）。</summary>
    private void UpdatePosePoints(PoseObservation? observation)
    {
        if (observation is null || observation.Landmarks.Count < HandPointCount)
        {
            // 这一帧没检测到手：把点移到画面外，别留个"上一帧的手"在那儿骗人
            foreach (var point in PosePoints) point.Hide();
            return;
        }

        for (int i = 0; i < HandPointCount; i++)
        {
            var landmark = observation.Landmarks[i];
            PosePoints[i].Move(landmark.X * RoiCanvasWidth, landmark.Y * RoiCanvasHeight);
        }

        _poseFrameCount++;
        if (_poseFrameCount == 1) OnPropertyChanged(nameof(HasPoseData));
    }

    private int _poseFrameCount;
    private bool _showPosePoints = true;
}

/// <summary>画面上的一个关键点（可绑定、可移动）。</summary>
public sealed class PosePoint : ObservableObject
{
    private double _x;
    private double _y;
    private bool _visible;

    public double X
    {
        get => _x;
        internal set => SetProperty(ref _x, value);   // internal：同程序集里可以用对象初始化器赋值
    }

    public double Y
    {
        get => _y;
        internal set => SetProperty(ref _y, value);
    }

    public bool Visible
    {
        get => _visible;
        internal set => SetProperty(ref _visible, value);
    }

    internal void Move(double x, double y)
    {
        X = x;
        Y = y;
        Visible = true;
    }

    /// <summary>
    /// 带置信度的移动：关节自己置信度太低（模型也不确定）就点暗一点。
    /// 现场一眼能看出"这个点是模型猜的"，不会把猜的当成测到的。
    /// </summary>
    internal void Move(double x, double y, bool confident)
    {
        X = x;
        Y = y;
        Visible = true;
        Confident = confident;
    }

    private bool _confident = true;

    /// <summary>这个关节的置信度够不够（不够就画暗一点）。</summary>
    public bool Confident
    {
        get => _confident;
        internal set
        {
            if (!SetProperty(ref _confident, value)) return;
            OnPropertyChanged(nameof(Opacity));
        }
    }

    /// <summary>低置信度的点用半透明显示。</summary>
    public double Opacity => _confident ? 1.0 : 0.35;

    internal void Hide() => Visible = false;
}
