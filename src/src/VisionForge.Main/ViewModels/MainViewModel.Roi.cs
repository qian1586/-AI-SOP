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

    /// <summary>预览基准画布尺寸。ROI 归一化坐标换算到这个坐标系。</summary>
    public const double RoiCanvasWidth = 960;
    public const double RoiCanvasHeight = 720;

    /// <summary>ROI 叠加框颜色调色板，按 ROI 顺序循环分配。</summary>
    private static readonly Brush[] RoiPalette =
    {
        new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),   // 绿
        new SolidColorBrush(Color.FromRgb(0x4F, 0x86, 0xF7)),   // 蓝
        new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),   // 橙
        new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xD4)),   // 青
        new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7)),   // 紫
        new SolidColorBrush(Color.FromRgb(0xF9, 0xA8, 0xD4)),   // 粉
    };
    private double _draftStartX, _draftStartY;
    private double _draftX, _draftY, _draftW, _draftH;
    private bool _draftVisible;

    /// <summary>是否处于 ROI 编辑模式。开启后画面支持拖拽画框。</summary>
    public bool IsRoiEditMode
    {
        get => _isRoiEditMode;
        private set
        {
            if (SetProperty(ref _isRoiEditMode, value))
            {
                OnPropertyChanged(nameof(RoiEditHint));
                OnPropertyChanged(nameof(RoiEditButtonText));
                if (!value) CancelRoiDraft();
                (ClearRoisCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>编辑模式提示语，画在画面上方。</summary>
    public string RoiEditHint => IsRoiEditMode
        ? "标定模式：按住左键拖出一个框（只点一下也行）。右键点框 = 删除。"
          + "滚轮缩放；要挪画面请按住滚轮拖（左键这会儿是画框）"
        : string.Empty;

    // ---- 草稿框（正在拖拽的临时框）----
    public double RoiDraftX
    {
        get => _draftX;
        private set => SetProperty(ref _draftX, value);
    }
    public double RoiDraftY
    {
        get => _draftY;
        private set => SetProperty(ref _draftY, value);
    }
    public double RoiDraftW
    {
        get => _draftW;
        private set => SetProperty(ref _draftW, value);
    }
    public double RoiDraftH
    {
        get => _draftH;
        private set => SetProperty(ref _draftH, value);
    }
    public bool RoiDraftVisible
    {
        get => _draftVisible;
        private set => SetProperty(ref _draftVisible, value);
    }

    private void GoRoiEdit()
    {
        SelectedPageIndex = 0;
        ShowRoiBoxes = true;                     // 标定必须看得见框
        if (!IsRoiEditMode) ToggleRoiEdit();
        StatusMessage = "已切到首页并进入 ROI 标定模式：在画面上按住左键拖框";
    }

    /// <summary>
    /// 标定过程的日志埋点。
    ///
    /// <para>现场反馈过"拖拽画不出框"——这类问题最难查的地方在于：到底是鼠标事件没进来、
    /// 还是坐标不对、还是提交被拒。把按下/松开时的坐标与状态记进日志，一眼就能看出来。</para>
    /// </summary>
    public void NoteRoiAction(string text) => _log.Debug("[ROI] " + text);

    // ==================================================================
    // ROI 鼠标编辑（现场标定）
    // ==================================================================
    // 设计说明：
    //   View 层把鼠标事件转成"基准画布坐标（960×720）"再调这些方法；
    //   ViewModel 负责把坐标换算成归一化 ROI 写回配方，以及命中检测。
    //   这样坐标换算逻辑集中在一处，View 只做纯转发。

    private void ToggleRoiEdit()
    {
        IsRoiEditMode = !IsRoiEditMode;
        if (IsRoiEditMode) ShowRoiBoxes = true;   // 进标定就先把框显示出来，否则"看着像没画上"
        StatusMessage = IsRoiEditMode
            ? "ROI 标定模式已开启：左键拖拽画框，右键删除"
            : "ROI 标定模式已关闭，记得保存配方";
    }

    /// <summary>开始画框。x/y 为基准画布坐标（0~960 / 0~720）。</summary>
    public void BeginRoiDraft(double x, double y)
    {
        if (!IsRoiEditMode || ActiveRecipe is null) return;

        _draftStartX = Clamp(x, 0, RoiCanvasWidth);
        _draftStartY = Clamp(y, 0, RoiCanvasHeight);
        RoiDraftX = _draftStartX;
        RoiDraftY = _draftStartY;
        RoiDraftW = 0;
        RoiDraftH = 0;
        RoiDraftVisible = true;
    }

    /// <summary>更新草稿框（支持从任意方向拖拽）。</summary>
    public void UpdateRoiDraft(double x, double y)
    {
        if (!RoiDraftVisible) return;

        var cx = Clamp(x, 0, RoiCanvasWidth);
        var cy = Clamp(y, 0, RoiCanvasHeight);

        RoiDraftX = Math.Min(_draftStartX, cx);
        RoiDraftY = Math.Min(_draftStartY, cy);
        RoiDraftW = Math.Abs(cx - _draftStartX);
        RoiDraftH = Math.Abs(cy - _draftStartY);
    }

    /// <summary>
    /// 把草稿框改成"以某点为中心、固定大小"的框。
    ///
    /// <para>用途：现场只是点了一下（没有拖）。这时候给一个默认大小的框，
    /// 比什么都不给好 —— 点一下就有框，位置不满意右键删掉重点即可。</para>
    /// </summary>
    public void SetRoiDraftCentered(double centerX, double centerY, double size)
    {
        if (!RoiDraftVisible) return;

        double w = Math.Min(size, RoiCanvasWidth);
        double h = Math.Min(size, RoiCanvasHeight);

        double left = Clamp(centerX - w / 2.0, 0, RoiCanvasWidth - w);
        double top = Clamp(centerY - h / 2.0, 0, RoiCanvasHeight - h);

        _draftStartX = left;
        _draftStartY = top;
        RoiDraftX = left;
        RoiDraftY = top;
        RoiDraftW = w;
        RoiDraftH = h;
    }

    /// <summary>松手提交草稿框，写回配方 ROI。</summary>
    public void CommitRoiDraft()
    {
        if (!RoiDraftVisible) return;
        RoiDraftVisible = false;

        // 太小的框当误触忽略（门槛从 8 调到 4：拖动距离本来就不该要求很大）
        if (RoiDraftW < 4 || RoiDraftH < 4 || ActiveRecipe is null)
        {
            _log.Debug($"[ROI] 草稿框太小已忽略（{RoiDraftW:F0}×{RoiDraftH:F0}）");
            return;
        }

        var roi = new RoiRegion
        {
            Name = $"区域{RoiOverlays.Count + 1}",
            X = RoiDraftX / RoiCanvasWidth,
            Y = RoiDraftY / RoiCanvasHeight,
            Width = RoiDraftW / RoiCanvasWidth,
            Height = RoiDraftH / RoiCanvasHeight,
            Enabled = true,
        };

        ActiveRecipe.Rois.Add(roi);
        RebuildRoiOverlays();
        ShowRoiBoxes = true;                     // 刚画好的框必须立刻看得见
        SyncSopStepsWithRois();                  // 画一个框就补一道工序（第 N 个框 = 第 N 道工序）
        RefreshRoiTargetOptions();               // 新框要立刻出现在"区域学习"的下拉里
        SelectedRoiTarget = RoiTargets.LastOrDefault() ?? SelectedRoiTarget;   // 顺手选中刚画的这个
        StatusMessage = $"已添加 ROI「{roi.Name}」，点「保存配方」生效";
    }

    /// <summary>取消当前草稿框（退出编辑模式时调用）。</summary>
    private void CancelRoiDraft()
    {
        _draftStartX = _draftStartY = 0;
        RoiDraftX = RoiDraftY = RoiDraftW = RoiDraftH = 0;
        RoiDraftVisible = false;
    }

    /// <summary>命中检测：返回包含 (x,y) 的 ROI 索引（从上层往下找）。找不到返回 -1。</summary>
    private int HitTestRoi(double x, double y)
    {
        for (int i = RoiOverlays.Count - 1; i >= 0; i--)
        {
            var roi = RoiOverlays[i];
            if (x >= roi.X && x <= roi.X + roi.Width &&
                y >= roi.Y && y <= roi.Y + roi.Height)
                return i;
        }
        return -1;
    }

    /// <summary>右键删除命中的 ROI。</summary>
    public void DeleteRoiAt(double x, double y)
    {
        if (!IsRoiEditMode || ActiveRecipe is null) return;

        var idx = HitTestRoi(x, y);
        if (idx < 0) return;

        // RoiOverlays 只含 Enabled 的，所以要映射回 ActiveRecipe.Rois 的下标
        var enabledIndex = -1;
        for (int i = 0; i < ActiveRecipe.Rois.Count; i++)
        {
            if (!ActiveRecipe.Rois[i].Enabled) continue;
            enabledIndex++;
            if (enabledIndex == idx)
            {
                var removed = ActiveRecipe.Rois[i];
                ActiveRecipe.Rois.RemoveAt(i);
                RebuildRoiOverlays();
                SyncSopStepsWithRois();          // 删一个框就少一道工序（"2/9 步"这种不一致就是这么来的）
                RefreshRoiTargetOptions();       // 删了框，"区域学习"的下拉也要跟着少一个
                StatusMessage = $"已删除 ROI「{removed.Name}」，点「保存配方」生效";
                return;
            }
        }
    }

    /// <summary>清空当前配方所有 ROI。</summary>
    private void ClearRois()
    {
        if (ActiveRecipe is null) return;
        ActiveRecipe.Rois.Clear();
        RebuildRoiOverlays();
        SyncSopStepsWithRois();              // 框全清掉 -> 工序也清掉，界面不会再显示"x/9 步"
        RefreshRoiTargetOptions();
        StatusMessage = "已清空全部 ROI，点「保存配方」生效";
    }

    private static double Clamp(double v, double min, double max) =>
        v < min ? min : (v > max ? max : v);
}
