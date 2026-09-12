namespace VisionForge.Core.Models;

/// <summary>
/// 一个工位在某条产线上的位置与身份。
///
/// <para>为什么要"编号"而不是只有名字：几十个工位汇总到一个终端时，
/// 编号是唯一主键（名字现场会改），MES 那边也是按编号对工位。</para>
/// </summary>
public sealed class StationIdentity
{
    /// <summary>工位编号，例如 ST-07。全厂唯一，建议与 MES 的工位编码一致。</summary>
    public string StationCode { get; set; } = "ST-01";

    /// <summary>工位名（现场看的），例如"主机装配 7 号位"。</summary>
    public string StationName { get; set; } = string.Empty;

    /// <summary>产线名，例如 Line-03。</summary>
    public string LineName { get; set; } = string.Empty;
}

/// <summary>单个工序在这一次上报里的状态（给终端详情页用）。</summary>
public sealed class StationStepReport
{
    public int Seq { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>ok / ng / active / pending（和界面同一套颜色键）。</summary>
    public string StateKey { get; set; } = "pending";

    /// <summary>这一步最近一次用时（秒）。0 = 还没做过。</summary>
    public double DurationSec { get; set; }
}

/// <summary>
/// 工位实时状态 —— 这是"汇总终端看板"的最小显示单元。
///
/// <para>刻意只放"一眼要看的东西"：工位、产线、状态、当前工序、进度、
/// OK/NG、合规率、节拍、今日告警/漏步。几十个工位同屏时，
/// 每个格子信息太多反而什么都看不清。</para>
/// </summary>
public sealed class StationReport
{
    public string StationCode { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string LineName { get; set; } = string.Empty;

    public string ProductModel { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string BatchNo { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;

    /// <summary>运行中 / 待机 / 已锁线 / 未连接。</summary>
    public string StateText { get; set; } = "未连接";

    /// <summary>颜色键：ok / ng / active / pending。</summary>
    public string StateKey { get; set; } = "pending";

    public string CurrentStepName { get; set; } = "—";
    public int StepDone { get; set; }
    public int StepTotal { get; set; }

    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int CompletedPieces { get; set; }

    /// <summary>合规率（按工序计，%）。</summary>
    public double PassRate { get; set; }

    /// <summary>最近一件节拍（秒）。</summary>
    public double LastCycleSec { get; set; }

    /// <summary>最近几件平均节拍（秒）。</summary>
    public double AverageCycleSec { get; set; }

    public int TodayAlarm { get; set; }
    public int TodaySkipped { get; set; }

    /// <summary>工位本机时间（终端据此判断"多久没上报了"）。</summary>
    public DateTime ReportedAt { get; set; } = DateTime.Now;

    public List<StationStepReport> Steps { get; set; } = new();
}

/// <summary>一件产品里的某个工序（MES / 追溯用，带开始结束时刻）。</summary>
public sealed class PieceStepReport
{
    public int Seq { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public double DurationSec { get; set; }
    public double? StandardSec { get; set; }
    public bool IsOk { get; set; } = true;
}

/// <summary>一整件产品的数据（本地追溯 + 上传 MES 都用它）。</summary>
public sealed class PieceReport
{
    public string StationCode { get; set; } = string.Empty;
    public string PieceId { get; set; } = string.Empty;
    public string BatchNo { get; set; } = string.Empty;
    public string ProductModel { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public double CycleSec { get; set; }

    public bool IsOk { get; set; } = true;
    public string JudgementReason { get; set; } = string.Empty;

    public List<PieceStepReport> Steps { get; set; } = new();
}

/// <summary>MES 上传里的单个工序。</summary>
public sealed class MesStepPayload
{
    public int Seq { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StartedAt { get; set; } = string.Empty;
    public string FinishedAt { get; set; } = string.Empty;
    public double DurationSec { get; set; }
    public double? StandardSec { get; set; }
    public bool IsOk { get; set; }
}

/// <summary>
/// 上传给 MES 的固定契约（<b>这就是"留好的 MES 接口"</b>）。
///
/// <para>字段名固定、时间统一 <c>yyyy-MM-dd HH:mm:ss</c> 字符串、判定统一 OK/NG，
/// 违规码与我们写入 PLC 的规则码一致（0=跳步 1=错序 2=目标未完成 …），
/// 这样 MES 侧不用理解我们的内部结构，照字段填表即可。</para>
/// </summary>
public sealed class MesUploadPayload
{
    /// <summary>契约版本。以后加字段不删字段，靠它区分。</summary>
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>本次上报的唯一号（幂等用：MES 侧按它去重）。</summary>
    public string ReportId { get; set; } = Guid.NewGuid().ToString("N");

    public string StationCode { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string LineName { get; set; } = string.Empty;

    public string PieceId { get; set; } = string.Empty;
    public string BatchNo { get; set; } = string.Empty;
    public string ProductModel { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;

    public string StartedAt { get; set; } = string.Empty;
    public string FinishedAt { get; set; } = string.Empty;
    public double CycleSec { get; set; }

    /// <summary>OK / NG。</summary>
    public string Verdict { get; set; } = "OK";

    /// <summary>违规码整数（与 PLC 规则码一致）。OK 时为 -1。</summary>
    public int ViolationCode { get; set; } = -1;

    public string JudgementReason { get; set; } = string.Empty;

    public List<MesStepPayload> Steps { get; set; } = new();
}

/// <summary>一次 MES 上传的结果。</summary>
public sealed class MesUploadResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>是否值得重试（网络问题、5xx 值得；400 参数错不值得）。</summary>
    public bool ShouldRetry { get; set; }

    public static MesUploadResult Ok(string message = "上传成功") =>
        new() { Success = true, StatusCode = 200, Message = message };

    public static MesUploadResult Fail(int status, string message, bool retry = true) =>
        new() { Success = false, StatusCode = status, Message = message, ShouldRetry = retry };
}
