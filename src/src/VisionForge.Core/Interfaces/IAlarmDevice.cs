using VisionForge.Core.Models;

namespace VisionForge.Core.Interfaces;

/// <summary>
/// 声光报警装置抽象。
///
/// 抽象的目的和相机/PLC 一样：上层只管"该亮红灯了"，
/// 底下是三色灯、塔灯、还是工控机小喇叭，业务代码不关心。
///
/// <para><b>三个设计上的讲究：</b></para>
/// <list type="number">
///   <item><b>亮灯和鸣笛分开两个方法</b>，不是一个 SetAlarm(bool)。
///         因为它们的生命周期不一样：灯要一直红着等人来处理，
///         声音响几秒就该停（否则工人会把喇叭拆了）。</item>
///   <item><b>所有方法失败都不抛异常</b>，只返回 bool。
///         报警装置是辅助设施，它坏了不能让检测主流程崩 ——
///         这个优先级判断在工控软件里必须明确。</item>
///   <item><b>设置的是"目标状态"而不是"动作"</b>，
///         所以重复设置同一个状态是幂等的，不用上层去记"上次亮的是啥"。</item>
/// </list>
/// </summary>
public interface IAlarmDevice : IDisposable
{
    string Name { get; }

    bool IsConnected { get; }

    /// <summary>当前信号状态。上层用它做幂等判断。</summary>
    AlarmSignal CurrentSignal { get; }

    /// <summary>蜂鸣器当前是否在响。</summary>
    bool IsBuzzerOn { get; }

    Task<bool> ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>
    /// 设置三色灯状态。重复设置同值应当是幂等的。
    /// </summary>
    Task<bool> SetSignalAsync(AlarmSignal signal, CancellationToken ct = default);

    /// <summary>控制蜂鸣器。与灯分开控制，见接口说明。</summary>
    Task<bool> SetBuzzerAsync(bool on, CancellationToken ct = default);

    /// <summary>全部复位（灭灯 + 停笛）。程序退出或"确认报警"时调用。</summary>
    Task ResetAsync(CancellationToken ct = default);
}
