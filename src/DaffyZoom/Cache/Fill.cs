using System.Buffers;
using System.Threading.Channels;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>
/// One answer being copied into the cache as it streams to the client.
/// The copy is made on the relay's thread, into memory reserved against the fill budget;
/// the moment it would go over the budget or the entry size limit it is abandoned, and the query
/// carries on, unaffected.
/// </summary>
internal sealed class Fill
{
    public required CacheKey Key { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }

    /// <summary>When the database's policy was last changed, as it stood at the fill: a change since makes the entry dead.</summary>
    public DateTime PolicyUpdatedUtc { get; init; }

    /// <summary>Tracked mode: the objects the answer depends on, and the Change Tracking version read before the request went to the server - the baseline each hit checks against.</summary>
    public TrackedTables? Tracked { get; init; }

    /// <summary>The login SQL Server saw: which watch list row the entry belongs to.</summary>
    public string TargetLogin { get; init; } = "";

    /// <summary>The database's flush count before the request went to the server: the race rule.</summary>
    public required long Epoch { get; init; }

    public required ulong FirstPacketHash { get; init; }

    /// <summary>What the analyzer found the request to be. Null on an early fill until <see cref="Confirm"/> finds it.</summary>
    public Classification? Classification { get; set; }

    /// <summary>
    /// An early fill (ttl mode only): begun while the slow run before it was still waiting to be
    /// analyzed - back-to-back runs, as GO 4 sends them, come in a millisecond after the run before
    /// them ends. Asked once this answer has ended whether that run makes it one to keep, and what
    /// the request is; or null and why not.
    /// </summary>
    public Func<(Classification? Classification, string? Refused)>? Confirmation { get; init; }

    /// <summary>The request carried a pooled connection's reset, so its answer begins with the reset's ENVCHANGEs.</summary>
    public required bool Reset { get; init; }

    public required bool ColumnEncryption { get; init; }

    private byte[]? _buffer;
    private int _length, _reserved;

    public string? Abandoned { get; private set; }
    public int Length => _length;
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    public void Append(ReadOnlySpan<byte> data, long maxBytes, ByteBudget budget)
    {
        if (Abandoned is not null || data.IsEmpty) return;
        if ((long)_length + data.Length > maxBytes)
        {
            Abandon("larger than the entry size limit", budget);
            return;
        }

        if (_buffer is null || _length + data.Length > _reserved)
        {
            int size = (int)Math.Min(maxBytes, Math.Max((long)_length + data.Length, Math.Max(64 * 1024, (long)_reserved * 2)));
            if (!budget.TryReserve(size - _reserved))
            {
                Abandon("the fill budget is in use by other fills", budget);
                return;
            }

            byte[] bigger = ArrayPool<byte>.Shared.Rent(size);
            if (_buffer is not null)
            {
                _buffer.AsSpan(0, _length).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(_buffer);
            }
            _buffer = bigger;
            _reserved = size;
        }

        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    public void Abandon(string reason, ByteBudget budget)
    {
        Abandoned ??= reason;
        Release(budget);
    }

    /// <summary>
    /// Called on the relay as the answer ends, before this run is itself handed to the analyzer, so
    /// that only the runs before it count: an early fill learns what the request is, or is abandoned.
    /// </summary>
    public void Confirm(ByteBudget budget)
    {
        if (Confirmation is null || Abandoned is not null) return;
        var (classification, refused) = Confirmation();
        if (classification is null) Abandon(refused ?? "it was not confirmed as cacheable", budget);
        else Classification = classification;
    }

    public void Release(ByteBudget budget)
    {
        if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
        budget.Release(_reserved);
        _buffer = null;
        _length = _reserved = 0;
    }
}

/// <summary>
/// Whether a filled answer may be replayed to another session, and where its replay starts.
/// Everything not positively allowed is refused.
/// </summary>
internal static class ReplayCheck
{
    /// <summary>The tokens an ordinary query's answer is made of. Anything else is not cached.</summary>
    private static readonly byte[] Allowed =
    [
        TdsConstants.Token.ColMetadata, TdsConstants.Token.Row, TdsConstants.Token.NbcRow,
        TdsConstants.Token.Done, TdsConstants.Token.DoneProc, TdsConstants.Token.DoneInProc,
        TdsConstants.Token.Info, TdsConstants.Token.Order, TdsConstants.Token.ReturnStatus, TdsConstants.Token.ReturnValue,
        TdsConstants.Token.EnvChange, TdsConstants.Token.SessionState,
    ];

