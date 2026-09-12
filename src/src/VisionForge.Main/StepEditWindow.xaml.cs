using System.Windows;
using VisionForge.Main.ViewModels;

namespace VisionForge.Main;

/// <summary>
/// 步骤设置对话框（右键步骤小方框弹出）。
///
/// <para>View 只管两件事：校验不通过就<b>不关窗</b>（错误贴在按钮上方），
/// 以及"到首页重新框"这个跳转意图。真正写回 SOP / 配方 / 过程层规则的是
/// <see cref="MainViewModel.ApplyStepEdit"/>。</para>
/// </summary>
public partial class StepEditWindow : Window
{
    public StepEditWindow()
    {
        InitializeComponent();

        // 图标只当装饰：文件格式不被接受时绝不能让窗口构造失败（主窗口踩过这个坑）
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/logo.ico", UriKind.Absolute));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("步骤设置窗口图标加载失败（忽略）：" + ex.Message);
        }
    }

    /// <summary>点「确定」：校验通过才允许关闭，否则把原因显示出来让用户改。</summary>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (DataContext is StepEditModel model && !model.Validate()) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 「到首页用鼠标重新框这一步」：先把当前改动带回去（确定），
    /// 再让主窗口切到首页并进入标定模式 —— 现场改框位置最自然的方式还是用鼠标框。
    /// </summary>
    private void OnReframe(object sender, RoutedEventArgs e)
    {
        if (DataContext is StepEditModel model)
        {
            if (!model.Validate()) return;
            model.RequestReframe = true;
        }

        DialogResult = true;
    }
}
