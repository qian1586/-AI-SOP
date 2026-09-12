using System.Windows;
using VisionForge.Main.ViewModels;

namespace VisionForge.Main;

/// <summary>
/// 「时间设置」窗口。View 只负责"校验不过就不关窗"，
/// 真正写回配置的是 <see cref="MainViewModel.ApplyTimingSettings"/>。
/// </summary>
public partial class TimingSettingsWindow : Window
{
    public TimingSettingsWindow()
    {
        InitializeComponent();

        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/logo.ico", UriKind.Absolute));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("时间设置窗口图标加载失败（忽略）：" + ex.Message);
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (DataContext is TimingSettingsModel model && !model.Validate()) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
