using System.IO;
using System.Windows;
using VisionForge.Common.Events;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Hardware.Alarm;
using VisionForge.Hardware.Plc;
using VisionForge.Hardware.Vision;
using VisionForge.Infrastructure.Config;
using VisionForge.Infrastructure.Logging;
using VisionForge.Infrastructure.Storage;
using VisionForge.Main.ViewModels;

namespace VisionForge.Main;

/// <summary>
/// 应用程序入口 —— 也就是所谓的「组合根」（Composition Root）。
///
/// <para>整套分层架构里，<b>只有这一个地方</b>知道所有具体实现类的存在：
/// 它知道要用 JsonRecipeRepository 而不是别的仓储，
/// 知道要用 ModbusTcpPlcClient 而不是别的 PLC 客户端。
/// 其他地方（Core / ViewModel / View）统统只认接口。</para>
///
/// <para>收益很直接：如果哪天要换成 SQLite 仓储，或者接欧姆龙 FINS，
/// 改的就是下面这几行装配代码，业务代码一行不动。</para>
///
/// <para>不引入 DI 容器（Autofac / Microsoft.Extensions.DependencyInjection）是刻意的：
/// 这个规模的项目手工装配一共二十来行，比引一个容器 + 学一套配置语法划算得多，
/// 而且依赖关系一眼看得见，不用去猜"这个接口到底是谁注册的"。</para>
/// </summary>
public partial class App : Application
{
    private FileLogger? _logger;
    private MainViewModel? _viewModel;

    /// <summary>全局配置。</summary>
    public static AppSettingsProvider Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 异常兜底：工控软件不能因为一个界面异常就整个退出 ----
        // 这个补丁的由来很具体：ProgressBar.Value 默认是双向绑定，
        // 绑到只读属性上会在渲染阶段抛 InvalidOperationException，
        // 直接把进程带崩，日志里只剩半行启动记录，极难定位。
        // 现在这类异常会被写进日志 + 弹窗提示，程序继续运行。
        DispatcherUnhandledException += (_, args) =>
        {
            try { _logger?.Error("界面线程未处理异常", args.Exception); } catch { }

            try
            {
                MessageBox.Show(
                    "界面出现异常，已写入日志，程序会继续运行：\n\n" + args.Exception.Message,
                    "VisionForge", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }

            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                    _logger?.Error("后台线程未处理异常（进程将退出）", ex);
                else
                    _logger?.Error("后台线程未处理异常：" + args.ExceptionObject);
            }
            catch { }
        };

