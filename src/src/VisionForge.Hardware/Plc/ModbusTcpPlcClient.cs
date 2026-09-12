using System.Buffers.Binary;
using System.Net.Sockets;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Plc;

/// <summary>
/// Modbus TCP 客户端（自实现，零第三方依赖）。
///
/// 为什么自己写：
///   · NModbus 等库为了通用性做了大量抽象，出问题时很难定位
///   · Modbus TCP 本身极简单（MBAP 头 7 字节 + PDU），200 行足够
///   · 现场排障时能一眼看到发出去/收回来的原始字节，比调试黑盒库快得多
///
/// 覆盖范围：三菱、汇川、台达、信捷、西门子(带 Modbus 网关)、绝大多数触摸屏，
/// 只要对方开的是标准 Modbus TCP 就够用。
///
/// <para><b>一个必须知道的坑：</b>Modbus TCP 是"一问一答"协议，
/// 同一条 TCP 连接上<b>不能并发发请求</b>，否则响应会张冠李戴。
/// 所以这里的读写全部用 <c>_gate</c> 信号量串行化。
/// 很多自研客户端栽在这上面：平时正常，产线一忙就偶尔读到别的地址的值。</para>
/// </summary>
public sealed class ModbusTcpPlcClient : IPlcClient
{
    // Modbus 功能码
    private const byte FcReadCoils = 0x01;
    private const byte FcReadHoldingRegisters = 0x03;
    private const byte FcWriteSingleCoil = 0x05;
    private const byte FcWriteSingleRegister = 0x06;

    private readonly PlcConnectionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private bool _disposed;

    public ModbusTcpPlcClient(PlcConnectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Name => _options.Name;

    public PlcBrand Brand => PlcBrand.ModbusTcp;

    public PlcConnectionState State { get; private set; } = PlcConnectionState.Disconnected;

    public event EventHandler<PlcConnectionState>? StateChanged;

    private void SetState(PlcConnectionState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(this, s);
    }

    // ==================================================================
    // 连接
    // ==================================================================
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            SetState(PlcConnectionState.Connecting);

            _tcp = new TcpClient
            {
                NoDelay = true,        // 禁用 Nagle，工控小包场景必须开，否则响应慢几十毫秒
                ReceiveTimeout = _options.ReadTimeoutMs,
                SendTimeout = _options.WriteTimeoutMs,
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.ConnectTimeoutMs);

            await _tcp.ConnectAsync(_options.IpAddress, _options.Port, timeoutCts.Token);
            _stream = _tcp.GetStream();

            SetState(PlcConnectionState.Connected);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Modbus] 连接失败 {_options.ToEndpoint()}: {ex.Message}");
            SetState(PlcConnectionState.Faulted);
            Cleanup();
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        Cleanup();
        SetState(PlcConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    private void Cleanup()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    // ==================================================================
    // 地址解析
    // ==================================================================
    private static (bool IsCoil, ushort Address) ParseAddress(PlcAddressItem item)
    {
        var raw = item.Address.Trim().ToUpperInvariant();

        // 传统 Modbus 编号：0xxxx 线圈 / 4xxxx 保持寄存器（1 基址，需减 1）
        if (raw.Length == 5 && int.TryParse(raw, out var classic))
        {
            if (classic is >= 1 and <= 9999) return (true, (ushort)(classic - 1));
            if (classic is >= 40001 and <= 49999) return (false, (ushort)(classic - 40001));
        }

        // 区域前缀风格：M/C 线圈，D/DM/HR/R 保持寄存器
        foreach (var prefix in new[] { "CO", "M", "C", "Y" })
        {
            if (raw.StartsWith(prefix, StringComparison.Ordinal)
                && ushort.TryParse(raw[prefix.Length..], out var coilAddr))
                return (true, coilAddr);
        }
        foreach (var prefix in new[] { "HR", "DM", "D", "R", "W" })
        {
            if (raw.StartsWith(prefix, StringComparison.Ordinal)
                && ushort.TryParse(raw[prefix.Length..], out var regAddr))
                return (false, regAddr);
        }

        // 纯数字按线圈处理
        if (ushort.TryParse(raw, out var plain)) return (true, plain);

        throw new FormatException($"无法解析 PLC 地址「{item.Address}」");
    }

    // ==================================================================
    // 读写
    // ==================================================================
    public async Task<object?> ReadAsync(PlcAddressItem address, CancellationToken ct = default)
    {
        if (State != PlcConnectionState.Connected) return null;

        try
        {
            var (isCoil, addr) = ParseAddress(address);
            ushort[] regs = isCoil
                ? new[] { (ushort)(await ReadCoilsAsync(addr, 1, ct) ? 1 : 0) }
                : await ReadHoldingRegistersAsync(addr, 1, ct);

            return ConvertRegister(regs[0], address.DataType);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Modbus] 读 {address.Address} 失败: {ex.Message}");
            if (ex is IOException or SocketException) SetState(PlcConnectionState.Faulted);
            return null;
        }
    }

