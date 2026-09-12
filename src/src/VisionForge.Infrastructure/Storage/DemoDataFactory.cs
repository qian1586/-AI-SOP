using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// 示例数据工厂 —— 生成一套"打开就能用"的配方 + SOP。
///
/// <para><b>关于画面上的框（这一条改过，很重要）：</b>早期版本会自动生成 6 个 ROI 框，
/// 本意是让演示有东西可看。但现场反馈很明确：<b>框必须自己建</b> ——
/// 相机机位、料盒位置每台设备都不一样，软件凭空画 6 个框只会让人以为"位置已经定好了"。
/// 所以现在示例配方<b>一个框都不生成</b>，框由操作员在画面上自己框出来
/// （首页 →「🎯 标定 ROI」）。</para>
///
/// <para>SOP 的 6 道工序仍然保留：那是流程定义（第几步做什么），不是画面上的框。</para>
/// </summary>
public static class DemoDataFactory
{
    /// <summary>示例配方 Id。固定值，方便升级时原地替换。</summary>
    public const string DemoRecipeId = "demo-wm-assembly-6step";

    /// <summary>示例配方里"演示用的框"的旧样子，升级时用来识别并清掉（见 EnsureDemoDataAsync）。</summary>
    private static readonly (double X, double Y, double W, double H)[] PlaceholderBoxes =
    {
        (0.06, 0.10, 0.24, 0.33),
        (0.38, 0.10, 0.24, 0.33),
        (0.70, 0.10, 0.24, 0.33),
        (0.06, 0.55, 0.24, 0.33),
        (0.38, 0.55, 0.24, 0.33),
        (0.70, 0.55, 0.24, 0.33),
    };

    /// <summary>老版本示例配方的识别特征（只有 2 个 ROI，和 6 步 SOP 对不上）。</summary>
    private const string LegacySampleName = "双 ROI 检测（示例）";

    public static Recipe CreateDemoRecipe(string cameraId = "CAM-001") => new()
    {
        Id = DemoRecipeId,
        Name = "洗地机主机装配 · 6 工位（示例）",
        ProductModel = "WM-XXX",
        AlgorithmKey = "sop-sequence",
        CameraId = cameraId,
        CameraBindingKey = "工位1-上视-GigE",
        Remark = "示例数据：框由自己建（ROI 顺序 = 工序顺序，第 N 个框就是第 N 道工序的作业区）",
        AlgorithmParameters = new List<ParameterValue>
        {
            new("Threshold", "128"),
            new("OccupancyRatio", "0.03"),
            new("SignalMode", "0"),
            new("StrictOrder", "True"),
            // 取 3 帧：真实现场就是"光电触发后连拍几帧取稳定判定"，
            // 模拟环境同样走这条路，测试才覆盖得到去抖逻辑。
            new("DebounceFrames", "3"),
            new("ResetOnComplete", "True"),
        },
        // 一个框都不预置 —— 框必须由现场自己在画面上框出来
        Rois = new List<RoiRegion>(),
    };

    /// <summary>与示例配方一一对应的 6 步 SOP。</summary>
    public static SopDefinition CreateDemoSop(string recipeId) => new()
    {
        Name = "洗地机主机装配 SOP（示例）",
        ProductModel = "WM-XXX",
        Remark = "每一步绑定同一个配方里的对应 ROI：第 N 步 = 第 N 个 ROI",
        Steps = new List<SopStepDefinition>
        {
            new() { Seq = 1, Id = "S1", Name = "取件并放入工装", RecipeId = recipeId, Hint = "从物料区取壳体，放入定位工装", BlockNextOnFail = true,  TimeoutSec = 25 },
            new() { Seq = 2, Id = "S2", Name = "安装电机",       RecipeId = recipeId, Hint = "装入电机并预定位",           BlockNextOnFail = true,  TimeoutSec = 30 },
            new() { Seq = 3, Id = "S3", Name = "电批锁付螺丝",   RecipeId = recipeId, Hint = "电批锁付 4 颗螺丝，用完归位", BlockNextOnFail = true,  TimeoutSec = 45 },
            new() { Seq = 4, Id = "S4", Name = "连接电源线束",   RecipeId = recipeId, Hint = "接入电源线束，插到位",       BlockNextOnFail = true,  TimeoutSec = 30 },
            new() { Seq = 5, Id = "S5", Name = "安装清水箱",     RecipeId = recipeId, Hint = "装配清水箱组件",             BlockNextOnFail = false, TimeoutSec = 35 },
            new() { Seq = 6, Id = "S6", Name = "成品下料",       RecipeId = recipeId, Hint = "确认合格后取下成品",         BlockNextOnFail = false, TimeoutSec = 25 },
        },
    };

