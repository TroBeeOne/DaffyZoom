using System.Collections.Concurrent;
using System.IO.Hashing;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>One cached answer: where it is, what it was for, and how it has been used.</summary>
internal sealed class CacheEntry
{
    public required CacheKey Key { get; init; }
    public required string Path { get; init; }

    /// <summary>The token stream's length, as it will be replayed.</summary>
    public required long Length { get; init; }

    /// <summary>What the file takes on disk, compressed and encrypted: what counts against the cache's size.</summary>
    public required long FileBytes { get; init; }

    public required string Server { get; init; }
    public required string Database { get; init; }

    /// <summary>When the database's policy was last changed, as it stood at the fill: a change since makes the entry dead.</summary>
    public DateTime PolicyUpdatedUtc { get; init; }

    /// <summary>Tracked mode: the objects the answer depends on, and the Change Tracking version read before the request went to the server - the baseline each hit checks against.</summary>
    public TrackedTables? Tracked { get; init; }

    /// <summary>The login SQL Server saw: which watch list row the entry belongs to.</summary>
    public string TargetLogin { get; init; } = "";

    /// <summary>The database's flush count when the answer was filled; a flush since makes it dead.</summary>
    public required long Epoch { get; init; }

    public required DateTime CreatedUtc { get; init; }

    /// <summary>How long the query took when it filled: what each hit saves, less the replay.</summary>
    public required double OriginalMs { get; init; }

    public required ulong FirstPacketHash { get; init; }

    /// <summary>The batch's leading SET statements, which a hit sends to the server on its own account.</summary>
    public string? LeadingSetText { get; init; }

    /// <summary>An sp_prepexec's answer: where its statement handle's four bytes are, which a hit writes the session's own handle over.</summary>
    public long? HandleOffset { get; init; }

    public required ulong ShapeHash { get; init; }
    public required string Shape { get; init; }

    private long _hits, _lastHitTicks;
    private double _savedMs;

    public long Hits => Interlocked.Read(ref _hits);
    public DateTime LastUsedUtc => Interlocked.Read(ref _lastHitTicks) is var ticks and > 0 ? new DateTime(ticks, DateTimeKind.Utc) : CreatedUtc;
    public double SavedMs { get { lock (this) return _savedMs; } }

    /// <summary>What keeping this entry is worth, per byte it occupies: what eviction goes by.</summary>
    public double Benefit => OriginalMs * (Hits + 1) / Math.Max(1, FileBytes);

    public void Hit(double replayMs)
    {
        Interlocked.Increment(ref _hits);
        Interlocked.Exchange(ref _lastHitTicks, DateTime.UtcNow.Ticks);
        lock (this) _savedMs += Math.Max(0, OriginalMs - replayMs);
    }
}

/// <summary>
/// The cache's index, in memory only: entries by key, the flush count of each database, the fills in
/// progress, and the prefilter - the first-packet hashes of every cached request - that lets the
/// relay pass everything else on without holding it.
/// </summary>
internal sealed class CacheIndex
{
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<CacheKey, byte> _filling = new();
    private readonly ConcurrentDictionary<ulong, int> _prefilter = new();
    /// <summary>Fills whose answer has ended and is being checked and written, and the prefilter's hashes for them.</summary>
    private readonly ConcurrentDictionary<CacheKey, Saving> _saving = new();
    private readonly ConcurrentDictionary<ulong, int> _savingPrefilter = new();
    /// <summary>Flushes, per server and database in any case: a flush spelled Sales also ends answers cached under SALES.</summary>
    private readonly ConcurrentDictionary<(string, string), long> _epochs = new(DatabaseId.Comparer);
    private long _fileBytes;

    public int Count => _entries.Count;
    public int Filling => _filling.Count;
    public long FileBytes => Interlocked.Read(ref _fileBytes);
    public IReadOnlyCollection<CacheEntry> Entries => (IReadOnlyCollection<CacheEntry>)_entries.Values;

    /// <summary>Told about every entry taken out, however: its copy in memory goes with it (<see cref="HotCopies"/>).</summary>
    public Action<CacheEntry>? Removed { get; set; }

    /// <summary>
    /// How much of a request's body the prefilter hashes. Every first packet holds at least this much
    /// of it: the smallest packet size is 512, less its header (8) and ALL_HEADERS (22, or a few dozen
    /// with a trace activity header). Hashing the whole first packet instead would make the hash
    /// depend on the client's packet size, and a client with a small one would never hit.
    /// </summary>
    public const int PrefixLength = 256;

