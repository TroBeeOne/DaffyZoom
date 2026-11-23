namespace DaffyZoom.Cache;

/// <summary>One query shape on the watch list, as :watchlist and daffyzoom.watchlist show it.</summary>
internal sealed record WatchRow(
    string Id, string State, long Runs, int Keys, double AverageMs, double P95Ms, long LastBytes,
    long WouldServe, double SavedMs, string? Database, string TargetLogin, DateTime LastSeenUtc, string Shape, ulong ShapeHash,
    string Server = "");

/// <summary>
/// A run that may fill: what the request is, and in tracked mode the tables it resolved to (null
/// until it has). Not <paramref name="Confirmed"/>: an early fill, counting on a slow run the analyzer
/// has not got to yet, so what the request is may not be known (<see cref="WatchList.Confirm"/>).
/// </summary>
internal sealed record FillCandidate(Classification? Classification, Resolution? Tracking, bool Confirmed = true);

/// <summary>
/// What the analyzer has learned: every slow exact request (by key) and every query shape (by
/// fingerprint), with the admission each would get.
///
/// In phase 1 nothing is served, so admission is played out on paper. A cacheable key's first slow
/// run makes it a candidate, its MinRuns-th fills, and every slow run after that within the max age
/// is one that "would have been served" - its time, less the estimated replay, is the time that
/// would have been saved.
///
/// Both maps are capped; the least recently seen go first. Written by the analyzer's one worker,
/// read by the prompt and T-SQL commands, under one lock.
/// </summary>
internal sealed class WatchList(CacheOptions options)
{
    private const int DurationsKept = 100;

    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, KeyEntry> _keys = [];
    private readonly Dictionary<ulong, ShapeEntry> _shapes = [];
    /// <summary>Slow runs handed to the analyzer and not yet recorded, by key.</summary>
    private readonly Dictionary<CacheKey, int> _pending = [];

    private sealed class KeyEntry
    {
        public required ulong Shape { get; init; }
        public required Classification Classification { get; init; }
        public DateTime LastSeenUtc { get; set; }
        public int SlowRuns { get; set; }
        public DateTime? FilledUtc { get; set; }

        /// <summary>Tracked mode: the tables the request resolved to, or why it cannot be tracked. Null: not resolved (yet, or again).</summary>
        public Resolution? Tracking { get; set; }
        public DateTime ResolvedUtc { get; set; }
        public bool Resolving { get; set; }
    }

