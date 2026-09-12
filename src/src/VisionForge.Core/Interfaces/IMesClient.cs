using VisionForge.Core.Models;

namespace VisionForge.Core.Interfaces;

/// <summary>
/// MES 上传接口 —— 这套软件与公司 MES 之间<b>唯一的耦合点</b>。
///
/// <para><b>为什么做成接口：</b>每家 MES 的接入方式都不一样
/// （HTTP/JSON、WebService、数据库直写、MQTT、甚至是中间件）。
/// 现场只要换一个实现类，主程序一行都不用动 —— 和"换相机/换算法"是同一个思路。</para>
///
/// <para>默认提供两种实现：
/// <list type="bullet">
///   <item><b>HTTP/JSON</b>：按 <see cref="MesUploadPayload"/> 的固定契约 POST 出去，覆盖大多数 MES</item>
///   <item><b>本地文件</b>：把同样的 JSON 落盘，用于没有 MES 的开发/试产阶段（也方便对接方先看数据长什么样）</item>
/// </list></para>
/// </summary>
public interface IMesClient
{
    /// <summary>实现名（界面上显示，例如"HTTP/JSON · http://mes/api/sop"）。</summary>
    string Name { get; }

    /// <summary>是否启用（配置里关掉就不上传，但数据照样本地留存）。</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 上传一条数据。
    ///
    /// <para>约定：<b>不要抛异常</b>，失败请返回 <see cref="MesUploadResult"/>，
    /// 由调用方决定是否进重试队列。产线上"上传失败"是常态（网络抖、MES 重启），
    /// 不能因为它把生产流程打断。</para>
    /// </summary>
    Task<MesUploadResult> UploadAsync(MesUploadPayload payload, CancellationToken ct = default);

    /// <summary>连通性自检（设置页的"测试连接"按钮用）。</summary>
    Task<MesUploadResult> TestAsync(CancellationToken ct = default);
}
