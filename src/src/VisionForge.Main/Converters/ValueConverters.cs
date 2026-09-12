using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VisionForge.Main.Converters;

/// <summary>
/// SOP 步骤状态 → 文字颜色。
/// 输入是 <see cref="VisionForge.Core.Services.SopStep.StateKey"/> 返回的字符串
/// （"ok" / "ng" / "active" / "pending"），刻意用字符串而非 Brush —— Core 层不依赖 WPF。
/// </summary>
public sealed class StateKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "pending";
        return key switch
        {
            "ok"      => Application.Current.Resources["StateOk"],
            "ng"      => Application.Current.Resources["StateNg"],
            "active"  => Application.Current.Resources["StateActive"],
            _         => Application.Current.Resources["TextMuted"],
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>SOP 步骤状态 → 整行背景（已通过的步骤淡绿色背景条）。</summary>
public sealed class StateKeyToBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "pending";
        return key switch
        {
            "ok"      => new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0xC5, 0x5E)),   // 22C55E 20%
            "ng"      => new SolidColorBrush(Color.FromArgb(0x33, 0xEF, 0x44, 0x44)),   // EF4444 20%
            "active"  => new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B)),   // F59E0B 20%
            _         => new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),   // 灰
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>检测结论枚举 → 显示文字（"OK" / "NG" / "—"。</summary>
public sealed class VerdictToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Core.Models.InspectionVerdict v)
        {
            return v switch
            {
                Core.Models.InspectionVerdict.Pass          => "OK",
                Core.Models.InspectionVerdict.Fail          => "NG",
                Core.Models.InspectionVerdict.Error         => "故障",
                _                                            => "—",
            };
        }
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>检测结论 → 文字颜色（绿/红/橙/灰）。</summary>
public sealed class VerdictToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Core.Models.InspectionVerdict v)
        {
            return v switch
            {
                Core.Models.InspectionVerdict.Pass  => Application.Current.Resources["StateOk"],
                Core.Models.InspectionVerdict.Fail  => Application.Current.Resources["StateNg"],
                Core.Models.InspectionVerdict.Error => Application.Current.Resources["StateWarn"],
                _                                   => Application.Current.Resources["TextMuted"],
            };
        }
        return Application.Current.Resources["TextMuted"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>检测结论 → 背景色（带透明度的标签底）。</summary>
public sealed class VerdictToBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Core.Models.InspectionVerdict v)
        {
            return v switch
            {
                Core.Models.InspectionVerdict.Pass  => new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0xC5, 0x5E)),
                Core.Models.InspectionVerdict.Fail  => new SolidColorBrush(Color.FromArgb(0x33, 0xEF, 0x44, 0x44)),
                Core.Models.InspectionVerdict.Error => new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B)),
                _                                   => new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            };
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 声光报警信号键 → 颜色。
/// 输入字符串来自 <c>MainViewModel.AlarmSignalKey</c>。
/// </summary>
public sealed class AlarmKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "none";
        return key switch
        {
            "ok"      => Application.Current.Resources["StateOk"],
            "warning" => Application.Current.Resources["StateWarn"],
            "fault"   => Application.Current.Resources["StateNg"],
            _         => Application.Current.Resources["TextMuted"],
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool 取反（用于"未运行时不显示运行灯"这类反向逻辑）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : DependencyProperty.UnsetValue;
}