    /// <summary>
    /// The prefilter's hash of a request: the first <see cref="PrefixLength"/> bytes of what its key
    /// hashes (<see cref="CacheKey.Body"/>), or all of it when shorter. Given the whole request or just
    /// its first packet, the same request gives the same hash - except when a first packet holds less
    /// than that and is not the last, or is not the last and ends in a run of spaces as long as most of
    /// a packet. That can only cost the hit, never cause a wrong one: the key decides, not this.
    /// </summary>
    public static ulong PrefixHash(byte requestType, ReadOnlySpan<byte> payload)
    {
        var body = CacheKey.Body(requestType, payload);
        return XxHash64.HashToUInt64(body[..Math.Min(PrefixLength, body.Length)]);
    }

    /// <summary>
    /// Whether a request starting with this first packet might have a cached answer - or one being
    /// saved now, which it may wait for (<see cref="SaveOf"/>). Cheap; false is final.
    /// </summary>
    public bool MightHit(ulong firstPacketHash) =>
        (!_prefilter.IsEmpty && _prefilter.ContainsKey(firstPacketHash))
        || (!_savingPrefilter.IsEmpty && _savingPrefilter.ContainsKey(firstPacketHash));

    public CacheEntry? Find(CacheKey key) => _entries.GetValueOrDefault(key);

    /// <summary>Claims the key for a fill: false when it is cached already or another session is filling it.</summary>
    public bool TryBeginFill(CacheKey key) => !_entries.ContainsKey(key) && _filling.TryAdd(key, 0);

    /// <summary>A fill's answer has ended and goes to be checked and written. Until <see cref="EndFill"/>, the same request may wait for it.</summary>
    public void BeginSave(CacheKey key, ulong firstPacketHash, double originalMs)
    {
        if (!_filling.ContainsKey(key) || !_saving.TryAdd(key, new Saving(firstPacketHash, originalMs))) return;
        _savingPrefilter.AddOrUpdate(firstPacketHash, 1, (_, n) => n + 1);
    }

    /// <summary>The save of this key's answer, if one is under way: done once it is kept or let go.</summary>
    public Saving? SaveOf(CacheKey key) => _saving.GetValueOrDefault(key);

    /// <summary>The fill is over, kept (its entry added first) or not; whoever waits for its save is let go.</summary>
    public void EndFill(CacheKey key)
    {
        _filling.TryRemove(key, out _);
        if (!_saving.TryRemove(key, out var saving)) return;
        _savingPrefilter.AddOrUpdate(saving.FirstPacketHash, 0, (_, n) => n - 1);
        if (_savingPrefilter.TryGetValue(saving.FirstPacketHash, out int left) && left <= 0) _savingPrefilter.TryRemove(saving.FirstPacketHash, out _);
        saving.End();
    }

    /// <summary>An answer being saved: what its query took, and a task done when the save is over.</summary>
    internal sealed class Saving(ulong firstPacketHash, double originalMs)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ulong FirstPacketHash { get; } = firstPacketHash;
        public double OriginalMs { get; } = originalMs;
        public Task Done => _done.Task;

        public void End() => _done.TrySetResult();
    }

    public void Add(CacheEntry entry)
    {
        _prefilter.AddOrUpdate(entry.FirstPacketHash, 1, (_, n) => n + 1);
        Interlocked.Add(ref _fileBytes, entry.FileBytes);
        if (_entries.TryGetValue(entry.Key, out var replaced)) Remove(replaced);
        _entries[entry.Key] = entry;
    }

    /// <summary>Takes an entry out and deletes its file; a hit already reading it finishes first, the file going when it closes.</summary>
    public bool Remove(CacheEntry entry)
    {
        if (!_entries.TryRemove(new KeyValuePair<CacheKey, CacheEntry>(entry.Key, entry))) return false;
        Interlocked.Add(ref _fileBytes, -entry.FileBytes);
        _prefilter.AddOrUpdate(entry.FirstPacketHash, 0, (_, n) => n - 1);
        if (_prefilter.TryGetValue(entry.FirstPacketHash, out int left) && left <= 0) _prefilter.TryRemove(entry.FirstPacketHash, out _);
        CacheStore.Delete(entry.Path);
        Removed?.Invoke(entry);
        return true;
    }

    public long Epoch(string server, string database) => _epochs.GetValueOrDefault((server, database));

    /// <summary>A flush: every entry of the database filled before now is dead at once; the sweeper deletes them.</summary>
    public long Flush(string server, string database) =>
        _epochs.AddOrUpdate((server, database), 1, (_, n) => n + 1);

    /// <summary>Removes every entry now. Returns how many there were.</summary>
    public int Clear()
    {
        int removed = 0;
        foreach (var entry in _entries.Values) if (Remove(entry)) removed++;
        return removed;
    }
}
