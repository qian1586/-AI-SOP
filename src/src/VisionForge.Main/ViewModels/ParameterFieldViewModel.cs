using VisionForge.Common.Mvvm;
using VisionForge.Core.Models;

namespace VisionForge.Main.ViewModels;

/// <summary>
/// 动态参数面板里的一个字段。
///
/// 这就是"动态参数面板"能成立的关键一环：
///   算法声明 <see cref="ParameterDefinition"/>（有哪些参数、什么类型、什么范围）
///   配方提供值
///   本类把两者拼成一个「可绑定、可校验」的对象
///   界面用 DataTemplate 按 <see cref="Definition"/> 的类型自动选控件
///
/// 于是：换算法 → 参数列表自动变 → 界面自动变，主程序一行不改。
/// </summary>
public sealed class ParameterFieldViewModel : ObservableObject
{
    private string _value = string.Empty;
    private string? _error;

    public ParameterFieldViewModel(ParameterDefinition definition, string? initialValue = null)
    {
        Definition = definition;
        _value = initialValue ?? DefaultText(definition);
        Validate();
    }

    public ParameterDefinition Definition { get; }

    /// <summary>参数名，界面上作为标签显示。</summary>
    public string DisplayName => Definition.DisplayName;

    public string Description => Definition.Description;

    public string Unit => Definition.Unit;

    // ------------------------------------------------------------------
    // 四种控件类型各看一个属性，XAML 用 DataTrigger 切换模板
    // ------------------------------------------------------------------
    public bool IsNumber => Definition.Type == ParameterType.Number;
    public bool IsBool => Definition.Type == ParameterType.Bool;
    public bool IsEnum => Definition.Type == ParameterType.Enum;
    public bool IsText => Definition.Type == ParameterType.Text;

    public IReadOnlyList<string> Options => Definition.Options;

    // ------------------------------------------------------------------
    /// <summary>字符串值。所有类型都通过它存取 —— 便于统一序列化进配方。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value)) return;
            Validate();
            OnPropertyChanged(nameof(NumberValue));
            OnPropertyChanged(nameof(BoolValue));
        }
    }

    /// <summary>给 Slider / 数值框用。</summary>
    public double NumberValue
    {
        get => double.TryParse(_value, out var d) ? d : Definition.Default;
        set => Value = value.ToString("G");
    }

    /// <summary>给 CheckBox 用。</summary>
    public bool BoolValue
    {
        get => bool.TryParse(_value, out var b) ? b : _value == "1";
        set => Value = value ? "True" : "False";
    }

    /// <summary>校验错误信息。为空表示合法。</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public bool HasDescription => !string.IsNullOrWhiteSpace(Definition.Description);

    /// <summary>范围提示，如 "0 ~ 255 灰度"。</summary>
    public string RangeHint
    {
        get
        {
            if (!IsNumber) return string.Empty;
            var unit = string.IsNullOrEmpty(Unit) ? "" : " " + Unit;
            return $"{Definition.Min:G} ~ {Definition.Max:G}{unit}";
        }
    }

    public bool HasRangeHint => IsNumber;

    // ------------------------------------------------------------------
    /// <summary>
    /// 校验。
    ///
    /// 校验放在 ViewModel 而不是界面，是因为同一份参数可能被
    /// 界面、配方导入、以及将来的其他入口修改，校验逻辑只能有一处。
    /// </summary>
    private void Validate()
    {
        if (Definition.Type == ParameterType.Number)
        {
            if (!double.TryParse(_value, out var d))
            {
                Error = "必须是数字";
                return;
            }
            if (d < Definition.Min || (Definition.Max > 0 && d > Definition.Max))
            {
                Error = $"超出范围 {Definition.Min:G} ~ {Definition.Max:G}";
                return;
            }
        }
        else if (Definition.Type == ParameterType.Bool)
        {
            if (!bool.TryParse(_value, out _) && _value != "0" && _value != "1")
            {
                Error = "必须是 True/False";
                return;
            }
        }
        else if (Definition.Type == ParameterType.Enum)
        {
            if (Options.Count > 0 && !Options.Contains(_value))
            {
                // 不报错，直接纠正 —— 枚举值不在列表里通常是旧配方，
                // 让它静默回退比弹一堆报错更实用
                _value = Options[0];
                OnPropertyChanged(nameof(Value));
            }
        }

        Error = null;
    }

    /// <summary>导出成配方里的存储形式。</summary>
    public ParameterValue ToParameterValue() => new(Definition.Key, _value);

    private static string DefaultText(ParameterDefinition def) => def.Type switch
    {
        ParameterType.Bool => "False",
        ParameterType.Number => def.Default.ToString("G"),
        ParameterType.Enum => def.Options.Count > 0 ? def.Options[0] : string.Empty,
        _ => string.Empty,
    };
}
