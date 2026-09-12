using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Alarm;

/// <summary>
/// 模拟报警装置 —— 没有硬件也能验证报警逻辑。
///
/// 它把报警状态记下来并抛事件，界面上可以画一个"虚拟三色灯"，
/// 开发阶段就能看清楚"什么时候该亮什么颜色"，不用等电工把灯接好。
/// 同时它是回归测试的抓手：可以断言"锁线时确实调了红灯"。
/// </summary>
public sealed class MockAlarmDevice : IAlarmDevice
{
    /// <summary>每次状态变化都抛一次，界面可订阅后画虚拟灯。</summary>
    public event EventHandler<AlarmSignal>? SignalChanged;

    public event EventHandler<bool>? BuzzerChanged;

    public string Name => "模拟报警灯";

    public bool IsConnected { get; private set; }

    public AlarmSignal CurrentSignal { get; private set; } = AlarmSignal.None;

    public bool IsBuzzerOn { get; private set; }

    /// <summary>状态变更历史，测试用。</summary>
    public List<(DateTime Time, string What, string Value)> Log { get; } = new();

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        Log.Add((DateTime.Now, "Connect", "OK"));
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        CurrentSignal = AlarmSignal.None;
        IsBuzzerOn = false;
        SignalChanged?.Invoke(this, AlarmSignal.None);
        BuzzerChanged?.Invoke(this, false);
        return Task.CompletedTask;
    }

    public Task<bool> SetSignalAsync(AlarmSignal signal, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.FromResult(false);

        // 幂等：同值不重复抛事件，避免界面闪
        if (CurrentSignal == signal) return Task.FromResult(true);

        CurrentSignal = signal;
        Log.Add((DateTime.Now, "Signal", signal.ToString()));
        SignalChanged?.Invoke(this, signal);
        return Task.FromResult(true);
    }

    public Task<bool> SetBuzzerAsync(bool on, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.FromResult(false);
        if (IsBuzzerOn == on) return Task.FromResult(true);

        IsBuzzerOn = on;
        Log.Add((DateTime.Now, "Buzzer", on ? "ON" : "OFF"));
        BuzzerChanged?.Invoke(this, on);
        return Task.FromResult(true);
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        CurrentSignal = AlarmSignal.None;
        IsBuzzerOn = false;
        SignalChanged?.Invoke(this, AlarmSignal.None);
        BuzzerChanged?.Invoke(this, false);
        Log.Add((DateTime.Now, "Reset", "-"));
        return Task.CompletedTask;
    }

    public void Dispose() => IsConnected = false;
}