    /// <summary>
    /// 过程层规则的初始形状：<b>空的</b>。
    ///
    /// <para>作业位（P1、P2…）就是画面上的框，同样必须由现场自己建：
    /// 在画面上框好之后点「⇄ 同步为判定区域」，软件会自动把框变成作业位与工序
    /// （见 MainViewModel.SyncRoisToTargets）。预先塞 6 个坐标只会误导现场。</para>
    ///
    /// <para>工位名、K 值、时间窗这些"与位置无关"的参数保留默认值，现场直接在设置页调。</para>
    /// </summary>
    public static ProcessRuleConfig CreateDemoProcessConfig() => new()
    {
        StationName = "工位1 · 洗地机主机装配",
        CameraModel = "HC-500-10GM",
        Fps = 23.5,
        OrderEnforced = true,
        Kms = 300,
        TimeWindowMs = 8000,
        MinConfidence = 0.5,
        MaxUnreliableMs = 1000,
        ShowConfidence = true,
        // 作业位与工序：现场框完框点「同步为判定区域」时自动生成，这里保持空
        Targets = new List<ProcessTarget>(),
        Steps = new List<ProcessStepDefinition>(),
        Required = new List<string>(),
    };

    /// <summary>
    /// 保证数据目录里是一套"自洽可演示"的数据。返回一句人话说明做了什么，
    /// 调用方直接写日志即可。
    ///
    /// <para>只在"没有别人家的配方"时动手 —— 现场师傅自己建的配方绝不碰。</para>
    /// </summary>
    public static async Task<string> EnsureDemoDataAsync(
        IRecipeRepository recipes, string dataRoot, string cameraId = "CAM-001")
    {
        var all = await recipes.LoadAllAsync();

        var demo = all.FirstOrDefault(r => r.Id == DemoRecipeId);
        var legacy = all.FirstOrDefault(r => r.Name.Contains(LegacySampleName));
        var others = all.Where(r => r.Id != DemoRecipeId && r.Id != legacy?.Id).ToList();

        if (demo is null && others.Count > 0)
        {
            // 用户已经有自己的配方了，不动现场数据
            return $"已有 {others.Count} 个自定义配方，跳过示例数据生成";
        }

        string action = "未改动";

        if (demo is null)
        {
            demo = CreateDemoRecipe(cameraId);
            await recipes.SaveAsync(demo);
            action = "已生成示例配方（不含任何框）";

            if (legacy is not null)
            {
                // 老示例只有 2 个 ROI，和 6 步 SOP 对不上，留着只会让人困惑
                await recipes.DeleteAsync(legacy.Id);
                action += "，并替换掉旧的 2 ROI 示例配方";
            }
        }
        else if (ArePlaceholderBoxes(demo.Rois))
        {
            // 老版本会在示例配方里预置 6 个框。现场明确要求"框必须自己建"，
            // 所以这里把它们清掉 —— 但只按"坐标原封不动"来识别：
            // 现场自己挪过 / 改过的框坐标一定不同，绝不会被误删。
            int removed = demo.Rois.RemoveAll(IsPlaceholderBox);
            if (removed > 0)
            {
                demo.UpdatedAt = DateTime.Now;
                await recipes.SaveAsync(demo);
                action = $"已清掉示例配方里预置的 {removed} 个框（框请自己在画面上框）";
            }
        }

        // SOP：步骤必须绑到示例配方上，否则"第 N 步 = 第 N 个 ROI"无从对应
        var sopPath = Path.Combine(dataRoot, "sop.json");
        var sop = SopDefinitionStore.Load(sopPath);
        bool changed = false;
        foreach (var step in sop.Steps)
        {
            if (step.RecipeId != demo.Id)
            {
                step.RecipeId = demo.Id;
                changed = true;
            }
        }
        if (changed)
        {
            SopDefinitionStore.Save(sop, sopPath);
            action += (action == "未改动" ? "" : "，") + "并把 SOP 步骤重新绑定到示例配方";
        }

        // 过程层规则：不存在就写一份示例。
        // JSON 是唯一真相源；同时导出一份 YAML，方便现场用文本工具查看和交接。
        var ruleJsonPath = Path.Combine(dataRoot, "process-rule.json");
        if (!File.Exists(ruleJsonPath))
        {
            var rule = CreateDemoProcessConfig();
            ProcessConfigStore.SaveJson(rule, ruleJsonPath);

            try
            {
                File.WriteAllText(Path.Combine(dataRoot, "process-rule.yaml"),
                                  ProcessConfigStore.ToYaml(rule),
                                  new System.Text.UTF8Encoding(true));
            }
            catch
            {
                // YAML 只是给人看的副本，写不出来不影响运行
            }

            action += (action == "未改动" ? "" : "，") + "已生成过程层规则（不含作业位，等框完之后同步）";
        }
        else if (ArePlaceholderTargets(LoadRule(ruleJsonPath)))
        {
            // 同理：老版本预置的 6 个作业位也要清掉，作业位同样由现场框出来
            var rule = LoadRule(ruleJsonPath);
            rule.Targets.Clear();
            rule.Steps.Clear();
            rule.Required.Clear();
            ProcessConfigStore.SaveJson(rule, ruleJsonPath);
            action += (action == "未改动" ? "" : "，") + "已清掉预置的 6 个作业位（框完后点「同步为判定区域」）";
        }

        return action;
    }

