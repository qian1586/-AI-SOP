using System.Collections.Concurrent;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Vision;

/// <summary>
/// SOP 序列合规检测插件 —— 把「漏步 / 跳步 / 逆序」检测能力并进本框架。
///
/// <para><b>它解决的是和别的算法不同的问题：</b></para>
/// 其他算法回答"这一刻产品合不合格"，本算法回答"**这个人有没有按顺序做**"。
/// 产品最后可能完全合格，但工序顺序错了 —— 下次就可能出坏品。这是过程合规检测。
///
/// <para><b>⚠️ 一个必须解决的架构冲突：</b></para>
/// <see cref="IInspectionAlgorithm.Run"/> 的契约是"一帧进、一判出"，无状态。
/// 但序列检测<b>天然需要跨帧记忆</b>——要知道"上一步做过没有"，就必须记住历史。
///
/// 解决办法：算法实例是单例（注册表里只有一份），所以在它内部按
/// <c>recipe.Id</c> 维护一份独立的状态机。每个配方各记各的，互不干扰。
/// 这样既满足无状态接口契约，又不丢掉跨帧能力。
/// 副作用是：算法实例会被长期持有，<b>必须提供 Reset 通道</b>（见 <see cref="ResetTracker"/>），
/// 否则换了产品还在用上一件的记忆，会凭空报跳步。
///
/// <para><b>怎么配置步骤顺序：不要额外配置，直接用 ROI 的先后顺序。</b></para>
/// 配方里 <c>Rois</c> 列表的第 0 个就是第 1 步，第 1 个就是第 2 步……以此类推。
/// 这样现场工程师在界面上拖框画 ROI 的时候，其实就在定义 SOP 步骤。
/// 少一套配置就少一处出错的地方。
///
/// 参数里的 <c>SignalMode</c> 决定"哪一瞬间算这一步完成"：
///   · 进入即完成 —— 手/物料进入该区域就认这一步做了（适合"放入"类动作）
///   · 离开即完成 —— 该区域从占用变空闲才算完成（适合"取走/用完归位"类动作）
/// </summary>
public sealed class SopSequenceAlgorithm : IInspectionAlgorithm
{
    /// <summary>每个配方一份状态机。键是 recipe.Id。</summary>
    private readonly ConcurrentDictionary<string, SequenceTracker> _trackers = new();

    /// <summary>ROI 被判定为"占用"的默认前景占比。</summary>
    private const double DefaultOccupancyRatio = 0.03;

    public string Key => "sop-sequence";

    public string DisplayName => "SOP 序列合规检测（漏步/跳步）";

    public string Description =>
        "按配方的 ROI 顺序作为工序步骤，检测作业是否按序执行。判定漏步、跳步、逆序三类过程异常。";

