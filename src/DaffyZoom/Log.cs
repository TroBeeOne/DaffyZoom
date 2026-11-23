using System.Collections.Concurrent;
using System.Text;

namespace DaffyZoom;

internal enum LogLevel { Debug, Info, Warn, Error }

/// <summary>One operational log line. <see cref="Code"/> is stable, so alerts can key on it.</summary>
internal readonly record struct LogEntry(DateTime TimestampUtc, LogLevel Level, string Code, int Session, string Message);

/// <summary>
/// DaffyZoom's own log - sessions, logins, the cache's health - for the people running it.
///
/// Every line carries a stable event code (session.open, upstream.login_failed, ...) alongside the
/// text a person reads, so the same call can feed the console and a machine-readable NDJSON file.
/// Console output goes through Console.Out on purpose: while the prompt owns the terminal, Console.Out
/// is the log buffer, and :log shows it on demand.
///
/// Logging never waits for the console or the disk. The console and the file are each written on a
/// thread of their own (<see cref="LogQueue"/>), so a session that logs only hands its line over, and
/// a console that stalls - a Windows console window someone has clicked into stops every write to it
/// until they press a key - holds up neither the sessions nor the file.
///
/// Passwords and connection strings never go through here.
/// </summary>
internal static class Log
{
    /// <summary>
    /// --quiet: the console leaves out every session's own lines - Debug, and session.* - keeping
    /// startup, the cache's lines, admin commands, warnings and errors. The file still gets everything.
    /// </summary>
    public static bool Quiet;

    private static readonly LogQueue ConsoleOutput = new("console", WriteConsole);
    private static volatile LogQueue? _file;

    /// <summary>
    /// The latest entries, Debug included even under --quiet, for daffyzoom.log. Kept whether or not
    /// a prompt is running, so a DaffyZoom started with --no-repl can still be asked what it has done.
    /// </summary>
    public static readonly RecentLog Recent = new();

    /// <summary>--log-file: every entry also goes to <paramref name="write"/>, in batches, on a thread of its own.</summary>
    public static void AttachFile(Action<IReadOnlyList<LogEntry>> write) => _file = new LogQueue("file", write);

    public static void Debug(string code, string message, int session = 0) => Write(LogLevel.Debug, code, message, session);
    public static void Info(string code, string message, int session = 0) => Write(LogLevel.Info, code, message, session);
    public static void Warn(string code, string message, int session = 0) => Write(LogLevel.Warn, code, message, session);
    public static void Error(string code, string message, int session = 0) => Write(LogLevel.Error, code, message, session);

    private static void Write(LogLevel level, string code, string message, int session)
    {
        var entry = new LogEntry(DateTime.UtcNow, level, code, session, message);
        Recent.Add(entry);
        if (ForConsole(entry, Quiet)) ConsoleOutput.Add(entry);
        _file?.Add(entry);
    }

    /// <summary>
    /// Waits until what has been logged so far is written, to the console and to the file, for at
    /// most <paramref name="timeout"/> each (2 s): before the startup banner, before the prompt takes
    /// the console over, and before DaffyZoom exits.
    /// </summary>
    public static void Flush(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        ConsoleOutput.Flush(limit);
        _file?.Flush(limit);
    }

    internal static bool ForConsole(LogEntry entry, bool quiet) =>
        !quiet || entry.Level >= LogLevel.Warn
        || (entry.Level == LogLevel.Info && !entry.Code.StartsWith("session.", StringComparison.Ordinal));

    internal static string ConsoleLine(LogEntry entry)
    {
        string marker = entry.Level switch { LogLevel.Warn => "warning: ", LogLevel.Error => "error: ", _ => "" };
        string prefix = entry.Session > 0 ? $"[s{entry.Session}] " : "";
        return $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss} {prefix}{marker}{entry.Message}";
    }

    /// <summary>A batch in one write: a console is slow per write, not per line.</summary>
    private static void WriteConsole(IReadOnlyList<LogEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries) text.Append(ConsoleLine(entry)).Append(Environment.NewLine);
        Console.Out.Write(text.ToString());
    }
}

/// <summary>
/// One place log lines go - the console, or the log file - written on a thread of its own, in batches
/// of whatever has come in since the last write. Whoever logs never waits: should the writing fall
/// <c>softLimit</c> lines behind, Debug and Info lines are left out, and at <c>hardLimit</c> every
/// line is, so a stalled console cannot fill the memory. When it catches up it says how many it
/// left out (log.dropped). Everything stays in <see cref="Log.Recent"/> regardless.
/// </summary>
internal sealed class LogQueue
{
    private readonly BlockingCollection<object> _items = new(new ConcurrentQueue<object>());
    private readonly Action<IReadOnlyList<LogEntry>> _write;
    private readonly int _softLimit, _hardLimit;
    private long _queued, _dropped, _droppedTotal;

    public LogQueue(string name, Action<IReadOnlyList<LogEntry>> write, int softLimit = 10_000, int hardLimit = 100_000)
    {
        _write = write;
        _softLimit = softLimit;
        _hardLimit = hardLimit;
        new Thread(Run) { IsBackground = true, Name = $"DaffyZoom log ({name})" }.Start();
    }

    /// <summary>Lines left out since the start.</summary>
    public long Dropped => Interlocked.Read(ref _droppedTotal);

    public void Add(LogEntry entry)
    {
        long queued = Interlocked.Read(ref _queued);
        if (queued >= _hardLimit || (queued >= _softLimit && entry.Level < LogLevel.Warn))
        {
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _droppedTotal);
            return;
        }
        Interlocked.Increment(ref _queued);
        _items.Add(entry);
    }

    /// <summary>Waits until every line added before this is written, for at most <paramref name="timeout"/>. False: it was not.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _items.Add(written);
        return written.Task.Wait(timeout);
    }

    private void Run()
    {
        var batch = new List<LogEntry>();
        var flushes = new List<TaskCompletionSource>();
        foreach (object first in _items.GetConsumingEnumerable())
        {
            Take(first);
            while (batch.Count < 1000 && _items.TryTake(out object? next)) Take(next);

            if (batch.Count > 0)
            {
                Write(batch);
                Interlocked.Add(ref _queued, -batch.Count);
                batch.Clear();
            }
            if (Interlocked.Exchange(ref _dropped, 0) is var dropped and > 0)
                Write([new LogEntry(DateTime.UtcNow, LogLevel.Warn, "log.dropped", 0,
                                    $"{dropped:N0} log line(s) were left out here: they came faster than they could be written")]);
            foreach (var flush in flushes) flush.TrySetResult();
            flushes.Clear();
        }

        void Take(object item)
        {
            if (item is LogEntry entry) batch.Add(entry);
            else flushes.Add((TaskCompletionSource)item);
        }
    }

    private void Write(IReadOnlyList<LogEntry> entries)
    {
        try { _write(entries); }
        catch { /* the log must never be the thing that breaks a session */ }
    }
}

/// <summary>The last thousand log entries, oldest first.</summary>
internal sealed class RecentLog(int capacity = 1000)
{
    private readonly Queue<LogEntry> _entries = new();
    private readonly object _gate = new();

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > capacity) _entries.Dequeue();
        }
    }

    public IReadOnlyList<LogEntry> Tail(int count)
    {
        lock (_gate) return _entries.Skip(Math.Max(0, _entries.Count - count)).ToList();
    }
}
