using System.Text;
using System.IO;
using System.Windows.Input;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Hardware.Simulation;
using VisionForge.Infrastructure.Storage;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 一键演示 —— 给现场演示 / 客户验收用的"全流程走一遍"。
///
/// <para>它做的事情，和操作员在工位上做的一模一样，只是用模拟的人和模拟的画面：</para>
/// <list type="number">
///   <item>自动建图：把作业位（料盒位置）建出来，坐标与配方的 ROI 对齐</item>
///   <item>模拟操作员"抓两个东西"：手依次到两个作业位，各停够规定时长</item>
///   <item>跑一遍判定：规则引擎判定 → 应判 OK、流程推进两步</item>
///   <item>再跑一个反例：跳过第二步直接做第三步 → 应判跳步/错序并锁线</item>
///   <item>教示识别小演示：教两条 OK 样本，再拿一个明显不一样的动作去认</item>
/// </list>
///
/// <para><b>为什么要有这个按钮：</b>新来的人（或客户）不该先读文档才能看懂这套东西。
/// 点一下，界面上一行行出中文结果，比讲十分钟管用。</para>
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isDemoRunning;
    private string _demoSummary = "点「▶ 一键演示」看完整流程：自动建图 → 模拟抓两个东西 → 跑判定 → 反例（漏抓）";

    /// <summary>一键演示命令（在构造函数里初始化）。</summary>
    public ICommand RunDemoCommand { get; }

    /// <summary>
    /// 当前运行的是哪一版：取主程序文件的编译时间，显示在底部状态条上。
    ///
    /// <para>加这个是因为踩过一次坑 —— "改了却没看到变化"到底是我没改对，
    /// 还是你看的是旧版程序，靠猜很费时间。状态条上直接写出版本时间，一眼就能判断。</para>
    /// </summary>
    public string BuildStampText
    {
        get
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    return "版本 " + File.GetLastWriteTime(exe).ToString("MM-dd HH:mm");
            }
            catch { }

            return string.Empty;
        }
    }

    /// <summary>演示是否正在跑（跑的时候按钮禁用，避免重复点）。</summary>
    public bool IsDemoRunning
    {
        get => _isDemoRunning;
        private set
        {
            if (SetProperty(ref _isDemoRunning, value))
            {
                OnPropertyChanged(nameof(DemoButtonText));
                (RunDemoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string DemoButtonText => IsDemoRunning ? "演示进行中…" : "▶ 一键演示";

    /// <summary>演示小结：一行一步，中文播报。</summary>
    public string DemoSummary
    {
        get => _demoSummary;
        private set => SetProperty(ref _demoSummary, value);
    }

    /// <summary>
    /// 跑一遍完整流程。
    ///
    /// <para>判定用的是<b>生产代码本身</b>（规则引擎 + 识别器 + 状态机），
    /// 差别只在于"人"和"画面"是模拟的 —— 所以这个演示不是动画，是真的在跑逻辑。</para>
    /// </summary>
    private async Task RunAutoDemoAsync()
    {
        if (IsDemoRunning) return;
        IsDemoRunning = true;

        var report = new StringBuilder();
        void Step(string text)
        {
            report.AppendLine(text);
            DemoSummary = report.ToString();
            StatusMessage = text;
            _log.Info("【一键演示】" + text);
        }

        try
        {
            Step("① 准备：先连上模拟相机（没有真相机也能演）");
            if (_camera?.IsConnected != true) await ConnectCameraAsync();
            Step(_camera?.IsConnected == true
                ? "   模拟相机已连接，画面区应该能看到图了"
                : "   相机没连上 —— 判定逻辑照常演，但画面上看不到东西");
            await Task.Delay(600);

            // ---- 建图：用现场自己框出来的框 ----
            //
            // 这里以前是"自动建 6 个作业位"，现在改成必须现场自己框：
            // 作业位坐标与相机机位、料盒位置强相关，软件凭空给坐标只会误导人。
            var recipe = ActiveRecipe;
            var boxes = recipe?.Rois.Where(r => r.Enabled).ToList() ?? new List<RoiRegion>();

            if (boxes.Count == 0)
            {
                Step("② 还没有作业位：先在画面上把料盒/抓取位置框出来");
                Step("   首页点「🎯 标定 ROI」→ 在画面上拖出框（或点一下生成默认框）→ 点「⇄ 同步为判定区域」");
                Step("   框完之后再点「▶ 一键演示」，演示就会用你自己框的位置跑一遍");
                Step("（演示到此结束：没有框就没法编造位置，这是刻意的）");
                return;
            }

            SyncRoisToTargets();                       // 把框变成作业位与工序
            var config = _processConfig;
            if (config is null || config.Targets.Count == 0)
            {
                Step("② 作业位没建起来（框可能无效），演示到此结束");
                return;
            }

            BuildProcessTargetStates();
            OnPropertyChanged(nameof(ProcessRuleSummary));

            Step($"② 建图：按你自己框的 {config.Targets.Count} 个作业位建图（第 N 个框 = 第 N 道工序）");
            Step("   " + string.Join("、", config.Targets.Select(t => $"{t.Id} {t.Name}")));
            await Task.Delay(800);

            // ---- 模拟"抓两个东西" ----
            var twoTargets = config.Targets.Take(2).Select(t => t.Id).ToList();
            Step($"③ 模拟操作员「抓两个东西」：手依次到 {string.Join("、", twoTargets)}，" +
                 $"每个位置停够 {config.Kms:F0}ms（规则里的 K）");

            var okScript = twoTargets
                .Select(id => new PoseSegment
                {
                    TargetId = id,
                    DurationMs = config.Kms + 150,
                    Note = "抓取 " + id,
                })
                .ToList();

            var okResult = RunScript(config, okScript);
            Step($"   判定结果：{TranslateVerdict(okResult.Verdict)} —— {okResult.Message}");
            Step($"   累计违规 {okResult.Violations.Count} 条（应为 0）");
            await Task.Delay(800);

            // ---- 反例：跳过中间一步 ----
            Step("④ 反例：故意跳过第 2 步，直接去做第 3 步（模拟漏抓 / 跳步）");

            var skipScript = new List<PoseSegment>
            {
                new() { TargetId = config.Targets[0].Id, DurationMs = config.Kms + 150, Note = "抓第一个" },
                new() { TargetId = config.Targets[2].Id, DurationMs = config.Kms + 150, Note = "跳过第二个直接抓第三个" },
            };

            var skipResult = RunScript(config, skipScript);
            Step($"   判定结果：{TranslateVerdict(skipResult.Verdict)} —— {skipResult.Message}");
            Step($"   违规明细：{string.Join("；", skipResult.Violations.Select(v => v.CodeText + " " + v.Message))}");
            Step("   现场表现：红灯亮 + 蜂鸣 + 锁线，必须返工合格才能继续");
            await Task.Delay(800);

            // ---- 教示识别小演示 ----
            Step("⑤ 教示识别演示：教两条「标准动作」样本，再拿一个明显不一样的动作去认");
            bool teachingOk = RunTeachingDemo();
            Step(teachingOk
                ? "   识别结果符合预期：像刚才教的 → 判 OK；明显不一样 → 判 NG 或「无法判定」"
                : "   识别演示未能全部通过（样本差异太小或相机未出图），可重复一次");

            Step("⑥ 演示结束。以上每一步都是真的在跑判定逻辑，不是动画。");
            Step("   下一步可以点「启动过程监测」看手部动作实时判定，或到「配方管理」调参数。");
        }
        catch (Exception ex)
        {
            Step("演示中断：" + ex.Message);
            _log.Error("一键演示异常", ex);
        }
        finally
        {
            IsDemoRunning = false;
        }
    }

    /// <summary>用给定脚本跑一遍规则引擎，返回最后一帧结论（不碰界面、不依赖真实时间）。</summary>
    private static ProcessEvalResult RunScript(ProcessRuleConfig config, List<PoseSegment> script)
    {
        var engine = new HandActionRuleEngine(config);
        var source = new SimulatedPoseSource(config, script);

        ProcessEvaluation last = new();
        foreach (var frame in source.Frames)
        {
            last = engine.Feed(frame);
            if (last.Verdict is ProcessVerdict.AllCompleted or ProcessVerdict.Violation) break;
        }

        return new ProcessEvalResult(last.Verdict, last.Message, engine.AllViolations.ToList());
    }

    private static string TranslateVerdict(ProcessVerdict verdict) => verdict switch
    {
        ProcessVerdict.AllCompleted => "✅ OK（整件合规）",
        ProcessVerdict.StepCompleted => "✅ 一步合规",
        ProcessVerdict.Violation => "❌ NG（违规，要拦）",
        ProcessVerdict.Unreliable => "⚠ 关键点丢失（报警，不算违规）",
        _ => "…进行中",
    };

    /// <summary>
    /// 教示识别演示：在模拟画面上造三种场景，教两条 OK + 一条 NG，再分别去认。
    /// 返回是否"认得像" —— 与教过的场景一致应判 OK，明显不同的场景不应误判成 OK。
    /// </summary>
    private bool RunTeachingDemo()
    {
        var sceneCamera = _camera as ISceneCamera;
        if (sceneCamera is null) return false;

        // 两个作业位有料 = 标准动作；另一个位置有料 = 明显不同的动作
        var okBlobs = MakeBlobs(_processConfig!, take: 2);
        var otherBlobs = MakeBlobs(_processConfig!, skip: 3, take: 1);

        var samples = new List<ActionSample>();

        sceneCamera.SetScene(okBlobs);
        samples.Add(NewSample(sceneCamera, isOk: true));

        sceneCamera.SetScene(otherBlobs);
        samples.Add(NewSample(sceneCamera, isOk: false));

        // 用这两条样本互相"考"一遍
        var classifier = new ActionSampleClassifier(samples) { MinSimilarity = SimilarityThreshold };

        var okRecognition = classifier.Recognize(samples[0].Feature);
        var otherRecognition = classifier.Recognize(samples[1].Feature);

        DemoSummary += $"   样本：{samples.Count} 条（OK 1 / NG 1）\n";
        DemoSummary += $"   再认标准动作 → {okRecognition.IsOk switch { true => "OK", false => "NG", null => "无法判定" }}（相似度 {okRecognition.Confidence:P0}）\n";
        DemoSummary += $"   再认不同动作 → {otherRecognition.IsOk switch { true => "OK", false => "NG", null => "无法判定" }}（相似度 {otherRecognition.Confidence:P0}）\n";

        return okRecognition.IsOk == true && otherRecognition.IsOk != true;
    }

    private static List<SceneBlob> MakeBlobs(ProcessRuleConfig config, int skip = 0, int take = 2)
    {
        return config.Targets
            .Skip(skip)
            .Take(take)
            .Select(t => new SceneBlob
            {
                X = t.X + t.Width * 0.1,
                Y = t.Y + t.Height * 0.1,
                Width = t.Width * 0.8,
                Height = t.Height * 0.8,
            })
            .ToList();
    }

    private static ActionSample NewSample(ISceneCamera camera, bool isOk)
    {
        var frame = camera.GrabOnceAsync(500).GetAwaiter().GetResult();
        return new ActionSample
        {
            IsOk = isOk,
            Feature = FrameFeature.Extract(frame!),
        };
    }

    /// <summary>演示结果的精简载体（避免把引擎内部类型泄漏到界面）。</summary>
    private sealed record ProcessEvalResult(
        ProcessVerdict Verdict, string Message, List<ProcessViolation> Violations);
}
