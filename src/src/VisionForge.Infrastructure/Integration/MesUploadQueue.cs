using System.Text;
using System.Text.Json;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Integration;

/// <summary>
/// MES 上传队列（"存起来、慢慢发"）。
///
/// <para><b>为什么必须有它：</b>车间网络抖动、MES 重启、网线被叉车撞掉 ——
/// 这些都是常态。如果上传失败就把数据丢掉，那追溯链就断了：
/// 明明做了巡检、查不到记录，比没做还麻烦。</para>
///
/// <para>做法很朴素：上传失败就把这条 JSON 追加到当天的 pending 文件里，
/// 之后每隔一段时间重发；发成功就从文件里删掉。断电最多丢"正在发的那一条"。</para>
/// </summary>
public sealed class MesUploadQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory;
    private readonly ILogger? _log;
    private readonly object _gate = new();

    public MesUploadQueue(string directory, ILogger? log = null)
    {
        _directory = directory;
        _log = log;
    }

    /// <summary>待重发的条数（界面上显示，让现场知道"有没有积压"）。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                if (!Directory.Exists(_directory)) return 0;

                int count = 0;
                foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
                {
                    try { count += File.ReadLines(file).Count(l => !string.IsNullOrWhiteSpace(l)); }
                    catch { /* 读不了就不算 */ }
                }
                return count;
            }
        }
    }

    /// <summary>失败的数据先进队列（返回 false 表示连落盘都失败了，此时只能记日志）。</summary>
    public bool Enqueue(MesUploadPayload payload)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                string path = Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error("MES 待发队列写入失败（这条数据将丢失）：" + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 尝试把积压的数据发出去。返回本次成功条数。
    ///
    /// <para>遇到"不值得重试"的失败（4xx）会把它挪到 rejected 目录并继续下一条 ——
    /// 一条坏数据不能把后面所有数据堵死。</para>
    /// </summary>
    public async Task<int> FlushAsync(IMesClient client, int maxItems = 50, CancellationToken ct = default)
    {
        if (!client.IsEnabled || !Directory.Exists(_directory)) return 0;

        List<string> files;
        lock (_gate)
        {
            files = Directory.EnumerateFiles(_directory, "*.jsonl")
                             .OrderBy(f => f, StringComparer.Ordinal)
                             .ToList();
        }

        int sent = 0;

        foreach (var file in files)
        {
            List<string> lines;
            lock (_gate)
            {
                try { lines = File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToList(); }
                catch { continue; }
            }

            if (lines.Count == 0)
            {
                lock (_gate) { try { File.Delete(file); } catch { } }
                continue;
            }

            var remaining = new List<string>();

            foreach (var line in lines)
            {
                if (sent >= maxItems) { remaining.Add(line); continue; }

                MesUploadPayload? payload;
                try { payload = JsonSerializer.Deserialize<MesUploadPayload>(line, JsonOptions); }
                catch { payload = null; }

                if (payload is null) continue;   // 坏行直接丢弃

                var result = await client.UploadAsync(payload, ct).ConfigureAwait(false);
                if (result.Success) { sent++; continue; }

                if (!result.ShouldRetry)
                {
                    MoveToRejected(line, result.Message);
                    continue;
                }

                remaining.Add(line);   // 值得重试：留着下次再发
            }

            lock (_gate)
            {
                try
                {
                    if (remaining.Count == 0) File.Delete(file);
                    else File.WriteAllLines(file, remaining, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    _log?.Warn("MES 队列回写失败：" + ex.Message);
                }
            }
        }

        if (sent > 0) _log?.Info($"MES 待发队列已补发 {sent} 条，剩余 {PendingCount} 条");
        return sent;
    }

    /// <summary>把"对方明确不要"的数据挪到 rejected 目录，留证据给人排查（不删）。</summary>
    private void MoveToRejected(string line, string reason)
    {
        try
        {
            string dir = Path.Combine(_directory, "rejected");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"),
                line + Environment.NewLine, new UTF8Encoding(false));

            _log?.Warn("MES 拒绝接收一条数据（已挪到 rejected，不再重试）：" + reason);
        }
        catch { /* 挪不动就丢掉，不能因为它影响生产 */ }
    }
}
