using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VisionForge.Main.Converters;

/// <summary>
/// 当前页索引 → 可见性。ConverterParameter 传目标页号（0=首页, 1=配方管理, 2=历史查询, 3=系统设置）。
///
/// <para>用"索引 + 可见性"而不是 TabControl：导航按钮已经在顶栏了，
/// 再套一层 TabControl 就得把它的页签头藏掉，反而更绕。</para>
/// </summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index && TryParseIndex(parameter, out int target) && index == target)
            return Visibility.Visible;

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    internal static bool TryParseIndex(object parameter, out int index)
    {
        index = 0;
        if (parameter is null) return false;
        return int.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }
}

/// <summary>
/// 当前页索引 ↔ 导航按钮的选中状态。
///
/// <para>RadioButton.IsChecked 默认是双向绑定，所以必须实现 ConvertBack。
/// 这里有个容易踩的坑：一组 RadioButton 里，新按钮选中时旧按钮会"取消选中"，
/// 如果把取消选中（false）也写回 ViewModel，页面会被立刻切回上一页 ——
/// 所以只在 true 时写回，false 一律 DoNothing。</para>
/// </summary>
public sealed class IndexToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int index
           && IndexToVisibilityConverter.TryParseIndex(parameter, out int target)
           && index == target;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked
            && IndexToVisibilityConverter.TryParseIndex(parameter, out int target))
            return target;

        return Binding.DoNothing;
    }
}
