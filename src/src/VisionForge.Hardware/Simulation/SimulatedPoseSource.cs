using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Simulation;

/// <summary>轨迹脚本的一段：手在某个目标上停留（或悬停在某点）一段时间。</summary>
public sealed class PoseSegment
{
    /// <summary>目标编号（给了它就自动取该目标中心）。</summary>
    public string? TargetId { get; set; }

    /// <summary>不指定目标时用的归一化坐标（用来表示"手在目标之间移动"）。</summary>
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;

    public double DurationMs { get; set; }

    /// <summary>关键点置信度。设成低于阈值的值即可模拟"手被挡住/看不清"。</summary>
    public double Confidence { get; set; } = 0.95;

    public bool HandVisible { get; set; } = true;

    /// <summary>对象状态轨：这一段里各目标的数量（可空）。</summary>
    public Dictionary<string, int>? TargetCounts { get; set; }

    /// <summary>备注，测试报告里会打出来。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// 模拟手部姿态源 —— 按脚本生成逐帧的 21 点手部关键点。
///
/// <para><b>它解决什么问题：</b>过程层的正确性（K 时间基准、低置信跳过、
/// 跳步/错序、超时判定）全都是纯逻辑，不该等到相机和模型到位才能验证。
/// 有了它，规则引擎可以拿"确定的手部轨迹"跑回归测试 ——
/// 和当初给保底层做模拟相机是同一个思路。</para>
///
/// <para>两条用法：<see cref="Frames"/> 直接拿全部帧（自检用，瞬间跑完）；
/// <see cref="Start"/> 按实际帧率实时播放（界面演示用，能看到手一格格移动）。</para>
/// </summary>
public sealed class SimulatedPoseSource : IPoseSource
{
    private readonly ProcessRuleConfig _config;
    private readonly List<PoseObservation> _frames;
    private Timer? _timer;
    private int _cursor;

    public SimulatedPoseSource(
        ProcessRuleConfig config,
        IEnumerable<PoseSegment> segments,
        string name = "模拟手部轨迹")
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Name = name;
        _frames = BuildFrames(config, segments);
    }

    public string Name { get; }

    public bool IsRunning => _timer is not null;

    /// <summary>整段轨迹的所有帧（自检直接委托跑）。</summary>
    public IReadOnlyList<PoseObservation> Frames => _frames;

    public event EventHandler<PoseObservation>? PoseArrived;

    public event EventHandler? Finished;