    public IReadOnlyList<ParameterDefinition> Parameters { get; } = new[]
    {
        new ParameterDefinition
        {
            Key = "Threshold", DisplayName = "二值化阈值", Type = ParameterType.Number,
            Min = 0, Max = 255, Default = 128, Unit = "灰度",
            Description = "把画面分成「有东西」和「空着」的分界线：亮度≥这个值的像素算有东西，" +
                          "再统计每个 ROI 被占了多少。现场怎么调：手/物料明显比背景亮取 100~160；" +
                          "调太低会把反光当成人手伸进来了（误判步骤已做）。",
        },
        new ParameterDefinition
        {
            Key = "OccupancyRatio", DisplayName = "占用判定比例", Type = ParameterType.Number,
            Min = 0.001, Max = 1, Default = DefaultOccupancyRatio,
            Description = "ROI 里有多少比例是「有东西」才算这一步做到位。默认 0.03（3%，很宽松，" +
                          "只要求区域里有东西）。调太小：人影、反光都会让它误以为做了；" +
                          "调太大：动作明明做了但手小一点就触发不了。",
        },
        new ParameterDefinition
        {
            Key = "SignalMode", DisplayName = "完成判定方式", Type = ParameterType.Enum,
            Default = 0, Options = new[] { "进入即完成", "离开即完成" },
            Description = "决定「哪一瞬间算这一步做完了」。" +
                          "放入类动作（把零件放进工装）选「进入即完成」；" +
                          "取走 / 用完归位类动作（把工具放回架子）选「离开即完成」。",
        },
        new ParameterDefinition
        {
            Key = "StrictOrder", DisplayName = "严格顺序（检测跳步）", Type = ParameterType.Bool,
            Default = 1,
            Description = "勾上：必须按第 1→2→3 步的顺序做，跳步会被判违规并锁线。" +
                          "不勾：只要求这些动作都做过，不管先后（适合顺序本来就不固定的工位）。",
        },
        new ParameterDefinition
        {
            Key = "DebounceFrames", DisplayName = "去抖帧数", Type = ParameterType.Number,
            Min = 1, Max = 30, Default = 3, Unit = "帧",
            Description = "要连续几帧看到的状态一致才算数：填 3 = 连拍 3 帧都一致才确认，" +
                          "用来滤掉偶发的单帧干扰；填 1 = 看一眼就下结论（反应快，但容易抖）。" +
                          "本机 23.5fps 下 3 帧约 128 毫秒。",
        },
        new ParameterDefinition
        {
            Key = "ResetOnComplete", DisplayName = "全部完成自动复位", Type = ParameterType.Bool,
            Default = 1,
            Description = "最后一步做完后自动开始下一件。连件生产（一件接一件）必须勾上，" +
                          "否则做完一件就停在完成状态，得人工点复位。",
        },
    };

