namespace VisionForge.Core.Models;

/// <summary>
/// 违规码表（方案 v1.3，借鉴合米 / 盈泰德两家的命名）。
///
/// <para>为什么一定要"码"而不是只有一句中文：码能统计、能追溯、能闭环处理。
/// 现场班长看到的是中文说明，但报表里按 <see cref="ViolationCode"/> 聚合 ——
/// "本周 STEP_SKIPPED 出现 12 次"这种信息，靠自由文本日志是聚合不出来的。</para>
/// </summary>
public enum ViolationCode
{
    /// <summary>跳步：某道工序从未执行就结束了。</summary>
    StepSkipped = 0,

    /// <summary>错序：还没做当前工序，就先做了后面的工序。</summary>
    StepOutOfOrder = 1,

    /// <summary>目标位置未被操作：时间窗内手没到位，或停留时间不够（动作不达标）。</summary>
    StepNotDone = 2,

    /// <summary>数量不符（对象状态轨），如期望 2 颗螺丝只有 1 颗。</summary>
    CountMismatch = 3,

    /// <summary>关键点持续丢失 —— 报警请人工确认，<b>不</b>判定违规。</summary>
    Unreliable = 4,

    /// <summary>规则配置错误（YAML/JSON 解析或字段校验失败）。</summary>
    ConfigInvalid = 5,
}

public static class ViolationCodeExtensions
{
    /// <summary>规则码文本。日志与报表用这个，别用中文枚举名。</summary>
    public static string ToCode(this ViolationCode code) => code switch
    {
        ViolationCode.StepSkipped => "STEP_SKIPPED",
        ViolationCode.StepOutOfOrder => "STEP_OUT_OF_ORDER",
        ViolationCode.StepNotDone => "STEP_NOT_DONE",
        ViolationCode.CountMismatch => "COUNT_MISMATCH",
        ViolationCode.Unreliable => "UNRELIABLE",
        ViolationCode.ConfigInvalid => "CONFIG_INVALID",
        _ => "UNKNOWN",
    };

    /// <summary>给操作员看的中文名。</summary>
    public static string ToChinese(this ViolationCode code) => code switch
    {
        ViolationCode.StepSkipped => "跳步",
        ViolationCode.StepOutOfOrder => "错序",
        ViolationCode.StepNotDone => "目标未完成",
        ViolationCode.CountMismatch => "数量不符",
        ViolationCode.Unreliable => "关键点丢失",
        ViolationCode.ConfigInvalid => "规则配置错误",
        _ => "未知",
    };
}

/// <summary>一条违规记录。</summary>
public sealed class ProcessViolation
{
    public ViolationCode Code { get; set; }
    public int StepSeq { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>是否已被处理（班组长确认）。界面上显示"已处理"用。</summary>
    public bool Handled { get; set; }

    /// <summary>
    /// 违规时刻的抓帧图路径（NG 保持图）。
    ///
    /// <para>只给一行规则码，操作员复盘不了——他得看到"当时手/物料是什么样"。
    /// 所以我们防的是过程手法错误，这条尤其重要：动作不对的瞬间必须有图。</para>
    /// </summary>
    public string? EvidenceImagePath { get; set; }

    public string CodeText => Code.ToCode();

    public string Display =>
        $"{CodeText} #{StepSeq}  {Message}  ({Timestamp:HH:mm:ss})";

    public override string ToString() => Display;
}

/// <summary>
/// 目标对象（料盒 / 定位销 / 产品位）。
///
/// <para><b>坐标空间说明：</b>方案里写的是"像素 ROI"（相机固定、不做物理标定）。
/// 本项目内部统一用<b>归一化坐标</b>（0~1，相对画面），原因是：
/// 界面上拖框是在 960×720 的预览画布上操作的，而相机是 2448×2048 ——
/// 存归一化才能真正做到"换个分辨率不改配置"。
/// 对现场来说体验完全一样：拖框即得，不用管坐标；导出的 YAML 里会标注坐标空间。</para>
/// </summary>
public sealed class ProcessTarget
{
    /// <summary>目标编号，如 P1。配置与违规记录都引用它。</summary>
    public string Id { get; set; } = "P1";

    /// <summary>中文名，如"弹簧盒"。教示时填写。</summary>
    public string Name { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>对象状态轨：期望数量。0 = 不校验数量。</summary>
    public int ExpectedCount { get; set; }

    public bool Contains(double x, double y) =>
        x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
}

/// <summary>SOP 的一道工序（工位级有序）。</summary>
public sealed class ProcessStepDefinition
{
    public int Seq { get; set; }