    public async Task<bool> WriteAsync(PlcAddressItem address, object value, CancellationToken ct = default)
    {
        if (State != PlcConnectionState.Connected) return false;

        try
        {
            var (isCoil, addr) = ParseAddress(address);

            if (isCoil)
            {
                bool on = value switch
                {
                    bool b => b,
                    short s => s != 0,
                    int i => i != 0,
                    _ => Convert.ToBoolean(value),
                };
                await WriteSingleCoilAsync(addr, on, ct);
            }
            else
            {
                await WriteSingleRegisterAsync(addr, ToRegister(value, address.DataType), ct);
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Modbus] 写 {address.Address} 失败: {ex.Message}");
            if (ex is IOException or SocketException) SetState(PlcConnectionState.Faulted);
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> ReadBatchAsync(
        IEnumerable<PlcAddressItem> addresses,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, object?>();
        if (State != PlcConnectionState.Connected) return result;

        // 按"是否线圈"分组，同组内尽量用连续地址一次读完
        var items = addresses.ToList();

        foreach (var item in items)
        {
            try
            {
                var (isCoil, addr) = ParseAddress(item);
                ushort raw = isCoil
                    ? (ushort)(await ReadCoilsAsync(addr, 1, ct) ? 1 : 0)
                    : (await ReadHoldingRegistersAsync(addr, 1, ct))[0];
                result[item.Name] = ConvertRegister(raw, item.DataType);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Modbus] 批量读 {item.Address} 失败: {ex.Message}");
                result[item.Name] = null;
            }
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

    // ==================================================================
    // 协议层
    // ==================================================================
    private async Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort count, CancellationToken ct)
    {
        var pdu = new byte[5];
        pdu[0] = FcReadHoldingRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);

        var resp = await TransactAsync(pdu, ct);

        // 响应：FC(1) + ByteCount(1) + Data(N)
        int byteCount = resp[1];
        if (byteCount != count * 2)
            throw new IOException($"保持寄存器响应长度异常：期望 {count * 2}，实际 {byteCount}");

        var regs = new ushort[count];
        for (int i = 0; i < count; i++)
            regs[i] = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(2 + i * 2));
        return regs;
    }

    private async Task<bool> ReadCoilsAsync(ushort start, ushort count, CancellationToken ct)
    {
        var pdu = new byte[5];
        pdu[0] = FcReadCoils;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);

        var resp = await TransactAsync(pdu, ct);
        return (resp[1] & 0x01) != 0;
    }

    private async Task WriteSingleRegisterAsync(ushort addr, ushort value, CancellationToken ct)
    {
        var pdu = new byte[5];
        pdu[0] = FcWriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), addr);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        await TransactAsync(pdu, ct);
    }

    private async Task WriteSingleCoilAsync(ushort addr, bool on, CancellationToken ct)
    {
        var pdu = new byte[5];
        pdu[0] = FcWriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), addr);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), on ? (ushort)0xFF00 : (ushort)0x0000);
        await TransactAsync(pdu, ct);
    }

    /// <summary>
    /// 发一帧请求、收一帧响应。
    ///
    /// <b>整个方法必须在 _gate 保护下执行</b> —— Modbus 一问一答，
    /// 并发会让响应串台，这是自研客户端最常见的隐性 bug。
    /// </summary>
    private async Task<byte[]> TransactAsync(byte[] pdu, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new IOException("PLC 未连接");

            // ---- MBAP 头（7 字节）----
            var request = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), ++_transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);        // 协议标识，Modbus 恒为 0
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(1 + pdu.Length));
            request[6] = _options.StationId;                                    // 单元标识
            pdu.CopyTo(request, 7);

            await stream.WriteAsync(request, ct);

            // ---- 先读 7 字节头，再按长度读剩余部分 ----
            var header = new byte[7];
            await stream.ReadExactlyAsync(header, ct);

            ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (length < 2) throw new IOException("Modbus 响应长度非法");

            var body = new byte[length - 1];
            await stream.ReadExactlyAsync(body, ct);

            // ---- 异常响应：功能码最高位置 1 ----
            if ((body[0] & 0x80) != 0)
                throw new IOException($"Modbus 异常响应，功能码 0x{body[0]:X2}，异常码 0x{body[1]:X2}");

            return body;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ==================================================================
    // 数据类型转换
    // ==================================================================
    private static object ConvertRegister(ushort raw, PlcDataType type) => type switch
    {
        PlcDataType.Bool => raw != 0,
        PlcDataType.Int16 => unchecked((short)raw),
        PlcDataType.UInt16 => raw,
        PlcDataType.Int32 => unchecked((int)(short)raw),
        PlcDataType.UInt32 => (uint)raw,
        PlcDataType.Float32 => BitConverter.UInt16BitsToHalf(raw),   // 单寄存器浮点极罕见，先按半精度近似
        _ => raw,
    };

    private static ushort ToRegister(object value, PlcDataType type)
    {
        try
        {
            return type switch
            {
                PlcDataType.Bool => Convert.ToBoolean(value) ? (ushort)1 : (ushort)0,
                PlcDataType.Int16 => unchecked((ushort)Convert.ToInt16(value)),
                PlcDataType.UInt16 => Convert.ToUInt16(value),
                PlcDataType.Int32 => unchecked((ushort)Convert.ToInt32(value)),
                PlcDataType.UInt32 => unchecked((ushort)Convert.ToUInt32(value)),
                _ => Convert.ToUInt16(value),
            };
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"值「{value}」无法转换为 {type}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
        _gate.Dispose();
    }
}
