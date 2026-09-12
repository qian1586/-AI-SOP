using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Plc;

/// <summary>
/// PLC 工厂 —— 按品牌创建对应实现。
///
/// 文章说"支持多种 PLC 品牌，通过配置文件切换"，落地就是这里的一个 switch。
/// 新增品牌只需要写一个 IPlcClient 实现并在这里加一行，主程序不受影响。
/// </summary>
public static class PlcFactory
{
    public static IPlcClient Create(PlcConnectionOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        return options.Brand switch
        {
            PlcBrand.ModbusTcp => new ModbusTcpPlcClient(options),
            PlcBrand.Mock => new MockPlcClient(options),

            // 预留：欧姆龙 FINS / 西门子 S7 / 三菱 MC
            // 实现要点：
            //   OmronFins  —— FINS/UDP 或 FINS/TCP，读 DM 区
            //   SiemensS7  —— ISO-on-TCP(102 端口) + S7comm，注意 TSAP 设置
            //   MitsubishiMc —— MC 协议 3E 帧，注意软元件代码的位/字区分
            PlcBrand.OmronFins => throw new NotSupportedException(
                "欧姆龙 FINS 尚未实现。若现场 PLC 支持 Modbus TCP，可优先用 ModbusTcp 对接。"),
            PlcBrand.SiemensS7 => throw new NotSupportedException(
                "西门子 S7 尚未实现。建议加装 Modbus TCP 网关后用 ModbusTcp 对接。"),
            PlcBrand.MitsubishiMc => throw new NotSupportedException(
                "三菱 MC 尚未实现。建议启用 PLC 的 Modbus TCP 功能后用 ModbusTcp 对接。"),

            _ => throw new NotSupportedException($"不支持的 PLC 品牌：{options.Brand}"),
        };
    }

    /// <summary>默认的一段地址表模板，界面上"新建地址表"时用。</summary>
    public static List<PlcAddressItem> CreateDefaultAddressTable() => new()
    {
        new PlcAddressItem { Name = "DeviceReady",  Address = "D100", DataType = PlcDataType.Int16, Writable = false, Description = "设备就绪" },
        new PlcAddressItem { Name = "PartArrived",  Address = "D101", DataType = PlcDataType.Int16, Writable = false, Description = "工件到位信号" },
        new PlcAddressItem { Name = "InspectResult",Address = "D200", DataType = PlcDataType.Int16, Writable = true,  Description = "检测结论：1=OK 0=NG -1=未检测" },
        new PlcAddressItem { Name = "AllowPass",    Address = "M100", DataType = PlcDataType.Bool,  Writable = true,  Description = "允许放行" },
        new PlcAddressItem { Name = "RejectCylinder",Address = "M101", DataType = PlcDataType.Bool, Writable = true,  Description = "剔除气缸动作" },
    };
}
