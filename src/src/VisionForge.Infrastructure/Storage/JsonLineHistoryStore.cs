using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// 检测历史仓储（JSON Lines 实现）。
///
/// 为什么用 JSONL 而不是 SQLite：
///   · 零依赖（SQLite 要引 Microsoft.Data.Sqlite，而本项目坚持零 NuGet）
///   · append-only 写入极快，不会因为锁表阻塞产线
///   · 文本格式，出问题时可以用记事本直接看，不需要数据库工具
///   · 单条记录损坏只影响那一行，不会像数据库那样整个文件打不开
///
/// <b>什么时候该换成 SQLite：</b>
/// 当单条产线一天记录超过 5 万条、历史累积超过 50 万条，
/// 或者需要"按任意字段组合查询 + 分页 + 聚合"时，JSONL 的全量扫描会开始吃力。
/// 那时把本类替换成 SQLite 实现即可 —— 因为上层只依赖 IInspectionHistoryStore，
/// 换存储不影响任何业务代码。这就是当初定接口的价值。
/// </summary>
public sealed class JsonLineHistoryStore : IInspectionHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, List<InspectionRecord>> _cache = new();

    public JsonLineHistoryStore(string directory, string ngImageDirectory)
    {
        HistoryDirectory = directory;
        NgImageDirectory = ngImageDirectory;
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(ngImageDirectory);
    }

    public string HistoryDirectory { get; }

    public string NgImageDirectory { get; }

    // ------------------------------------------------------------------
    public async Task<long> SaveAsync(InspectionRecord record, CancellationToken ct = default)
    {
        record.Id = DateTime.Now.Ticks;

        var path = PathForDay(record.Timestamp);
        var line = JsonSerializer.Serialize(record, Options);

        await _writeGate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, Encoding.UTF8, ct);
        }
        finally
        {
            _writeGate.Release();
        }

        // 同步进内存缓存，省得马上查询时又去读盘
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(path, out var list)) list.Add(record);
        }

        return record.Id;
    }

    public async Task<IReadOnlyList<InspectionRecord>> QueryAsync(
        HistoryQuery query, CancellationToken ct = default)
    {
        var result = new List<InspectionRecord>();

        foreach (var file in EnumerateFiles(query.From, query.To))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var rec in await ReadFileCachedAsync(file, ct))
            {
                if (!Matches(rec, query)) continue;
                result.Add(rec);
                if (result.Count >= query.Limit) goto done;
            }
        }

    done:
        return result.OrderByDescending(r => r.Timestamp).ToList();
    }

    public async Task<int> CountAsync(HistoryQuery? query = null, CancellationToken ct = default)
    {
        query ??= new HistoryQuery { Limit = int.MaxValue };
        var effective = new HistoryQuery
        {
            From = query.From, To = query.To, BatchNo = query.BatchNo,
            ProductModel = query.ProductModel, RecipeId = query.RecipeId,
            Verdict = query.Verdict, Limit = int.MaxValue,
        };

        var all = await QueryAsync(effective, ct);
        return all.Count;
    }

    public async Task ExportCsvAsync(
        HistoryQuery query, string filePath, CancellationToken ct = default)
    {
        var records = await QueryAsync(query, ct);

        var sb = new StringBuilder();
        sb.AppendLine("时间,配方,产品型号,批次号,操作员,结论,判断理由,耗时ms,测量数据,缺陷");

        foreach (var r in records)
        {
            sb.Append(Csv(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))).Append(',');
            sb.Append(Csv(r.RecipeName)).Append(',');
            sb.Append(Csv(r.ProductModel)).Append(',');
            sb.Append(Csv(r.BatchNo)).Append(',');
            sb.Append(Csv(r.Operator)).Append(',');
            sb.Append(Csv(r.Verdict.ToString())).Append(',');
            sb.Append(Csv(r.JudgementReason)).Append(',');
            sb.Append(r.ElapsedMs.ToString("F1")).Append(',');
            sb.Append(Csv(string.Join(" | ", r.Measurements.Select(m => m.Display)))).Append(',');
            sb.Append(Csv(string.Join(" | ", r.Defects.Select(d => d.ToString()))));
            sb.AppendLine();
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 关键：必须带 BOM。否则 Excel 打开 CSV 时中文全是乱码，
        // 这是工控软件被投诉最多的小问题之一。
        await File.WriteAllTextAsync(filePath, sb.ToString(), new UTF8Encoding(true), ct);
    }

    public async Task<IReadOnlyList<StatisticsItem>> StatisticsAsync(
        DateTime from, DateTime to, StatisticsDimension dimension, CancellationToken ct = default)
    {
        var records = await QueryAsync(
            new HistoryQuery { From = from, To = to, Limit = int.MaxValue }, ct);

        Func<InspectionRecord, string> keySelector = dimension switch
        {
            StatisticsDimension.ByProductModel => r => r.ProductModel,
            StatisticsDimension.ByRecipe => r => r.RecipeName,
            StatisticsDimension.ByBatch => r => r.BatchNo,
            StatisticsDimension.ByDay => r => r.Timestamp.ToString("yyyy-MM-dd"),
            StatisticsDimension.ByOperator => r => r.Operator,
            _ => r => "全部",
        };

        return records
            .GroupBy(keySelector)
            .Select(g => new StatisticsItem
            {
                Key = string.IsNullOrWhiteSpace(g.Key) ? "(未填写)" : g.Key,
                Total = g.Count(),
                Pass = g.Count(r => r.Verdict == InspectionVerdict.Pass),
                Fail = g.Count(r => r.Verdict == InspectionVerdict.Fail),
                AverageElapsedMs = Math.Round(g.Average(r => r.ElapsedMs), 1),
            })
            .OrderByDescending(s => s.Total)
            .ToList();
    }

    // ------------------------------------------------------------------
    private string PathForDay(DateTime ts) =>
        Path.Combine(HistoryDirectory, $"{ts:yyyy-MM-dd}.jsonl");

    private IEnumerable<string> EnumerateFiles(DateTime? from, DateTime? to)
    {
        if (!Directory.Exists(HistoryDirectory)) yield break;

        // 直接枚举目录里的 .jsonl，再按文件名中的日期过滤。
        //
        // 这里修掉一个真实缺陷：以前是"从 from 逐天拼文件名到 to"，
        // 而没给时间范围时 from/to 取的是 DateTime.MinValue / MaxValue，
        // 又被下面那句"最多扫 10 年"的防御一夹，搜索区间落到了公元 1~11 年 ——
        // 于是 CountAsync() 和"不带时间条件"的 QueryAsync() 永远返回空。
        // 界面上看不出来，只是历史表一直空着，像没有数据，其实是查询范围错了。
        var start = from?.Date ?? DateTime.MinValue;
        var end = to?.Date ?? DateTime.MaxValue;

        foreach (var file in Directory.EnumerateFiles(HistoryDirectory, "*.jsonl")
                                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParse(name, out var day) && (day.Date < start || day.Date > end))
                continue;

            yield return file;
        }
    }

    private async Task<List<InspectionRecord>> ReadFileCachedAsync(string path, CancellationToken ct)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(path, out var cached)) return cached;
        }

        var list = new List<InspectionRecord>();
        try
        {
            var lines = await File.ReadAllLinesAsync(path, ct);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<InspectionRecord>(line, Options);
                    if (rec is not null) list.Add(rec);
                }
                catch
                {
                    // 跳过损坏行，不影响其他记录
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] 读取 {path} 失败: {ex.Message}");
        }

        lock (_cacheGate)
        {
            _cache[path] = list;
        }
        return list;
    }

    private static bool Matches(InspectionRecord r, HistoryQuery q)
    {
        if (q.From is { } f && r.Timestamp < f) return false;
        if (q.To is { } t && r.Timestamp > t) return false;
        if (!string.IsNullOrWhiteSpace(q.BatchNo) &&
            !string.Equals(r.BatchNo, q.BatchNo, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(q.ProductModel) &&
            !string.Equals(r.ProductModel, q.ProductModel, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(q.RecipeId) &&
            !string.Equals(r.RecipeId, q.RecipeId, StringComparison.Ordinal)) return false;
        if (q.Verdict is { } v && r.Verdict != v) return false;
        return true;
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // 含逗号/引号/换行时必须加引号并转义，否则 Excel 会错列
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
