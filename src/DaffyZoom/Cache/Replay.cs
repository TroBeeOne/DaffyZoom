namespace DaffyZoom.Cache;

/// <summary>
/// A cache hit, checked and ready: the entry, its verified content, and what the server must be
/// told on the session's account before the answer is sent - the request's reset, and the batch's
/// leading SET statements.
/// </summary>
internal sealed class CacheHit
{
    public required CacheEntry Entry { get; init; }
    public required EntryContent Content { get; init; }

    /// <summary>The request's reset flags, which the server must still act on.</summary>
    public required byte ResetFlags { get; init; }

    public required ulong Transaction { get; init; }

    /// <summary>
    /// For an sp_prepexec: DaffyZoom's own sp_prepare of the same statement (an RPC payload), which
    /// the server must answer with a handle before the replay can be sent - it carries the reset too.
    /// </summary>
    public byte[]? Prepare { get; init; }

    /// <summary>What to send the server first as a batch, or null when nothing needs to be: the leading SETs, or a no-op carrying the reset.</summary>
    public string? OwnBatch => Prepare is not null ? null : Entry.LeadingSetText ?? (ResetFlags != 0 ? "-- DaffyZoom: pooled connection reset" : null);
}

/// <summary>What the relay asks of the cache, for the sessions it may serve (not MARS, TDS 7.3 or later).</summary>
internal interface ICacheGate
{
    /// <summary>Requests larger than this are not held back to be looked at.</summary>
    int HoldLimit { get; }

    /// <summary>
    /// Whether a request starting with this packet might have a cached answer - one hash and one
    /// lookup, and false for nearly everything, which then streams through without being held.
    /// </summary>
    bool MightServe(ReadOnlySpan<byte> firstPacket);

    /// <summary>
    /// The hit for a whole held request, checked to its last byte before the first is sent - or null,
    /// and the relay forwards the request unchanged. Takes at most the hit deadline.
    /// </summary>
    Task<CacheHit?> TryHitAsync(IReadOnlyList<byte[]> packets, CancellationToken cancellationToken);

    /// <summary>
    /// The hit was not used after all (the relay found a reason), before anything was sent to the
    /// client. <paramref name="stale"/>: SQL Server would not do what the hit needed of it - the entry goes.
    /// </summary>
    void NotUsed(CacheHit hit, string reason, bool stale = false);

    /// <summary>The hit was sent, all of it or (when <paramref name="completed"/> is false) part.</summary>
    void Served(CacheHit hit, double milliseconds, bool completed);
}

/// <summary>
/// Frames a token stream into TDS packets as it is produced: the session's packet size, its SPID,
/// packet numbers from 1, end-of-message on the last. Tokens may cross packet boundaries anywhere,
/// as they do from SQL Server, so a stream filled on one session replays on another with a different
/// packet size. Each packet is handed on whole: over TLS each becomes its own record, which
/// SqlClient's native SNI needs (see Relay.WriteInPacketSizedPiecesAsync).
/// </summary>
internal sealed class ReplayFramer(int packetSize, ushort spid, Func<ReadOnlyMemory<byte>, ValueTask> emit)
{
    private readonly byte[] _packet = new byte[packetSize];
    private int _fill = Tds.TdsConstants.HeaderLength;
    private byte _packetId = 1;

    public long Bytes { get; private set; }

    public async ValueTask AppendAsync(ReadOnlyMemory<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_fill == _packet.Length) await EmitAsync(last: false);
            int take = Math.Min(_packet.Length - _fill, data.Length);
            data.Span[..take].CopyTo(_packet.AsSpan(_fill));
            _fill += take;
            data = data[take..];
        }
    }

    /// <summary>The last packet, with end-of-message - also when the stream was empty.</summary>
    public ValueTask FinishAsync() => EmitAsync(last: true);

    private async ValueTask EmitAsync(bool last)
    {
        _packet[0] = Tds.TdsConstants.PacketType.TabularResult;
        _packet[1] = last ? Tds.TdsConstants.PacketStatus.EndOfMessage : Tds.TdsConstants.PacketStatus.Normal;
        _packet[2] = (byte)(_fill >> 8);
        _packet[3] = (byte)_fill;
        _packet[4] = (byte)(spid >> 8);
        _packet[5] = (byte)spid;
        _packet[6] = _packetId;
        _packet[7] = 0;

        await emit(_packet.AsMemory(0, _fill));
        Bytes += _fill;
        _packetId = unchecked((byte)(_packetId + 1));
        _fill = Tds.TdsConstants.HeaderLength;
    }
}

/// <summary>
/// Stops serving for a while after repeated failures of the hit path - the disk erroring, entries
/// that will not verify, deadlines missed - so that a fault costs the deadline a few times, not on
/// every request. Opens after <c>threshold</c> failures within a minute; stays open for a back-off
/// that doubles each time (30 s up to 10 min) and resets after a clean stretch. Logged when it opens
/// and when it closes, not on every failure.
/// </summary>
internal sealed class CircuitBreaker(int threshold = 5)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Queue<DateTime> _failures = new();
    private DateTime? _openUntil;
    private TimeSpan _backoff = FirstBackoff;
    private DateTime _lastFailure;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                if (_openUntil is not { } until) return false;
                if (DateTime.UtcNow < until) return true;
                _openUntil = null;
                Log.Info("cache.resumed", "serving from the cache again after a pause");
                return false;
            }
        }
    }

    public DateTime? OpenUntilUtc { get { lock (_gate) return _openUntil; } }

    public void Failure(string reason)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastFailure > MaxBackoff * 2) _backoff = FirstBackoff;   // a clean stretch: start gently again
            _lastFailure = now;

            _failures.Enqueue(now);
            while (_failures.Count > 0 && now - _failures.Peek() > Window) _failures.Dequeue();
            if (_openUntil is not null || _failures.Count < threshold) return;

            _openUntil = now + _backoff;
            Log.Warn("cache.paused",
                $"serving from the cache is paused for {_backoff.TotalSeconds:F0}s after {_failures.Count} failures in a minute " +
                $"(the last: {reason}); every request goes to SQL Server meanwhile");
            _backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, _backoff.Ticks * 2));
            _failures.Clear();
        }
    }
}
