using System.Security.Cryptography;
using System.Text;

namespace VisionForge.Core.Services;

/// <summary>三级角色。数字越大权限越高，用来判断"这次切换是升级还是降级"。</summary>
public enum UserRole
{
    /// <summary>操作员：只作业，改不了任何配置。</summary>
    Operator = 0,

    /// <summary>技术员：能调参数、标定 ROI、人工放行，但不能增删配方或改系统设置。</summary>
    Technician = 1,

    /// <summary>工程师：全部权限。</summary>
    Engineer = 2,
}

/// <summary>角色的名字与说明（界面和日志都用这里的字，避免各处写死出现两种叫法）。</summary>
public static class Roles
{
    public const string Operator = "操作员";
    public const string Technician = "技术员";
    public const string Engineer = "工程师";

    /// <summary>界面上那份下拉框的顺序：从高到低（工程师在最上面，符合现场习惯）。</summary>
    public static readonly string[] All = { Engineer, Technician, Operator };

    public static UserRole Parse(string? name) => name switch
    {
        Engineer => UserRole.Engineer,
        Technician => UserRole.Technician,
        _ => UserRole.Operator,
    };

    public static string Display(UserRole role) => role switch
    {
        UserRole.Engineer => Engineer,
        UserRole.Technician => Technician,
        _ => Operator,
    };

    /// <summary>权限说明（直接显示给现场看，所以写成一句人话）。</summary>
    public static string Describe(UserRole role) => role switch
    {
        UserRole.Engineer => "全部权限：配方增删改、标定、放行、系统设置",
        UserRole.Technician => "可调参数与标定 ROI、可人工放行；不能新建或删除配方",
        _ => "仅作业：启动监测 / 触发检测 / 消音，不能改配方或放行",
    };
}

/// <summary>
/// 口令哈希与校验。
///
/// <para><b>为什么不能直接存密码：</b>配置文件是放在工位机上的普通文本，
/// 存明文等于把密码写在便利贴上贴屏幕边。这里用 PBKDF2-SHA256 加随机盐迭代 10 万次 ——
/// 即使文件被拷走，也几乎不可能反推出原文。</para>
///
/// <para>存储格式：<c>PBKDF2$迭代次数$盐(base64)$哈希(base64)</c>。
/// 把迭代次数写进字符串里，是为了以后调高迭代次数时旧口令仍然能校验通过
/// （不然一升级，全厂工位都进不去工程师了）。</para>
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2";
    private const int DefaultIterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(password, salt, iterations);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 校验。任何解析不出来的字符串一律返回 false（不抛异常）——
    /// 配置文件被手工改坏了，结果应该是"进不去"，而不是"程序崩了"。
    /// </summary>
    public static bool Verify(string? password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        if (password is null) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out int iterations) || iterations < 1) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch
        {
            return false;
        }

        byte[] actual = Derive(password, salt, iterations, expected.Length);

        // 定时安全比较：不用 SequenceEqual 是为了避免"按第一个不同字节提前返回"带来的时间侧信道
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? string.Empty), salt, iterations, HashAlgorithmName.SHA256, length);
}

/// <summary>一个角色的账号信息。</summary>
public sealed class RoleAccount
{
    public string Role { get; set; } = Roles.Operator;

    /// <summary>口令哈希（绝不是明文）。留空表示该角色不需要口令。</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>是否仍是出厂默认口令 —— 界面上要据此提醒现场尽快改掉。</summary>
    public bool IsDefaultPassword { get; set; } = true;

    public DateTime? PasswordUpdatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>连续输错次数（输对一次清零）。</summary>
    public int FailedAttempts { get; set; }

    /// <summary>锁定到这个时刻之前不允许再试（防有人坐在工位上一直猜）。</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>该角色是否设了口令。</summary>
    public bool HasPassword => !string.IsNullOrWhiteSpace(PasswordHash);
}

