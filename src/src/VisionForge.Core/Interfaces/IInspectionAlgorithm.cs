using VisionForge.Core.Models;

namespace VisionForge.Core.Interfaces;

/// <summary>
/// 检测算法接口 —— "插件化检测方案"的落点。
///
/// 文章原话："不同的零件检测需求通过'检测方案'抽象，
/// 新增一种零件只需要新增一套方案，不需要改主程序。"
///
/// 关键设计：算法<b>自己声明</b>它需要哪些参数（<see cref="Parameters"/>）。
/// 界面据此动态生成输入控件，配方据此存储取值。
/// 主程序从头到尾不需要知道任何具体算法的存在。
///
/// 新增一个检测方案 = 写一个实现类 + 注册到 AlgorithmRegistry，收工。
/// </summary>
public interface IInspectionAlgorithm
{
    /// <summary>唯一标识。配方里的 AlgorithmKey 引用它。改这个值会导致旧配方失效。</summary>
    string Key { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>
    /// 本算法需要哪些参数。动态参数面板的数据源。
    /// 返回空列表表示该算法无可调参数。
    /// </summary>
    IReadOnlyList<ParameterDefinition> Parameters { get; }

    /// <summary>
    /// 执行检测。
    ///
    /// 约定：
    ///   - 算法内部异常不要直接抛，返回 <see cref="InspectionResult.Error"/> 更友好
    ///   - 必须填 <see cref="InspectionResult.JudgementReason"/>，且写成现场人能看懂的话
    ///   - 不允许修改传入的 frame 和 recipe
    /// </summary>
    InspectionResult Run(CameraFrame frame, Recipe recipe);
}

/// <summary>
/// 算法注册表。凭 Key 找到算法实例。
///
/// 有了它，加载配方时才能知道"这个 AlgorithmKey 该由哪个算法跑"，
/// 以及"界面上该显示哪些参数输入框"。
/// </summary>
public interface IAlgorithmRegistry
{
    IReadOnlyList<IInspectionAlgorithm> All { get; }

    IInspectionAlgorithm? Find(string key);

    void Register(IInspectionAlgorithm algorithm);
}