    // ==================================================================
    public InspectionResult Run(CameraFrame frame, Recipe recipe)
    {
        var result = new InspectionResult
        {
            RecipeId = recipe.Id,
            RecipeName = recipe.Name,
            ProductModel = recipe.ProductModel,
            Timestamp = DateTime.Now,
        };

        var p = new ParameterReader(recipe, Parameters);
        int threshold = p.GetInt("Threshold", 128);
        double occupancyRatio = p.GetDouble("OccupancyRatio", DefaultOccupancyRatio);
        bool releaseMode = p.GetEnum("SignalMode", "进入即完成") == "离开即完成";
        bool strictOrder = p.GetBool("StrictOrder", true);
        int debounce = Math.Max(1, p.GetInt("DebounceFrames", 3));
        bool resetOnComplete = p.GetBool("ResetOnComplete", true);

        // ---- 用 ROI 顺序作为步骤顺序 ----
        var rois = recipe.Rois.Where(r => r.Enabled).ToList();
        if (rois.Count < 2)
        {
            return InspectionResult.Error(
                $"SOP 序列检测至少需要 2 个 ROI（当前 {rois.Count} 个）。" +
                "ROI 的先后顺序就是工序步骤顺序，请在配方里按工艺顺序依次添加。");
        }

        var tracker = _trackers.GetOrAdd(recipe.Id, _ => new SequenceTracker(rois.Count));
        if (tracker.StepCount != rois.Count)
        {
            // ROI 数量变了（改了配方），重建状态机，否则下标会越界
            tracker = new SequenceTracker(rois.Count);
            _trackers[recipe.Id] = tracker;
        }

        // ---- L1 感知：算每个 ROI 的占用比例 ----
        var occupancies = new double[rois.Count];
        for (int i = 0; i < rois.Count; i++)
            occupancies[i] = MeasureOccupancy(frame, rois[i], threshold);

        // ---- L2 事件：由 tracker 内部做去抖与边沿检测 ----
        // ---- L3 状态机：判定 ----
        var snapshot = tracker.Update(occupancies, occupancyRatio, releaseMode, debounce,
                                      strictOrder, resetOnComplete);

        // ---- 组装输出 ----
        for (int i = 0; i < rois.Count; i++)
        {
            result.Measurements.Add(new MeasurementItem
            {
                Name = $"步骤{i + 1}「{rois[i].Name}」占用率",
                Value = Math.Round(occupancies[i] * 100, 2),
                Unit = "%",
            });
        }

        result.Measurements.Add(new MeasurementItem
        {
            Name = "当前步骤", Value = snapshot.CurrentStep + 1, Unit = "步",
        });
        result.Measurements.Add(new MeasurementItem
        {
            Name = "本件违规次数", Value = snapshot.ViolationCount, Unit = "次",
        });

        // 进度透出来给上位机：只有"进度增加"才算一道工序完成
        result.ProgressSteps = snapshot.CompletedSteps;

        if (snapshot.NewViolations.Count > 0)
        {
            // 有新的违规 —— 这是本算法最核心的输出
            result.Verdict = InspectionVerdict.Fail;
            result.JudgementReason = string.Join("；", snapshot.NewViolations.Select(v => v.ToString()));

            foreach (var v in snapshot.NewViolations)
            {
                result.Defects.Add(new DefectItem
                {
                    Type = v.Kind switch
                    {
                        ViolationKind.Skipped => "跳步",
                        ViolationKind.Reversed => "逆序",
                        ViolationKind.Missed => "漏步",
                        _ => "顺序异常",
                    },
                    Confidence = 1.0,
                    Area = v.ToString(),
                });
            }
        }
        else if (snapshot.JustCompleted)
        {
            result.Verdict = InspectionVerdict.Pass;
            result.ProgressSteps = rois.Count;
            result.JudgementReason =
                $"整件完成，{rois.Count} 道工序全部按序执行" +
                (snapshot.ViolationCount > 0 ? $"（过程中有 {snapshot.ViolationCount} 次违规记录）" : "");
        }
        else if (snapshot.ProgressAdvanced)
        {
            // 按序又完成了一道工序 —— 这才是上位机要推进 SOP 的信号。
            // 少了这一支，"做完一步"和"什么都没做"没法区分，
            // 现象就是整件不做完、界面上永远停在第一步（自检 T1/T2 就是抓这个的）。
            int idx = Math.Clamp(snapshot.CompletedSteps - 1, 0, rois.Count - 1);
            result.Verdict = InspectionVerdict.Pass;
            result.ProgressSteps = snapshot.CompletedSteps;
            result.JudgementReason =
                $"第 {snapshot.CompletedSteps} 道工序「{rois[idx].Name}」按序完成，" +
                $"进度 {snapshot.CompletedSteps}/{rois.Count}";
        }
        else
        {
            // 流程进行中 —— 既不是不良，也不是"本步通过"。
            //
            // 这里刻意返回 None 而不是 Pass：返回 Pass 会让上位机认为
            // "这道工序做完了"，于是每触发一次就推进一步 ——
            // 员工什么都没干，进度条照样往前走。真正要拦的是"违规"，
            // 而"没做完"应该表现为"不推进"。
            result.Verdict = InspectionVerdict.None;
            result.JudgementReason =
                $"进行中：已完成 {snapshot.CompletedSteps}/{rois.Count} 步，" +
                $"当前应执行第 {snapshot.CurrentStep + 1} 步「{rois[snapshot.CurrentStep].Name}」";
        }

        return result;
    }

    // ==================================================================
    /// <summary>清空某个配方的序列状态。换产品 / 换班时必须调用。</summary>
    public void ResetTracker(string recipeId) => _trackers.TryRemove(recipeId, out _);

    /// <summary>清空所有配方状态。</summary>
    public void ResetAll() => _trackers.Clear();

