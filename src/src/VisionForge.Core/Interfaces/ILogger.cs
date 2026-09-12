namespace VisionForge.Core.Interfaces;

/// <summary>
/// 日志抽象。
///
/// 工控上位机的日志和互联网应用很不一样，这里定几条约定：
///   1. <b>必须落盘</b>。产线半夜出问题，第二天要能翻记录，光打控制台没用
///   2. <b>必须带时间戳且精确到毫秒</b>。排查节拍问题时光有秒不够
///   3. <b>写入不能阻塞检测主流程</b>。用后台队列，别让磁盘 IO 卡住产线
///   4. 必须能被现场工程师看懂 —— 不要写"操作失败"，要写"连接 192.168.1.10:502 超时 3000ms"
/// </summary>
public interface ILogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
