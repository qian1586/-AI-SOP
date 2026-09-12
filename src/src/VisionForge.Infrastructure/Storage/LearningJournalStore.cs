using System.Text;
using System.Text.Json;
using VisionForge.Core.Services;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// 一条"自学账":什么时候、哪个框、学了什么、为什么没学。
/// </summary>
public sealed class LearningJournalEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>固定写成字符串（"Auto"/"Corrected"），手改 JSON 时不至于看不懂。</summary>
    public string Source { get; set; } = SampleSource.Auto;

    /// <summary>Accepted / Reinforced / Rejected。</summary>
    public string Decision { get; set; } = "Rejected";

    public int RoiIndex { get; set; }

    public string RoiName { get; set; } = string.Empty;

    /// <summary>OK / NG（没学时也记，方便查"当时系统认为是什么"）。</summary>
    public string Label { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Display =>
        $"{Timestamp:MM-dd HH:mm:ss} · 第{RoiIndex}框 {Label} · " +
        $"{LearningJournalStore.DecisionText(Decision)} · {Reason}";
}

/// <summary>
/// 自学记录的落盘：一天一个 JSONL（`data\learning\yyyy-MM-dd.jsonl`）。
///
/// <para><b>为什么要专门记这个：</b>自主学习是"软件自己改自己的行为"，
/// 一旦现场觉得"识别怎么变了"，必须能回答"它什么时候学的、学的什么、为什么学"。
/// 没有这本账，自学习就是个黑盒，工控现场不可能接受。</para>
///
/// <para>另外这本账也是调参依据：拒绝理由扎堆在"置信度不够"，
/// 说明阈值偏高或样本太少；扎堆在"这一步还没完成"，说明工序时序需要先调。</para>
/// </summary>
public static class LearningJournalStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DirectoryOf(string dataRoot) => Path.Combine(dataRoot, "learning");

    public static string DecisionText(string decision) => decision switch
    {
        "Added" => "已收录",
        "Reinforced" => "已强化",
        "Corrected" => "已纠错",
        _ => "未收录",
    };

    /// <summary>追加一条。失败只返回 false（自学不能因为写不了日志就打断生产判定）。</summary>
    public static bool Append(LearningJournalEntry entry, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, entry.Timestamp.ToString("yyyy-MM-dd") + ".jsonl");

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(entry, Options));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读最近 N 条（界面上的"自学记录"列表）。坏行跳过。</summary>
    public static List<LearningJournalEntry> LoadRecent(string directory, int limit = 200)
    {
        var list = new List<LearningJournalEntry>();
        if (!Directory.Exists(directory)) return list;

        var files = Directory.EnumerateFiles(directory, "*.jsonl")
                             .OrderByDescending(f => f, StringComparer.Ordinal)
                             .Take(7)
                             .ToList();

        foreach (var file in files)
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<LearningJournalEntry>(line, Options);
                        if (rec is not null) list.Add(rec);
                    }
                    catch { /* 跳过坏行 */ }
                }
            }
            catch { /* 单文件坏了不影响其它天 */ }
        }

        return list.OrderByDescending(e => e.Timestamp).Take(limit).ToList();
    }
}
