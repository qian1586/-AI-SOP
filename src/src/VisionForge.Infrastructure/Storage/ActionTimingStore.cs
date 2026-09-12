using System.Text;
using System.Text.Json;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// 动作计时记录的落盘：一天一个 JSONL 文件（`data\timings\yyyy-MM-dd.jsonl`）。
///
/// <para><b>为什么是 JSONL 而不是一个大 JSON：</b>这套数据是"每做一步追加一行"，
/// 一天几千到几万行。JSONL 追加是 O(1)，断电最多丢最后一行；
/// 而每次读改写一个大 JSON，既慢又容易在断电时把历史全毁掉。</para>
///
/// <para>查询、回看、趋势图都从这些文件来 —— 数据永远在本地，不依赖任何服务。</para>
/// </summary>
public static class ActionTimingStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DirectoryOf(string dataRoot) => Path.Combine(dataRoot, "timings");

    /// <summary>追加一条。失败只返回 false，由调用方写日志（不抛出去打断生产）。</summary>
    public static bool Append(ActionTimingRecord record, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, record.FinishedAt.ToString("yyyy-MM-dd") + ".jsonl");

            // 追加写 + 立即落盘：断电最多丢这一条，不会毁掉当天的历史
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(record, Options));
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按时间段读取（含起止当天）。坏行跳过，不让一行坏数据毁掉整段历史。</summary>
    public static List<ActionTimingRecord> Load(string directory, DateTime from, DateTime to)
    {
        var list = new List<ActionTimingRecord>();
        if (!Directory.Exists(directory)) return list;

        DateTime start = from.Date;
        DateTime end = to.Date;

        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParse(name, out var day) && (day.Date < start || day.Date > end)) continue;

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<ActionTimingRecord>(line, Options);
                        if (rec is not null) list.Add(rec);
                    }
                    catch { /* 跳过坏行 */ }
                }
            }
            catch { /* 单个文件读不了不影响其它天 */ }
        }

        return list.OrderBy(r => r.FinishedAt).ToList();
    }

    /// <summary>导出 CSV（给工艺/IE 用 Excel 做进一步分析）。</summary>
    public static void ExportCsv(IEnumerable<ActionTimingRecord> records, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("完成时刻,件号,批次,产品型号,操作员,工序号,工序名,用时秒,上次用时秒,标准工时秒,与上次差秒,与标准差秒,快慢,判定,配方");

        foreach (var r in records)
        {
            sb.Append(Csv(r.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',');
            sb.Append(Csv(r.PieceId)).Append(',');
            sb.Append(Csv(r.BatchNo)).Append(',');
            sb.Append(Csv(r.ProductModel)).Append(',');
            sb.Append(Csv(r.Operator)).Append(',');
            sb.Append(r.StepSeq).Append(',');
            sb.Append(Csv(r.StepName)).Append(',');
            sb.Append(r.DurationSec.ToString("F2")).Append(',');
            sb.Append(r.PreviousDurationMs is null ? "" : (r.PreviousDurationMs.Value / 1000.0).ToString("F2")).Append(',');
            sb.Append(r.StandardMs is null ? "" : (r.StandardMs.Value / 1000.0).ToString("F2")).Append(',');
            sb.Append(r.DeltaMs is null ? "" : (r.DeltaMs.Value / 1000.0).ToString("F2")).Append(',');
            sb.Append(r.DeltaVsStandardMs is null ? "" : (r.DeltaVsStandardMs.Value / 1000.0).ToString("F2")).Append(',');
            sb.Append(Csv(r.PaceText)).Append(',');
            sb.Append(r.VerdictText).Append(',');
            sb.Append(Csv(r.RecipeName));
            sb.AppendLine();
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
