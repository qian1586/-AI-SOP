using VisionForge.Core.Models;

namespace VisionForge.Core.Interfaces;

/// <summary>
/// 相机抽象接口 —— 整个"硬件抽象层"的地基。
///
/// 文章里说"相机和 PLC 的底层驱动都做了抽象接口，切换具体型号时只需要替换实现类"，
/// 这个接口就是那句话的落地。
///
/// 抽象时踩过的三个点，写在这里免得后人再踩：
///   1. <b>必须区分"连续采集"和"软触发单帧"</b>。
///      产线上位机通常要在 PLC 给出到位信号的瞬间拍一帧，这叫软触发。
///      只提供连续采集接口的抽象是不合格的。
///   2. <b>取流必须带超时</b>。相机断线时如果没有超时，上层会永久卡住。
///   3. <b>不要把 SDK 的类型泄漏到接口上</b>。
///      一旦签名里出现 MvCamera 或 HObject，抽象就白做了。
/// </summary>
public interface ICamera : IDisposable
{
    /// <summary>相机静态信息。</summary>
    CameraInfo Info { get; }

    bool IsConnected { get; }

    bool IsGrabbing { get; }

    /// <summary>连续采集模式下，每来一帧触发一次。注意回调运行在采集线程上。</summary>
    event EventHandler<CameraFrame>? FrameArrived;

    /// <summary>连接相机。</summary>
    Task<bool> ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>开始连续采集。</summary>
    Task StartGrabbingAsync(CancellationToken ct = default);

    /// <summary>停止采集。</summary>
    Task StopGrabbingAsync();

    /// <summary>
    /// 软触发采集单帧。这是与 PLC 节拍同步的关键入口。
    /// 超时返回 null，而不是抛异常 —— 产线上"没拍到"是常态，不该炸整个流程。
    /// </summary>
    Task<CameraFrame?> GrabOnceAsync(int timeoutMs = 2000, CancellationToken ct = default);

    /// <summary>
    /// 批量应用相机参数（曝光、增益、帧率……）。
    /// 配方里的相机参数段直接喂进来，实现类负责翻译成自家 SDK 的写法。
    /// </summary>
    Task ApplyParametersAsync(IReadOnlyDictionary<string, double> parameters);

    /// <summary>当前相机支持哪些可调参数，UI 用来决定显示哪些输入框。</summary>
    IReadOnlyList<CameraParameterDescriptor> QueryParameters();
}

/// <summary>相机可调参数的描述。</summary>
public sealed class CameraParameterDescriptor
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public double Min { get; init; }
    public double Max { get; init; }
    public double Default { get; init; }
    public string Unit { get; init; } = string.Empty;
}