/// <summary>
/// 权限相关的可调项（跟着配置走，现场能改）。
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// 空闲多久自动退回操作员（分钟，0 = 不自动降级）。
    ///
    /// <para>这是工控现场最容易被忽略的一条：工程师调完参数就走了，
    /// 界面还停在"工程师"，下一个路过的人就能改配置。
    /// 默认 15 分钟，让高权限自己过期。</para>
    /// </summary>
    public int SessionTimeoutMinutes { get; set; } = 15;

    /// <summary>切到"操作员"要不要口令（默认不要：生产岗位本来就该直接能用）。</summary>
    public bool OperatorNeedsPassword { get; set; }

    /// <summary>口令最短长度。</summary>
    public int MinPasswordLength { get; set; } = 4;

    /// <summary>连续输错几次开始锁定。</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>锁定时长（秒）。</summary>
    public int LockoutSeconds { get; set; } = 30;
}

/// <summary>一次登录尝试的结果。</summary>
public sealed class LoginResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>被锁定中（界面据此提示还要等多久）。</summary>
    public TimeSpan? LockRemaining { get; init; }

    public static LoginResult Success(string message) => new() { Ok = true, Message = message };
    public static LoginResult Fail(string message, TimeSpan? lockRemaining = null)
        => new() { Ok = false, Message = message, LockRemaining = lockRemaining };
}

/// <summary>一次改密的结果。</summary>
public sealed class PasswordChangeResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;

    public static PasswordChangeResult Success(string message) => new() { Ok = true, Message = message };
    public static PasswordChangeResult Fail(string message) => new() { Ok = false, Message = message };
}

/// <summary>
/// 权限服务：管三档角色的口令、登录、锁定与改密。
///
/// <para><b>设计原则（按工控现场的实际用法定的）：</b></para>
/// <list type="number">
///   <item><b>升级要口令，降级不要</b>：从工程师退回操作员是"交还权限"，不该再拦一道；
///         反向就必须验口令，否则谁都能点一下拿到全部权限。</item>
///   <item><b>出厂有默认口令，但会一直提醒你改</b>：第一次装机就得能用，
///         所以默认口令必须存在；但只要还没改，界面上就一直挂着提醒，
///         并且这条提醒写进日志 —— 不给"忘了改"留余地。</item>
///   <item><b>输错要锁一下</b>：连续错 5 次锁 30 秒。
///         不做永久锁死是刻意的 —— 车间里没有 IT 值守，
///         永久锁死等于把整条线的运维堵死。</item>
///   <item><b>每次切换都留痕</b>：成功、失败、锁定、改密，全部写审计日志。</item>
/// </list>
/// </summary>
public sealed class AccessControlService
{
    private readonly Dictionary<string, RoleAccount> _accounts = new(StringComparer.Ordinal);

    public AccessControlService(IEnumerable<RoleAccount>? accounts, SecurityOptions? options = null)
    {
        Options = options ?? new SecurityOptions();

        foreach (var account in accounts ?? Array.Empty<RoleAccount>())
        {
            if (string.IsNullOrWhiteSpace(account.Role)) continue;
            _accounts[account.Role] = account;
        }

        // 缺哪个角色补哪个 —— 老版本配置文件里没有这一段时，不能让程序起不来
        foreach (var role in Roles.All)
        {
            if (!_accounts.ContainsKey(role))
                _accounts[role] = MakeDefaultAccount(role);
        }
    }

    public SecurityOptions Options { get; }

    public IReadOnlyCollection<RoleAccount> Accounts => _accounts.Values;

    public RoleAccount Get(string role) => _accounts.TryGetValue(role, out var a)
        ? a
        : _accounts[Roles.Operator];

    /// <summary>出厂默认口令。现场必须尽快改掉，界面上会一直提醒。</summary>
    public static string DefaultPasswordOf(string role) => role switch
    {
        Roles.Engineer => "888888",
        Roles.Technician => "123456",
        _ => string.Empty,        // 操作员默认不需要口令
    };

    public static RoleAccount MakeDefaultAccount(string role)
    {
        string password = DefaultPasswordOf(role);
        return new RoleAccount
        {
            Role = role,
            PasswordHash = password.Length == 0 ? string.Empty : PasswordHasher.Hash(password),
            IsDefaultPassword = true,
        };
    }

    public static List<RoleAccount> CreateDefaultAccounts()
        => Roles.All.Select(MakeDefaultAccount).ToList();