    // ==================================================================
    /// <summary>
    /// 算 ROI 内前景像素占比。
    ///
    /// 为什么用"占比"而不是"占位数"：占比与 ROI 大小无关，
    /// 现场调好的阈值换个大小的 ROI 还能用，不用重调。
    /// </summary>
    private static double MeasureOccupancy(CameraFrame frame, RoiRegion roi, int threshold)
    {
        var (x0, y0, w, h) = roi.ToPixels(frame.Width, frame.Height);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        w = Math.Min(w, frame.Width - x0);
        h = Math.Min(h, frame.Height - y0);

        if (w <= 0 || h <= 0) return 0;

        long foreground = 0;
        long total = (long)w * h;

        // 每 2 个像素采样一次 —— 工业现场 ROI 动辄几十万像素，
        // 全采样纯属浪费算力，隔行采样对"占比"的估计几乎无损
        for (int y = y0; y < y0 + h; y += 2)
        {
            for (int x = x0; x < x0 + w; x += 2)
            {
                byte lum = frame.Format == PixelFormat.Mono8 ? frame.GetGray(x, y) : frame.GetLuminance(x, y);
                if (lum >= threshold) foreground++;
            }
        }

        long sampled = ((long)((h + 1) / 2)) * ((w + 1) / 2);
        return sampled == 0 ? 0 : (double)foreground / sampled;
    }
}

// ======================================================================
// 序列状态机（本文件内部使用）
// ======================================================================

public enum ViolationKind
{
    /// <summary>跳步：还没做当前步，后面步骤的信号先到了。</summary>
    Skipped,

    /// <summary>逆序：已经推进到后面，又回头做前面的步骤。</summary>
    Reversed,

    /// <summary>漏步：某步骤一直没做，本件就结束了。</summary>
    Missed,
}

public sealed record Violation(ViolationKind Kind, int Step, string StepName, string Detail)
{
    public override string ToString() => Kind switch
    {
        ViolationKind.Skipped => $"跳步：跳过第 {Step + 1} 步「{StepName}」直接执行后续工序",
        ViolationKind.Reversed => $"逆序：已进行到后面，又回头执行第 {Step + 1} 步「{StepName}」",
        ViolationKind.Missed => $"漏步：第 {Step + 1} 步「{StepName}」在本件作业中从未执行",
        _ => Detail,
    };
}

public sealed class SequenceSnapshot
{
    public int CurrentStep { get; init; }
    public int CompletedSteps { get; init; }
    public int StepCount { get; init; }
    public int ViolationCount { get; init; }
    public bool JustCompleted { get; init; }

    /// <summary>
    /// 本次调用是否又完成了一道工序。
    /// 上位机靠它把"这一步做完了"和"什么都没发生"分开 ——
    /// 没有这个位，中间步骤全部只能报"进行中"，SOP 永远推不动。
    /// </summary>
    public bool ProgressAdvanced { get; init; }

    public List<Violation> NewViolations { get; init; } = new();
}

/// <summary>
/// 单件作业的序列跟踪器。
///
/// 逻辑从 Python 版 ai-sop-assembly 的 SOP 状态机移植过来，核心判定规则一致：
///   · 事件属于当前步骤  → 正常推进
///   · 事件属于后面步骤  → 中间的都算跳步
///   · 事件属于前面步骤  → 逆序
///   · 本件结束仍有步骤没做 → 漏步
/// </summary>
internal sealed class SequenceTracker
{
    private readonly bool[] _confirmed;      // 去抖后的稳定状态
    private readonly bool[] _raw;            // 本帧原始状态
    private readonly int[] _counter;         // 连续一致帧数
    private readonly object _gate = new();

    public SequenceTracker(int stepCount)
    {
        StepCount = stepCount;
        _confirmed = new bool[stepCount];
        _raw = new bool[stepCount];
        _counter = new int[stepCount];
        Reset();
    }

    public int StepCount { get; }

    public int CurrentStep { get; private set; }
    public int ViolationCount { get; private set; }
    public bool Completed { get; private set; }

    /// <summary>上一次"报告出去"的完成数。用来判断本次是不是新的里程碑。</summary>
    private int _reportedCompleted;

