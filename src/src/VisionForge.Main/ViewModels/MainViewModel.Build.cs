using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Infrastructure.Storage;
using VisionForge.Main.Imaging;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 建图：把"抓取位置"定义出来，并让它直接参与判定。
///
/// <para>两条路径，现场按条件选：</para>
/// <list type="number">
///   <item><b>有相机</b>：连上相机 → 在实时画面上框出料盒/抓取位置</item>
///   <item><b>没相机 / 想先离线标</b>：导入一张现场照片 → 在照片上框出抓取位置</item>
/// </list>
///
/// <para>框完之后点「把框同步为判定区域」，这些框就变成过程层判定用的作业位 ——
/// 之后"人手进入对应区域"就会被判为这一步做到了，不需要再配别的东西。</para>
/// </summary>
public sealed partial class MainViewModel
{
    private string _sceneImageHint = "底图：相机实时画面";

    /// <summary>当前底图来源说明（相机画面 / 导入的现场照片）。</summary>
    public string SceneImageHint
    {
        get => _sceneImageHint;
        private set => SetProperty(ref _sceneImageHint, value);
    }

    /// <summary>导入现场照片当底图（没相机也能标位置）。</summary>
    public ICommand ImportSceneImageCommand { get; }

    /// <summary>把配方里的框同步成过程层判定区域（人手进入即判定做到）。</summary>
    public ICommand SyncRoisToTargetsCommand { get; }

    /// <summary>点步骤小框 → 选中该步并跳到它的检测参数。</summary>
    public ICommand SelectStepCommand { get; }

    /// <summary>画面上 ROI 框的显隐开关。</summary>
    public ICommand ToggleShowRoiBoxesCommand { get; }

    /// <summary>从导入的现场照片切回相机实时画面。</summary>
    public ICommand UseCameraPreviewCommand { get; }

    /// <summary>显隐按钮上的文字（一眼看出当前是显示还是隐藏）。</summary>
    public string ShowRoiBoxesButtonText => ShowRoiBoxes ? "▣ 框：显示" : "▢ 框：隐藏";

    private void ToggleShowRoiBoxes()
    {
        ShowRoiBoxes = !ShowRoiBoxes;
        StatusMessage = ShowRoiBoxes ? "画面上的 ROI 框已显示" : "画面上的 ROI 框已隐藏（判定照常跑）";
    }

    /// <summary>
    /// 切回相机实时画面。
    ///
    /// <para>导入的现场照片只是"标定底图"，看完必须能回到相机 ——
    /// 否则预览会一直定格在照片上，现场会以为相机坏了。</para>
    /// </summary>
    private void UseCameraPreview()
    {
        _useStaticSceneImage = false;
        SceneImageHint = _camera?.IsConnected == true
            ? "底图：相机实时画面"
            : "底图：未连接相机（可导入现场照片）";

        if (_camera?.IsConnected != true)
            PreviewImage = FrameConverter.CreatePlaceholder(text: "相机未连接");

        StatusMessage = "已切回相机实时画面";
    }

    private void ImportSceneImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择一张现场照片（在上面框出抓取位置）",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "已取消导入";
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 读完就释放文件，不占句柄
            bitmap.UriSource = new Uri(dialog.FileName);
            bitmap.EndInit();
            bitmap.Freeze();

            PreviewImage = bitmap;
            _useStaticSceneImage = true;          // 冻住相机渲染，照片才不会被下一帧顶掉
            ResetViewport();                      // 换了底图，视图回到 100% 居中（否则可能一进来是"看不清的角落"）
            SceneImageHint = "底图：现场照片 " + Path.GetFileName(dialog.FileName);

            // 记住这张底图，下次打开配方自动带出来
            var recipeToSave = ActiveRecipe;
            if (recipeToSave is not null)
            {
                recipeToSave.SceneImagePath = dialog.FileName;
                _ = Task.Run(async () =>
                {
                    try { await _recipes.SaveAsync(recipeToSave); }
                    catch (Exception ex) { _log.Warn("保存底图路径失败：" + ex.Message); }
                });
            }

