using System.Collections.ObjectModel;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Models;
using VisionForge.Core.Services;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 「步骤设置」对话框的数据：把一步工序的名字、指引、对应画面区域、判定参数、
/// 检测参数摊在一张表上，改完点确定写回。
///
/// <para>为什么要有这个对话框：现场问得最多的是"这一步叫什么、框在哪、判到什么程度算过"。
/// 以前这三件事分散在 SOP 文件、配方 ROI、算法参数三个地方，
/// 操作员不知道去哪改，就干脆不改了 —— 界面必须能就地改。</para>
/// </summary>
public sealed class StepEditModel : ObservableObject
{
    private string _name = string.Empty;
    private string _hint = string.Empty;
    private double _timeoutSec;
    private bool _blockNextOnFail = true;

    private double _roiLeftPercent;
    private double _roiTopPercent;
    private double _roiWidthPercent;
    private double _roiHeightPercent;

    private string _errorText = string.Empty;

    public StepEditModel(SopStep step, Recipe? recipe, RoiRegion? roi,
                         string algorithmName, IEnumerable<ParameterFieldViewModel> parameters)
    {
        Step = step;
        Recipe = recipe;
        Roi = roi;

        _name = step.Name;
        _hint = step.Hint;
        _timeoutSec = step.TimeoutSec;
        _blockNextOnFail = step.BlockNextOnFail;

        if (roi is not null)
        {
            _roiLeftPercent = roi.X * 100.0;
            _roiTopPercent = roi.Y * 100.0;
            _roiWidthPercent = roi.Width * 100.0;
            _roiHeightPercent = roi.Height * 100.0;
        }

        AlgorithmName = algorithmName;
        Parameters = new ObservableCollection<ParameterFieldViewModel>(parameters);
    }

    /// <summary>被编辑的那一步（确定后直接改它）。</summary>
    public SopStep Step { get; }

    /// <summary>这一步所属的检测配方。</summary>
    public Recipe? Recipe { get; }

    /// <summary>这一步对应的画面区域（第 Seq 个框）。没有就是 null。</summary>
    public RoiRegion? Roi { get; }

    public int Seq => Step.Seq;

    public string Title => $"第 {Seq} 步 · 设置";

    public string StepIdText => string.IsNullOrWhiteSpace(Step.Id) ? "—" : Step.Id;

    /// <summary>步骤名称。确定后会同时改画面上这个框的名字（两边保持一致）。</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>作业指引：现场员工看到的那句话。</summary>
    public string Hint
    {
        get => _hint;
        set => SetProperty(ref _hint, value);
    }

    /// <summary>允许耗时（秒）。0 = 不检查超时。</summary>
    public double TimeoutSec
    {
        get => _timeoutSec;
        set => SetProperty(ref _timeoutSec, value);
    }

    /// <summary>这一步不合格时是否锁线（关键工序勾上，辅助工序不勾）。</summary>
    public bool BlockNextOnFail
    {
        get => _blockNextOnFail;
        set => SetProperty(ref _blockNextOnFail, value);
    }

    // ---- 对应画面区域（按画面百分比填，比归一化小数好理解）----
    public bool HasRoi => Roi is not null;

    public string RoiMissingHint =>
        $"配方里还没有第 {Seq} 个框：到首页点「🎯 标定 ROI」，在画面上框出这一步的抓取位置";

    /// <summary>
    /// 画面上的框名（只读展示）。
    /// 名称统一由上面的"步骤名称"决定 —— 两边各有一个名字框，现场一定会改乱一个。
    /// </summary>
    public string RoiLabel => Roi is null
        ? "—"
        : $"第 {Seq} 个框：{Roi.Name}";

    /// <summary>框左边位置（画面宽的百分之几）。</summary>
    public double RoiLeftPercent
    {
        get => _roiLeftPercent;
        set => SetProperty(ref _roiLeftPercent, value);
    }

    /// <summary>框上边位置（画面高的百分之几）。</summary>
    public double RoiTopPercent
    {
        get => _roiTopPercent;
        set => SetProperty(ref _roiTopPercent, value);
    }

    /// <summary>框宽（画面宽的百分之几）。</summary>
    public double RoiWidthPercent
    {
        get => _roiWidthPercent;
        set => SetProperty(ref _roiWidthPercent, value);
    }

    /// <summary>框高（画面高的百分之几）。</summary>
    public double RoiHeightPercent
    {
        get => _roiHeightPercent;
        set => SetProperty(ref _roiHeightPercent, value);
    }

    // ---- 检测参数（来自配方的算法声明，改了只影响这个配方）----
    public string AlgorithmName { get; }

    public ObservableCollection<ParameterFieldViewModel> Parameters { get; }

    public bool HasParameters => Parameters.Count > 0;

    /// <summary>校验失败的原因，显示在对话框底部。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    /// <summary>
    /// 用户在对话框里点了「到首页用鼠标重新框这一步」。
    /// 确定之后主窗口会切到首页并进入标定模式，而不是只把数值存下来。
    /// </summary>
    public bool RequestReframe { get; set; }

    /// <summary>
    /// 确定前的校验。
    ///
    /// <para>校验放在这里而不是界面按钮里：将来从别的地方（导入、脚本）改步骤时，
    /// 同一套规则不用再写一遍。</para>
    /// </summary>
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorText = "步骤名称不能为空";
            return false;
        }

        if (TimeoutSec < 0)
        {
            ErrorText = "超时秒数不能是负数（0 = 不检查超时）";
            return false;
        }

        foreach (var p in Parameters)
        {
            if (!p.HasError) continue;
            ErrorText = $"参数「{p.DisplayName}」不合法：{p.Error}";
            return false;
        }

        if (Roi is not null)
        {
            if (RoiWidthPercent <= 0 || RoiHeightPercent <= 0)
            {
                ErrorText = "区域宽和高必须大于 0";
                return false;
            }

            if (RoiLeftPercent < 0 || RoiTopPercent < 0 ||
                RoiLeftPercent + RoiWidthPercent > 100.5 ||
                RoiTopPercent + RoiHeightPercent > 100.5)
            {
                ErrorText = "区域超出画面范围（左边 + 宽 不能超过 100%）";
                return false;
            }
        }

        ErrorText = string.Empty;
        return true;
    }
}