        // ---- 自检模式：不开界面，把监测逻辑用例跑完就退出 ----
        // 用法：VisionForge.Main.exe --selftest
        // 退出码：0 = 全部通过，1 = 有失败项（可直接接 CI / 交付验收）
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int exitCode;
            try
            {
                string reportPath = Path.Combine(AppContext.BaseDirectory, "data", "selftest", "report.txt");

                // 必须丢到线程池上跑：OnStartup 就在 UI 线程里，
                // 而自检里大量"同步等待异步"，在 UI 线程上跑会自己把自己锁死
                exitCode = Task.Run(() => SelfTest.SelfTestRunner.Run(AppContext.BaseDirectory, reportPath))
                               .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "data", "selftest-error.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 自检运行异常：" + ex + Environment.NewLine);
                exitCode = 1;
            }

            Environment.Exit(exitCode);
            return;
        }

        // ---- 路径：统一挂在 exe 同级目录，避免受"当前工作目录"影响 ----
        // 用相对路径的话，从不同方式启动（快捷方式 / 计划任务 / 调试器）
        // 会指向不同目录，日志和配方就找不到了，这类问题很难排查。
        var baseDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDir, "config", "appsettings.json");

        Settings = new AppSettingsProvider(configPath);
        var s = Settings.Current;

        if (!Path.IsPathRooted(s.DataRoot))
            s.DataRoot = Path.Combine(baseDir, s.DataRoot);

        // ---- 配置自愈：补上实时拦截要用的"规则码"地址 ----
        // 契约是把违规码写到 PLC 的某个寄存器（默认 D201）。
        // 老配置文件里没有这项，自动补上并落盘 —— 否则这个功能会静默失效。
        if (!s.PlcAddresses.Any(a => a.Name == "RuleCode"))
        {
            s.PlcAddresses.Add(new PlcAddressItem
            {
                Name = "RuleCode",
                Address = "D201",
                DataType = PlcDataType.Int16,
                Writable = true,
                Description = "违规规则码 0=跳步 1=错序 2=目标未完成 3=数量不符 4=关键点丢失 5=配置错误",
            });
            Settings.Save(force: true);
        }

        // ---- 各层装配 ----
        _logger = new FileLogger(s.LogDirectory);
        _logger.Info("========== VisionForge 启动 ==========");
        _logger.Info($"数据目录：{s.DataRoot}");

        var events = new EventAggregator();
        var algorithms = AlgorithmRegistry.CreateDefault();
        var recipes = new JsonRecipeRepository(s.RecipeDirectory);
        var history = new JsonLineHistoryStore(s.HistoryDirectory, s.NgImageDirectory);

        // ---- 示例数据 ----
        // 示例配方只提供"流程骨架"（SOP 六道工序 + 一套算法参数），
        // **不在画面上预置任何框** —— 框的位置和相机机位、料盒位置强相关，必须现场自己建。
        // 这里还负责把老版本自动生成的 6 个预置框清掉（按坐标逐一对号，
        // 现场自己挪过/改过的框一律不碰）。现场师傅自己建的配方绝不会被碰。
        try
        {
            string cameraId = s.Cameras.FirstOrDefault()?.Id ?? "CAM-001";
            string demoNote = Task.Run(() => DemoDataFactory.EnsureDemoDataAsync(recipes, s.DataRoot, cameraId))
                                  .GetAwaiter().GetResult();
            _logger.Info("示例数据：" + demoNote);
        }
        catch (Exception ex)
        {
            _logger.Warn("示例数据检查失败：" + ex.Message);
        }

        IPlcClient plc;
        try
        {
            plc = PlcFactory.Create(s.Plc);
        }
        catch (NotSupportedException ex)
        {
            // 配置里写了个还没实现的 PLC 品牌时，不能让程序起不来 ——
            // 退回模拟 PLC，并在日志里说清楚，操作员能继续用视觉部分
            _logger.Warn($"PLC 初始化失败（{ex.Message}），已回退到模拟 PLC");
            plc = new MockPlcClient(s.Plc);
        }

        _logger.Info($"已注册算法插件：{string.Join("、", algorithms.All.Select(a => a.DisplayName))}");
        _logger.Info($"PLC：{s.Plc.Brand} @ {s.Plc.ToEndpoint()}");

        // ---- 声光报警 ----
        var alarm = AlarmFactory.Create(s.Alarm, plc, s.PlcAddresses);
        if (alarm is PlcAlarmDevice pa)
            _logger.Info($"声光报警：PLC 输出（绑定 {pa.BoundLightCount} 路灯，蜂鸣器={(pa.HasBuzzer ? "有" : "无")}）");
        else
            _logger.Info("声光报警：模拟模式（地址表里没找到灯光地址，或已在配置里关闭）");

        // ---- ViewModel 与主窗口 ----
        _viewModel = new MainViewModel(algorithms, recipes, history, plc, alarm, _logger, events, Settings);

        var window = new MainWindow { DataContext = _viewModel };

        // ---- 界面冒烟自检：真把主窗口开一次、布局一遍、再关掉 ----
        //
        // 用法：VisionForge.Main.exe --smoketest
        // 退出码：0 = 界面正常打开，2 = 失败（报告在 data\smoketest\report.txt）
        //
        // 为什么需要它：XAML 里资源键写错、转换器找不到、样式目标类型不对，
        // 静态扫描全都看不出来 —— 只有真正构造并布局一次窗口才会炸，
        // 而现场看到的只是"打不开"三个字，没法定位。
        // 有了它，每次重建都能先把这句"打不开"变成一个说清楚位置的错误。
        if (e.Args.Any(a => string.Equals(a, "--smoketest", StringComparison.OrdinalIgnoreCase)))
        {
            string smokeReport = Path.Combine(baseDir, "data", "smoketest", "report.txt");

            // 让消息队列空转一小会儿：绑定、数据模板、转换器都是在这段时间里真正求值的，
            // 只构造不跑消息，会漏掉一批"只有渲染阶段才暴露"的问题。
            static void PumpMessages(System.Windows.Threading.Dispatcher dispatcher)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }

            int smokeExit;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(smokeReport)!);

                window.Show();
                window.WindowState = WindowState.Maximized;   // 现场就是最大化用的，量出来才有意义
                window.UpdateLayout();

                var settleUntil = DateTime.Now.AddMilliseconds(500);
                while (DateTime.Now < settleUntil)
                {
                    PumpMessages(window.Dispatcher);
                    System.Threading.Thread.Sleep(25);
                }

                // ---- 布局体检：量一次"这一栏的内容有没有超出屏幕" ----
                //
                // 这类问题（卡片底部被挤出屏幕、按钮看不见）现场报过好几次，
                // 而它们全都是"内容自然高度 > 容器给的高度"，只能在真开窗口之后量出来。
                // 这里只记录、不判失败 —— 布局问题不该拦住程序启动，但必须留下证据。
                string layoutNote = "（没找到现场状态栏，跳过布局体检）";
                try
                {
                    if (window.FindName("SiteStatusCard") is System.Windows.Controls.Border card &&
                        card.Child is System.Windows.FrameworkElement inner &&
                        card.ActualHeight > 0)
                    {
                        double available = card.ActualHeight - card.Padding.Top - card.Padding.Bottom;
                        double natural = inner.DesiredSize.Height;   // 内容"想要"多高
                        double arranged = inner.ActualHeight;        // 实际排布多高（被拉伸后）

                        // 两件事要分开看：
                        //   natural > available → 内容装不下（会被裁或滚动）
                        //   arranged < available → 内容没铺满，底部会空一块
                        string head;
                        if (natural > available + 1)
                            head = $"⚠ 装不下，超出 {natural - available:F0}px";
                        else if (arranged < available - 2)
                            head = $"⚠ 底部还空着 {available - arranged:F0}px";
                        else
                            head = "✔ 已铺满";

                        layoutNote =
                            $"现场状态栏：容器可用 {available:F0}px，内容自然高 {natural:F0}px，" +
                            $"实际占满 {arranged:F0}px —— {head}" +
                            Environment.NewLine +
                            $"屏幕 {SystemParameters.PrimaryScreenWidth:F0}×{SystemParameters.PrimaryScreenHeight:F0}，" +
                            $"窗口 {window.ActualWidth:F0}×{window.ActualHeight:F0}";
                    }

                    // 相机展示位到底有多大、被下面两张卡占掉多少 —— 这是现场最关心的那个数
                    if (window.FindName("PreviewViewport") is System.Windows.FrameworkElement viewport)
                    {
                        layoutNote += Environment.NewLine +
                            $"相机画面区高度：{viewport.ActualHeight:F0}px";
                    }

                    if (window.FindName("RoiLearnCard") is System.Windows.FrameworkElement learnCard)
                    {
                        layoutNote += $"｜区域学习卡高度：{learnCard.ActualHeight:F0}px";
                    }

                    if (window.FindName("HistoryStrip") is System.Windows.FrameworkElement historyStrip)
                    {
                        layoutNote += $"｜底部历史条高度：{historyStrip.ActualHeight:F0}px";
                    }

                    // 浮层不占布局高度，但它盖住多少画面就是实打实少看多少，所以也要量
                    if (window.FindName("CameraToolbar") is System.Windows.FrameworkElement cameraToolbar)
                    {
                        layoutNote += $"｜画面浮层工具条高度：{cameraToolbar.ActualHeight:F0}px";
                    }

                    // 右列两张卡：量"给的高度 vs 内容要的高度"，判断是空着还是装不下
                    foreach (var (name, label) in new[]
                             {
                                 ("ProcessMonitorCard", "过程监测卡"),
                                 ("CurrentStepCard", "当前工序卡"),
                             })
                    {
                        if (window.FindName(name) is System.Windows.Controls.Border c &&
                            c.Child is System.Windows.FrameworkElement kid && c.ActualHeight > 0)
                        {
                            double avail = c.ActualHeight - c.Padding.Top - c.Padding.Bottom;
                            double need = kid.DesiredSize.Height;
                            double diff = need - avail;
                            layoutNote += Environment.NewLine +
                                $"{label}：可用 {avail:F0}px，内容需要 {need:F0}px —— " +
                                (diff > 1
                                    ? $"⚠ 装不下，超出 {diff:F0}px（会滚动/被裁）"
                                    : diff < -2
                                        ? $"⚠ 底部空着 {(-diff):F0}px"
                                        : "✔ 已铺满");
                        }
                    }
                }
                catch (Exception layoutEx)
                {
                    layoutNote = "布局体检本身出错了：" + layoutEx.Message;
                }

                window.Close();

                // ---- 登录框也真开一次 ----
                // 它是权限的唯一入口：这里要是渲染不出来，现场就永远进不了工程师，
                // 而那条路只在"有人想切角色"时才走得到，靠人工验收很容易漏。
                string loginNote;
                try
                {
                    var loginProbe = new LoginWindow(
                        Core.Services.Roles.Engineer,
                        Core.Services.Roles.Describe(Core.Services.UserRole.Engineer),
                        "（冒烟自检：这是故意传进去的测试提示）",
                        usingDefaultPassword: true);

                    loginProbe.Show();
                    PumpMessages(loginProbe.Dispatcher);
                    loginProbe.Close();
                    loginNote = "✔ 登录框能正常打开";
                }
                catch (Exception loginEx)
                {
                    loginNote = "⚠ 登录框打不开：" + loginEx.Message;
                }

                File.WriteAllText(smokeReport,
                    "界面冒烟自检：通过" + Environment.NewLine +
                    $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                    "主窗口已真实打开、完成一次布局与绑定求值，没有抛异常。" + Environment.NewLine +
                    "（说明 XAML 能解析、资源键都在、转换器都能找到、绑定不会把界面打崩）" + Environment.NewLine,
                    new System.Text.UTF8Encoding(true));

                // 把布局体检结果追加进报告（单独一段，方便一眼看到）
                File.AppendAllText(smokeReport,
                    Environment.NewLine + "---------- 布局体检 ----------" + Environment.NewLine +
                    layoutNote + Environment.NewLine +
                    "登录框：" + loginNote + Environment.NewLine,
                    new System.Text.UTF8Encoding(true));

                Console.WriteLine("[冒烟自检] 通过：界面能正常打开（XAML / 资源 / 转换器 / 绑定都过了一遍）");
                Console.WriteLine("[布局体检] " + layoutNote.Replace(Environment.NewLine, " | "));
                smokeExit = 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(smokeReport,
                        "界面冒烟自检：失败" + Environment.NewLine +
                        $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                        ex, new System.Text.UTF8Encoding(true));
                }
                catch { /* 连报告都写不了就只能靠控制台了 */ }

                Console.WriteLine("[冒烟自检] 失败：" + ex.Message);
                Console.WriteLine(ex);
                smokeExit = 2;
            }

            Environment.Exit(smokeExit);
            return;
        }

        // 注意这里不要写 async：方法体内没有 await，
        // 编译器会报 CS1998（"此异步方法缺少 await"）——本项目的目标是 0 警告。
        window.Closing += (_, _) =>
        {
            _logger?.Info("窗口关闭，正在释放资源…");
            _viewModel?.Dispose();
            Settings.Save(force: true);
            _logger?.Flush(TimeSpan.FromSeconds(2));
            _logger?.Dispose();
        };

        window.Show();

        // 界面显示后再异步初始化，避免启动时白屏
        _ = _viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _logger?.Dispose();
        base.OnExit(e);
    }

}