    private static ProcessRuleConfig? LoadRule(string path)
    {
        try { return ProcessConfigStore.Load(path); }
        catch { return null; }
    }

    /// <summary>这组框是不是"老版本预置的那 6 个"（原封不动才算）。</summary>
    private static bool ArePlaceholderBoxes(IReadOnlyList<RoiRegion> rois)
        => rois.Any(IsPlaceholderBox);

    /// <summary>
    /// 这个框是不是老版本预置的（按坐标逐一对号）。
    ///
    /// <para>只看坐标、不看名字：名字现场一定会改，坐标才是身份。
    /// 现场自己框出来的框，坐标不可能和预置的 6 个分毫不差，所以不会被误删。</para>
    /// </summary>
    private static bool IsPlaceholderBox(RoiRegion roi)
        => PlaceholderBoxes.Any(expect =>
               Math.Abs(roi.X - expect.X) < 1e-6 &&
               Math.Abs(roi.Y - expect.Y) < 1e-6 &&
               Math.Abs(roi.Width - expect.W) < 1e-6 &&
               Math.Abs(roi.Height - expect.H) < 1e-6);

    /// <summary>作业位是不是"老版本预置的那 6 个"（原封不动才算）。</summary>
    private static bool ArePlaceholderTargets(ProcessRuleConfig? rule)
        => rule is not null && rule.Targets.Any(target =>
               PlaceholderBoxes.Any(expect =>
                   Math.Abs(target.X - expect.X) < 1e-6 &&
                   Math.Abs(target.Y - expect.Y) < 1e-6 &&
                   Math.Abs(target.Width - expect.W) < 1e-6 &&
                   Math.Abs(target.Height - expect.H) < 1e-6));
}
