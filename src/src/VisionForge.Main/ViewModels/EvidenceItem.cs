using System.Windows.Media.Imaging;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 一张"保持图"（NG 抓帧）。
///
/// <para>借鉴基恩士的 NG 保持图：最多留几张，出问题时能直接看当时画面。
/// 我们防的是过程手法错误，异常条目如果只有一行规则码，复盘成本极高 ——
/// 必须能点开看到"手当时在不在位、哪个目标没覆盖"。</para>
/// </summary>
public sealed class EvidenceItem
{
    /// <summary>磁盘路径（data\ng-images 下）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>说明，如"STEP_OUT_OF_ORDER · 装产品壳"。</summary>
    public string Caption { get; init; } = string.Empty;

    /// <summary>已解码好的位图（用 OnLoad 载入，不占着文件句柄）。</summary>
    public BitmapSource? Image { get; init; }

    /// <summary>时间戳，界面上显示。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public string TimeText => Timestamp.ToString("HH:mm:ss");
}
