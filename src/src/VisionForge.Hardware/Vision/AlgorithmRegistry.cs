using VisionForge.Core.Interfaces;

namespace VisionForge.Hardware.Vision;

/// <summary>
/// 算法注册表。
///
/// 主界面从头到尾只跟这个注册表打交道：
///   · 新建配方时，从 <see cref="All"/> 里选一个算法
///   · 选中后，界面读它的 <c>Parameters</c> 动态生成参数输入框
///   · 执行检测时，凭配方的 AlgorithmKey 找到实例去 Run
///
/// 所以"新增一种零件只需新增一套方案，不改主程序"才成立。
/// </summary>
public sealed class AlgorithmRegistry : IAlgorithmRegistry
{
    private readonly Dictionary<string, IInspectionAlgorithm> _map =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IInspectionAlgorithm> All => _map.Values.ToList();

    public IInspectionAlgorithm? Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _map.TryGetValue(key, out var a) ? a : null;
    }

    public void Register(IInspectionAlgorithm algorithm)
    {
        if (algorithm is null) throw new ArgumentNullException(nameof(algorithm));
        if (string.IsNullOrWhiteSpace(algorithm.Key))
            throw new ArgumentException("算法的 Key 不能为空", nameof(algorithm));

        _map[algorithm.Key] = algorithm;
    }

    /// <summary>
    /// 创建内置算法齐全的注册表。
    /// 之后新增算法，只要在这里加一行 Register，界面立刻就能选到它。
    /// </summary>
    public static AlgorithmRegistry CreateDefault()
    {
        var registry = new AlgorithmRegistry();
        registry.Register(new ThresholdBlobAlgorithm());
        registry.Register(new DimensionMeasureAlgorithm());
        registry.Register(new HalconInspectionAlgorithm());   // Halcon 作为一个插件接入
        registry.Register(new SopSequenceAlgorithm());        // 过程合规检测（漏步/跳步/逆序）
        return registry;
    }
}