    /// <summary>动作名（教示时命名，如"取弹簧""装外壳"）。界面实时显示它。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本步必须覆盖的目标。</summary>
    public List<string> Targets { get; set; } = new();

    /// <summary>标称时长（ms），0 = 不参考。</summary>
    public double NominalTimeMs { get; set; }

    /// <summary>本步的停留阈值（ms），0 = 用全局 K。</summary>
    public double Kms { get; set; }
}

/// <summary>对象状态轨的开关（型号 / 数量 / 正反）。</summary>
public sealed class ProcessObjectStateRule
{
    /// <summary>型号比对（V1 增强，MVP 占位）。</summary>
    public bool Model { get; set; }

    /// <summary>数量计数（MVP 支持）。</summary>
    public bool Count { get; set; } = true;

    /// <summary>正反朝向（V1 增强，MVP 占位）。</summary>
    public bool Orientation { get; set; }
}

/// <summary>
/// 过程层规则配置（v1.3 统一模型）。
///
/// <para>SOP 是"工位级有序步骤序列"，每一步绑定目标与判定方式；
/// 单步内部是集合覆盖（目标到过即可，不分先后）。</para>
/// </summary>
public sealed class ProcessRuleConfig
{
    public string StationName { get; set; } = "工位1";
    public string CameraModel { get; set; } = "HC-500-10GM";

    /// <summary>实际帧率。K 按时间基准，帧率变化不影响判定标准。</summary>
    public double Fps { get; set; } = 23.5;

    public List<ProcessTarget> Targets { get; set; } = new();
    public List<ProcessStepDefinition> Steps { get; set; } = new();

    /// <summary>必需目标集合 S（留空表示用当前步骤自身的 Targets）。</summary>
    public List<string> Required { get; set; } = new();

    /// <summary>步骤间是否强制顺序（跳步/错序检测）。</summary>
    public bool OrderEnforced { get; set; } = true;

    /// <summary>停留阈值 K（ms）。默认 300ms（方案 v1.1：K 按时间，不按帧）。</summary>
    public double Kms { get; set; } = 300;

    /// <summary>动作时间窗 T（ms）。超时仍未完成即"目标未完成"。</summary>
    public double TimeWindowMs { get; set; } = 5000;

    /// <summary>关键点置信度阈值。低于它视为"看不清"。</summary>
    public double MinConfidence { get; set; } = 0.5;

    /// <summary>连续不可信时长上限（ms）。超过则报警（不判违规）。</summary>
    public double MaxUnreliableMs { get; set; } = 1000;

    public ProcessObjectStateRule ObjectState { get; set; } = new();

    /// <summary>是否在界面显示置信度条（方案 v7 补丁 B）。</summary>
    public bool ShowConfidence { get; set; } = true;

    /// <summary>一帧的时长（ms）。</summary>
    public double FrameMs => Fps > 0 ? 1000.0 / Fps : 1000.0 / 23.5;

    public ProcessTarget? FindTarget(string id) =>
        Targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 配置校验。返回空列表表示通过。
    /// 校验失败要变成一条明确的 CONFIG_INVALID，而不是运行期"莫名不动作"。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Fps <= 0) errors.Add("fps 必须大于 0");
        if (Kms <= 0) errors.Add("K_ms 必须大于 0");
        if (TimeWindowMs <= 0) errors.Add("T_ms 必须大于 0");
        if (MinConfidence <= 0 || MinConfidence >= 1) errors.Add("置信度阈值应在 0~1 之间");
        if (MaxUnreliableMs <= 0) errors.Add("max_unreliable_ms 必须大于 0");

        if (Targets.Count == 0) errors.Add("至少要配置一个目标（料盒/定位销/产品位）");

        var dupIds = Targets.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() > 1)
                            .Select(g => g.Key)
                            .ToList();
        if (dupIds.Count > 0) errors.Add("目标编号重复：" + string.Join(",", dupIds));

        foreach (var t in Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) errors.Add("存在编号为空的目标");
            if (t.Width <= 0 || t.Height <= 0) errors.Add($"目标 {t.Id} 的宽或高为 0");
            if (t.X < 0 || t.Y < 0 || t.X + t.Width > 1.0001 || t.Y + t.Height > 1.0001)
                errors.Add($"目标 {t.Id} 超出画面范围（坐标是归一化值，应为 0~1）");
        }

