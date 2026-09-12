using VisionForge.Core.Models;

namespace VisionForge.Core.Interfaces;

/// <summary>
/// 姿态数据源 —— 过程层的输入抽象。
///
/// <para><b>真实实现：</b>手部检测器 + 21 点手部关键点模型（RTMPose / ONNX Runtime CPU），
/// 逐帧输出掌心中心与置信度。</para>
///
/// <para><b>模拟实现：</b>脚本化手部轨迹 —— 没有模型、没有相机也能把规则引擎、
/// 拦截逻辑、界面跑通并做回归测试。这点很关键：整条过程层链路的正确性，
/// 不应该等到硬件到货才能验证。</para>
///
/// <para>接口放在 Core：规则引擎只认"一帧姿态"，不关心它是模型算出来的还是脚本编出来的。</para>
/// </summary>
public interface IPoseSource : IDisposable
{
    /// <summary>数据源名称（界面显示，如"手部模型" / "模拟轨迹"）。</summary>
    string Name { get; }

    bool IsRunning { get; }

    /// <summary>每来一帧触发一次。注意：可能在采集线程上触发，界面要自己切线程。</summary>
    event EventHandler<PoseObservation>? PoseArrived;

    /// <summary>数据源自然结束（如脚本轨迹播完、视频读完）。界面据此复位按钮状态。</summary>
    event EventHandler? Finished;

    void Start();

    void Stop();
}