    /// <summary>The ENVCHANGE types a pooled connection's reset is answered with: it puts the session back as the login left it.</summary>
    private static readonly byte[] ResetChanges =
    [
        TdsConstants.EnvChangeType.ResetConnectionAck, TdsConstants.EnvChangeType.Database, TdsConstants.EnvChangeType.Language,
        TdsConstants.EnvChangeType.CharacterSet, TdsConstants.EnvChangeType.PacketSize, 0x05, 0x06, TdsConstants.EnvChangeType.SqlCollation,
    ];

    /// <summary>
    /// Null and the offset the replay starts at, or why the answer cannot be kept.
    ///
    /// SESSIONSTATE tokens - session recovery's numbered reports of state changes - are never stored:
    /// numbered for the session that filled, they would put another session's recovery state out of
    /// step. They are allowed only where <paramref name="stateExplained"/>: the request's reset or the
    /// batch's leading SETs made them, and a hit makes the server send fresh ones by doing the same.
    /// Anywhere else they mean the request changed the session in a way the cache cannot repeat.
    /// </summary>
    public static (string? Refused, int Start) Check(ResponseDetail detail, bool reset, bool stateExplained = false)
    {
        if (detail.Problem is not null) return ($"it could not be read to its end ({detail.Problem})", 0);
        if (detail.Errors.Count > 0) return ($"it carried an error ({detail.Errors[0].Number})", 0);
        if (detail.NoMetadata) return ("its results came without column metadata", 0);
        if (detail.EnvChangeAfterStart) return ("it changed the session", 0);
        if (detail.SessionStates.Count > 0 && !stateExplained) return ("it changed the session (session recovery state)", 0);

        for (int token = 0; token < 256; token++)
            if (detail.TokensSeen[token] && Array.IndexOf(Allowed, (byte)token) < 0)
                return ($"it held a {TdsConstants.TokenName((byte)token)} token", 0);

        if (detail.LeadingEnvChanges.Count == 0) return (null, 0);
        if (!reset) return ("it changed the session", 0);
        if (detail.LeadingEnvChanges.Any(t => Array.IndexOf(ResetChanges, t) < 0)) return ("it changed the session beyond its reset", 0);

        // The reset's own ENVCHANGEs are this session's; a hit gets fresh ones from its own reset.
        return (null, detail.LeadingEnvChangeEnd);
    }
}

/// <summary>A fill whose answer has ended, on its way to being checked and written.</summary>
internal sealed record CompletedFill(Fill Fill, double DurationMs, ushort DoneStatus, bool Attention);

/// <summary>
/// Checks and writes filled answers, one at a time, off the relay. Its queue is bounded; a fill that
/// cannot be queued is let go, and the query it came from was never affected either way.
/// </summary>
internal sealed class Filler(CacheOptions options, CacheIndex index, CacheStore store, ByteBudget budget, CacheCounters counters,
                             CacheSpace space) : IAsyncDisposable
{
    private readonly Channel<CompletedFill> _queue = Channel.CreateBounded<CompletedFill>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private Task? _worker;

    /// <summary>Told about every fill that ends: the key, and null when it was kept, or why not.</summary>
    public event Action<CacheKey, string?>? Finished;

    /// <summary>For tests: until this is done, the worker waits before each answer it checks and writes, as a slow disk would.</summary>
    internal Task? Held { get; set; }

    public void Start() => _worker = Task.Run(WorkAsync);

    /// <summary>
    /// Called on the relay when an answer has ended, before its last packet goes on to the client.
    /// Never waits. Until the answer is written, the same request coming in - the next run of GO 4,
    /// sent the moment this one ends - may wait for it rather than go to SQL Server (<see cref="CacheIndex.SaveOf"/>).
    /// </summary>
    public void Complete(CompletedFill completed)
    {
        var fill = completed.Fill;
        if (fill.Abandoned is null && !completed.Attention) index.BeginSave(fill.Key, fill.FirstPacketHash, completed.DurationMs);
        if (_queue.Writer.TryWrite(completed)) return;
        End(fill, "the filler is busy");
    }

    /// <summary>Called on the relay when a fill cannot go on: the session closed, the answer was cut off.</summary>
    public void Abandon(Fill fill, string reason) => End(fill, reason);

    private async Task WorkAsync()
    {
        await foreach (var completed in _queue.Reader.ReadAllAsync())
        {
            string? refused;
            try
            {
                if (Held is { } held) await held;
                refused = Keep(completed);
            }
            catch (Exception ex)
            {
                refused = $"it could not be written ({ex.GetType().Name}: {ex.Message})";
                Log.Warn("cache.fill_failed", $"a cached answer could not be written: {ex.Message}");
            }
            End(completed.Fill, refused);
        }
    }

    /// <summary>Null when the answer was kept; otherwise why it was not.</summary>
    internal string? Keep(CompletedFill completed)
    {
        var fill = completed.Fill;
        if (fill.Abandoned is { } abandoned) return abandoned;
        if (completed.Attention) return "the client cancelled it";
        if ((completed.DoneStatus & (TdsConstants.DoneStatus.Error | TdsConstants.DoneStatus.ServerError)) != 0) return "it ended in an error";
        if (completed.DurationMs < options.SlowMs) return "it was not slow this time";
        if (completed.DurationMs < options.MinBenefit * options.EstimatedReplayMs(fill.Length)) return "replaying it would not save enough";
        if (index.Epoch(fill.Server, fill.Database) != fill.Epoch) return "the database was flushed while it ran";
        if (fill.Classification is not { } classification) return "it was not confirmed as cacheable";

        var detail = ResponseReader.Read(fill.Span, fill.ColumnEncryption);
        bool stateExplained = fill.Reset || classification.LeadingSetText is not null;
        var (refused, start) = ReplayCheck.Check(detail, fill.Reset, stateExplained);
        if (refused is not null) return refused;

        long? handleOffset = null;
        if (classification.Prepares)
        {
            if (detail.OutputHandleOffset is not { } offset || offset < start) return "its prepared statement's handle was not found in it";
            handleOffset = PreparedStatements.StoredOffset(offset, start, detail.SessionStates);
        }

        byte[] stored = WithoutSessionStates(fill.Span, start, detail.SessionStates);

        // Room for it: under the cap and leaving the disk its minimum free, displacing only entries worth
        // less per byte than it (its size before compression stands in for its file: an underestimate
        // of its worth, so it displaces less, not more).
        double benefit = completed.DurationMs / Math.Max(1, stored.Length);
        if (!space.MakeRoom(stored.Length, benefit)) return "there is no room for it: the cache is full of answers worth more, or its disk is short of space";

        string path;
        long fileBytes;
        try
        {
            (path, fileBytes) = store.Write(fill.Key, stored);
        }
        catch (IOException) when (space.ShortOf(stored.Length))
        {
            // The disk filled under it - something else wrote there. Room once more, then one more try.
            if (!space.MakeRoom(stored.Length, benefit)) return "its disk filled up, and nothing worth less could make way";
            (path, fileBytes) = store.Write(fill.Key, stored);
        }
        index.Add(new CacheEntry
        {
            Key = fill.Key, Path = path, Length = stored.Length, FileBytes = fileBytes,
            Server = fill.Server, Database = fill.Database, Epoch = fill.Epoch, PolicyUpdatedUtc = fill.PolicyUpdatedUtc, Tracked = fill.Tracked, TargetLogin = fill.TargetLogin, CreatedUtc = DateTime.UtcNow,
            OriginalMs = completed.DurationMs, FirstPacketHash = fill.FirstPacketHash,
            LeadingSetText = classification.LeadingSetText, HandleOffset = handleOffset,
            ShapeHash = classification.ShapeHash, Shape = classification.Shape,
        });
        return null;
    }

    /// <summary>The answer from <paramref name="start"/>, with its SESSIONSTATE tokens taken out (see <see cref="ReplayCheck.Check"/>).</summary>
    internal static byte[] WithoutSessionStates(ReadOnlySpan<byte> answer, int start, List<(int Start, int Length)> states)
    {
        var kept = new byte[answer.Length - start - states.Where(s => s.Start >= start).Sum(s => s.Length)];
        int at = 0, from = start;
        foreach (var (stateStart, length) in states)
        {
            if (stateStart < start) continue;                   // in the reset's leading run, which is not kept anyway
            answer[from..stateStart].CopyTo(kept.AsSpan(at));
            at += stateStart - from;
            from = stateStart + length;
        }
        answer[from..].CopyTo(kept.AsSpan(at));
        return kept;
    }

    private void End(Fill fill, string? refused)
    {
        int length = fill.Length;
        fill.Release(budget);
        index.EndFill(fill.Key);
        if (refused is null)
        {
            counters.CountFilled();
            string shape = fill.Classification?.Shape ?? "";
            Log.Info("cache.filled", $"{fill.Server}/{fill.Database}: cached {length:N0} bytes for {shape[..Math.Min(80, shape.Length)]}");
        }
        else
        {
            counters.CountFillAbandoned(refused);
            // Each reason is logged the first few times it happens, then counted only (:status shows the counts).
            if (counters.FillsAbandoned.GetValueOrDefault(refused) <= 3)
                Log.Info("cache.not_kept", $"{fill.Server}/{fill.Database}: an answer was not cached: {refused}");
        }
        Finished?.Invoke(fill.Key, refused);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        while (_queue.Reader.TryRead(out var left)) End(left.Fill, "DaffyZoom is stopping");
    }
}
