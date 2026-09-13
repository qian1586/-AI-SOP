using System.Text;
using System.Text.Json;
using VisionForge.Core.Services;

namespace VisionForge.Infrastructure.Storage;

/// <summary>一条权限审计记录：谁在什么时候把角色切成了什么、成没成。</summary>
public sealed class AccessAuditEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>角色切换 / 口令修改 / 恢复默认 / 自动降级。</summary>
    public string Action { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public string Detail { get; set; } = string.Empty;

    public string Display =>
        $"{Timestamp:MM-dd HH:mm:ss} · {Action} · {Role} · {(Ok ? "成功" : "失败")} · {Detail}";
}

/// <summary>
/// 账号与审计的落盘。
///
/// <para><b>为什么账号单独一个文件，不塞进 appsettings.json：</b>
/// appsettings.json 是现场会随手编辑、也可能整份拷给别人参考的配置；
/// 口令哈希再安全也不该混在里面。放到 <c>data\security\accounts.json</c>，
/// 备份、清理、权限收敛都好办。</para>
/// </summary>
public static class SecurityStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DirectoryOf(string dataRoot) => Path.Combine(dataRoot, "security");

    public static string AccountsPath(string dataRoot) => Path.Combine(DirectoryOf(dataRoot), "accounts.json");

    /// <summary>读账号。文件不存在或坏了都不抛异常：返回一份出厂账号，保证程序能起来。</summary>
    public static List<RoleAccount> Load(string dataRoot)
    {
        string path = AccountsPath(dataRoot);
        if (!File.Exists(path)) return AccessControlService.CreateDefaultAccounts();

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var list = JsonSerializer.Deserialize<List<RoleAccount>>(json, Options);
            return list is { Count: > 0 } ? list : AccessControlService.CreateDefaultAccounts();
        }
        catch
        {
            // 坏文件先备份再回默认：现场最怕的是"配置一坏，工程师也进不去"
            try { File.Move(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true); }
            catch { }

            return AccessControlService.CreateDefaultAccounts();
        }
    }

    public static void Save(IEnumerable<RoleAccount> accounts, string dataRoot)
    {
        string path = AccountsPath(dataRoot);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(accounts.ToList(), Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>追加一条审计（一天一个 JSONL，追加写，断电最多丢一条）。</summary>
    public static void AppendAudit(AccessAuditEntry entry, string dataRoot)
    {
        try
        {
            string dir = DirectoryOf(dataRoot);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, entry.Timestamp.ToString("yyyy-MM-dd") + ".jsonl");

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(entry, LineOptions));
            writer.Flush();
        }
        catch
        {
            // 审计写不进去也不能影响现场作业
        }
    }

    /// <summary>读最近 N 条审计（配置页上给人看）。</summary>
    public static List<AccessAuditEntry> LoadRecentAudit(string dataRoot, int limit = 100)
    {
        var list = new List<AccessAuditEntry>();
        string dir = DirectoryOf(dataRoot);
        if (!Directory.Exists(dir)) return list;

        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl")
                                      .OrderByDescending(f => f, StringComparer.Ordinal)
                                      .Take(7))
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<AccessAuditEntry>(line, LineOptions);
                        if (rec is not null) list.Add(rec);
                    }
                    catch { /* 跳过坏行 */ }
                }
            }
            catch { /* 单个文件坏了不影响其它天 */ }
        }

        return list.OrderByDescending(e => e.Timestamp).Take(limit).ToList();
    }
}