    public void Start()
    {
        if (_timer is not null) return;
        if (_frames.Count == 0) return;

        _cursor = 0;
        int intervalMs = Math.Max(10, (int)Math.Round(_config.FrameMs));
        _timer = new Timer(_ => OnTick(), null, 0, intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTick()
    {
        if (_cursor >= _frames.Count)
        {
            Stop();
            try { Finished?.Invoke(this, EventArgs.Empty); }
            catch { /* 订阅方异常不影响数据源 */ }
            return;
        }

        var frame = _frames[_cursor++];
        try { PoseArrived?.Invoke(this, frame); }
        catch { /* 订阅方异常不能把采集线程带崩 */ }
    }

    public void Dispose() => Stop();

    // ==================================================================
    /// <summary>标准作业轨迹：依次走到每道工序的每个目标，停留 K+余量。</summary>
    public static List<PoseSegment> BuildCorrectScript(ProcessRuleConfig config, double dwellExtraMs = 120)
    {
        var segments = new List<PoseSegment>();
        double k = config.Kms;

        foreach (var step in config.Steps)
        {
            foreach (var id in step.Targets)
            {
                segments.Add(new PoseSegment
                {
                    TargetId = id,
                    DurationMs = k + dwellExtraMs,
                    Note = $"规范作业：{step.Seq}. {step.Name} → {id}",
                });
            }

            // 每步之间插一小段"手离开"（模拟抬手换料），顺便验证"离开即清零"
            segments.Add(new PoseSegment { X = 0.5, Y = 0.95, DurationMs = 80, Note = "抬手换料" });
        }

        return segments;
    }

    /// <summary>跳步轨迹：跳过指定的那一序，直接做后面的工序。</summary>
    public static List<PoseSegment> BuildSkipScript(ProcessRuleConfig config, int skipSeq)
    {
        var segments = new List<PoseSegment>();
        double k = config.Kms;

        foreach (var step in config.Steps)
        {
            if (step.Seq == skipSeq) continue;   // 这一道不做

            foreach (var id in step.Targets)
            {
                segments.Add(new PoseSegment
                {
                    TargetId = id,
                    DurationMs = k + 120,
                    Note = $"跳过第 {skipSeq} 道，直接做 {step.Seq}. {step.Name}",
                });
            }
        }

        return segments;
    }

    /// <summary>快掠轨迹：手在目标上只停留极短时间（动作不达标）。</summary>
    public static List<PoseSegment> BuildTooFastScript(ProcessRuleConfig config, double ratio = 0.3)
    {
        var segments = new List<PoseSegment>();
        double k = config.Kms;

        foreach (var step in config.Steps)
        {
            foreach (var id in step.Targets)
            {
                segments.Add(new PoseSegment
                {
                    TargetId = id,
                    DurationMs = k * ratio,
                    Note = $"快掠：{id} 只停留 {k * ratio:F0}ms（要求 {k:F0}ms）",
                });
            }
            segments.Add(new PoseSegment { X = 0.5, Y = 0.95, DurationMs = 60, Note = "抬手" });
        }

        return segments;
    }

    /// <summary>遮挡轨迹：手在第一个目标前被挡住一段时间（低置信），再正常作业。</summary>
    public static List<PoseSegment> BuildOcclusionScript(ProcessRuleConfig config, double blockedMs = 1300)
    {
        var segments = new List<PoseSegment>();
        double k = config.Kms;
        string firstTarget = config.Steps.Count > 0 && config.Steps[0].Targets.Count > 0
            ? config.Steps[0].Targets[0]
            : (config.Targets.Count > 0 ? config.Targets[0].Id : "P1");

        // 手已经在目标上停留了 k*0.6（已累计一部分），接着被挡住 ——
        // 关键点：低置信帧必须"跳过不清零"，遮挡结束后应当接着累加到达标
        segments.Add(new PoseSegment
        {
            TargetId = firstTarget,
            DurationMs = k * 0.6,
            Note = "先正常停留一部分",
        });
        segments.Add(new PoseSegment
        {
            TargetId = firstTarget,
            DurationMs = blockedMs,
            Confidence = 0.1,
            Note = "被零件遮挡，关键点不可信",
        });
        segments.Add(new PoseSegment
        {
            TargetId = firstTarget,
            DurationMs = k * 0.6,
            Note = "遮挡结束，继续累加",
        });

        foreach (var step in config.Steps)
        {
            foreach (var id in step.Targets)
            {
                if (id == firstTarget && step == config.Steps[0]) continue;
                segments.Add(new PoseSegment { TargetId = id, DurationMs = k + 120, Note = $"规范作业：{id}" });
            }
        }

        return segments;
    }

    // ------------------------------------------------------------------
    private static List<PoseObservation> BuildFrames(ProcessRuleConfig config, IEnumerable<PoseSegment> segments)
    {
        var frames = new List<PoseObservation>();
        double frameMs = config.FrameMs;
        double time = 0;

        foreach (var segment in segments)
        {
            double x = segment.X;
            double y = segment.Y;

            if (!string.IsNullOrWhiteSpace(segment.TargetId))
            {
                var target = config.FindTarget(segment.TargetId!);
                if (target is not null)
                {
                    x = target.CenterX;
                    y = target.CenterY;
                }
            }

            int count = Math.Max(1, (int)Math.Round(segment.DurationMs / frameMs));
            for (int i = 0; i < count; i++)
            {
                frames.Add(new PoseObservation
                {
                    TimestampMs = time,
                    HandVisible = segment.HandVisible,
                    Landmarks = BuildHand(x, y, segment.Confidence),
                    TargetCounts = segment.TargetCounts,
                });
                time += frameMs;
            }
        }

        return frames;
    }

    /// <summary>
    /// 造一只手的 21 个点。
    ///
    /// <para>刻意让 0（腕）落在掌心点、5/9/13/17（四指根）在四周对称分布且偏移量互相抵消 ——
    /// 这样"五点均值"恰好等于我们想表达的手心位置。规则引擎读的是均值，
    /// 于是这个模拟源真的在检验掌心算法，而不是绕过它。</para>
    /// </summary>
    private static List<HandLandmark> BuildHand(double centerX, double centerY, double confidence)
    {
        var points = new List<HandLandmark>(21);

        for (int i = 0; i < 21; i++)
            points.Add(new HandLandmark { X = centerX, Y = centerY, Confidence = confidence });

        points[0] = new HandLandmark { X = centerX, Y = centerY, Confidence = confidence };

        double[,] rootOffsets =
        {
            { -0.010,  0.010 },
            {  0.010,  0.010 },
            {  0.010, -0.010 },
            { -0.010, -0.010 },
        };
        int[] rootIndex = { 5, 9, 13, 17 };

        for (int i = 0; i < rootIndex.Length; i++)
        {
            points[rootIndex[i]] = new HandLandmark
            {
                X = centerX + rootOffsets[i, 0],
                Y = centerY + rootOffsets[i, 1],
                Confidence = confidence,
            };
        }

        return points;
    }
}
