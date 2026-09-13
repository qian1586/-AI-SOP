using System.Collections.ObjectModel;
using System.Windows.Threading;
using VisionForge.Core.Services;
using VisionForge.Infrastructure.Storage;

namespace VisionForge.Main.ViewModels;

/// <summary>有人想切换到需要口令的角色时抛出来的请求（由主窗口负责弹登录框）。</summary>
public sealed class RoleChangeRequest
{
    public RoleChangeRequest(string role, UserRole target) { Role = role; Target = target; }

    /// <summary>要切到的角色名。</summary>
    public string Role { get; }

    public UserRole Target { get; }
}

/// <summary>
/// 权限：三档角色的口令校验、会话超时与审计。
///
/// <para><b>为什么升级才要口令：</b>降级（工程师 → 操作员）是"交还权限"，
/// 再拦一道只会让现场嫌烦；而升级必须验口令，否则那个下拉框就是个摆设 ——
/// 谁点一下都能拿到全部权限，等于没有权限系统。</para>
///
/// <para><b>为什么会话会自己过期：</b>工程师调完参数就下工位了，界面还停在"工程师"，
/// 下一个路过的人就能改配置。所以空闲超过设定时间自动退回操作员（默认 15 分钟）。</para>
/// </summary>
public sealed partial class MainViewModel
{
    private AccessControlService? _access;
    private DispatcherTimer? _sessionTimer;
    private DateTime _lastActivityAt = DateTime.Now;
    private string _securityHint = "权限模块尚未加载";

    /// <summary>想要切换角色（且需要口令）时触发，主窗口订阅它来弹登录框。</summary>
    public event EventHandler<RoleChangeRequest>? RoleChangeRequested;

    /// <summary>权限服务（自检和设置页都要用）。</summary>
    public AccessControlService? Access => _access;

    /// <summary>权限审计（设置页里给人看：谁什么时候切过角色）。</summary>
    public ObservableCollection<AccessAuditEntry> AccessAudit { get; } = new();

    /// <summary>还有哪些角色在用出厂默认口令（界面据此一直提醒）。</summary>
    public bool HasDefaultPasswords =>
        _access is not null && _access.RolesUsingDefaultPassword().Count > 0;

    /// <summary>
    /// 当前角色没权限做某些事时的提示文字。
    ///
    /// <para><b>为什么要专门做这一条：</b>v2.3 把开机默认角色改成"操作员"之后，
    /// 顶部工具条上的「标定 ROI」按钮就变灰了 —— 但界面上没有任何说明，
    /// 现场的原话是"标定 ROI 不能用，什么情况"。
    /// 灰按钮不解释，等于把权限系统做成了故障。现在只要角色不够，
    /// 工具条上就直接写出"为什么不能用、去哪儿切"。</para>
    /// </summary>
    public string PermissionHintText => IsOperator
        ? "🔒 当前是「操作员」：标定 ROI / 改参数需要「技术员」或「工程师」权限 —— 点右上角 🔑 切换（工程师默认口令 888888）"
        : string.Empty;

    public bool HasPermissionHint => !CanTuneRecipe;

    public string SecurityHintText
    {
        get => _securityHint;
        private set => SetProperty(ref _securityHint, value);
    }

    /// <summary>空闲多少分钟自动退回操作员（0 = 不自动降级）。</summary>
    public int SessionTimeoutMinutes
    {
        get => _settings.Current.Security.SessionTimeoutMinutes;
        set
        {
            int clamped = Math.Clamp(value, 0, 480);
            if (_settings.Current.Security.SessionTimeoutMinutes == clamped) return;

            _settings.Current.Security.SessionTimeoutMinutes = clamped;
            _settings.Save();
            OnPropertyChanged();
            RaiseSecurityProps();
        }
    }

    /// <summary>切到操作员要不要口令（默认不要）。</summary>
    public bool OperatorNeedsPassword
    {
        get => _settings.Current.Security.OperatorNeedsPassword;
        set
        {
            if (_settings.Current.Security.OperatorNeedsPassword == value) return;

            _settings.Current.Security.OperatorNeedsPassword = value;
            _settings.Save();
            OnPropertyChanged();
            RaiseSecurityProps();
        }
    }

    // ------------------------------------------------------------------ 加载

