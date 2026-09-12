using System.IO;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Infrastructure.Storage;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 步骤设置（界面上右键步骤小方框 → 改名称 / 改参数）。
///
/// <para><b>一步的"参数"到底散在哪三处（这也是要合并到一个对话框的原因）：</b></para>
/// <list type="number">
///   <item><b>步骤本身</b>：名称、作业指引、超时、不合格是否锁线 —— 存在 <c>data\sop.json</c></item>
///   <item><b>画面上的框</b>：第 Seq 个 ROI 的位置与大小 —— 存在配方里</item>
///   <item><b>检测参数</b>：算法阈值这类 —— 存在配方的算法参数里（配方级，改了对整个配方生效）</item>
/// </list>
/// <para>三处一起改、一起存，现场才不会出现"改了没生效 / 重启又变回去"。</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 按步骤准备一份可编辑的数据（供对话框显示）。
    ///
    /// <para>找不到配方或第 Seq 个框时照样能打开 —— 名称、指引、超时这些不依赖画框，
    /// 现场"先写名字、再去框位置"是常态。</para>
    /// </summary>
    public StepEditModel? CreateStepEdit(SopStep? step)
    {
        if (step is null) return null;

        var recipe = RecipeList.FirstOrDefault(r => r.Id == step.RecipeId) ?? ActiveRecipe;
        var roi = FindRoiForStep(step, recipe);

        var algo = recipe is null ? null : _algorithms.Find(recipe.AlgorithmKey);
        var fields = new List<ParameterFieldViewModel>();

        if (algo is not null && recipe is not null)
        {
            foreach (var def in algo.Parameters)
            {
                var existing = recipe.AlgorithmParameters
                    .FirstOrDefault(v => string.Equals(v.Key, def.Key, StringComparison.OrdinalIgnoreCase));
                fields.Add(new ParameterFieldViewModel(def, existing?.RawValue));
            }
        }

        return new StepEditModel(step, recipe, roi,
                                 algo?.DisplayName ?? "（未指定检测算法）", fields);
    }

    /// <summary>
    /// 第 N 步 ↔ 第 N 个框：两边顺序都按"从上到下 / 从左到右"，
    /// 所以一个框就是一道工序的作业位，不需要再配映射表。
    /// </summary>
    private static RoiRegion? FindRoiForStep(SopStep step, Recipe? recipe)
    {
        if (recipe is null) return null;

        var enabled = recipe.Rois.Where(r => r.Enabled).ToList();
        int index = step.Seq - 1;
        return index >= 0 && index < enabled.Count ? enabled[index] : null;
    }

    /// <summary>确定后写回。返回 false 表示校验没过（界面上会显示原因，对话框不关）。</summary>
    public bool ApplyStepEdit(StepEditModel? model)
    {
        if (model is null) return false;
        if (!model.Validate()) return false;

        var step = model.Step;
        var recipe = model.Recipe;

        // 改的是哪一步，就先切到它所属的配方 ——
        // 否则"改完保存了，画面上的框却没动"（因为同步用的是另一个配方）。
        if (recipe is not null && !ReferenceEquals(recipe, ActiveRecipe))
            ActiveRecipe = recipe;

        // ① 步骤本身
        step.Name = model.Name.Trim();
        step.Hint = model.Hint is null ? string.Empty : model.Hint.Trim();
        step.TimeoutSec = model.TimeoutSec;
        step.BlockNextOnFail = model.BlockNextOnFail;

        // ② 画面上的框：名字跟步骤名保持一致（一个框 = 一道工序），坐标按对话框里填的百分比写回
        var roi = model.Roi;
        if (roi is not null)
        {
            roi.Name = step.Name;
            roi.X = model.RoiLeftPercent / 100.0;
            roi.Y = model.RoiTopPercent / 100.0;
            roi.Width = model.RoiWidthPercent / 100.0;
            roi.Height = model.RoiHeightPercent / 100.0;
        }

        // ③ 检测参数（配方级）
        if (recipe is not null && model.Parameters.Count > 0)
        {
            foreach (var field in model.Parameters)
            {
                var existing = recipe.AlgorithmParameters
                    .FirstOrDefault(v => string.Equals(v.Key, field.Definition.Key, StringComparison.OrdinalIgnoreCase));

                if (existing is null) recipe.AlgorithmParameters.Add(field.ToParameterValue());
                else existing.RawValue = field.Value;
            }

            recipe.UpdatedAt = DateTime.Now;
        }

        // ④ 过程层规则里的同一处：目标名 / 坐标 / 工序名跟着改（有对应的才动，没有就不碰）
        SyncProcessConfigWithRois();

        // ⑤ 落盘：SOP 与配方都要存，否则重启就白改了
        SaveSopDefinition();
        if (recipe is not null) SaveRecipeInBackground(recipe);

        // ⑥ 界面刷新
        if (recipe is null || ReferenceEquals(recipe, ActiveRecipe))
        {
            RebuildRoiOverlays();
            RebuildParameterPanel();
        }

        RaiseMonitorProps();

        StatusMessage = $"第 {step.Seq} 步已保存：名称「{step.Name}」" +
                        (roi is null ? "（这一步还没有对应的框）" : "，画面上的框已同步");
        _log.Info($"步骤设置已保存：第 {step.Seq} 步「{step.Name}」" +
                  $"（超时 {step.TimeoutSec:F0}s，锁线={step.BlockNextOnFail}，" +
                  (roi is null ? "无对应框）" : $"框 {roi.BoxText}）"));

        return true;
    }

    /// <summary>
    /// 把画面上的框（名称 / 坐标）同步到过程层规则里同序的目标与工序。
    ///
    /// <para>过程层判定用的是 <c>P1/P2…</c> 那套坐标，如果只改配方不同步它，
    /// 就会出现"画面上的框挪了、判定还在按老位置判"—— 这是最隐蔽也最伤人的一类不一致。</para>
    /// </summary>
    private void SyncProcessConfigWithRois()
    {
        var config = _processConfig;
        var recipe = ActiveRecipe;
        if (config is null || recipe is null) return;

        var enabled = recipe.Rois.Where(r => r.Enabled).ToList();
        bool changed = false;

        for (int i = 0; i < enabled.Count; i++)
        {
            var roi = enabled[i];
            string targetId = "P" + (i + 1);

            var target = config.FindTarget(targetId);
            if (target is not null)
            {
                target.Name = roi.Name;
                target.X = roi.X;
                target.Y = roi.Y;
                target.Width = roi.Width;
                target.Height = roi.Height;
                changed = true;
            }

            var processStep = config.Steps.FirstOrDefault(s => s.Seq == i + 1);
            if (processStep is not null)
            {
                processStep.Name = roi.Name;
                changed = true;
            }
        }

        if (!changed) return;

        try
        {
            ProcessConfigStore.SaveJson(config, Path.Combine(_settings.Current.DataRoot, "process-rule.json"));
        }
        catch (Exception ex)
        {
            _log.Warn("保存过程层规则失败：" + ex.Message);
        }

        // 规则引擎如果还没建，等"启动过程监测"时自然会用上新配置；
        // 已经在跑的话它读的就是同一个 config 实例，坐标已经是最新的，不必重建（重建会把进度清零）。
        BuildProcessTargetStates();
        OnPropertyChanged(nameof(ProcessRuleSummary));
    }

    /// <summary>把运行中的步骤名称/指引/超时/锁线写回 SOP 定义并落盘。</summary>
    private void SaveSopDefinition()
    {
        var definition = Sop.Definition;
        if (definition is null) return;

        foreach (var step in Sop.Steps)
        {
            var def = definition.Steps.FirstOrDefault(d => d.Seq == step.Seq);
            if (def is null) continue;

            def.Name = step.Name;
            def.Hint = step.Hint;
            def.TimeoutSec = step.TimeoutSec;
            def.BlockNextOnFail = step.BlockNextOnFail;
        }

        definition.UpdatedAt = DateTime.Now;

        try
        {
            SopDefinitionStore.Save(definition, Path.Combine(_settings.Current.DataRoot, "sop.json"));
        }
        catch (Exception ex)
        {
            _log.Warn("保存 SOP 失败：" + ex.Message);
        }
    }

    /// <summary>配方写盘放后台线程（这里在 UI 线程上，同步等待会重演异步死锁）。</summary>
    private void SaveRecipeInBackground(Recipe recipe)
    {
        _ = Task.Run(async () =>
        {
            try { await _recipes.SaveAsync(recipe); }
            catch (Exception ex) { _log.Warn("保存配方失败：" + ex.Message); }
        });
    }
}
