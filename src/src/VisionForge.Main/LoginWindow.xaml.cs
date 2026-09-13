using System.Windows;
using System.Windows.Input;

namespace VisionForge.Main;

/// <summary>
/// 权限校验对话框：切到需要口令的角色时弹它。
///
/// <para>只负责"收一个口令串"这件事，校验交给 <c>AccessControlService</c> ——
/// 界面不碰任何口令逻辑，将来换指纹/刷卡也只需要换这个窗口。</para>
/// </summary>
public partial class LoginWindow : Window
{
    public LoginWindow(string role, string permissionText, string? previousError = null,
                       bool usingDefaultPassword = false)
    {
        InitializeComponent();

        TitleText.Text = $"切换到「{role}」需要口令";
        RoleHintText.Text = permissionText;

        if (!string.IsNullOrWhiteSpace(previousError))
        {
            ErrorText.Text = previousError;
            ErrorText.Visibility = Visibility.Visible;
        }

        // 还在用出厂默认口令时，窗口右下角一直挂着提醒
        DefaultPasswordHint.Visibility = usingDefaultPassword
            ? Visibility.Visible
            : Visibility.Collapsed;

        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>用户输入的口令（取消时是空串）。</summary>
    public string EnteredPassword { get; private set; } = string.Empty;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        EnteredPassword = PasswordInput.Password;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>口令框里回车 = 点确定（现场不用去够鼠标）。</summary>
    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        EnteredPassword = PasswordInput.Password;
        DialogResult = true;
        e.Handled = true;
    }
}
