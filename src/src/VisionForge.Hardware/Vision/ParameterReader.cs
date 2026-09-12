using System.Globalization;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Vision;

/// <summary>
/// 参数读取助手 —— 算法从配方里取值时统一走它。
///
/// 存在的意义：<b>让算法对"参数缺失"免疫</b>。
///
/// 真实场景里配方和算法会各自演进：
///   · 算法 v1.1 新增了一个参数，但现场还存着 v1.0 时代的配方
///   · 现场工程师手工改了配方文件，删掉了一行
///   · 有人改了参数的 Key 拼写
/// 如果算法直接索引取值，上面任何一种都会抛异常，产线直接停。
/// 统一走 helper + 内置兜底值，最坏情况也只是"用默认参数跑"，不会停线。
/// </summary>
public sealed class ParameterReader
{
    private readonly Dictionary<string, ParameterValue> _values;
    private readonly Dictionary<string, ParameterDefinition> _definitions;

    public ParameterReader(Recipe recipe, IReadOnlyList<ParameterDefinition> definitions)
    {
        _definitions = definitions.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        _values = recipe.AlgorithmParameters
            .GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>该参数在本次配方里是否存在（不代表值合法）。</summary>
    public bool Has(string key) => _values.ContainsKey(key);

    public double GetDouble(string key, double fallback = 0)
    {
        if (_values.TryGetValue(key, out var v) && TryParseNumber(v.RawValue, out var d))
            return d;
        return DefinitionDefault(key, fallback);
    }

    /// <summary>
    /// 数值解析要同时接受 "0.03" 和 "0,03" 两种写法。
    ///
    /// <para>为什么较真这一条：配方里存的是字面量，而 double.TryParse 默认按<b>当前区域设置</b>解析。
    /// 在把逗号当小数点的区域（德语、法语等），"0.03" 会解析失败 → 静默退回默认值 →
    /// 阈值悄悄变成 0.03 的默认值，现场表现为"参数改了没效果"，极难查。
    /// 先按不变文化解析，再按当前文化兜底，两边都不会错。</para>
    /// </summary>
    private static bool TryParseNumber(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    public int GetInt(string key, int fallback = 0) => (int)GetDouble(key, fallback);

    public bool GetBool(string key, bool fallback = false)
    {
        if (_values.TryGetValue(key, out var v))
        {
            if (bool.TryParse(v.RawValue, out var b)) return b;
            if (TryParseNumber(v.RawValue, out var d)) return d > 0.5;
        }
        return fallback;
    }

    public string GetString(string key, string fallback = "")
    {
        if (_values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.RawValue))
            return v.RawValue;
        return fallback;
    }

    /// <summary>枚举型参数：校验取值必须在候选列表内，否则回退到第一项。</summary>
    public string GetEnum(string key, string fallback = "")
    {
        var val = GetString(key, fallback);
        if (_definitions.TryGetValue(key, out var def) && def.Options.Count > 0)
        {
            if (!def.Options.Contains(val, StringComparer.OrdinalIgnoreCase))
                return def.Options[0];
        }
        return val;
    }

    private double DefinitionDefault(string key, double fallback)
    {
        // 只有调用方没给兜底值时才退回定义的默认值
        if (fallback != 0) return fallback;
        return _definitions.TryGetValue(key, out var def) ? def.Default : fallback;
    }
}