    private readonly List<Violation> _pending = new();

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_confirmed);
            Array.Clear(_raw);
            Array.Clear(_counter);
            CurrentStep = 0;
            ViolationCount = 0;
            Completed = false;
            _reportedCompleted = 0;
            _pending.Clear();
        }
    }

    /// <summary>
    /// 喂入本帧各 ROI 的占用比例，返回判定快照。
    /// </summary>
    public SequenceSnapshot Update(
        double[] occupancies,
        double occupancyRatio,
        bool releaseMode,
        int debounce,
        bool strictOrder,
        bool resetOnComplete)
    {
        lock (_gate)
        {
            _pending.Clear();
            bool justCompleted = false;

            for (int i = 0; i < StepCount && i < occupancies.Length; i++)
            {
                bool raw = occupancies[i] >= occupancyRatio;

                // ---- 去抖：连续 N 帧一致才认 ----
                if (raw != _raw[i]) { _raw[i] = raw; _counter[i] = 1; }
                else _counter[i]++;

                if (_counter[i] < debounce) continue;
                if (_confirmed[i] == raw) continue;

                _confirmed[i] = raw;

                // ---- 边沿 → 事件 ----
                // "进入即完成"：占用上升沿算完成
                // "离开即完成"：占用下降沿算完成
                bool isCompletion = releaseMode ? !raw : raw;
                if (!isCompletion) continue;

                if (Completed) continue;

                if (i == CurrentStep)
                {
                    // 正常推进
                    CurrentStep++;
                    if (CurrentStep >= StepCount)
                    {
                        Completed = true;
                        justCompleted = true;

                        // 本件结束时回头看有没有漏步（中间不可能有，因为跳步会即时捕获，
                        // 但"最后几步没做就重新开始"的情况需要在这里兜住）
                    }
                }
                else if (i > CurrentStep)
                {
                    // 跳步：中间那些步骤都没做。
                    //
                    // 关键：这里**不**推进 CurrentStep。早期版本会直接跳到 i+1，
                    // 后果是"操作员回头补做被跳过的那一步"反而被判成逆序，
                    // 现场表现就是"修好了却怎么也过不去"。
                    // 正确语义与 SOP 状态机一致：NG 后停在原地，
                    // 补做当前步骤才算通过，违规记录照样留着。
                    if (strictOrder)
                    {
                        for (int k = CurrentStep; k < i; k++)
                        {
                            _pending.Add(new Violation(
                                ViolationKind.Skipped, k, $"步骤{k + 1}",
                                $"未执行第 {k + 1} 步，直接开始了第 {i + 1} 步"));
                            ViolationCount++;
                        }
                    }
                }
                else
                {
                    // 逆序：已经推进到后面，又回头做前面的
                    _pending.Add(new Violation(
                        ViolationKind.Reversed, i, $"步骤{i + 1}",
                        $"已进行到第 {CurrentStep + 1} 步，又回头执行第 {i + 1} 步"));
                    ViolationCount++;
                }
            }

            // ---- 进度里程碑：本次是否又完成了一道工序 ----
            int completedNow = Completed ? StepCount : CurrentStep;
            bool progressAdvanced = completedNow > _reportedCompleted;
            if (progressAdvanced) _reportedCompleted = completedNow;

            // ---- 全部完成 → 复位，准备下一件 ----
            if (justCompleted && resetOnComplete)
            {
                // 违规记录要带出去，所以先取快照再清
                var snapshot = BuildSnapshot(justCompleted: true, progressAdvanced: true);
                Reset();
                return snapshot;
            }

            return BuildSnapshot(justCompleted, progressAdvanced);
        }
    }

    private SequenceSnapshot BuildSnapshot(bool justCompleted, bool progressAdvanced) => new()
    {
        CurrentStep = Math.Min(CurrentStep, StepCount - 1),
        CompletedSteps = CurrentStep,
        StepCount = StepCount,
        ViolationCount = ViolationCount,
        JustCompleted = justCompleted,
        ProgressAdvanced = progressAdvanced,
        NewViolations = new List<Violation>(_pending),
    };
}
