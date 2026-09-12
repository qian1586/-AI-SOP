using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace VisionForge.Main;

/// <summary>
/// SOP 作业看板窗口 —— 给车间大屏用的。
///
/// 职责很轻：显示 ViewModel 的状态 + 跑一个时钟。
/// 所有业务状态都来自绑定的 MainViewModel，它自己不做任何判断。
/// </summary>
public partial class SopBoardWindow : Window
{
    private readonly DispatcherTimer _clock;

    public SopBoardWindow()
    {
        InitializeComponent();

        // 秒级时钟。大屏上有时间戳，出现异常时方便对录像，这是刚需。
        _clock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        _clock.Start();

        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");

        Closed += (_, _) => _clock.Stop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            // F11 在"无边框全屏"和"有边框窗口"之间切换，
            // 方便工程师在自己电脑上调试时不用全屏挡着别的东西
            case Key.F11:
                if (WindowStyle == WindowStyle.None)
                {
                    WindowStyle = WindowStyle.SingleBorderWindow;
                    WindowState = WindowState.Normal;
                }
                else
                {
                    WindowStyle = WindowStyle.None;
                    WindowState = WindowState.Maximized;
                }
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }
}
