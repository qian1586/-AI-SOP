using System.Collections.Concurrent;
using System.Text;
using VisionForge.Core.Interfaces;

namespace VisionForge.Infrastructure.Logging;

/// <summary>
/// 文件日志。按天分文件，后台线程写入，不阻塞检测主流程。
///
/// 设计取舍说明：
///   · 用 <see cref="BlockingCollection{T}"/> + 单后台线程，而不是每条日志直接写文件。
///     因为检测节拍可能到 100ms 一次，同步写盘在机械盘上会造成明显抖动。
///   · 队列满时<b>丢弃</b>日志而不是阻塞。宁可丢日志也不能卡产线 ——
///     这个取舍在工控软件里是基本原则，业务优先级永远高于可观测性。
///   · 同时输出到 Debug/Console，开发期不用去翻文件。
/// </summary>
public sealed class FileLogger : ILogger, IDisposable
{
    private readonly BlockingCollection<string> _queue =
        new(new ConcurrentQueue<string>(), 8192);

    private readonly Task _writerTask;
    private readonly string _directory;
    private readonly object _fileGate = new();
    private long _dropped;
    private bool _disposed;

    public FileLogger(string directory, bool echoToConsole = true)
    {
        _directory = directory;
        EchoToConsole = echoToConsole;
        Directory.CreateDirectory(_directory);

        _writerTask = Task.Factory.StartNew(
            WriterLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public bool EchoToConsole { get; set; }

    /// <summary>最低输出级别。</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>因为队列满而丢弃的日志条数。持续增长说明磁盘写入跟不上。</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    // ------------------------------------------------------------------
    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error, exception is null ? message : $"{message}\n{exception}");

    private void Write(LogLevel level, string message)
    {
        if (_disposed || level < MinimumLevel) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag(level)}] {message}";

        if (EchoToConsole)
        {
            var color = level switch
            {
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Debug => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray,
            };
            var old = Console.ForegroundColor;
            try { Console.ForegroundColor = color; Console.WriteLine(line); }
            finally { Console.ForegroundColor = old; }
        }

        // 满了就丢，绝不阻塞产线
        if (!_queue.TryAdd(line))
            Interlocked.Increment(ref _dropped);
    }

    // ------------------------------------------------------------------
    private void WriterLoop()
    {
        var sb = new StringBuilder();
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                // 批量攒一下再写，减少磁盘 IO 次数
                sb.Clear().AppendLine(line);
                while (sb.Length < 32 * 1024 && _queue.TryTake(out var more, 50))
                    sb.AppendLine(more);

                var path = Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.log");
                lock (_fileGate)
                {
                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // 日志系统自己出错时不能连锁崩掉，吞掉即可
                System.Diagnostics.Debug.WriteLine($"[FileLogger] 写入失败: {ex.Message}");
            }
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "???",
    };

    /// <summary>把队列里剩余日志刷完再退出。程序关闭前应调用。</summary>
    public void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (_queue.Count > 0 && DateTime.Now < deadline)
            Thread.Sleep(20);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();
        try { _writerTask.Wait(TimeSpan.FromSeconds(3)); } catch { /* 退出时忽略 */ }
        _queue.Dispose();
    }
}

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}
