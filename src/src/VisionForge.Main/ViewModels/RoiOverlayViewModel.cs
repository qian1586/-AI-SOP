using System.Windows;
using System.Windows.Media;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Models;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 左侧画面上的 ROI 叠加框。
///
/// <para><b>为什么这里用像素坐标、Core 里用归一化坐标：</b></para>
/// Core 的 <see cref="RoiRegion"/> 存 0~1 的归一化坐标，好处是换相机分辨率不用改配方。
/// 但 WPF 的 Canvas 要的是绝对像素，所以这里在"预览基准画布"（960×720）上做一次换算，
/// 换算逻辑只存在于 View 层，Core 模型保持分辨率无关。
/// </summary>
public sealed class RoiOverlayViewModel : ObservableObject
{
    private readonly Brush _paletteStroke;
    private string _stateKey = string.Empty;

    /// <summary>ROI 名称，画在框左上角标签里（含序号前缀）。</summary>
    public string Label { get; }

    /// <summary>画布上的像素坐标（已从归一化换算）。</summary>
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    /// <summary>
    /// 判定状态：空 = 用调色板配色；ok / active / ng = 跟随判定结果变色。
    ///
    /// <para>借鉴基恩士的"结果变色"：框的颜色要随判定结果变，
    /// 操作员不用去读文字就知道哪一块出问题。没跑检测时保留原来的调色板配色，
    /// 相邻框仍然能一眼分开。</para>
    /// </summary>
    public string StateKey
    {
        get => _stateKey;
        set
        {
            if (!SetProperty(ref _stateKey, value)) return;
            OnPropertyChanged(nameof(Stroke));
            OnPropertyChanged(nameof(LabelBackground));
        }
    }

    /// <summary>描边颜色：有判定状态时按状态上色，否则用调色板配色。</summary>
    public Brush Stroke => ResolveStroke();

    /// <summary>左上角名称标签的底色，跟描边同色。</summary>
    public Brush LabelBackground => ResolveStroke();

    public RoiOverlayViewModel(RoiRegion roi, int seq, double canvasWidth, double canvasHeight, Brush stroke)
    {
        Label = string.IsNullOrWhiteSpace(roi.Name) ? $"ROI {seq}" : $"{seq}. {roi.Name}";
        X = roi.X * canvasWidth;
        Y = roi.Y * canvasHeight;
        Width = roi.Width * canvasWidth;
        Height = roi.Height * canvasHeight;
        _paletteStroke = stroke;
    }

    private Brush ResolveStroke()
    {
        if (string.IsNullOrEmpty(_stateKey)) return _paletteStroke;

        string resourceKey = _stateKey switch
        {
            "ok" => "StateOk",
            "active" => "StateActive",
            "ng" => "StateNg",
            "pending" => "StatePending",
            _ => string.Empty,
        };

        if (resourceKey.Length == 0) return _paletteStroke;

        // 资源查不到就退回调色板 —— 一个颜色取不到不该让界面报错
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? _paletteStroke;
    }
}
