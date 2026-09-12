using VisionForge.Core.Models;

namespace VisionForge.Core.Interfaces;

/// <summary>
/// 场景里的一块"料"。坐标是归一化值（0~1），和 <see cref="RoiRegion"/> 同一套坐标系，
/// 所以"把 ROI 填满"就是一行换算了。
/// </summary>
public sealed class SceneBlob
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>灰度值。默认 220，远高于默认二值化阈值 128。</summary>
    public byte Intensity { get; set; } = 220;
}

/// <summary>
/// 可编程场景相机 —— 模拟相机 / 回放相机实现它。
///
/// <para><b>为什么需要这个接口：</b></para>
/// 真正的检测逻辑是"看 ROI 里有没有东西"，可模拟相机如果只是随机撒亮块，
/// ROI 的占用状态就是随机的 —— 测不出"操作员这一步做没做对"，
/// 也复现不了"漏装 / 跳步 / 工具未归位"这些场景。
///
/// <para>有了它，测试逻辑就能<b>构造确定的画面</b>：
/// "第 3 个工位有料、第 4 个没有"，然后断言算法给出的判定与流程走向。
/// 这是整套 SOP 监测功能能离线验证的前提。</para>
///
/// <para>接口放在 Core 层、实现放在 Hardware 层：上层只依赖"能设场景"这件事，
/// 不关心是模拟相机、回放相机还是录像播放器。</para>
/// </summary>
public interface ISceneCamera : ICamera
{
    /// <summary>设置当前场景。传空集合表示"画面干净，什么都没有"。</summary>
    void SetScene(IReadOnlyList<SceneBlob> blobs);

    /// <summary>退出场景模式，回到相机自己的默认出图逻辑。</summary>
    void ClearScene();

    /// <summary>当前场景；未处于场景模式时为 null。</summary>
    IReadOnlyList<SceneBlob>? CurrentScene { get; }
}