    /// <summary>启动时调用：读账号、拉起会话超时检查。</summary>
    private void LoadSecurity()
    {
        try
        {
            string dataRoot = _settings.Current.DataRoot;
            _access = new AccessControlService(SecurityStore.Load(dataRoot), _settings.Current.Security);

            AccessAudit.Clear();
            foreach (var entry in SecurityStore.LoadRecentAudit(dataRoot, 50))
                AccessAudit.Add(entry);

            var defaults = _access.RolesUsingDefaultPassword();
            _log.Info(defaults.Count == 0
                ? "权限模块已加载：所有角色都已改过口令"
                : $"权限模块已加载：仍在使用出厂默认口令的角色 = {string.Join("、", defaults)}");

            if (defaults.Count > 0)
                _log.Warn($"安全提醒：{string.Join("、", defaults)} 还在用出厂默认口令，请在「系统设置」里改掉");
        }
        catch (Exception ex)
        {
            _access = null;
            _log.Error("权限模块加载失败", ex);
        }

        _lastActivityAt = DateTime.Now;
        _sessionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _sessionTimer.Tick -= OnSessionTimerTick;
        _sessionTimer.Tick += OnSessionTimerTick;
        _sessionTimer.Start();

        RaiseSecurityProps();
    }

    private void RaiseSecurityProps()
    {
        OnPropertyChanged(nameof(HasDefaultPasswords));
        OnPropertyChanged(nameof(PermissionHintText));
        OnPropertyChanged(nameof(HasPermissionHint));
        OnPropertyChanged(nameof(SecurityHintText));
        OnPropertyChanged(nameof(SessionTimeoutMinutes));
        OnPropertyChanged(nameof(OperatorNeedsPassword));
        OnPropertyChanged(nameof(RolePermissionText));
    }

    /// <summary>
    /// 界面上有动作就刷新"最后活动时刻"（主窗口的鼠标/键盘事件调它）。
    ///
    /// <para>用"有没有操作"而不是"有没有点权限按钮"来判断是否空闲：
    /// 人在工位上盯着屏幕看，本身就该算"还有人"。省得看着看着被踢回操作员。</para>
    /// </summary>
    public void TouchActivity() => _lastActivityAt = DateTime.Now;

    /// <summary>空闲超时检查（每 30 秒一次）。</summary>
    private void OnSessionTimerTick(object? sender, EventArgs e)
        => EnforceSessionTimeout(DateTime.Now - _lastActivityAt);

    /// <summary>
    /// 按"已经空闲了多久"判断要不要退回操作员，返回 true 表示刚刚降级了。
    ///
    /// <para>把它单独做成一个公开方法（而不是藏在定时器回调里）是为了能让自检直接调 ——
    /// 不然"空闲 15 分钟自动降级"这条只能靠人坐在工位上等一刻钟来验证。</para>
    /// </summary>
    public bool EnforceSessionTimeout(TimeSpan idleFor)
    {
        if (_access is null) return false;
        if (IsOperator) return false;

        int minutes = _settings.Current.Security.SessionTimeoutMinutes;
        if (minutes <= 0) return false;
        if (idleFor.TotalMinutes < minutes) return false;

        ApplyRole(Roles.Operator, $"空闲超过 {minutes} 分钟，自动退回操作员");
        WriteAudit("自动降级", Roles.Operator, true, $"空闲 {idleFor.TotalMinutes:F1} 分钟");
        return true;
    }

    // ------------------------------------------------------------------ 切换角色

    /// <summary>
    /// 界面下拉框改角色时走这里。
    ///
    /// <para>降级直接放行；升级需要口令 —— 这时不改值、只发一个请求出去，
    /// 主窗口弹登录框，由 <see cref="AttemptRoleChange"/> 完成真正的校验。</para>
    /// </summary>
    private void RequestRoleChange(string role)
    {
        EnsureSecurity();

        var target = Roles.Parse(role);
        var current = Roles.Parse(_currentUser);

        if (target == current) return;

        // 权限模块没起来时**不允许**提升权限：宁可拦住，也不能悄悄放行
        if (_access is null)
        {
            OnPropertyChanged(nameof(CurrentUser));
            StatusMessage = "权限模块未加载，无法切换角色（请检查 data\\security\\ 目录）";
            return;
        }

        if (target > current && _access.NeedsPassword(target))
        {
            // 下拉框先弹回原值：值由 AttemptRoleChange 校验通过后才真正改
            OnPropertyChanged(nameof(CurrentUser));

            // 延到消息队列下一轮再弹窗：在绑定回调里同步弹模态框会把绑定过程卡住
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            dispatcher.BeginInvoke(
                new Action(() => RoleChangeRequested?.Invoke(this, new RoleChangeRequest(role, target))),
                DispatcherPriority.Background);
            return;
        }

        ApplyRole(role, "本角色无需口令");
    }

