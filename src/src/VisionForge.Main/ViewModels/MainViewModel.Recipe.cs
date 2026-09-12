using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionForge.Common.Events;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Hardware.Camera;
using VisionForge.Hardware.Simulation;
using VisionForge.Hardware.Vision;
using VisionForge.Infrastructure.Config;
using VisionForge.Infrastructure.Storage;
using VisionForge.Main.Imaging;

namespace VisionForge.Main.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IAlgorithmRegistry _algorithms;
    private readonly IRecipeRepository _recipes;
    private Recipe? _activeRecipe;

    /// <summary>当前配方。切换时会重建动态参数面板。</summary>
    public Recipe? ActiveRecipe
    {
        get => _activeRecipe;
        set
        {
            if (!SetProperty(ref _activeRecipe, value)) return;
            RebuildParameterPanel();
            RebuildRoiOverlays();

            // 换配方 = 换产品：它自己的现场底图要跟着回来（没配就是相机画面）。
            // 少了这一步，现场会觉得"配方里明明存了照片，打开却是黑的"。
            RestoreSceneImageForRecipe();

            // 换配方 = 换一套框：区域学习的下拉与识别器都要跟着换
            RefreshRoiTargetOptions();

            // 换配方 = 换产品：模拟器进度必须归零，
            // 否则画面里还留着上一种产品的料，演示时会凭空报跳步
            if (_simulator is not null && value is not null) _simulator.Reset(value);
            RaiseMonitorProps();
            RefreshCommands();
        }
    }

    // ==================================================================
    // 配方管理页
    // ==================================================================
    /// <summary>配方页切换算法后重建参数面板：参数由算法自己声明，换算法就换一套参数。</summary>
    public void OnAlgorithmChanged()
    {
        RebuildParameterPanel();
        StatusMessage = "已按新算法重建参数面板";
    }

    private async Task RefreshRecipesAsync()
    {
        var list = await _recipes.LoadAllAsync();
        RecipeList.Clear();
        foreach (var recipe in list) RecipeList.Add(recipe);

        ActiveRecipe ??= RecipeList.FirstOrDefault();
        (DeleteRecipeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task DeleteRecipeAsync()
    {
        var recipe = ActiveRecipe;
        if (recipe is null) return;

        // 仓储实现是"移到 _deleted 目录"而不是真删 —— 配方误删的代价太高
        await _recipes.DeleteAsync(recipe.Id);
        ActiveRecipe = null;
        await RefreshRecipesAsync();

        StatusMessage = $"配方「{recipe.Name}」已移入 _deleted（可找回）";
        _log.Warn($"配方已删除（移入 _deleted）：{recipe.Name}");
    }

    // ==================================================================
    // 动态参数面板
    // ==================================================================
    /// <summary>
    /// 按当前配方 + 算法声明，重建参数面板。
    ///
    /// 注意这里没有任何 switch(参数名) 之类的硬编码 ——
    /// 面板长什么样完全由算法自己声明的 Parameters 决定。
    /// 新增一个算法，界面自动就能显示它的参数，主程序不用改。
    /// </summary>
    private void RebuildParameterPanel()
    {
        ParameterFields.Clear();
        if (ActiveRecipe is null) return;

        var algo = _algorithms.Find(ActiveRecipe.AlgorithmKey);
        if (algo is null) return;

        foreach (var def in algo.Parameters)
        {
            var existing = ActiveRecipe.AlgorithmParameters
                .FirstOrDefault(v => string.Equals(v.Key, def.Key, StringComparison.OrdinalIgnoreCase));

            ParameterFields.Add(new ParameterFieldViewModel(def, existing?.RawValue));
        }

        // 算法换了之后，配方里可能残留旧算法的参数，这里顺手清理掉
        var validKeys = algo.Parameters.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ActiveRecipe.AlgorithmParameters.RemoveAll(v => !validKeys.Contains(v.Key));
    }

    private void SyncParametersToRecipe(Recipe recipe)
    {
        recipe.AlgorithmParameters = ParameterFields.Select(f => f.ToParameterValue()).ToList();
    }

    /// <summary>
    /// 按当前配方重建左侧画面的 ROI 叠加框。
    ///
    /// <para>ROI 顺序 = 调色板颜色顺序。对 <c>sop-sequence</c> 插件尤其有意义：
    /// 它的 ROI 顺序就是 SOP 工序顺序，所以"第一个框=第 1 步"一目了然。</para>
    /// </summary>
    private void RebuildRoiOverlays()
    {
        RoiOverlays.Clear();
        if (ActiveRecipe is null) return;

        for (int i = 0; i < ActiveRecipe.Rois.Count; i++)
        {
            var roi = ActiveRecipe.Rois[i];
            if (!roi.Enabled) continue;

            var brush = RoiPalette[i % RoiPalette.Length];
            RoiOverlays.Add(new RoiOverlayViewModel(roi, i + 1, RoiCanvasWidth, RoiCanvasHeight, brush));
        }

        (ClearRoisCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ==================================================================
    // 配方增删改
    // ==================================================================
    private void NewRecipe()
    {
        var firstAlgo = AvailableAlgorithms.FirstOrDefault();
        var recipe = new Recipe
        {
            Name = "新配方-" + DateTime.Now.ToString("MMddHHmm"),
            ProductModel = "未指定",
            AlgorithmKey = firstAlgo?.Key ?? string.Empty,
            CameraId = Cameras.FirstOrDefault()?.Id ?? string.Empty,
            CameraBindingKey = Cameras.FirstOrDefault()?.DisplayName ?? string.Empty,
        };

        RecipeList.Insert(0, recipe);
        ActiveRecipe = recipe;
        StatusMessage = "已新建配方，填写参数后点「保存配方」";
    }

    private async Task SaveRecipeAsync()
    {
        if (ActiveRecipe is null) return;

        SyncParametersToRecipe(ActiveRecipe);

        var errors = ActiveRecipe.Validate();
        if (errors.Count > 0)
        {
            StatusMessage = "配方校验未通过：" + string.Join("；", errors);
            return;
        }

        await _recipes.SaveAsync(ActiveRecipe);
        _settings.Current.LastRecipeId = ActiveRecipe.Id;
        _settings.Save();

        if (!RecipeList.Contains(ActiveRecipe))
            RecipeList.Insert(0, ActiveRecipe);

        StatusMessage = $"配方已保存：{ActiveRecipe.Name}";
        _log.Info($"保存配方 {ActiveRecipe.Name}（{ActiveRecipe.Id}）");
    }
}
