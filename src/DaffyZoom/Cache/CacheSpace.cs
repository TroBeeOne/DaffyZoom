namespace DaffyZoom.Cache;

/// <summary>
/// How large the cache may be: the smaller of --cache-max-gb and what its disk holds while keeping
/// --cache-min-free-gb free - by default the larger of 1 GB and 5% of the disk - whatever else is
/// filling that disk. The sweeper trims to it, and a new answer
/// makes room in it before it is written. Entries saving the least time per byte go first, and a new
/// answer only ever displaces entries worth less than itself: a cache full of valuable answers is
/// never emptied for one that saves little.
///
/// The cache's own size plus the disk's free space is what eviction can work with, and evicting does
/// not change it, so the allowance is measured once per decision. A disk DaffyZoom cannot measure (a
/// share) leaves the size cap alone in force.
/// </summary>
internal sealed class CacheSpace(CacheOptions options, CacheIndex index, string directory,
                                 Func<string, (long Available, long Total)?>? measure = null)
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private static readonly TimeSpan ShortLogInterval = TimeSpan.FromMinutes(10);

    private readonly Func<string, (long Available, long Total)?> _measure = measure ?? MeasureDisk;
    private readonly object _gate = new();
    private DateTime _lastShortLog = DateTime.MinValue;

    /// <summary>The disk's free space and size, or null when it cannot be told.</summary>
    public (long Available, long Total)? Disk => _measure(directory);

    /// <summary>What is kept free on a disk of this size.</summary>
    public long MinFree(long total) => options.CacheMinFreeBytes ?? Math.Max(Gigabyte, total / 20);

    /// <summary>How large the cache may be now: never more than the cap, never so large the disk keeps less than its minimum free.</summary>
    public long Allowance()
    {
        long allowance = options.CacheMaxBytes;
        if (Disk is { } disk) allowance = Math.Min(allowance, index.FileBytes + disk.Available - MinFree(disk.Total));
        return Math.Max(0, allowance);
    }

    /// <summary>The sweeper's trim: over 90% of the allowance, the entries worth least per byte go, down to 75%. Returns how many went.</summary>
    public int Trim()
    {
        lock (_gate)
        {
            long allowance = Allowance();
            if (index.FileBytes <= allowance * 0.90) return 0;
            int evicted = EvictTo((long)(allowance * 0.75), double.MaxValue);
            if (allowance < options.CacheMaxBytes) ShortOfSpace($"{evicted} entr{(evicted == 1 ? "y" : "ies")} made way");
            return evicted;
        }
    }

    /// <summary>
    /// Room for a new answer of about <paramref name="bytes"/>, saving <paramref name="benefit"/> ms per
    /// byte: true when it fits, at once or once entries worth less have gone (down to 75% of the
    /// allowance, as the sweeper would). False when it cannot fit - and then nothing has been evicted.
    /// </summary>
    public bool MakeRoom(long bytes, double benefit)
    {
        lock (_gate)
        {
            long allowance = Allowance();
            if (index.FileBytes + bytes <= allowance) return true;

            long evictable = index.Entries.Where(e => e.Benefit < benefit).Sum(e => e.FileBytes);
            if (index.FileBytes - evictable + bytes > allowance)
            {
                if (allowance < options.CacheMaxBytes) ShortOfSpace("an answer was not cached");
                return false;
            }

            EvictTo(Math.Min((long)(allowance * 0.75), allowance - bytes), benefit);
            return index.FileBytes + bytes <= allowance;
        }
    }

    /// <summary>Whether the disk now has less room than an answer of <paramref name="bytes"/> needs: after a write failed, whether space is why.</summary>
    public bool ShortOf(long bytes) => Disk is { } disk && disk.Available - bytes < MinFree(disk.Total);

    /// <summary>For the prompt: the allowance and what limits it.</summary>
    public string Describe()
    {
        long allowance = Allowance();
        if (Disk is not { } disk) return $"{Size(allowance)} allowed (--cache-max-gb; the disk's free space cannot be measured)";
        string limit = allowance < options.CacheMaxBytes
            ? $"its disk keeps {Size(MinFree(disk.Total))} free (--cache-min-free-gb)"
            : "--cache-max-gb";
        return $"{Size(allowance)} allowed by {limit}; {Size(disk.Available)} free on its disk";
    }

    private int EvictTo(long target, double below)
    {
        int evicted = 0;
        foreach (var entry in index.Entries.OrderBy(e => e.Benefit).ToList())
        {
            if (index.FileBytes <= target || entry.Benefit >= below) break;
            if (index.Remove(entry)) evicted++;
        }
        return evicted;
    }

    private void ShortOfSpace(string what)
    {
        var now = DateTime.UtcNow;
        if (now - _lastShortLog < ShortLogInterval) return;
        _lastShortLog = now;
        var disk = Disk;
        Log.Warn("cache.disk_short",
            $"the cache's disk is short of space ({(disk is { } d ? $"{Size(d.Available)} free, {Size(MinFree(d.Total))} kept free" : "unmeasured")}; " +
            $"the cache holds {Size(index.FileBytes)}): {what}. Logged at most every {ShortLogInterval.TotalMinutes:F0} minutes");
    }

    /// <summary>
    /// The free space and size of the disk holding <paramref name="path"/>: its drive on Windows, the
    /// longest mount point above it elsewhere. Null for a network share, or when it cannot be read.
    /// </summary>
    internal static (long Available, long Total)? MeasureDisk(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
            {
                string? root = Path.GetPathRoot(full);
                if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                var drive = new DriveInfo(root);
                return (drive.AvailableFreeSpace, drive.TotalSize);
            }

            var best = DriveInfo.GetDrives()
                .Where(d => d.IsReady && Under(full, d.RootDirectory.FullName))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            return best is null ? null : (best.AvailableFreeSpace, best.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool Under(string path, string root)
    {
        string prefix = root.EndsWith('/') ? root : root + "/";
        return path == root || path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Size(long bytes) => bytes >= Gigabyte ? $"{bytes / (double)Gigabyte:F1} GB" : $"{bytes / (1024.0 * 1024):F0} MB";
}
