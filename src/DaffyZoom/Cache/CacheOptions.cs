namespace DaffyZoom.Cache;

/// <summary>
/// The cache's settings, fixed at startup. Every one of them can be set on the command line.
/// </summary>
internal sealed class CacheOptions
{
    /// <summary>A request at least this slow, first byte in to last byte out, is worth a look.</summary>
    public int SlowMs { get; init; } = 5000;

    /// <summary>The slow run of an exact request that fills the cache: 2 means the second, so the third is served.</summary>
    public int MinRuns { get; init; } = 2;

    /// <summary>A candidate that is not seen slow again within this long is forgotten.</summary>
    public TimeSpan CandidateWindow { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Tracked mode: how soon a request that could not be resolved (no connection, a timeout) is tried again.</summary>
    public TimeSpan RetryResolution { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tracked mode: how soon a request refused (a table without Change Tracking) is resolved again, in case that changed.</summary>
    public TimeSpan ResolveAgain { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Tracked mode: the longest a request's resolution may take, on a background connection.</summary>
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an entry would live, for counting what would have been served, until databases have
    /// policies of their own (phase 2).
    /// </summary>
    public TimeSpan DefaultMaxAge { get; init; } = TimeSpan.FromHours(4);

    /// <summary>The largest request that is copied off the relay and considered. Anything larger is relayed and ignored.</summary>
    public int MaxRequestBytes { get; init; } = 1024 * 1024;

    /// <summary>A response is walked for the database it left the session in only up to this size.</summary>
    public int ResponseScanBytes { get; init; } = 64 * 1024;

    /// <summary>What all in-flight request copies together may hold. Past it, requests are relayed without being considered.</summary>
    public long CopyBudgetBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Slow requests waiting for the analyzer. When it is full, new ones are dropped and counted.</summary>
    public int AnalyzerQueue { get; init; } = 1000;

    /// <summary>A response larger than this is never cached.</summary>
    public long MaxEntryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>An answer is kept only when it took at least this many times longer than replaying it would.</summary>
    public double MinBenefit { get; init; } = 10;

    /// <summary>How fast a cached answer is assumed to replay, until replays are measured.</summary>
    public double ReplayBytesPerSecond { get; init; } = 200_000_000;

    /// <summary>Exact requests remembered by the watch list; the least recently seen go first.</summary>
    public int MaxKeys { get; init; } = 50_000;

    /// <summary>Query shapes remembered by the watch list; the least recently seen go first.</summary>
    public int MaxFingerprints { get; init; } = 5_000;

    /// <summary>What all answers being filled together may hold in memory. A fill that would go over it is abandoned.</summary>
    public long FillBudgetBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>The most a hit may take before its first byte; also never more than 10% of what the query took.</summary>
    public int HitDeadlineMs { get; init; } = 2000;

    /// <summary>
    /// An entry without a shared copy (<see cref="HotCopies"/>) and up to this size is kept decrypted
    /// from the check its hit makes, and sent from memory; a larger one is decrypted again as it is sent.
    /// </summary>
    public int InMemoryReplayBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// --memory-cache-mb: what the decrypted copies of the answers being hit may hold in memory,
    /// together (0 keeps none). Null: the smaller of 1 GB and an eighth of the memory DaffyZoom may use.
    /// </summary>
    public long? MemoryCacheBytes { get; init; }

    public long MemoryCacheCapacity => MemoryCacheBytes ?? Math.Min(1L << 30, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 8);

    /// <summary>
    /// --check-connections: tracked mode's own connections to each target, at most, open at once. A
    /// check that finds them all busy waits for one rather than opening another login.
    /// </summary>
    public int CheckConnections { get; init; } = 8;

    /// <summary>The cache's size on disk: past 90% the least useful entries go, down to 75%.</summary>
    public long CacheMaxBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    /// <summary>What the cache leaves free on its disk, whatever else fills it; null: the larger of 1 GB and 5% of the disk (see <see cref="CacheSpace"/>).</summary>
    public long? CacheMinFreeBytes { get; init; }

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    public double EstimatedReplayMs(long bytes) => bytes / ReplayBytesPerSecond * 1000;
}
