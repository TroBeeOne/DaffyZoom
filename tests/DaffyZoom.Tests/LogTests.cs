using System.Collections.Concurrent;
using System.Diagnostics;

namespace DaffyZoom.Tests;

/// <summary>
/// The log's console and file are written on threads of their own: whoever logs never waits for
/// them, lines arrive in order, a stalled one drops what it cannot keep and says so, and --quiet
/// leaves each session's own lines off the console.
/// </summary>
public sealed class LogTests
{
    private static LogEntry Entry(LogLevel level, int n, string code = "test.line", int session = 0) =>
        new(DateTime.UtcNow, level, code, session, $"line {n}");

    [Fact]
    public void LinesArriveInOrderAndAFlushWaitsForThem()
    {
        var written = new List<string>();
        var queue = new LogQueue("test", entries =>
        {
            Thread.Sleep(1);                       // a slowish disk: lines pile up into batches meanwhile
            lock (written) written.AddRange(entries.Select(e => e.Message));
        });

        for (int i = 0; i < 5000; i++) queue.Add(Entry(LogLevel.Info, i));
        Assert.True(queue.Flush(TimeSpan.FromSeconds(10)));

        lock (written) Assert.Equal(Enumerable.Range(0, 5000).Select(i => $"line {i}"), written);
        Assert.Equal(0, queue.Dropped);
    }

    /// <summary>
    /// A console nobody is reading - a Windows console window clicked into stops every write until a key
    /// is pressed. Logging goes on at once; past the soft limit Debug and Info lines are left out, past the
    /// hard limit everything is; and once the console moves again it is told how many.
    /// </summary>
    [Fact]
    public void AStalledOutputNeverHoldsUpWhoeverLogs()
    {
        using var stalled = new ManualResetEventSlim();
        var written = new ConcurrentQueue<LogEntry>();
        var queue = new LogQueue("test", entries =>
        {
            stalled.Wait();
            foreach (var entry in entries) written.Enqueue(entry);
        }, softLimit: 100, hardLimit: 200);

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) queue.Add(Entry(LogLevel.Info, i));
        for (int i = 0; i < 300; i++) queue.Add(Entry(LogLevel.Warn, i));
        Assert.True(clock.ElapsedMilliseconds < 2000, $"logging took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(900 + 200, queue.Dropped);

        stalled.Set();
        Assert.True(queue.Flush(TimeSpan.FromSeconds(10)));
        Assert.Equal(100, written.Count(e => e.Level == LogLevel.Info));
        Assert.Equal(100, written.Count(e => e.Level == LogLevel.Warn && e.Code == "test.line"));
        var note = Assert.Single(written, e => e.Code == "log.dropped");
        Assert.Contains(1100.ToString("N0"), note.Message);
    }

    [Fact]
    public void AnOutputThatThrowsIsNotTheLoggersProblem()
    {
        int calls = 0;
        var queue = new LogQueue("test", _ =>
        {
            Interlocked.Increment(ref calls);
            throw new IOException("the disk is full");
        });
        queue.Add(Entry(LogLevel.Error, 1));
        Assert.True(queue.Flush(TimeSpan.FromSeconds(10)));
        queue.Add(Entry(LogLevel.Error, 2));
        Assert.True(queue.Flush(TimeSpan.FromSeconds(10)));
        Assert.True(calls >= 2);
    }

    /// <summary>--quiet: "log startup and warnings, not every session" - each session's own lines stay off the console.</summary>
    [Fact]
    public void QuietLeavesEachSessionsOwnLinesOffTheConsole()
    {
        var everyone = new[]
        {
            Entry(LogLevel.Debug, 1, "session.accepted", 3), Entry(LogLevel.Info, 2, "session.open", 3),
            Entry(LogLevel.Info, 3, "cache.flush"), Entry(LogLevel.Info, 4, "admin.command", 3),
            Entry(LogLevel.Warn, 5, "session.login_refused", 3), Entry(LogLevel.Error, 6, "cache.sweep_failed"),
        };
        Assert.All(everyone, entry => Assert.True(Log.ForConsole(entry, quiet: false)));
        Assert.Equal(new[] { "line 3", "line 4", "line 5", "line 6" }, everyone.Where(e => Log.ForConsole(e, quiet: true)).Select(e => e.Message));
    }

    [Fact]
    public void AConsoleLineReadsAsItAlwaysHas()
    {
        var at = new DateTime(2026, 11, 14, 9, 30, 15, DateTimeKind.Utc);
        string time = at.ToLocalTime().ToString("HH:mm:ss");
        Assert.Equal($"{time} [s7] warning: login refused", Log.ConsoleLine(new LogEntry(at, LogLevel.Warn, "session.login_refused", 7, "login refused")));
        Assert.Equal($"{time} stopped", Log.ConsoleLine(new LogEntry(at, LogLevel.Info, "proxy.stopped", 0, "stopped")));
    }

    [Fact]
    public void TheLogFileTakesABatchInOneWrite()
    {
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "zoom.log");
        using (var file = new RollingFileWriter(path))
        {
            file.WriteBatch(["one\n", "two\n"]);
            file.WriteBatch(["three\n"]);
        }
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(path));
    }
}
