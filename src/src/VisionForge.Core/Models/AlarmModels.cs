namespace VisionForge.Core.Models;

/// <summary>
/// 声光报警信号状态。
///
/// 工业三色灯的标准约定（各厂略有差异，但对齐了现场就少解释）：
///   <list type="bullet">
///   <item><b>绿</b> — 正常运行 / 设备就绪</item>
///   <item><b>黄</b> — 警示：检测异常但不影响流转（非关键工序 NG、系统小故障）</item>
///   <item><b>红</b> — 故障 / 停线：流程锁死、必须人工介入</item>
///   </list>
///
/// 注意和 <see cref="InspectionVerdict"/> 的区别：
/// 检测结论是"产品行不行"，报警状态是"产线的灯该亮什么颜色"。
/// 两者不是一一对应 —— 例如"非关键工序 NG"在产品层面是 Fail，
/// 但产线不该停，灯应该是黄色而不是红色。
/// </summary>
public enum AlarmSignal
{
    /// <summary>全灭。设备未运行时用。</summary>
    None = 0,

    /// <summary>绿灯：正常。</summary>
    Ok = 1,

    /// <summary>黄灯：警示，不停线。</summary>
    Warning = 2,

    /// <summary>红灯：故障，停线。</summary>
    Fault = 3,
}

/// <summary>
/// 声光报警配置。
///
/// 灯和蜂鸣器都走 PLC 输出点 —— 这是工控现场最省事的做法：
/// 不用额外装驱动、不用管 USB 设备被系统禁用、接线就在已有的端子排上。
/// </summary>
public sealed class AlarmOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 各类信号对应的 PLC 地址项逻辑名（对应 <c>AppSettings.PlcAddresses</c> 里的 Name）。
    /// 留空表示不接该路信号。
    /// </summary>
    public string GreenLightAddress { get; set; } = "LightGreen";
    public string YellowLightAddress { get; set; } = "LightYellow";
    public string RedLightAddress { get; set; } = "LightRed";
    public string BuzzerAddress { get; set; } = "Buzzer";

    /// <summary>
    /// 蜂鸣器自动消音秒数。0 表示不自动消音。
    ///
    /// <b>这个参数很关键，必须有。</b>
    /// 蜂鸣器一直响的后果不是"提醒到位"，而是：
    ///   1. 工人用胶带把蜂鸣器贴死（真的会）
    ///   2. 整个车间的人对报警脱敏，以后真出事没人管
    /// 所以默认 30 秒自动消音 —— 灯保持红，但声音停下，等人来确认。
    /// </summary>
    public int BuzzerAutoOffSeconds { get; set; } = 30;

    /// <summary>
    /// 蜂鸣器是否跟随报警状态。关掉后只亮灯不响铃，
    /// 适合"旁边就是办公室"或者夜班场景。
    /// </summary>
    public bool BuzzerEnabled { get; set; } = true;

    /// <summary>报警解除后是否自动回到绿灯（正常态）。</summary>
    public bool AutoRecoverToGreen { get; set; } = true;
}