    private sealed class ShapeEntry
    {
        public required string Shape { get; init; }
        public required ulong ShapeHash { get; init; }
        public required string TargetLogin { get; set; }
        public string Server { get; set; } = "";
        public string? Database { get; set; }
        public long Runs { get; set; }
        public int Keys { get; set; }
        public double TotalMs { get; set; }
        public Queue<double> Durations { get; } = new();
        public long LastBytes { get; set; }
        public string State { get; set; } = "";
        public long WouldServe { get; set; }
        public double SavedMs { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    /// <summary>Exact requests remembered.</summary>
    public int KeyCount { get { lock (_gate) return _keys.Count; } }

    /// <summary>
    /// Whether this run of a request should fill the cache: it is cacheable (or <paramref name="allowed"/>
    /// says so, for a procedure the policy names), and enough of its slow runs have been seen, recently
    /// enough, that this one is the run that fills. Asked on the relay, as the request goes to the server.
    ///
    /// <paramref name="early"/> (ttl mode): slow runs still waiting for the analyzer count too. The
    /// next run of GO 4 comes in a millisecond after the last one ends, long before that run has been
    /// analyzed; without this it would never fill, and its answer would be served a run later. Such a
    /// fill is not confirmed: whether it is kept is settled when its answer ends (<see cref="Confirm"/>).
    /// </summary>
    public FillCandidate? FillWanted(CacheKey key, Func<Classification, bool> allowed, DateTime nowUtc, bool early = false)
    {
        lock (_gate)
        {
            _keys.TryGetValue(key, out var entry);
            bool recent = entry is not null && nowUtc - entry.LastSeenUtc <= options.CandidateWindow;
            int slowRuns = recent ? entry!.SlowRuns : 0;
            int pending = early ? _pending.GetValueOrDefault(key) : 0;

            if (!recent && pending == 0) return null;
            if (slowRuns + pending < options.MinRuns - 1) return null;
            if (entry is not null && !allowed(entry.Classification)) return null;
            return new FillCandidate(entry?.Classification, entry?.Tracking, Confirmed: recent && slowRuns >= options.MinRuns - 1);
        }
    }

    /// <summary>
    /// An early fill's answer has ended: what the request is, if the slow runs it counted on have now
    /// been analyzed and make it one to keep; otherwise null and why not. Asked before the run that
    /// filled is itself handed to the analyzer, so it never counts towards its own fill.
    /// </summary>
    public (Classification? Classification, string? Refused) Confirm(CacheKey key, Func<Classification, bool> allowed)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out var entry)) return (null, "the run before it had not been analyzed by the time it ended");
            if (!allowed(entry.Classification)) return (null, $"it may not be cached ({entry.Classification.Describe()})");
            if (entry.SlowRuns < options.MinRuns - 1)
                return (null, _pending.ContainsKey(key)
                    ? "the run before it had not been analyzed by the time it ended"
                    : $"it had not been seen slow {options.MinRuns - 1} time(s) before");
            return (entry.Classification, null);
        }
    }

    /// <summary>A slow run of this request has been handed to the analyzer.</summary>
    public void Pending(CacheKey key)
    {
        lock (_gate) _pending[key] = _pending.GetValueOrDefault(key) + 1;
    }

    /// <summary>The analyzer is done with a slow run of this request: recorded, or dropped.</summary>
    public void NoLongerPending(CacheKey key)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out int count)) return;
            if (count <= 1) _pending.Remove(key);
            else _pending[key] = count - 1;
        }
    }

    /// <summary>
    /// Tracked mode: whether this key should be resolved to its tables now - never yet, or resolved
    /// before and since forgotten (a definition changed), or refused a while ago (Change Tracking may
    /// have been turned on since), or a try that could not run. Claims it, so it is resolved once.
    /// </summary>
    public bool ClaimResolution(CacheKey key, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out var entry) || entry.Resolving) return false;
            bool wanted = entry.Tracking switch
            {
                null => true,
                { Transient: true } => nowUtc - entry.ResolvedUtc > options.RetryResolution,
                { Problem: not null } => nowUtc - entry.ResolvedUtc > options.ResolveAgain,
                _ => false,
            };
            if (wanted) entry.Resolving = true;
            return wanted;
        }
    }

    /// <summary>What resolving the key found. A refusal shows on the watch list as the shape's state.</summary>
    public void Resolved(CacheKey key, Resolution resolution, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out var entry)) return;
            entry.Resolving = false;
            entry.Tracking = resolution;
            entry.ResolvedUtc = nowUtc;
            if (resolution.Problem is { } problem && _shapes.TryGetValue(entry.Shape, out var shape))
                shape.State = $"refused: {resolution.Kind}: {problem}";
        }
    }

    /// <summary>A definition the key depends on changed: it is resolved again on its next slow run.</summary>
    public void ForgetTracking(CacheKey key)
    {
        lock (_gate)
        {
            if (_keys.TryGetValue(key, out var entry) && !entry.Resolving) entry.Tracking = null;
        }
    }

    /// <summary>The classification already made for this key: a request seen before is not parsed again.</summary>
    public Classification? Known(CacheKey key)
    {
        lock (_gate) return _keys.TryGetValue(key, out var entry) ? entry.Classification : null;
    }

    /// <summary>
    /// One slow run of a request. <paramref name="run"/> is what was wrong with this run in
    /// particular - an error, too large, not worth replaying - which keeps it from filling, or from
    /// counting as served, without changing what the request itself is.
    /// </summary>
    public string Record(CacheKey key, Classification classification, Refusal? run, SlowRequest request, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out var entry))
            {
                entry = new KeyEntry { Shape = RowKey(classification.ShapeHash, request.Context.TargetLogin), Classification = classification, LastSeenUtc = nowUtc };
                _keys[key] = entry;
                ShapeFor(classification, request).Keys++;
                if (_keys.Count > options.MaxKeys) TrimKeys();
            }

            // An entry past its max age is gone; a candidate not seen again in time is forgotten.
            if (entry.FilledUtc is { } filled && nowUtc - filled > options.DefaultMaxAge)
            {
                entry.FilledUtc = null;
                entry.SlowRuns = 0;
            }
            else if (entry.FilledUtc is null && nowUtc - entry.LastSeenUtc > options.CandidateWindow)
            {
                entry.SlowRuns = 0;
            }

            entry.LastSeenUtc = nowUtc;
            if (run is null) entry.SlowRuns++;      // only a clean run counts towards filling: not one with an error, too large, not worth it

            var shape = ShapeFor(classification, request);
            string state;
            double saved = 0;
            if (!classification.Cacheable)
            {
                state = $"refused: {classification.Describe()}";
            }
            else if (entry.Tracking is { Problem: { } problem, Transient: false } tracking)
            {
                state = $"refused: {tracking.Kind}: {problem}";
            }
            else if (run is { } refusal)
            {
                state = $"not this run: {refusal}";
            }
            else if (entry.FilledUtc is not null)
            {
                state = "would serve";
                saved = Math.Max(0, request.DurationMs - options.EstimatedReplayMs(request.ResponseBytes));
                shape.WouldServe++;
            }
            else if (entry.SlowRuns >= options.MinRuns)
            {
                entry.FilledUtc = nowUtc;
                state = "would fill";
            }
            else
            {
                state = $"candidate ({entry.SlowRuns} of {options.MinRuns} slow runs)";
            }

            shape.Runs++;
            shape.TotalMs += request.DurationMs;
            shape.Durations.Enqueue(request.DurationMs);
            if (shape.Durations.Count > DurationsKept) shape.Durations.Dequeue();
            shape.LastBytes = request.ResponseBytes;
            shape.SavedMs += saved;
            shape.State = state;
            shape.Server = request.Context.Target;
            shape.Database = request.Context.Database;
            shape.TargetLogin = request.Context.TargetLogin;
            shape.LastSeenUtc = nowUtc;
            return state;
        }
    }

    /// <summary>The shapes worth looking at first: most time that would have been saved, then most time spent.</summary>
    public IReadOnlyList<WatchRow> Top(int count)
    {
        lock (_gate)
        {
            return _shapes
                .OrderByDescending(s => s.Value.SavedMs)
                .ThenByDescending(s => s.Value.TotalMs)
                .Take(count)
                .Select(s => new WatchRow(
                    s.Key.ToString("x16")[..10], s.Value.State, s.Value.Runs, s.Value.Keys,
                    s.Value.Runs == 0 ? 0 : s.Value.TotalMs / s.Value.Runs, Percentile95(s.Value.Durations), s.Value.LastBytes,
                    s.Value.WouldServe, s.Value.SavedMs, s.Value.Database, s.Value.TargetLogin, s.Value.LastSeenUtc, s.Value.Shape, s.Value.ShapeHash,
                    s.Value.Server))
                .ToList();
        }
    }

    /// <summary>
    /// A row is a query shape as one target login runs it: logins can differ in what they may read and
    /// in what tracked mode can resolve for them, so one may be cached and another refused.
    /// </summary>
    private static ulong RowKey(ulong shapeHash, string targetLogin)
    {
        Span<byte> hash = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(hash, shapeHash);
        var row = new System.IO.Hashing.XxHash64();
        row.Append(hash);
        row.Append(System.Text.Encoding.UTF8.GetBytes(targetLogin));
        return row.GetCurrentHashAsUInt64();
    }

    private ShapeEntry ShapeFor(Classification classification, SlowRequest request)
    {
        ulong key = RowKey(classification.ShapeHash, request.Context.TargetLogin);
        if (!_shapes.TryGetValue(key, out var shape))
        {
            shape = new ShapeEntry { Shape = classification.Shape, ShapeHash = classification.ShapeHash, TargetLogin = request.Context.TargetLogin };
            _shapes[key] = shape;
            if (_shapes.Count > options.MaxFingerprints) TrimShapes(key);
        }
        return shape;
    }

    /// <summary>Forgets the tenth of the keys seen longest ago, all at once, so trimming is rare.</summary>
    private void TrimKeys()
    {
        foreach (var (key, entry) in _keys.OrderBy(k => k.Value.LastSeenUtc).Take(Math.Max(1, _keys.Count / 10)).ToList())
        {
            _keys.Remove(key);
            if (_shapes.TryGetValue(entry.Shape, out var shape)) shape.Keys--;
        }
    }

    private void TrimShapes(ulong keep)
    {
        foreach (var hash in _shapes.Where(s => s.Key != keep).OrderBy(s => s.Value.LastSeenUtc)
                                    .Take(Math.Max(1, _shapes.Count / 10)).Select(s => s.Key).ToList())
            _shapes.Remove(hash);
    }

    private static double Percentile95(Queue<double> durations)
    {
        if (durations.Count == 0) return 0;
        var sorted = durations.Order().ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
    }
}
