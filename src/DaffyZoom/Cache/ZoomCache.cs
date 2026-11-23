using System.Globalization;
using System.IO.Hashing;
using DaffyZoom.Cli;
using DaffyZoom.Proxy;
using DaffyZoom.Repl;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>
/// The cache, as the rest of DaffyZoom sees it: one observer per session, the analyzer and watch
/// list behind them, and - for databases in ttl mode - the store, the index, the filler, the
/// sweeper and the signals that flush.
///
/// Every decision here leans one way: when anything is unclear, the request goes to SQL Server.
/// </summary>
internal sealed class ZoomCache : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<string, TargetEndpoint?> _mappingFor;
    /// <summary>Each watermarked database's last answer (its hash), or null and why while it cannot be read. Absent: not read yet.</summary>
    private readonly Dictionary<string, (ulong? Hash, string? Problem)> _watermarks = [];
    private Task? _sweeper, _watermarker;
    private volatile bool _serving = true;
    private DateTime _lastScheduleCheckLocal = DateTime.Now;
    private long _writeFlushesLogged;

    /// <param name="measureDisk">The cache disk's free space and size; tests stand in for the disk here.</param>
    public ZoomCache(CacheOptions options, PolicySet policies, string cacheDirectory, Func<string, TargetEndpoint?>? mappingFor = null,
                     Func<string, (long Available, long Total)?>? measureDisk = null)
    {
        Options = options;
        Policies = policies;
        Budget = new ByteBudget(options.CopyBudgetBytes);
        FillBudget = new ByteBudget(options.FillBudgetBytes);
        WatchList = new WatchList(options);
        Analyzer = new Analyzer(options, WatchList, Counters, Budget);
        Store = CacheStore.Open(cacheDirectory);
        Space = new CacheSpace(options, Index, Store.Directory, measureDisk);
        Filler = new Filler(options, Index, Store, FillBudget, Counters, Space);
        Hot = new HotCopies(Store, options.MemoryCacheCapacity, options.InMemoryReplayBytes);
        Index.Removed = Hot.Drop;
        Background = new BackgroundConnections(options.CheckConnections);
        _mappingFor = mappingFor ?? (_ => null);
        Analyzer.Analyzed += OnAnalyzed;
    }

    public CacheOptions Options { get; }
    public PolicySet Policies { get; }
    public CacheCounters Counters { get; } = new();
    public ByteBudget Budget { get; }
    public ByteBudget FillBudget { get; }
    public WatchList WatchList { get; }
    public Analyzer Analyzer { get; }
    public CacheStore Store { get; }
    public CacheIndex Index { get; } = new();
    public Filler Filler { get; }
    public CacheSpace Space { get; }

    /// <summary>The answers being hit, decrypted once and shared by every hit on them.</summary>
    public HotCopies Hot { get; }
    public CircuitBreaker Breaker { get; } = new();

    /// <summary>Runs a watermark query as a mapping and returns its answer's token stream. Tests stand in for SQL Server here.</summary>
    internal Func<TargetEndpoint, string, CancellationToken, Task<byte[]>> WatermarkQuery { get; init; } = QueryAsync;

    /// <summary>The kill switch: off, nothing is served or filled, and every request goes to SQL Server. Observing goes on.</summary>
    public bool Serving
    {
        get => _serving;
        set
        {
            if (_serving == value) return;
            _serving = value;
            Log.Info("cache.serving", value ? "serving from the cache is switched on" : "serving from the cache is switched off; everything goes to SQL Server");
        }
    }

    /// <summary>One phrase for daffyzoom.health: whether anything can be served now, and if not, why not.</summary>
    public string State =>
        !Serving ? "switched off"
        : Breaker.IsOpen ? "paused after failures"
        : !Policies.All.Any(p => p.Mode != CacheMode.Off) ? "observing only"
        : "serving";

    public void Start()
    {
        Analyzer.Start();
        Filler.Start();
        _sweeper = Task.Run(() => SweepAsync(_stopping.Token));
        _watermarker = Task.Run(() => WatermarksAsync(_stopping.Token));
    }

    /// <summary>
    /// The observers for a session that has just logged in. MARS sessions get none: under MARS the
    /// client's packets travel in SMP frames whose sequence numbers the target keeps, so no answer
    /// could ever be slipped in.
    /// </summary>
    public IEnumerable<IRelayObserver> ObserversFor(ProxySession session)
    {
        if (session.Mars)
        {
            Counters.CountMarsSession();
            return [];
        }
        return [new CacheObserver(Options, Counters, Budget, Analyzer.TrySubmit, session, SessionContext.For(session), this)];
    }

    // ---- filling ----------------------------------------------------------------------------------

    /// <summary>
    /// Whether a request may be cached under a database's policy. In ttl mode: cacheable, or a
    /// procedure the policy names. In tracked mode: cacheable and reading committed rows only - Change
    /// Tracking never records a change that rolled back, so a dirty read could stay wrong - and no
    /// procedures, whose tables cannot be known from outside.
    /// </summary>
    private static bool Allowed(Classification classification, DatabasePolicy policy) => policy.Mode switch
    {
        CacheMode.Ttl => classification.Cacheable
                         || classification is { Refusal: Refusal.Procedure, Detail: { } procedure } && policy.AllowsProcedure(procedure),
        CacheMode.Tracked => classification is { Cacheable: true, DirtyRead: false },
        _ => false,
    };

    /// <summary>
    /// Asked on the relay as an eligible request goes to the server: should its answer be copied? Only
    /// for a cached database outside a blackout, a request the analyzer found cacheable and has seen slow
    /// often enough, nothing cached for it already, and no other session filling it. In tracked mode,
    /// also: its tables resolved, and a Change Tracking version known - read before this request goes to
    /// the server, so any change from now on is newer than the baseline.
    ///
    /// In ttl mode a fill may also begin early, counting on a slow run of the request that the analyzer
    /// has not recorded yet - unless its text may write (<paramref name="mayWrite"/>). Whether it is
    /// kept is settled when its answer ends, by the same rules (<see cref="WatchList.Confirm"/>).
    /// Tracked mode never fills early: its tables must be resolved before the request goes to the server.
    /// </summary>
    public Fill? TryBeginFill(in ContextSnapshot context, CacheKey key, byte requestType, ReadOnlySpan<byte> payload, bool reset, bool columnEncryption,
                              bool mayWrite = false)
    {
        if (!Serving || context.Database is not { } database) return null;
        if (Policies.For(context.Target, database) is not { Mode: not CacheMode.Off } policy || policy.InBlackout(DateTime.Now) || !WatermarkRead(policy)) return null;
        if (Index.Find(key) is not null) return null;
        bool early = policy.Mode == CacheMode.Ttl && !mayWrite;
        if (WatchList.FillWanted(key, c => Allowed(c, policy), DateTime.UtcNow, early) is not { } candidate) return null;

        TrackedTables? tracked = null;
        if (policy.Mode == CacheMode.Tracked)
        {
            if (context.DirtyReads || candidate.Tracking?.Tables is not { } tables || KnownVersion(context.Target, database) is not { } version) return null;
            tracked = tables with { Version = version };
        }
        if (!Index.TryBeginFill(key)) return null;

        Counters.CountFillStarted();
        return new Fill
        {
            Key = key, Server = context.Target.ToLowerInvariant(), Database = database,
            Epoch = Index.Epoch(context.Target, database), PolicyUpdatedUtc = policy.UpdatedUtc, Tracked = tracked, TargetLogin = context.TargetLogin,
            FirstPacketHash = CacheIndex.PrefixHash(requestType, payload),
            Classification = candidate.Classification, Reset = reset, ColumnEncryption = columnEncryption,
            Confirmation = candidate.Confirmed ? null : () => WatchList.Confirm(key, c => Allowed(c, policy)),
        };
    }

    // ---- tracked mode -----------------------------------------------------------------------------

    /// <summary>Each tracked database's latest Change Tracking version, as last read by a resolution or a check.</summary>
    private readonly Dictionary<(string Server, string Database), long> _versions = new(DatabaseId.Comparer);
    private readonly SemaphoreSlim _resolving = new(4);
    private int _resolveFailuresLogged;

    /// <summary>The background connections tracked mode's queries run on, as the clients' own mapped logins.</summary>
    public BackgroundConnections Background { get; init; }

    /// <summary>Runs one of tracked mode's queries as a mapping: on <see cref="Background"/>, unless a test stands in for SQL Server.</summary>
    internal Func<TargetEndpoint, string, CancellationToken, Task<byte[]>>? TrackedQuery { get; init; }

    private Task<byte[]> TrackedQueryAsync(TargetEndpoint target, string sql, CancellationToken cancellationToken) =>
        TrackedQuery is { } query ? query(target, sql, cancellationToken) : Background.QueryAsync(target, sql, cancellationToken);

    /// <summary>
    /// A hit's check: shared with identical checks still waiting for a connection, and run to the
    /// longest a hit may wait, whoever gives up on it first.
    /// </summary>
    private Task<byte[]> TrackedCheckAsync(TargetEndpoint target, string sql, CancellationToken cancellationToken) =>
        TrackedQuery is { } query ? query(target, sql, cancellationToken)
            : Background.QuerySharedAsync(target, sql, TimeSpan.FromMilliseconds(Options.HitDeadlineMs), cancellationToken);

    /// <summary>
    /// For daffyzoom.tracking: a database's Change Tracking as <paramref name="login"/>'s mapping sees
    /// it - asked of SQL Server now, on a background connection as that login - beside what DaffyZoom
    /// knows: the database's policy, the last version it saw, and the live entries of that target
    /// login that depend on each table.
    /// </summary>
    public async Task<TrackingReport> TrackingAsync(string login, string database, bool allTables, CancellationToken cancellationToken)
    {
        if (_mappingFor(login) is not { } target) return new TrackingReport { Status = new(null, [], $"the DaffyZoom login '{login}' has no mapping") };

        string server = $"{target.Host}:{target.Port}".ToLowerInvariant();
        string targetLogin = target.IsEntra ? target.ClientId : target.UserName;
        TrackingStatus status;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(Options.ResolveTimeout);
            try
            {
                status = ChangeTracking.ParseStatus(await TrackedQueryAsync(target with { Database = database }, ChangeTracking.StatusBatch(allTables), timeout.Token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                status = new TrackingStatus(null, [], ex is OperationCanceledException ? $"SQL Server did not answer within {Options.ResolveTimeout.TotalSeconds:F0}s" : ex.Message);
            }
        }
        var now = DateTime.UtcNow;
        var policy = Policies.For(server, database);
        var entries = Index.Entries.Where(e => e.Tracked is not null && e.Server == server && DatabasePolicy.SameDatabase(e.Database, database) && e.TargetLogin == targetLogin
                                               && Invalid(e, policy, now) is null).ToList();
        return new TrackingReport
        {
            Server = server, Database = database, TargetLogin = targetLogin, Policy = policy, KnownVersion = KnownVersion(server, database),
            Status = status, Entries = entries,
        };
    }

    private long? KnownVersion(string server, string database)
    {
        lock (_versions) return _versions.TryGetValue((server, database), out long version) ? version : null;
    }

    private void SawVersion(string server, string database, long? version)
    {
        if (version is not { } seen) return;
        lock (_versions)
        {
            var id = (server, database);
            if (!_versions.TryGetValue(id, out long known) || seen > known) _versions[id] = seen;
        }
    }

    /// <summary>
    /// Every analyzed slow run: in a tracked database, a cacheable request is resolved to its tables
    /// in the background, as the session's own mapped login, so its next slow run can fill.
    /// </summary>
    private void OnAnalyzed(SlowRequest request, CacheKey key, Classification classification, string verdict)
    {
        var context = request.Context;
        if (context.Database is not { } database || Policies.For(context.Target, database) is not { Mode: CacheMode.Tracked } policy) return;

        // What ttl mode would cache but tracked mode will not: the watch list says so, rather than "would serve".
        Resolution? refused =
            classification is { Cacheable: true, DirtyRead: true } || classification.Cacheable && context.DirtyReads
                ? new Resolution(null, "it may read uncommitted rows (NOLOCK, READ UNCOMMITTED), and Change Tracking never sees a change that rolls back")
                  { Kind = Refusal.DirtyRead }
            : classification.Refusal == Refusal.Procedure
                ? new Resolution(null, $"{classification.Detail}: which tables a procedure reads cannot be known from outside it") { Kind = Refusal.Procedure }
            : null;
        if (refused is not null)
        {
            WatchList.Resolved(key, refused, DateTime.UtcNow);
            return;
        }

        if (!Allowed(classification, policy) || !WatchList.ClaimResolution(key, DateTime.UtcNow)) return;
        _ = ResolveAsync(key, context.Target, database, request.Login, classification.References);
    }

    internal async Task ResolveAsync(CacheKey key, string server, string database, string login, IReadOnlyList<ObjectReference> references)
    {
        Resolution resolution;
        await _resolving.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            resolution = await ResolveCoreAsync(server, database, login, references).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
        {
            if (Interlocked.Increment(ref _resolveFailuresLogged) <= 10)
                Log.Warn("cache.resolve_failed", $"{server}/{database}: could not resolve a request's tables as '{login}': {ex.Message}");
            resolution = new Resolution(null, $"its tables could not be resolved: {ex.Message}") { Transient = true };
        }
        finally
        {
            _resolving.Release();
        }
        WatchList.Resolved(key, resolution, DateTime.UtcNow);
    }

    private async Task<Resolution> ResolveCoreAsync(string server, string database, string login, IReadOnlyList<ObjectReference> references)
    {
        if (references.FirstOrDefault(r => r.Database is { } other && !other.Equals(database, StringComparison.OrdinalIgnoreCase)) is { } elsewhere)
            return new Resolution(null, $"it reads another database: {elsewhere}");
        if (references.Count == 0) return new Resolution(null, "it reads no table");
        if (_mappingFor(login) is not { } target) return new Resolution(null, $"the DaffyZoom login '{login}' has no mapping") { Transient = true };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(Options.ResolveTimeout);
        var names = references.Select(r => r.Quoted).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var answer = await TrackedQueryAsync(target with { Database = database }, ChangeTracking.ResolveBatch(names), timeout.Token).ConfigureAwait(false);
        var resolution = ChangeTracking.ParseResolution(answer);
        SawVersion(server, database, resolution.Tables?.Version);
        return resolution;
    }

    /// <summary>A request known not to write: classified cacheable, or answered from the cache.</summary>
    public bool KnownReadOnly(CacheKey key) => Index.Find(key) is not null || WatchList.Known(key) is { Cacheable: true };

    /// <summary>
    /// A write went through DaffyZoom to a database: in ttl mode its answers can no longer be trusted,
    /// so it is flushed. Writes that bypass DaffyZoom are the flush signals' business.
    /// </summary>
    public void WriteSeen(string server, string? database, string what)
    {
        // Tracked mode needs no flush: Change Tracking sees every committed write, through DaffyZoom or not.
        if (database is null || Policies.For(server, database) is not { Mode: CacheMode.Ttl }) return;
        Index.Flush(server, database);
        Counters.CountInvalidated();
        if (Interlocked.Increment(ref _writeFlushesLogged) % 100 == 1)
            Log.Info("cache.write_flush", $"{server}/{database} flushed: {what} went through DaffyZoom");
    }

    // ---- hits -------------------------------------------------------------------------------------

    public bool MightHit(ulong firstPacketHash) => Serving && Index.MightHit(firstPacketHash);

    /// <summary>
    /// The hit for a request, if there is one that may be served - checked to its last byte within the
    /// deadline, before the first is sent. Null for anything else, and the request goes to SQL Server.
    /// </summary>
    public async Task<CacheHit?> TryHitAsync(ContextSnapshot context, byte type, ReadOnlyMemory<byte> payload, byte resetFlags,
                                             ulong transaction, string login, CancellationToken cancellationToken)
    {
        if (!Serving || Breaker.IsOpen || context.Database is not { } database) return null;

        var key = CacheKey.Compute(context, type, payload.Span);
        if ((Index.Find(key) ?? await SavedAsync(key, cancellationToken)) is not { } entry) return null;

        var policy = Policies.For(context.Target, database);
        var now = DateTime.UtcNow;
        if (Invalid(entry, policy, now) is { } invalid)
        {
            if (Index.Remove(entry)) Counters.CountFillAbandoned($"removed: {invalid}");
            return null;
        }
        if (policy!.InBlackout(DateTime.Now)) return null;

        // An sp_prepexec's hit prepares its statement on the session first, made from the request itself.
        byte[]? prepare = null;
        if (entry.HandleOffset is not null && (prepare = PreparedStatements.PrepareFor(payload.Span)) is null) return null;

        // Never more than 10% of what the query took: a failing check must not cost a five-second
        // query two seconds. Never less than a tenth of a second, which any healthy check fits in.
        var deadline = TimeSpan.FromMilliseconds(Math.Min(Options.HitDeadlineMs, Math.Max(100, entry.OriginalMs * 0.10)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        try
        {
            // Tracked mode asks SQL Server, as this session's own mapped login, while the entry is read.
            Task<FreshnessCheck>? checking = null;
            if (entry.Tracked is { } tracked)
            {
                if (_mappingFor(login) is not { } target) return null;
                checking = CheckAsync(target with { Database = database }, tracked, timeout.Token);
            }

            var verifying = Hot.OpenAsync(entry, timeout.Token);
            if (checking is not null)
            {
                FreshnessCheck check;
                try
                {
                    check = await checking;
                }
                finally
                {
                    if (!checking.IsCompletedSuccessfully || !checking.Result.Fresh) _ = verifying.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                }
                SawVersion(context.Target, database, check.Version);
                if (check.Unavailable)
                {
                    // No answer at all - the server is down, the login fails: the entry may well be
                    // right, but cannot be shown to be. Repeated, it pauses serving (the breaker).
                    Fallback($"the freshness check could not run ({check.Problem})");
                    return null;
                }
                if (!check.Fresh)
                {
                    if (Index.Remove(entry)) Counters.CountInvalidated();
                    if (check.Problem is not null || !check.DefinitionsUnchanged) WatchList.ForgetTracking(key);
                    if (check.Problem is not null) Log.Debug("cache.check_failed", $"{entry.Server}/{entry.Database}: the freshness check failed: {check.Problem}");
                    return null;
                }
            }

            var content = await verifying;
            return new CacheHit { Entry = entry, Content = content, ResetFlags = resetFlags, Transaction = transaction, Prepare = prepare };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Fallback($"the entry was not ready within {deadline.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex) when (ex is CacheCorruptException or IOException or UnauthorizedAccessException)
        {
            Index.Remove(entry);
            Fallback(ex is CacheCorruptException ? $"an entry did not verify ({ex.Message})" : $"an entry could not be read ({ex.Message})");
        }
        return null;
    }

    /// <summary>
    /// The entry for a request whose answer has just been copied and is being written now. The next
    /// run of GO 4 comes in within a millisecond of the last one ending, and the copy takes some tens
    /// of milliseconds to check, compress, encrypt and write, so it is worth waiting for - as long as
    /// a hit may take to start, at most (the same bound: a tenth of what the query took, within
    /// --hit-deadline-ms). Null when it is not kept, or not ready by then: the request goes to SQL
    /// Server, as it would have anyway. Not a failure, so not one the breaker counts.
    /// </summary>
    private async Task<CacheEntry?> SavedAsync(CacheKey key, CancellationToken cancellationToken)
    {
        if (Index.SaveOf(key) is not { } saving) return null;
        var wait = TimeSpan.FromMilliseconds(Math.Min(Options.HitDeadlineMs, Math.Max(100, saving.OriginalMs * 0.10)));
        try
        {
            await saving.Done.WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
            Counters.CountFallback($"its answer was still being saved after {wait.TotalMilliseconds:F0} ms");
            return null;
        }
        return Index.Find(key);
    }

    /// <summary>The per-hit check of a tracked entry: one batch on a background connection.</summary>
    private async Task<FreshnessCheck> CheckAsync(TargetEndpoint target, TrackedTables tracked, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await TrackedCheckAsync(target, ChangeTracking.CheckBatch(tracked.Objects, tracked.Version), cancellationToken).ConfigureAwait(false);
            return ChangeTracking.ParseCheck(answer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FreshnessCheck(false, false, null, ex.Message) { Unavailable = true };
        }
    }

    /// <summary>
    /// The entries that could be served now. A flush makes entries dead at once, but the sweeper
    /// deletes them later; what is listed should not include them.
    /// </summary>
    public IEnumerable<CacheEntry> LiveEntries()
    {
        var now = DateTime.UtcNow;
        return Index.Entries.Where(e => Invalid(e, Policies.For(e.Server, e.Database), now) is null);
    }

    /// <summary>Why an entry may no longer be served, or null.</summary>
    internal string? Invalid(CacheEntry entry, DatabasePolicy? policy, DateTime nowUtc)
    {
        if (policy is not { Mode: not CacheMode.Off }) return "the database is no longer cached";
        if ((policy.Mode == CacheMode.Tracked) != (entry.Tracked is not null)) return "its database's mode changed";
        if (entry.Epoch != Index.Epoch(entry.Server, entry.Database)) return "flushed";
        if (entry.PolicyUpdatedUtc != policy.UpdatedUtc) return "its database's policy changed";
        if (nowUtc - entry.CreatedUtc > policy.MaxAge) return "past its max age";
        if (policy.Idle is { } idle && nowUtc - entry.LastUsedUtc > idle) return "not used for its idle time";
        if (!WatermarkRead(policy)) return "its database's watermark is not being read";
        return null;
    }

    /// <summary>
    /// Whether a database's watermark, if it has one, has been read and still is. Until its first
    /// answer, and while it fails, a load could finish unseen - so nothing of the database is served
    /// or filled. When it works again the database is flushed (<see cref="PollWatermarkAsync"/>).
    /// </summary>
    private bool WatermarkRead(DatabasePolicy policy)
    {
        if (policy.WatermarkSql is null || policy.WatermarkLogin is null) return true;
        lock (_watermarks) return _watermarks.TryGetValue(policy.Id, out var state) && state.Hash is not null;
    }

    /// <summary>For daffyzoom.databases: whether a database's answers are being served now, and if not, why not.</summary>
    public string StateOf(DatabasePolicy policy)
    {
        if (policy.Mode == CacheMode.Off) return "off";
        if (!Serving) return "not served: serving is switched off";
        if (Breaker.IsOpen) return "not served: paused after repeated failures";
        if (policy.InBlackout(DateTime.Now)) return "not served: in a blackout window";
        if (policy.WatermarkSql is not null && policy.WatermarkLogin is not null)
        {
            lock (_watermarks)
            {
                if (!_watermarks.TryGetValue(policy.Id, out var state)) return "not served yet: its watermark has not been read";
                if (state.Hash is null) return $"not served: {state.Problem}";
            }
        }
        return "serving";
    }

    public void Fallback(string reason)
    {
        Counters.CountFallback(reason);
        Breaker.Failure(reason);
    }

    public void Served(CacheHit hit, double milliseconds, bool completed)
    {
        if (!completed) return;
        hit.Entry.Hit(milliseconds);
        Counters.CountHit(hit.Content.Length);
    }

    // ---- flushing ---------------------------------------------------------------------------------

    /// <summary>Flushes one database (every server holding one by that name, when <paramref name="server"/> is null). Returns how many.</summary>
    public int Flush(string? server, string database, string by)
    {
        var targets = Policies.All.Where(p => DatabasePolicy.SameDatabase(p.Database, database) && (server is null || p.Server.Equals(server, StringComparison.OrdinalIgnoreCase)))
                                  .Select(p => p.Server).ToList();
        if (server is not null && !targets.Contains(server, StringComparer.OrdinalIgnoreCase)) targets.Add(server.ToLowerInvariant());
        foreach (string s in targets) Index.Flush(s, database);
        Log.Info("cache.flush", $"{string.Join(", ", targets.Select(s => $"{s}/{database}"))} flushed by {by}");
        return targets.Count;
    }

    public int FlushAll(string by)
    {
        int removed = Index.Clear();
        Log.Info("cache.flush", $"everything flushed by {by}: {removed} entr{(removed == 1 ? "y" : "ies")}");
        return removed;
    }

    /// <summary>
    /// Every 30 seconds: entries that can no longer be served go; the scheduled flushes and blackout
    /// edges that came due are applied; and the cache is brought back under its size cap.
    /// </summary>
    private async Task SweepAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Options.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
            {
                try { Sweep(DateTime.UtcNow, DateTime.Now); }
                catch (Exception ex) { Log.Error("cache.sweep_failed", $"the cache sweep failed: {ex}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void Sweep(DateTime nowUtc, DateTime nowLocal)
    {
        Background.Prune();

        // Flush times and blackout edges (a load starts and ends) passed since the last look.
        var since = _lastScheduleCheckLocal;
        _lastScheduleCheckLocal = nowLocal;
        foreach (var policy in Policies.All.Where(p => p.Mode != CacheMode.Off))
        {
            var edges = policy.FlushAt.Select(t => DatabasePolicy.TryParseTime(t, out var time) ? time : (TimeOnly?)null)
                .Concat(policy.Blackouts.SelectMany(w => DatabasePolicy.TryParseWindow(w, out var from, out var to) ? [from, to] : Array.Empty<TimeOnly?>()))
                .OfType<TimeOnly>();
            if (edges.Any(edge => Passed(edge, since, nowLocal)))
            {
                Index.Flush(policy.Server, policy.Database);
                Log.Info("cache.flush", $"{policy.Id} flushed on its schedule");
            }
        }

        foreach (var entry in Index.Entries.ToList())
            if (Invalid(entry, Policies.For(entry.Server, entry.Database), nowUtc) is not null && Index.Remove(entry))
                Counters.CountInvalidated();

        // Over the high-water mark - of the size cap, or of what the disk can spare - the entries worth
        // least per byte go, down to the low-water mark.
        Space.Trim();
    }

    /// <summary>Whether a time of day came in (since, now], allowing for midnight in between.</summary>
    internal static bool Passed(TimeOnly edge, DateTime since, DateTime now)
    {
        if (now <= since) return false;
        for (var day = since.Date; day <= now.Date; day = day.AddDays(1))
        {
            var at = day + edge.ToTimeSpan();
            if (at > since && at <= now) return true;
        }
        return false;
    }

    /// <summary>
    /// Each ttl database with a watermark query: run it every so often as the mapping named for it,
    /// and flush when its answer changes - an ETL control table's last load time, say. The answer's
    /// bytes are compared, so nothing about its shape needs to be known.
    /// </summary>
    private async Task WatermarksAsync(CancellationToken stopping)
    {
        var due = new Dictionary<string, DateTime>();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stopping);
                foreach (var policy in Policies.All.Where(p => p.Mode != CacheMode.Off && p.WatermarkSql is not null && p.WatermarkLogin is not null))
                {
                    if (due.TryGetValue(policy.Id, out var next) && DateTime.UtcNow < next) continue;
                    due[policy.Id] = DateTime.UtcNow.AddSeconds(Math.Max(5, policy.WatermarkSeconds));
                    await PollWatermarkAsync(policy, stopping);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task PollWatermarkAsync(DatabasePolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            if (_mappingFor(policy.WatermarkLogin!) is not { } target)
            {
                Remember(policy, $"its watermark login '{policy.WatermarkLogin}' has no mapping");
                return;
            }

            var answer = await WatermarkQuery(target with { Database = policy.Database }, policy.WatermarkSql!, cancellationToken);
            if (ResponseReader.Read(answer, columnEncryption: false) is { Errors.Count: > 0 } detail)
            {
                Remember(policy, $"its watermark query failed: {detail.Errors[0]}");
                return;
            }

            // Any answer but the last one flushes. That includes the first - entries filled before it
            // may predate a load it already reflects - and the first after a stretch of failures (last
            // is null then), in which a load may have finished. The flush comes before the answer is
            // recorded, so nothing filled before it can be served after it.
            ulong hash = XxHash64.HashToUInt64(answer);
            ulong? last;
            bool known;
            lock (_watermarks) { known = _watermarks.TryGetValue(policy.Id, out var state); last = state.Hash; }
            if (known && last == hash) return;

            Index.Flush(policy.Server, policy.Database);
            lock (_watermarks) _watermarks[policy.Id] = (hash, null);
            if (!known) Log.Info("cache.watermark", $"{policy.Id}: its watermark was read; serving starts");
            else if (last is null) Log.Info("cache.flush", $"{policy.Id} flushed: its watermark can be read again");
            else Log.Info("cache.flush", $"{policy.Id} flushed: its watermark changed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Remember(policy, $"its watermark query could not run: {ex.Message}");
        }
    }

    /// <summary>A watermark that cannot be read is logged when it starts failing, not on every poll.</summary>
    private void Remember(DatabasePolicy policy, string problem)
    {
        lock (_watermarks)
        {
            bool failing = _watermarks.TryGetValue(policy.Id, out var last) && last.Hash is null;
            _watermarks[policy.Id] = (null, problem);
            if (failing) return;
        }
        Log.Warn("cache.watermark_failed", $"{policy.Id}: {problem}; nothing of it is served until it works");
    }

    /// <summary>One small query on a connection of DaffyZoom's own, logged in as a mapping, its answer's token stream returned.</summary>
    private static async Task<byte[]> QueryAsync(TargetEndpoint target, string sql, CancellationToken cancellationToken)
    {
        await using var upstream = await UpstreamConnector.ConnectAsync(target, UpstreamConnector.CreateValidationLogin(target), mars: false, cancellationToken);
        int packetSize = upstream.Login.PacketSize ?? TdsConstants.DefaultPacketSize;
        await upstream.Stream.WriteAsync(Relay.BuildBatch(sql, 0, 0, packetSize), cancellationToken);
        var reader = new TdsMessageReader(upstream.Stream);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var reply = await reader.ReadAsync(timeout.Token) ?? throw new IOException("the server closed the connection");
        return reply.Message.Payload;
    }

    // ---- the prompt -------------------------------------------------------------------------------

    /// <summary>:watchlist, :entries, :flush and :serve at the prompt, and the cache's lines in :status.</summary>
    public IEnumerable<ReplCommand> PromptCommands()
    {
        yield return new ReplCommand(
            [":watchlist", ":wl"], ":watchlist [n]", "slow queries that repeat, and whether they are or would be cached",
            (args, output) =>
            {
                WriteWatchList(output, Count(args, 20));
                return Task.CompletedTask;
            },
            WriteStatus);

        yield return new ReplCommand(
            [":entries"], ":entries [n]", "what is cached now, most useful first",
            (args, output) =>
            {
                WriteEntries(output, Count(args, 20));
                return Task.CompletedTask;
            });

        yield return new ReplCommand(
            [":flush"], ":flush SERVER/DB | DB | all", "drop cached answers: one database, or everything",
            (args, output) =>
            {
                if (args.Length == 0) output.WriteLine("  :flush SERVER/DB, :flush DB (on every server), or :flush all");
                else if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase)) output.WriteLine($"  flushed everything ({FlushAll("the console")} entries)");
                else
                {
                    string? server = null;
                    string database = args[0];
                    if (args[0].Contains('/')) (server, database) = DatabasePolicy.ParseId(args[0]);
                    output.WriteLine($"  flushed {Flush(server, database, "the console")} database(s)");
                }
                return Task.CompletedTask;
            });

        yield return new ReplCommand(
            [":serve"], ":serve [on|off]", "the kill switch: off sends every request to SQL Server",
            (args, output) =>
            {
                if (args.Length > 0) Serving = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                output.WriteLine($"  serving from the cache is {(Serving ? "on" : "off")}");
                return Task.CompletedTask;
            });
    }

    private static int Count(string[] args, int fallback) =>
        args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 1, 500) : fallback;

    public void WriteWatchList(TextWriter output, int count)
    {
        var rows = WatchList.Top(count);
        if (rows.Count == 0)
        {
            output.WriteLine($"  nothing slower than {Options.SlowMs / 1000.0:0.#} s has run yet");
            return;
        }

        var cached = CachedByShape();
        TextTable.Write(output,
            ["id", "state", "runs", "keys", "avg s", "p95 s", "size", "hits", "saved", "database", "login", "query"],
            rows.Select(r =>
            {
                var (entries, hits, saved) = cached.GetValueOrDefault((r.ShapeHash, r.TargetLogin));
                return new[]
                {
                    r.Id, entries > 0 ? $"cached ({entries})" : RowState(r), r.Runs.ToString("N0"), r.Keys.ToString("N0"),
                    Seconds(r.AverageMs), Seconds(r.P95Ms), Size(r.LastBytes),
                    entries > 0 ? hits.ToString("N0") : $"({r.WouldServe:N0})", Elapsed(entries > 0 ? saved : r.SavedMs),
                    r.Database ?? "?", r.TargetLogin, r.Shape,
                };
            }).ToList());
        output.WriteLine("  hits in brackets: what would have been served, for a database that is not cached");
    }

    /// <summary>
    /// A watch list row's state as shown, when nothing of it is cached: what the analyzer found, and
    /// for a query DaffyZoom could cache but whose database has no policy, that - with the line that
    /// would cache it. Everything is watched; nothing is cached until its database has a policy.
    /// </summary>
    public string RowState(WatchRow row)
    {
        if (row.State.StartsWith("refused", StringComparison.Ordinal) || row.Database is null || row.Server.Length == 0) return row.State;
        return Policies.For(row.Server, row.Database) switch
        {
            null => $"{row.State}; not cached, its database has no policy: daffyzoom db set {DatabasePolicy.CommandId(row.Server, row.Database)} --mode ttl",
            { Mode: CacheMode.Off } => $"{row.State}; not cached, its database's policy is off",
            _ => row.State,
        };
    }

    /// <summary>Per query shape: entries cached now, their hits, and the time they saved.</summary>
    public Dictionary<(ulong ShapeHash, string TargetLogin), (int Entries, long Hits, double SavedMs)> CachedByShape() =>
        LiveEntries().GroupBy(e => (e.ShapeHash, e.TargetLogin))
                     .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(e => e.Hits), g.Sum(e => e.SavedMs)));

    public void WriteEntries(TextWriter output, int count)
    {
        var entries = LiveEntries().OrderByDescending(e => e.SavedMs).ThenByDescending(e => e.OriginalMs).Take(count).ToList();
        if (entries.Count == 0)
        {
            output.WriteLine("  nothing is cached" + (Policies.All.Any(p => p.Mode != CacheMode.Off) ? "" : " - no database has a policy yet (daffyzoom db set SERVER/DB --mode ttl)"));
            return;
        }

        var now = DateTime.UtcNow;
        TextTable.Write(output,
            ["key", "database", "took s", "size", "on disk", "hits", "saved", "age", "last used", "query"],
            entries.Select(e => new[]
            {
                e.Key.Short, $"{e.Server}/{e.Database}", Seconds(e.OriginalMs), Size(e.Length), Size(e.FileBytes), e.Hits.ToString("N0"),
                Elapsed(e.SavedMs), Elapsed((now - e.CreatedUtc).TotalMilliseconds), Elapsed((now - e.LastUsedUtc).TotalMilliseconds) + " ago", e.Shape,
            }).ToList());
    }

    private void WriteStatus(TextWriter output)
    {
        var c = Counters;
        var policies = Policies.All;
        int ttl = policies.Count(p => p.Mode == CacheMode.Ttl), tracked = policies.Count(p => p.Mode == CacheMode.Tracked);
        string databases = ttl + tracked == 0 ? "no database is cached yet (daffyzoom db set SERVER/DB --mode ttl)"
                         : $"{ttl + tracked} database(s) cached ({ttl} ttl, {tracked} tracked)";
        output.WriteLine($"  cache    : {(Serving ? Breaker.IsOpen ? $"PAUSED until {Breaker.OpenUntilUtc?.ToLocalTime():HH:mm:ss} after repeated failures" : "serving" : "OFF (:serve on)")}; " +
                         $"{databases}; slow is {Options.SlowMs / 1000.0:0.#} s; fills on slow run {Options.MinRuns}");
        output.WriteLine($"  entries  : {LiveEntries().Count():N0} live ({Index.Count:N0} on disk, {Size(Index.FileBytes)}; {Space.Describe()}); {Index.Filling} filling now");
        output.WriteLine($"  memory   : {Hot.Count:N0} answer(s) kept decrypted for hits, {Size(Hot.Bytes)} of {Size(Hot.Capacity)}");
        output.WriteLine($"  hits     : {c.Hits:N0} ({Size(c.HitBytes)}); fills {c.Filled:N0} of {c.FillsStarted:N0} kept; " +
                         $"{c.Fallbacks:N0} fell back to SQL Server; {c.Invalidated:N0} invalidated");
        output.WriteLine($"  queries  : {c.Requests:N0} seen, {c.Eligible:N0} could be cached, {c.Slow:N0} slow, {c.Analyzed:N0} analyzed" +
                         (c.DroppedQueueFull > 0 ? $", {c.DroppedQueueFull:N0} dropped (analyzer busy)" : "") +
                         (c.NotCopied > 0 ? $", {c.NotCopied:N0} not copied (budget)" : "") +
                         (c.MarsSessions > 0 ? $"; {c.MarsSessions:N0} MARS session(s) relayed only" : ""));
        if (!c.Bypassed.IsEmpty)
            output.WriteLine("  bypassed : " + string.Join(", ", c.Bypassed.OrderByDescending(b => b.Value).Select(b => $"{b.Key} {b.Value:N0}")));
        if (!c.FillsAbandoned.IsEmpty)
            output.WriteLine("  not kept : " + string.Join(", ", c.FillsAbandoned.OrderByDescending(b => b.Value).Take(5).Select(b => $"{b.Key} {b.Value:N0}")));
        if (!c.FallbackReasons.IsEmpty)
            output.WriteLine("  fell back: " + string.Join(", ", c.FallbackReasons.OrderByDescending(b => b.Value).Take(5).Select(b => $"{b.Key} {b.Value:N0}")));
    }

    internal static string Seconds(double ms) => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture);

    internal static string Elapsed(double ms) =>
        ms < 60_000 ? $"{ms / 1000:0.0} s" : ms < 3_600_000 ? $"{ms / 60_000:0.0} min" : $"{ms / 3_600_000:0.0} h";

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        foreach (var task in new[] { _sweeper, _watermarker })
            if (task is not null) { try { await task.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        await Analyzer.DisposeAsync();
        await Filler.DisposeAsync();
        await Background.DisposeAsync();
        Index.Clear();
    }
}
