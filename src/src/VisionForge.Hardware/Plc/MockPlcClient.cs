using System.Collections.Concurrent;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Plc;

/// <summary>
/// 模拟 PLC —— 内存里的假 PLC，没有硬件也能验证完整闭环。
///
/// 它的用处不只是"跑得起来"：
///   · 可以在没有产线的时候演示"检测 NG → 写 PLC → 禁止放行"整个逻辑
///   · 可以故意制造"写入失败""连接断开"，测试上位机的容错分支
///   · 回归测试时可以断言"NG 时确实往地址 D200 写了 0"
/// </summary>
public sealed class MockPlcClient : IPlcClient
{
    private readonly ConcurrentDictionary<string, object?> _memory = new();
    private readonly Random _random;
    private readonly PlcConnectionOptions _options;

    public MockPlcClient(PlcConnectionOptions? options = null, int seed = 7)
    {
        _options = options ?? new PlcConnectionOptions { Brand = PlcBrand.Mock, Name = "模拟PLC" };
        _random = new Random(seed);

        // 预置几个常见地址，模拟真实 PLC 的初值
        _memory["D100"] = (short)0;          // 设备就绪
        _memory["D101"] = (short)0;          // 到位信号
        _memory["D200"] = (short)-1;         // 检测结论：-1未检测 / 0 NG / 1 OK
    }

    public string Name => _options.Name;

    public PlcBrand Brand => PlcBrand.Mock;

    public PlcConnectionState State { get; private set; } = PlcConnectionState.Disconnected;

    public event EventHandler<PlcConnectionState>? StateChanged;

    /// <summary>模拟写入失败率（0~1）。用来测试上位机的容错分支。</summary>
    public double WriteFailureRate { get; set; }

    /// <summary>模拟读超时率（0~1）。</summary>
    public double ReadTimeoutRate { get; set; }

    /// <summary>写入历史，回归测试可断言。</summary>
    public List<(string Address, object? Value, DateTime Time)> WriteLog { get; } = new();

    private void SetState(PlcConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        SetState(PlcConnectionState.Connecting);
        await Task.Delay(200, ct);          // 模拟握手耗时
        SetState(PlcConnectionState.Connected);
        return true;
    }

    public Task DisconnectAsync()
    {
        SetState(PlcConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task<object?> ReadAsync(PlcAddressItem address, CancellationToken ct = default)
    {
        if (State != PlcConnectionState.Connected) return null;
        await Task.Delay(_random.Next(1, 4), ct);

        if (_random.NextDouble() < ReadTimeoutRate) return null;

        return _memory.TryGetValue(address.Address, out var v) ? v : default(short);
    }

    public async Task<bool> WriteAsync(PlcAddressItem address, object value, CancellationToken ct = default)
    {
        if (State != PlcConnectionState.Connected) return false;
        await Task.Delay(_random.Next(1, 4), ct);

        if (_random.NextDouble() < WriteFailureRate) return false;

        _memory[address.Address] = value;
        WriteLog.Add((address.Address, value, DateTime.Now));
        return true;
    }

    public async Task<IReadOnlyDictionary<string, object?>> ReadBatchAsync(
        IEnumerable<PlcAddressItem> addresses,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, object?>();

        if (State != PlcConnectionState.Connected) return result;

        // 批量读只握一次手 —— 这也是为什么接口要单独提供批量方法
        await Task.Delay(3, ct);

        foreach (var addr in addresses)
        {
            if (_random.NextDouble() < ReadTimeoutRate) continue;
            result[addr.Name] = _memory.TryGetValue(addr.Address, out var v) ? v : default(short);
        }
        return result;
    }

    public async Task<bool> WriteInspectionVerdictAsync(
        PlcAddressItem address,
        InspectionVerdict verdict,
        CancellationToken ct = default)
    {
        short code = verdict switch
        {
            InspectionVerdict.Pass => 1,
            InspectionVerdict.Fail => 0,
            _ => -1,
        };
        return await WriteAsync(address, code, ct);
    }

    /// <summary>直接读内存值，测试用，绕过连接状态判断。</summary>
    public object? Peek(string address) => _memory.TryGetValue(address, out var v) ? v : null;

    public void Dispose()
    {
        State = PlcConnectionState.Disconnected;
    }
}