            StatusMessage = $"已导入现场照片：{Path.GetFileName(dialog.FileName)} —— " +
                            "点「🎯 标定 ROI」在上面框出抓取位置";
            _log.Info("导入现场底图：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = "导入图片失败：" + ex.Message;
            _log.Error("导入现场底图失败", ex);
        }
    }

    /// <summary>
    /// 把配方里画好的框，同步成过程层的判定区域。
    ///
    /// <para>同步后的对应关系：第 N 个框 = 第 N 道工序的作业位（P + N）。
    /// 判定逻辑用"手部中心进入该区域并停留够 K 毫秒"——也就是现场说的
    /// "人手去对应区域取"，不需要再额外配置。</para>
    /// </summary>
    private void SyncRoisToTargets()
    {
        var recipe = ActiveRecipe;
        if (recipe is null)
        {
            StatusMessage = "还没有选中配方";
            return;
        }

        var rois = recipe.Rois.Where(r => r.Enabled).ToList();
        if (rois.Count == 0)
        {
            StatusMessage = "配方里还没有框：先点「🎯 标定 ROI」在画面上框出抓取位置";
            return;
        }

        var config = _processConfig ?? DemoDataFactory.CreateDemoProcessConfig();

        // 用配方里的框重建作业位与工序（一个框 = 一道工序）
        config.Targets = rois.Select((roi, index) => new ProcessTarget
        {
            Id = "P" + (index + 1),
            Name = string.IsNullOrWhiteSpace(roi.Name) ? $"区域{index + 1}" : roi.Name,
            X = roi.X,
            Y = roi.Y,
            Width = roi.Width,
            Height = roi.Height,
        }).ToList();

        config.Steps = rois.Select((roi, index) => new ProcessStepDefinition
        {
            Seq = index + 1,
            Name = string.IsNullOrWhiteSpace(roi.Name) ? $"第 {index + 1} 步" : roi.Name,
            Targets = new List<string> { "P" + (index + 1) },
        }).ToList();

        config.Required = config.Targets.Select(t => t.Id).ToList();
        _processConfig = config;

        var errors = config.Validate();
        if (errors.Count > 0)
        {
            StatusMessage = "同步失败：" + string.Join("；", errors);
            _log.Warn("同步判定区域失败：" + string.Join("；", errors));
            return;
        }

        try
        {
            ProcessConfigStore.SaveJson(config, Path.Combine(_settings.Current.DataRoot, "process-rule.json"));
        }
        catch (Exception ex)
        {
            _log.Warn("保存判定区域失败：" + ex.Message);
        }

        // 顺手把配方也存了：框是刚在画面上画出来的，没保存就断电会白标一遍
        recipe.UpdatedAt = DateTime.Now;
        _ = Task.Run(async () =>
        {
            try { await _recipes.SaveAsync(recipe); }
            catch (Exception ex) { _log.Warn("保存配方失败：" + ex.Message); }
        });

        BuildProcessTargetStates();
        _processEngine = new HandActionRuleEngine(config);
        SyncSopStepsWithRois();      // 工序跟着框走（第 N 个框 = 第 N 道工序）
        RefreshRoiTargetOptions();   // 框变了，区域学习的下拉与识别器一起刷新
        OnPropertyChanged(nameof(ProcessRuleSummary));
        OnPropertyChanged(nameof(ProcessConfig));

        StatusMessage = $"已把 {rois.Count} 个框同步为判定区域（配方 + 规则都已保存）：" +
                        "人手进入对应区域并停留够时间，就算这一步做到了";
        _log.Info($"判定区域已同步：{rois.Count} 个（{string.Join("、", config.Targets.Select(t => t.Id + " " + t.Name))}）");
    }

    /// <summary>配方切换时恢复它自己的底图（有就显示，没有就用相机画面）。</summary>
    private void RestoreSceneImageForRecipe()
    {
        var path = ActiveRecipe?.SceneImagePath;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();

                PreviewImage = bitmap;
                _useStaticSceneImage = true;
                ResetViewport();
                SceneImageHint = "底图：现场照片 " + Path.GetFileName(path);
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("底图载入失败：" + ex.Message);
            }
        }

        // 这个配方没有底图：回到相机画面
        _useStaticSceneImage = false;
        SceneImageHint = _camera?.IsConnected == true ? "底图：相机实时画面" : "底图：未连接相机（可导入现场照片）";
    }

    /// <summary>
    /// 点一下步骤小框：选中该步并直接跳到它的检测参数（配方管理页）。
    ///
    /// <para>现场的操作逻辑是"哪一步不对，就点那一步去调参数"。检测参数在配方里，
    /// 所以这里做的是"带路"，而不是把几十个参数塞进首页。</para>
    /// </summary>
    private void SelectStepForEdit(SopStep? step)
    {
        if (step is null) return;

        var recipe = RecipeList.FirstOrDefault(r => r.Id == step.RecipeId) ?? ActiveRecipe;
        if (recipe is not null && !ReferenceEquals(recipe, ActiveRecipe))
            ActiveRecipe = recipe;

        SelectedPageIndex = 1;   // 配方管理页

        // 第 N 步 ↔ 第 N 个框：两边都是"从上到下 / 从左到右"的顺序，
        // 所以点第 3 步就是调第 3 个框的算法与阈值。
        int roiCount = recipe?.Rois.Count ?? 0;
        string roiNote = roiCount >= step.Seq
            ? $"对应第 {step.Seq} 个 ROI 框（下面「ROI 区域」里那一条）"
            : $"配方里目前只有 {roiCount} 个框，还没有第 {step.Seq} 个 —— 到首页点「🎯 标定 ROI」补上";

        StatusMessage = $"第 {step.Seq} 步「{step.Name}」→ 配方「{recipe?.Name}」：{roiNote}，" +
                        "算法、阈值、区域都在这一页调";
        _log.Info($"点步骤 {step.Seq}「{step.Name}」→ 打开配方参数页（配方 {recipe?.Name}）");
    }
}
