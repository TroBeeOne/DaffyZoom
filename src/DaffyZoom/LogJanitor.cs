using System.Globalization;

namespace DaffyZoom;

/// <summary>
/// --log-retention (default 24h): DaffyZoom's own files on disk do not pile up. Looked at when
/// DaffyZoom starts and every hour after:
///
/// - a --log-file named with {date} has a file a day, and each is deleted once nothing has been
///   written to it for the retention;
/// - a --log-file without {date} is one file, moved to NAME.old (replacing the one before) and
///   started afresh once its first line is older than the retention;
/// - transcripts (--transcript), which hold SQL in the clear, are deleted once nothing has been
///   written to them for the retention.
///
/// Only files of DaffyZoom's own naming are touched, never anything else in those directories. A
/// file that cannot be deleted - open elsewhere, no permission - is left for the next time. The
/// cache directory is not a log: it is emptied at every start and kept within its own limits.
/// </summary>
internal static class LogJanitor
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>24h, 7d, 90m; off or 0 keeps everything. Null for anything else: a bare number would leave the unit to a guess.</summary>
    public static TimeSpan? ParseRetention(string text)
    {
        string value = text.Trim().ToLowerInvariant();
        if (value is "off" or "0" or "never" or "none") return TimeSpan.Zero;
        if (value.Length < 2) return null;

        double factor = value[^1] switch { 'd' => 86400, 'h' => 3600, 'm' => 60, _ => 0 };
        if (factor == 0 || !double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
            || amount <= 0 || double.IsInfinity(amount) || amount * factor > TimeSpan.MaxValue.TotalSeconds / 2)
            return null;
        return TimeSpan.FromSeconds(amount * factor);
    }

    public static string Describe(TimeSpan retention) =>
        retention <= TimeSpan.Zero ? "off: log files and transcripts are kept"
        : retention.TotalDays >= 2 && retention.TotalDays % 1 == 0 ? $"{retention.TotalDays:0}d"
        : retention.TotalHours >= 1 && retention.TotalHours % 1 == 0 ? $"{retention.TotalHours:0}h"
        : $"{retention.TotalMinutes:0}m";

    public static async Task RunAsync(TimeSpan retention, RollingFileWriter? logFile, string? transcripts, CancellationToken cancellationToken)
    {
        if (retention <= TimeSpan.Zero || (logFile is null && transcripts is null)) return;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Clean(retention, logFile, transcripts, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Log.Warn("log.clean_failed", $"old log files could not be cleaned: {ex.Message}");
            }

            try { await Task.Delay(Interval, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One look. Returns how many files were deleted, and whether the single log file was started afresh.</summary>
    internal static (int Deleted, bool Restarted) Clean(TimeSpan retention, RollingFileWriter? logFile, string? transcripts, DateTime nowUtc)
    {
        if (retention <= TimeSpan.Zero) return (0, false);
        var cutoff = nowUtc - retention;
        int deleted = 0;
        bool restarted = false;

        if (logFile is not null)
        {
            if (logFile.Rolls)
            {
                deleted += DeleteOlder(logFile.DailyFiles(), cutoff, except: logFile.CurrentPath);
            }
            else if (logFile.FirstEntryUtc() is { } first && first < cutoff)
            {
                logFile.Restart();
                restarted = true;
            }
        }

        if (transcripts is not null && Directory.Exists(transcripts))
            deleted += DeleteOlder(Directory.EnumerateFiles(transcripts, "session-*.txt").Where(IsTranscript).ToList(), cutoff, except: null);

        string kept = Describe(retention);
        if (restarted) Log.Info("log.restarted", $"the log file was started afresh; the one before is {logFile!.CurrentPath}.old (--log-retention {kept})");
        if (deleted > 0) Log.Info("log.cleaned", $"deleted {deleted} log or transcript file(s) not written to for {kept} (--log-retention {kept})");
        return (deleted, restarted);
    }

    /// <summary>A transcript's own name: session-yyyyMMdd-HHmmss-NNNN.txt.</summary>
    private static bool IsTranscript(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^session-\d{8}-\d{6}-\d{4,}\.txt$",
                                                     System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static int DeleteOlder(IReadOnlyList<string> files, DateTime cutoffUtc, string? except)
    {
        int deleted = 0;
        foreach (string file in files)
        {
            if (except is not null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(except), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoffUtc) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open elsewhere, or not ours to delete: the next look tries again.
            }
        }
        return deleted;
    }
}
