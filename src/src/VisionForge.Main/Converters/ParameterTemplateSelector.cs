using System.Windows;
using System.Windows.Controls;
using VisionForge.Core.Models;
using VisionForge.Main.ViewModels;

namespace VisionForge.Main.Converters;

/// <summary>
/// 动态参数面板的模板选择器 —— 按参数类型自动挑控件模板。
///
/// 这是"动态参数面板"的最后一块拼图：
///   算法声明 ParameterDefinition.Type → 本选择器挑模板 → 界面渲染出对应控件
///   数值 → 输入框＋范围提示
///   布尔 → 复选框
///   枚举 → 下拉框
///   文本 → 输入框＋说明
///
/// <b>关键在于这里没有任何 switch(参数名) 的硬编码。</b>
/// 新算法只要声明参数类型，界面自动就能正确渲染 —— 不用改 XAML，不用重编译界面。
/// </summary>
public sealed class ParameterTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NumberTemplate { get; set; }
    public DataTemplate? BoolTemplate { get; set; }
    public DataTemplate? EnumTemplate { get; set; }
    public DataTemplate? TextTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not ParameterFieldViewModel field)
            return base.SelectTemplate(item, container);

        return field.Definition.Type switch
        {
            ParameterType.Number => NumberTemplate,
            ParameterType.Bool => BoolTemplate,
            ParameterType.Enum => EnumTemplate,
            ParameterType.Text => TextTemplate,
            _ => TextTemplate,
        };
    }
}