        if (Steps.Count == 0) errors.Add("至少要配置一道工序");

        var dupSeq = Steps.GroupBy(s => s.Seq).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupSeq.Count > 0) errors.Add("工序序号重复：" + string.Join(",", dupSeq));

        foreach (var s in Steps)
        {
            if (s.Targets.Count == 0) errors.Add($"工序 {s.Seq}「{s.Name}」没有绑定任何目标");
            foreach (var id in s.Targets)
            {
                if (FindTarget(id) is null) errors.Add($"工序 {s.Seq} 引用了不存在的目标 {id}");
            }
        }

        foreach (var id in Required)
        {
            if (FindTarget(id) is null) errors.Add($"必需目标 {id} 不存在");
        }

        return errors;
    }
}

/// <summary>手部关键点（21 点，归一化坐标）。索引含义：0=腕，5/9/13/17=四指根。</summary>
public sealed class HandLandmark
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Confidence { get; set; } = 0.9;
}

/// <summary>
/// 一帧的姿态观测。这是过程层的输入契约 ——
/// 真实实现来自手部检测 + 21 点关键点模型，模拟实现来自脚本化轨迹。
/// </summary>
public sealed class PoseObservation
{
    /// <summary>帧时间戳（ms）。由数据源提供，引擎用它做时间基准。</summary>
    public double TimestampMs { get; set; }

    /// <summary>手里是否在画面内（手部检测器给出）。</summary>
    public bool HandVisible { get; set; } = true;

    /// <summary>21 个关键点。空表示这一帧没有检测到手。</summary>
    public IReadOnlyList<HandLandmark> Landmarks { get; set; } = Array.Empty<HandLandmark>();

    /// <summary>对象状态轨：各目标当前数量（可选，来自对象检测）。</summary>
    public IReadOnlyDictionary<string, int>? TargetCounts { get; set; }

    /// <summary>整手最低置信度。低于阈值就整帧判为"看不清"。</summary>
    public double MinConfidence =>
        Landmarks.Count == 0 ? 0 : Landmarks.Min(l => l.Confidence);

    /// <summary>
    /// 掌心中心 = 腕点(0) + 四指根(5/9/13/17) 的均值。
    /// 方案 v1.3 特意把判定点从腕点升级成掌心：腕点在手摆动时抖动大，
    /// 质心稳得多，能显著减少 ROI 边界处的误判。
    /// </summary>
    public bool TryGetPalmCenter(out double x, out double y)
    {
        x = y = 0;
        if (Landmarks.Count < 21) return false;

        int[] idx = { 0, 5, 9, 13, 17 };
        double sx = 0, sy = 0;
        foreach (var i in idx)
        {
            sx += Landmarks[i].X;
            sy += Landmarks[i].Y;
        }

        x = sx / idx.Length;
        y = sy / idx.Length;
        return true;
    }
}

/// <summary>过程层的判定结论。</summary>
public enum ProcessVerdict
{
    /// <summary>还在进行中（本帧没有结论）。</summary>
    InProgress = 0,

    /// <summary>刚完成一道工序，推进到下一道。</summary>
    StepCompleted = 1,

    /// <summary>全部工序完成。</summary>
    AllCompleted = 2,

    /// <summary>违规（要拦截）。</summary>
    Violation = 3,

    /// <summary>关键点持续丢失 —— 报警请人工确认，不算违规。</summary>
    Unreliable = 4,
}

/// <summary>一次判定的完整结果。</summary>
public sealed class ProcessEvaluation
{
    public ProcessVerdict Verdict { get; set; } = ProcessVerdict.InProgress;
    public int StepSeq { get; set; }
    public string StepName { get; set; } = string.Empty;

    /// <summary>当前动作完成度（0~0.99）。方案 v7 补丁 B：MVP 用"停留/K"近似置信度。</summary>
    public double Confidence { get; set; }

    /// <summary>整件进度 0~1。</summary>
    public double Progress { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>本帧新产生的违规（没有就是空）。</summary>
    public IReadOnlyList<ProcessViolation> NewViolations { get; set; } = Array.Empty<ProcessViolation>();

    /// <summary>已覆盖（停留达标并完成过）的目标编号，界面三态用。</summary>
    public IReadOnlyList<string> CoveredTargets { get; set; } = Array.Empty<string>();

    /// <summary>当前步骤正在等待的目标（界面高亮用）。</summary>
    public IReadOnlyList<string> ActiveTargets { get; set; } = Array.Empty<string>();
}
