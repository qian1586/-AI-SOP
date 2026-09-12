using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Alarm;

/// <summary>
/// 走 PLC 输出点的声光报警装置。
///
/// <para><b>为什么用 PLC 而不是 USB 灯 / 声卡：</b></para>
/// <list type="bullet">
///   <item>产线本来就布了 PLC 输出端子排，接线成本最低</item>
///   <item>工控机的 USB 经常被各种安全策略禁用，插上去不一定认</item>
///   <item>声卡输出在车间噪音环境下根本听不见，正经三色灯带蜂鸣器才管用</item>
/// </list>
///
/// <para><b>切换颜色的正确顺序：先灭旧灯，再亮新灯。</b></para>
/// 如果反过来（先亮新灯再灭旧灯），会有一瞬间两个颜色同时亮 ——
/// 双色同时亮在现场的含义通常是"设备故障"，会误导人。
/// 虽然只有几十毫秒，但操作工如果正好抬头看，就是一次误判事件。
/// </summary>
public sealed class PlcAlarmDevice : IAlarmDevice
{
    private readonly IPlcClient _plc;
    private readonly AlarmOptions _options;
    private readonly Dictionary<AlarmSignal, PlcAddressItem> _lightMap = new();
    private readonly PlcAddressItem? _buzzer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlcAlarmDevice(IPlcClient plc, AlarmOptions options, IEnumerable<PlcAddressItem> addressTable)
    {
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // 按逻辑名在地址表里找对应项 —— 具体物理地址由现场维护，
        // 代码只认逻辑名，换 PLC 型号时地址变了也不用改代码
        var table = addressTable.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        if (TryBind(table, options.GreenLightAddress, AlarmSignal.Ok, out var green)) _lightMap[AlarmSignal.Ok] = green!;
        if (TryBind(table, options.YellowLightAddress, AlarmSignal.Warning, out var yellow)) _lightMap[AlarmSignal.Warning] = yellow!;
        if (TryBind(table, options.RedLightAddress, AlarmSignal.Fault, out var red)) _lightMap[AlarmSignal.Fault] = red!;

        if (!string.IsNullOrWhiteSpace(options.BuzzerAddress) &&
            table.TryGetValue(options.BuzzerAddress, out var bz))
            _buzzer = bz;
    }

    private static bool TryBind(
        IReadOnlyDictionary<string, PlcAddressItem> table,
        string? logicalName,
        AlarmSignal signal,
        out PlcAddressItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(logicalName)) return false;
        if (!table.TryGetValue(logicalName, out var found)) return false;

        item = found;
        return true;
    }

    public string Name => "PLC 声光报警";

    public bool IsConnected => _plc.State == PlcConnectionState.Connected;

    public AlarmSignal CurrentSignal { get; private set; } = AlarmSignal.None;

    public bool IsBuzzerOn { get; private set; }

    /// <summary>实际绑定上的信号数量，用于启动时给个明确提示。</summary>
    public int BoundLightCount => _lightMap.Count;

    public bool HasBuzzer => _buzzer is not null;

    // ------------------------------------------------------------------
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        if (_plc.State != PlcConnectionState.Connected)
        {
            var ok = await _plc.ConnectAsync(ct);
            if (!ok) return false;
        }

        await ResetAsync(ct);
        return true;
    }

    public Task DisconnectAsync() => Task.CompletedTask;   // PLC 的生命周期由上层统一管

    // ------------------------------------------------------------------
    public async Task<bool> SetSignalAsync(AlarmSignal signal, CancellationToken ct = default)
    {
        if (!IsConnected) return false;
        if (CurrentSignal == signal) return true;          // 幂等

        await _gate.WaitAsync(ct);
        try
        {
            // 先灭掉所有灯，再亮目标灯 —— 避免双色同亮的误导
            foreach (var (sig, addr) in _lightMap)
            {
                if (sig == signal) continue;
                await SafeWriteAsync(addr, false, ct);
            }

            if (signal != AlarmSignal.None && _lightMap.TryGetValue(signal, out var target))
                await SafeWriteAsync(target, true, ct);

            CurrentSignal = signal;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetBuzzerAsync(bool on, CancellationToken ct = default)
    {
        if (!IsConnected) return false;
        if (_buzzer is null) return false;
        if (IsBuzzerOn == on) return true;

        var ok = await SafeWriteAsync(_buzzer, on, ct);
        if (ok) IsBuzzerOn = on;
        return ok;
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        if (IsConnected)
        {
            await SetSignalAsync(AlarmSignal.None, ct);
            await SetBuzzerAsync(false, ct);
        }

        CurrentSignal = AlarmSignal.None;
        IsBuzzerOn = false;
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// 写失败不抛异常。报警装置是辅助设施，它坏了不能把检测主流程带崩 ——
    /// 写不出去就记一笔，让人知道灯可能没亮，但产线该跑还得跑。
    /// </summary>
    private async Task<bool> SafeWriteAsync(PlcAddressItem addr, bool value, CancellationToken ct)
    {
        try
        {
            var probe = addr.Clone();
            probe.DataType = PlcDataType.Bool;       // 灯和蜂鸣器都是位信号
            probe.Writable = true;
            return await _plc.WriteAsync(probe, value, ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Alarm] 写 {addr.Name} 失败: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// 报警装置工厂。和相机/PLC 一样，按配置决定造哪一种。
/// </summary>
public static class AlarmFactory
{
    public static IAlarmDevice Create(
        AlarmOptions options,
        IPlcClient plc,
        IEnumerable<PlcAddressItem> addressTable)
    {
        if (!options.Enabled)
            return new MockAlarmDevice();     // 关掉报警时给个哑实现，避免上层到处判空

        // 用 PLC 地址表里是否存在报警相关地址来判断接没接实体灯。
        // 这样不用额外加一个"报警类型"配置项 —— 地址表本身就是事实来源。
        var table = addressTable.ToList();
        bool hasAnyLight = table.Any(a =>
            string.Equals(a.Name, options.GreenLightAddress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, options.YellowLightAddress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, options.RedLightAddress, StringComparison.OrdinalIgnoreCase));

        return hasAnyLight
            ? new PlcAlarmDevice(plc, options, table)
            : new MockAlarmDevice();
    }
}
