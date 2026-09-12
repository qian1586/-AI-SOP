namespace VisionForge.Core.Models;

/// <summary>PLC 品牌。新增品牌时在这里加一项，并在 Hardware 层加对应实现。</summary>
public enum PlcBrand
{
    /// <summary>通用 Modbus TCP。三菱、汇川、台达、信捷大多支持。</summary>
    ModbusTcp = 0,

    /// <summary>欧姆龙 FINS over TCP。</summary>
    OmronFins = 1,

    /// <summary>西门子 S7（ISO-on-TCP）。</summary>
    SiemensS7 = 2,

    /// <summary>三菱 MC 协议。</summary>
    MitsubishiMc = 3,

    /// <summary>本地模拟，无硬件联调用。</summary>
    Mock = 99,
}

public enum PlcConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Faulted = 3,
}

/// <summary>PLC 数据区的数据类型。决定读写时怎么解析字节。</summary>
public enum PlcDataType
{
    Bool = 0,
    Int16 = 1,
    UInt16 = 2,
    Int32 = 3,
    UInt32 = 4,
    Float32 = 5,
    String = 6,
}

/// <summary>
/// 一条 PLC 地址项。
///
/// 为什么要做成"表"而不是散落在代码里的常量？
///   产线换个 PLC 型号，地址可能从 D100 变成 DM100。
///   做成表格 + 可导入导出，现场工程师自己就能改，不用找开发。
///   这是文章里"PLC 地址管理表格，支持增删改、可导入导出"的用意。
/// </summary>
public class PlcAddressItem
{
    /// <summary>逻辑名，代码里只引用这个名字，不引用物理地址。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>物理地址，如 "D100" / "DM100" / "MW10" / "40001"。</summary>
    public string Address { get; set; } = string.Empty;

    public PlcDataType DataType { get; set; } = PlcDataType.Int16;

    /// <summary>是否可写。只读项在界面上应禁用编辑。</summary>
    public bool Writable { get; set; }

    public string Description { get; set; } = string.Empty;

    public PlcAddressItem Clone() => (PlcAddressItem)MemberwiseClone();

    public override string ToString() => $"{Name} @ {Address} ({DataType})";
}

/// <summary>
/// PLC 连接配置。由配置文件加载，界面上可编辑。
///
/// 放在 Models 而不是 Interfaces，是因为它本质是一份"数据"，
/// 需要被 JSON 序列化、被界面绑定、被配方引用。
/// 接口层只描述"能做什么"，不描述"配置长什么样"。
/// </summary>
public sealed class PlcConnectionOptions
{
    public string Name { get; set; } = "产线PLC";

    public PlcBrand Brand { get; set; } = PlcBrand.ModbusTcp;

    public string IpAddress { get; set; } = "192.168.1.10";

    public int Port { get; set; } = 502;

    /// <summary>站号 / 单元号。Modbus 从站地址、FINS 的单元号都走这个字段。</summary>
    public byte StationId { get; set; } = 1;

    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ReadTimeoutMs { get; set; } = 1000;
    public int WriteTimeoutMs { get; set; } = 1000;

    /// <summary>断线是否自动重连。产线上必须开。</summary>
    public bool AutoReconnect { get; set; } = true;

    public int ReconnectIntervalMs { get; set; } = 5000;

    public string ToEndpoint() => $"{IpAddress}:{Port}";
}