    /// <summary>
    /// 校验口令并（成功时）切换角色。登录框和自检都调它 —— 走的是同一条路。
    /// </summary>
    public LoginResult AttemptRoleChange(string role, string? password)
    {
        EnsureSecurity();
        if (_access is null) return LoginResult.Fail("权限模块未加载，无法切换角色");

        var result = _access.SignIn(role, password);
        WriteAudit(result.Ok ? "角色切换" : "角色切换被拒", role, result.Ok, result.Message);

        if (!result.Ok)
        {
            _log.Warn($"权限：{role} 登录失败 —— {result.Message}");
            SecurityHintText = result.Message;
            return result;
        }

        ApplyRole(role, result.Message);
        return result;
    }

    /// <summary>
    /// 保证权限模块已加载。
    ///
    /// <para>之所以做成"用到才加载"：自检里是直接 new 出 ViewModel 来跑用例的，
    /// 不走 <c>InitializeAsync</c>；而"没加载就不许提升权限"这条又必须成立。
    /// 两个要求合起来，就是这里的懒加载。</para>
    /// </summary>
    private void EnsureSecurity()
    {
        if (_access is not null) return;
        LoadSecurity();
    }

    /// <summary>真正把角色落到界面上（含权限属性刷新与审计）。</summary>
    private void ApplyRole(string role, string reason)
    {
        if (!string.Equals(_currentUser, role, StringComparison.Ordinal))
        {
            _currentUser = role;
            OnPropertyChanged(nameof(CurrentUser));
        }

        _lastActivityAt = DateTime.Now;

        RaiseRoleProps();
        RefreshCommands();
        RaiseSecurityProps();

        StatusMessage = $"当前角色：{role} —— {RolePermissionText}";
        SecurityHintText = reason + "｜" + RolePermissionText;

        _log.Info($"权限：切换为 {role}（{reason}）");

        // 降级不写审计（免得刷屏），升级与自动降级都写
        if (role != Roles.Operator) WriteAudit("角色生效", role, true, reason);
    }

    // ------------------------------------------------------------------ 口令维护

    /// <summary>
    /// 改口令（设置页调；密码框不进绑定，值由窗口代码传进来）。
    /// </summary>
    public void SubmitPasswordChange(string role, string? oldPassword, string? newPassword, string? confirm)
    {
        if (_access is null)
        {
            SecurityHintText = "权限模块未加载，无法改口令";
            return;
        }

        var result = _access.ChangePassword(role, oldPassword, newPassword, confirm);
        WriteAudit(result.Ok ? "修改口令" : "修改口令被拒", role, result.Ok, result.Message);

        SecurityHintText = result.Message;

        if (result.Ok)
        {
            PersistAccounts();
            _log.Info($"权限：{role} 口令已更新");
        }
        else
        {
            _log.Warn($"权限：{role} 改口令失败 —— {result.Message}");
        }

        RaiseSecurityProps();
    }

    /// <summary>
    /// 把某角色恢复成出厂默认口令。
    ///
    /// <para>必须先验过工程师口令 —— 否则这就是个"一键夺取权限"的后门。
    /// 现场口令忘了就靠它救回来，所以不能没有，但必须锁死。</para>
    /// </summary>
    public bool ResetRolePassword(string role, string? engineerPassword)
    {
        if (_access is null) return false;

        if (!_access.SignIn(Roles.Engineer, engineerPassword).Ok)
        {
            SecurityHintText = "恢复默认口令需要先输入正确的工程师口令";
            WriteAudit("恢复默认口令被拒", role, false, "工程师口令不正确");
            return false;
        }

        _access.ResetToDefault(role);
        PersistAccounts();
        WriteAudit("恢复默认口令", role, true, "已恢复为出厂默认口令");

        SecurityHintText = $"{role} 已恢复为出厂默认口令";
        RaiseSecurityProps();
        return true;
    }

    private void PersistAccounts()
    {
        try
        {
            if (_access is not null) SecurityStore.Save(_access.Accounts, _settings.Current.DataRoot);
        }
        catch (Exception ex)
        {
            _log.Error("账号保存失败", ex);
            SecurityHintText = "账号保存失败：" + ex.Message;
        }
    }

    private void WriteAudit(string action, string role, bool ok, string detail)
    {
        var entry = new AccessAuditEntry
        {
            Timestamp = DateTime.Now,
            Action = action,
            Role = role,
            Ok = ok,
            Detail = detail,
        };

        AccessAudit.Insert(0, entry);
        while (AccessAudit.Count > 50) AccessAudit.RemoveAt(AccessAudit.Count - 1);

        SecurityStore.AppendAudit(entry, _settings.Current.DataRoot);
    }
}