    /// <summary>切到这个角色需不需要口令。</summary>
    public bool NeedsPassword(UserRole target)
    {
        var account = Get(Roles.Display(target));
        if (!account.HasPassword) return false;

        // 操作员那档允许配成"免口令"
        if (target == UserRole.Operator && !Options.OperatorNeedsPassword) return false;

        return true;
    }

    /// <summary>还有哪个角色在用出厂默认口令（空 = 都改过了）。</summary>
    public IReadOnlyList<string> RolesUsingDefaultPassword()
        => _accounts.Values.Where(a => a is { IsDefaultPassword: true, HasPassword: true })
                           .Select(a => a.Role)
                           .OrderBy(r => r, StringComparer.Ordinal)
                           .ToList();

    /// <summary>
    /// 尝试登录到某个角色。
    ///
    /// <para>返回结果是给人看的一句话，直接显示到界面上，不抛异常。</para>
    /// </summary>
    public LoginResult SignIn(string role, string? password, DateTime? now = null)
    {
        var stamp = now ?? DateTime.Now;
        var account = Get(role);

        if (account.LockedUntil is { } until && until > stamp)
        {
            return LoginResult.Fail(
                $"{role} 因连续输错被临时锁定，请等 {Math.Ceiling((until - stamp).TotalSeconds):F0} 秒后再试",
                until - stamp);
        }

        if (!account.HasPassword) return LoginResult.Success($"{role} 不需要口令");

        if (PasswordHasher.Verify(password, account.PasswordHash))
        {
            account.FailedAttempts = 0;
            account.LockedUntil = null;
            account.LastLoginAt = stamp;
            return LoginResult.Success($"已切换为 {role}");
        }

        account.FailedAttempts++;

        int max = Math.Max(1, Options.MaxFailedAttempts);
        if (account.FailedAttempts >= max)
        {
            account.LockedUntil = stamp.AddSeconds(Math.Max(1, Options.LockoutSeconds));
            account.FailedAttempts = 0;
            return LoginResult.Fail(
                $"口令错误 {max} 次，已锁定 {Options.LockoutSeconds} 秒（现场没有 IT 值守，所以只临时锁定，不会永久锁死）");
        }

        return LoginResult.Fail($"口令错误（第 {account.FailedAttempts} 次，连续错 {max} 次会临时锁定）");
    }

    /// <summary>改口令。旧口令必须对；新口令要够长、两次要一致。</summary>
    public PasswordChangeResult ChangePassword(string role, string? oldPassword, string? newPassword, string? confirm, DateTime? now = null)
    {
        var account = Get(role);

        if (account.HasPassword && !PasswordHasher.Verify(oldPassword, account.PasswordHash))
            return PasswordChangeResult.Fail("原口令不对");

        if (string.IsNullOrWhiteSpace(newPassword))
            return PasswordChangeResult.Fail("新口令不能为空");

        int min = Math.Max(1, Options.MinPasswordLength);
        if (newPassword.Length < min)
            return PasswordChangeResult.Fail($"新口令至少 {min} 位");

        if (newPassword != confirm)
            return PasswordChangeResult.Fail("两次输入的新口令不一致");

        if (string.Equals(newPassword, DefaultPasswordOf(role), StringComparison.Ordinal))
            return PasswordChangeResult.Fail("新口令不能和出厂默认口令一样");

        account.PasswordHash = PasswordHasher.Hash(newPassword);
        account.IsDefaultPassword = false;
        account.PasswordUpdatedAt = now ?? DateTime.Now;
        account.FailedAttempts = 0;
        account.LockedUntil = null;

        return PasswordChangeResult.Success($"{role} 口令已更新");
    }

    /// <summary>把某个角色恢复成出厂默认口令（工程师用：现场把口令忘了时的补救手段）。</summary>
    public void ResetToDefault(string role)
    {
        var account = Get(role);
        string password = DefaultPasswordOf(role);
        account.PasswordHash = password.Length == 0 ? string.Empty : PasswordHasher.Hash(password);
        account.IsDefaultPassword = true;
        account.FailedAttempts = 0;
        account.LockedUntil = null;
    }
}
