using VisionForge.Core.Models;

namespace VisionForge.Core.Interfaces;

/// <summary>
/// PLC 通信接口 —— 视觉系统与产线设备之间的握手通道。
///
/// 文章原话："检测结果通过 PLC 输出给产线设备"。
/// 典型闭环：
///     PLC 给出"到位"信号 → 相机软触发拍照 → 算法判定
///     → 以 OK/NG 写回 PLC → PLC 决定放行或剔除
///
/// 抽象要点：
///   1. 读写都以 <see cref="PlcAddressItem"/> 为单位，物理地址不进入业务代码
///   2. 连接状态必须可观测（<see cref="StateChanged"/>），断线要能自动重连
///   3. 批量读要有，否则每读十个地址就握手十次，节拍扛不住
/// </summary>
public interface IPlcClient : IDisposable
{
    /// <summary>连接名，如"产线PLC"。</summary>
    string Name { get; }

    PlcBrand Brand { get; }

    PlcConnectionState State { get; }

    event EventHandler<PlcConnectionState>? StateChanged;

    Task<bool> ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>读单个地址。失败返回 null，不抛异常。</summary>
    Task<object?> ReadAsync(PlcAddressItem address, CancellationToken ct = default);

    /// <summary>写单个地址。返回是否成功。</summary>
    Task<bool> WriteAsync(PlcAddressItem address, object value, CancellationToken ct = default);

    /// <summary>
    /// 批量读。一次握手读多个地址，节拍敏感场景必须用它。
    /// 返回字典：逻辑名 → 值。
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> ReadBatchAsync(
        IEnumerable<PlcAddressItem> addresses,
        CancellationToken ct = default);

    /// <summary>
    /// 写检测结论到 PLC。
    ///
    /// 单独抽出来是因为这是本系统最高频的写操作，
    /// 不同品牌的实现差异也集中在这里（有的写单个位，有的写一个字）。
    /// </summary>
    Task<bool> WriteInspectionVerdictAsync(
        PlcAddressItem address,
        InspectionVerdict verdict,
        CancellationToken ct = default);
}
