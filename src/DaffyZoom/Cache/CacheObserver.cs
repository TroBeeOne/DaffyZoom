using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>
/// The cache's eyes and hands on one session (not MARS: those are relayed only).
///
/// As an observer it follows the session's context - database, settings, taint - through every
/// request, pairs each request with its response, and times it; those slower than the threshold go
/// to the analyzer. When a request's answer is to be cached it copies the answer as it streams by,
/// and when a request writes to a ttl database it has the database flushed. As the relay's gate it
/// says whether a held request has a cached answer, in the same context the observer will then
/// record for it.
///
/// It runs on the relay's threads with the relay's buffers, and keeps the relay's rules: no waiting,
/// no I/O, copies only within their budgets. The bytes are seen before they are forwarded, in both
/// directions (see <see cref="IRelayObserver"/>), so a request's effects on the context are recorded
/// before its answer reaches the client, and the client's next request meets the context it left.
///
/// TDS on a non-MARS session is strictly one request, then its response; an attention (cancel) is
/// the one exception, and is followed the way DaffyTee's tap follows it.
/// </summary>
internal sealed class CacheObserver : IRelayObserver, ICacheGate
{
    private readonly CacheOptions _options;
    private readonly CacheCounters _counters;
    private readonly ByteBudget _budget;
    private readonly Func<SlowRequest, bool> _submit;
    private readonly ProxySession _session;
    private readonly SessionContext _context;
    private readonly ZoomCache? _cache;
    private readonly string? _sessionIneligible;

    private readonly PacketTracker _requestTracker = new();
    private readonly PacketTracker _responseTracker = new();
    private readonly RequestListener _requests;
    private readonly ResponseListener _responses;

    private readonly object _gate = new();
    private Exchange? _awaiting;

    /// <summary>A database written to inside a transaction: flushed again when the transaction is over.</summary>
    private string? _flushAfterTransaction;

    public CacheObserver(CacheOptions options, CacheCounters counters, ByteBudget budget, Func<SlowRequest, bool> submit,
                         ProxySession session, SessionContext context, ZoomCache? cache = null)
    {
        _options = options;
        _counters = counters;
        _budget = budget;
        _submit = submit;
        _session = session;
        _context = context;
        _cache = cache;
        _requests = new RequestListener(this);
        _responses = new ResponseListener(this);

        bool modern = session.TdsVersion >= TdsConstants.TdsVersion73 || session.TdsVersion == TdsConstants.TdsVersion80;
        _sessionIneligible = modern ? null : "TDS older than 7.3";
    }

    /// <summary>The session's context as it stands. For tests and the prompt.</summary>
    public ContextSnapshot Context
    {
        get { lock (_gate) return _context.Snapshot(); }
    }

    public void OnClientBytes(ReadOnlySpan<byte> data, long timestamp)
    {
        _requests.Timestamp = timestamp;
        _requestTracker.Feed(data, _requests);
    }

    public void OnServerBytes(ReadOnlySpan<byte> data, long timestamp)
    {
        _responses.Timestamp = timestamp;
        _responseTracker.Feed(data, _responses);
    }

    public void OnClosed(string reason)
    {
        Exchange? awaiting;
        lock (_gate)
        {
            awaiting = _awaiting;
            _awaiting = null;
        }
        Release(awaiting, "the session ended");
        Release(_requests.TakeCurrent(), "the session ended");
        _responses.Discard();
    }

    private void Release(Exchange? exchange, string reason)
    {
        if (exchange is null) return;
        if (exchange.Fill is { } fill) _cache?.Filler.Abandon(fill, reason);
        exchange.Fill = null;
        exchange.Release(_budget);
    }

    /// <summary>One request and, in time, how its answer went.</summary>
    private sealed class Exchange
    {
        public required byte Type { get; init; }
        public required bool Reset { get; init; }
        public required long StartTimestamp { get; init; }
        public required DateTime StartUtc { get; init; }
        public long EndTimestamp { get; set; }


        public byte[]? Payload { get; private set; }
        public int Length { get; private set; }
        public int Reserved { get; private set; }

        /// <summary>Not all of the request was copied: past the size limit, or the budget ran out.</summary>
        public bool Truncated { get; private set; }

        public ContextSnapshot Context { get; set; }
        public string? Ineligible { get; set; }
        public bool ScanResponse { get; set; }
        public bool Attention { get; set; }
        public bool Writes { get; set; }
        public Fill? Fill { get; set; }

        /// <summary>The request's key, when it is eligible: handed on to the analyzer with it.</summary>
        public CacheKey? Key { get; set; }

        public ReadOnlySpan<byte> Span => Payload.AsSpan(0, Length);

        public void Append(ReadOnlySpan<byte> data, int limit, ByteBudget budget, CacheCounters counters)
        {
            if (Truncated) return;
            if (Length + data.Length > limit)
            {
                Truncated = true;
                return;
            }

            if (Payload is null || Length + data.Length > Reserved)
            {
                int size = Math.Min(limit, Math.Max(Length + data.Length, Math.Max(4096, Reserved * 2)));
                if (!budget.TryReserve(size - Reserved))
                {
                    Truncated = true;
                    counters.CountNotCopied();
                    return;
                }

                byte[] bigger = ArrayPool<byte>.Shared.Rent(size);
                if (Payload is not null)
                {
                    Payload.AsSpan(0, Length).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(Payload);
                }
                Payload = bigger;
                Reserved = size;
            }

            data.CopyTo(Payload.AsSpan(Length));
            Length += data.Length;
        }

        /// <summary>Hands the copy over to a <see cref="SlowRequest"/>, which will release it.</summary>
        public (byte[] Payload, int Length, int Reserved) Take()
        {
            var taken = (Payload!, Length, Reserved);
            Payload = null;
            Length = Reserved = 0;
            return taken;
        }

        public void Release(ByteBudget budget)
        {
            if (Payload is not null) ArrayPool<byte>.Shared.Return(Payload);
            budget.Release(Reserved);
            Payload = null;
            Length = Reserved = 0;
        }
    }

    // ---- the gate (client pump, before the request is forwarded) ----------------------------------

    public int HoldLimit => _options.MaxRequestBytes;

    public bool MightServe(ReadOnlySpan<byte> firstPacket) =>
        _cache is not null && _sessionIneligible is null && firstPacket.Length > TdsConstants.HeaderLength
        && firstPacket[0] is TdsConstants.PacketType.SqlBatch or TdsConstants.PacketType.Rpc
        && _cache.MightHit(CacheIndex.PrefixHash(firstPacket[0], firstPacket[TdsConstants.HeaderLength..]));

    public async Task<CacheHit?> TryHitAsync(IReadOnlyList<byte[]> packets, CancellationToken cancellationToken)
    {
        if (_cache is null) return null;

        byte type = packets[0][0];
        byte reset = (byte)(packets[0][1] & (TdsConstants.PacketStatus.ResetConnection | TdsConstants.PacketStatus.ResetConnectionSkipTran));
        byte[] payload = new byte[packets.Sum(p => p.Length - TdsConstants.HeaderLength)];
        int offset = 0;
        foreach (var packet in packets)
        {
            packet.AsSpan(TdsConstants.HeaderLength).CopyTo(payload.AsSpan(offset));
            offset += packet.Length - TdsConstants.HeaderLength;
        }

        // The context this request will run in - after its reset, if it carries one - exactly as the
        // observer will record it when the relay shows it the request.
        ContextSnapshot context;
        lock (_gate) context = reset != 0 ? _context.SnapshotAfterReset() : _context.Snapshot();
        if (context.Taint is not null || context.Database is null) return null;

        ulong transaction = AllHeaders.TransactionDescriptor(payload) ?? 0;
        if (transaction != 0 || AllHeaders.Has(payload, TdsConstants.HeaderType.QueryNotifications)) return null;

        return await _cache.TryHitAsync(context, type, payload, reset, transaction, _session.Login ?? "", cancellationToken);
    }

    public void NotUsed(CacheHit hit, string reason, bool stale = false)
    {
        _counters.CountFallback(reason);
        if (stale) _cache?.Index.Remove(hit.Entry);
    }

    public void Served(CacheHit hit, double milliseconds, bool completed) => _cache?.Served(hit, milliseconds, completed);

    // ---- the client's half (client pump) ----------------------------------------------------------

    private sealed class RequestListener(CacheObserver owner) : IPacketListener
    {
        private Exchange? _current;
        private bool _inAttention;

        public long Timestamp { get; set; }

        public Exchange? TakeCurrent()
        {
            var current = _current;
            _current = null;
            return current;
        }

        public void OnPacketStart(in PacketHeader header)
        {
            if (header.Type == TdsConstants.PacketType.Attention)
            {
                _inAttention = true;
                owner.Attention();
                return;
            }

            _current ??= new Exchange
            {
                Type = header.Type, Reset = header.ResetsConnection, StartTimestamp = Timestamp, StartUtc = DateTime.UtcNow,
            };
        }

        public void OnPacketPayload(ReadOnlySpan<byte> payload)
        {
            if (_inAttention || _current is null) return;
            if (_current.Type is TdsConstants.PacketType.SqlBatch or TdsConstants.PacketType.Rpc)
                _current.Append(payload, owner._options.MaxRequestBytes, owner._budget, owner._counters);
        }

        public void OnPacketEnd(in PacketHeader header)
        {
            if (_inAttention)
            {
                _inAttention = false;
                return;
            }

            if (_current is null) return;
            if (!header.IsEndOfMessage) return;

            var finished = _current;
            _current = null;
            finished.EndTimestamp = Timestamp;
            owner.RequestComplete(finished);
        }
    }

    /// <summary>
    /// A whole request has been seen, and is about to go to the target (or be answered from the cache).
    /// Its context is the session's as it stands, after a reset if it carries one; whether its answer
    /// is to be cached is decided now, before the first byte of the answer can come back; what it does
    /// to the session is recorded for the requests after it.
    /// </summary>
    private void RequestComplete(Exchange exchange)
    {
        Exchange? superseded;
        string? flushNow = null;
        lock (_gate)
        {
            if (exchange.Reset)
            {
                _context.Reset();
                _session.Database = _context.Database;
            }

            exchange.Context = _context.Snapshot();
            exchange.Ineligible = Ineligible(exchange);
            var payload = exchange.Span;
            ulong transaction = exchange.Length > 0 ? AllHeaders.TransactionDescriptor(payload) ?? 0 : 0;

            CacheKey? key = null;
            if (exchange.Ineligible is null && _cache is not null) key = exchange.Key = CacheKey.Compute(exchange.Context, exchange.Type, payload);

            bool writes = false;
            if (exchange.Type == TdsConstants.PacketType.SqlBatch)
            {
                if (exchange.Truncated)
                {
                    // What a batch this large does to the session cannot be read, so assume the worst.
                    _context.TaintWith("a batch too large to read");
                    writes = true;
                }
                else
                {
                    var body = payload[AllHeaders.BodyOffset(payload)..];
                    var text = MemoryMarshal.Cast<byte, char>(body[..(body.Length & ~1)]);
                    var effects = BatchText.Scan(text);
                    if (effects.Taint is { } taint) _context.TaintWith(taint);
                    if (effects.ChangesSettings) _context.ApplySettings(effects, text);
                    if (effects.QuotedIdentifier is { } quoted) _context.QuotedIdentifier = quoted;
                    exchange.ScanResponse = effects.Use;
                    writes = effects.Writes;
                }
            }
            else if (exchange.Type == TdsConstants.PacketType.Rpc)
            {
                if (BatchText.RpcTaint(payload) is { } taint) _context.TaintWith(taint);
                writes = BatchText.RpcWrites(payload);
            }
            else if (exchange.Type == TdsConstants.PacketType.BulkLoad)
            {
                writes = true;
            }

            // A request known to be read-only does not flush, whatever words its text happens to hold:
            // a report whose WHERE says 'Update' would otherwise flush itself on every hit.
            if (writes && key is { } known && _cache is not null && _cache.KnownReadOnly(known)) writes = false;
            exchange.Writes = writes;

            // Whether its answer is to be copied is decided now, from the context it was sent in - and
            // knowing whether its text may write, which keeps an early fill off a request that might.
            if (key is { } eligible)
                exchange.Fill = _cache!.TryBeginFill(exchange.Context, eligible, exchange.Type, payload,
                                                     exchange.Reset, _session.ColumnEncryption, mayWrite: writes);

            if (writes && transaction != 0) _flushAfterTransaction ??= exchange.Context.Database;

            // A write inside a transaction flushes when it is made, and again once the transaction is
            // over, so that no answer filled meanwhile, from before the commit, outlives it.
            if (transaction == 0 && _flushAfterTransaction is { } pending)
            {
                flushNow = pending;
                _flushAfterTransaction = null;
            }

            superseded = _awaiting;
            _awaiting = exchange;
        }

        if (flushNow is not null) _cache?.WriteSeen(_context.Target, flushNow, "a transaction that wrote");

        // Only if a response went missing: let the old one go rather than pair it wrongly.
        Release(superseded, "its answer went missing");

        if (exchange.Type is TdsConstants.PacketType.SqlBatch or TdsConstants.PacketType.Rpc)
        {
            _counters.CountRequest();
            if (exchange.Ineligible is { } reason) _counters.CountBypass(reason);
            else _counters.CountEligible();
        }
    }

    /// <summary>Why this request cannot be considered, or null when it can.</summary>
    private string? Ineligible(Exchange exchange)
    {
        if (exchange.Type is not (TdsConstants.PacketType.SqlBatch or TdsConstants.PacketType.Rpc)) return "not a query";
        if (_sessionIneligible is not null) return _sessionIneligible;
        if (exchange.Context.Taint is not null) return "session context changed (until reset)";
        if (exchange.Context.Database is null) return "database unknown";
        if (exchange.Truncated) return "request too large";

        var payload = exchange.Span;
        if ((AllHeaders.TransactionDescriptor(payload) ?? 0) != 0) return "in a transaction";
        if (AllHeaders.Has(payload, TdsConstants.HeaderType.QueryNotifications)) return "query notification";
        return null;
    }

    private void Attention()
    {
        lock (_gate)
        {
            if (_awaiting is not null) _awaiting.Attention = true;
        }
    }

    // ---- the target's half (server pump) ----------------------------------------------------------

    /// <summary>Where each response starts and ends, its size, its final DONE, the fill's copy, and - for a USE - its tokens.</summary>
    private sealed class ResponseListener(CacheObserver owner) : IPacketListener
    {
        private const int TailLength = 13;
        private readonly byte[] _tail = new byte[TailLength];
        private int _tailFill;
        private bool _inResponse;

        private byte[]? _buffer;
        private int _bufferLength;
        private bool _scan, _overflow;
        private Fill? _fill;

        public long Timestamp { get; set; }
        public long StartTimestamp { get; private set; }
        public long Bytes { get; private set; }
        public bool Overflow => _overflow;
        public ReadOnlySpan<byte> Buffered => _buffer.AsSpan(0, _bufferLength);

        /// <summary>The final DONE's status: the last token of every response is a DONE, DONEPROC or DONEINPROC.</summary>
        public ushort DoneStatus => FinalDoneStatus(_tail.AsSpan(0, _tailFill), owner._session.TdsVersion);

        public void OnPacketStart(in PacketHeader header)
        {
            if (_inResponse) return;
            _inResponse = true;
            StartTimestamp = Timestamp;
            Bytes = 0;
            _tailFill = 0;
            (_scan, _fill) = owner.WhatTheAnswerIsFor();
            _bufferLength = 0;
            _overflow = false;
        }

        public void OnPacketPayload(ReadOnlySpan<byte> payload)
        {
            Bytes += payload.Length;
            KeepTail(payload);
            if (_scan && !_overflow) Buffer(payload);
            _fill?.Append(payload, owner._options.MaxEntryBytes, owner._cache!.FillBudget);
        }

        public void OnPacketEnd(in PacketHeader header)
        {
            if (!header.IsEndOfMessage) return;
            _inResponse = false;
            _fill = null;
            owner.ResponseComplete(this);
        }

        public void Discard()
        {
            if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
            _bufferLength = 0;
        }

        private void Buffer(ReadOnlySpan<byte> payload)
        {
            int limit = owner._options.ResponseScanBytes;
            if (_bufferLength + payload.Length > limit)
            {
                _overflow = true;
                return;
            }

            _buffer ??= ArrayPool<byte>.Shared.Rent(limit);
            payload.CopyTo(_buffer.AsSpan(_bufferLength));
            _bufferLength += payload.Length;
        }

        private void KeepTail(ReadOnlySpan<byte> payload)
        {
            if (payload.Length >= TailLength)
            {
                payload[^TailLength..].CopyTo(_tail);
                _tailFill = TailLength;
                return;
            }

            int keep = Math.Min(_tailFill, TailLength - payload.Length);
            _tail.AsSpan(_tailFill - keep, keep).CopyTo(_tail);
            payload.CopyTo(_tail.AsSpan(keep));
            _tailFill = keep + payload.Length;
        }
    }

    /// <summary>
    /// The status of the DONE a response ends with, from its last bytes: token, status, curcmd and an
    /// 8-byte row count - the last 13 - or, before TDS 7.2, a 4-byte one in the last 9. Read at 13
    /// bytes, a TDS 7.1 response's status would be missed, and with it a cancel's acknowledgement.
    /// 0 when the response does not end in one.
    /// </summary>
    internal static ushort FinalDoneStatus(ReadOnlySpan<byte> tail, uint tdsVersion)
    {
        int at = tail.Length - (TdsConstants.DoneLength(tdsVersion) + 1);
        return at >= 0 && tail[at] is TdsConstants.Token.Done or TdsConstants.Token.DoneProc or TdsConstants.Token.DoneInProc
            ? (ushort)(tail[at + 1] | (tail[at + 2] << 8))
            : (ushort)0;
    }

    /// <summary>For the answer about to arrive: whether its tokens must be read (a USE), and the fill copying it, if any.</summary>
    private (bool Scan, Fill? Fill) WhatTheAnswerIsFor()
    {
        lock (_gate) return _awaiting is { } awaiting ? (awaiting.ScanResponse, awaiting.Fill) : (false, null);
    }

    /// <summary>
    /// A whole response has been seen, and is about to go to the client. For a USE, the database it
    /// left the session in is read from it now - before the client can send anything else. A fill's
    /// copy goes to the filler, a write has its database flushed, and the request is either let go
    /// or, being eligible and slow, handed to the analyzer.
    /// </summary>
    private void ResponseComplete(ResponseListener response)
    {
        Exchange? exchange;
        ushort done = response.DoneStatus;
        string? movedTo = null;
        lock (_gate)
        {
            exchange = _awaiting;
            _awaiting = null;
            if (exchange is { ScanResponse: true })
            {
                _context.Database = DatabaseAfter(response);
                movedTo = _context.Database;
                _session.Database = _context.Database;      // what daffyzoom.status and :sessions show: null while unknown
            }
        }

        // Normally the acknowledgement of an attention that came after its request had been answered.
        if (exchange is null) return;

        if ((done & TdsConstants.DoneStatus.Attention) != 0) exchange.Attention = true;
        double durationMs = Stopwatch.GetElapsedTime(exchange.StartTimestamp, response.Timestamp).TotalMilliseconds;

        if (exchange.Fill is { } fill)
        {
            exchange.Fill = null;
            fill.Confirm(_cache!.FillBudget);       // an early fill: before this run is handed to the analyzer below
            _cache.Filler.Complete(new CompletedFill(fill, durationMs, done, exchange.Attention));
        }

        if (exchange.Writes && _cache is not null)
        {
            _cache.WriteSeen(_context.Target, exchange.Context.Database, "a write");
            if (movedTo is not null && movedTo != exchange.Context.Database) _cache.WriteSeen(_context.Target, movedTo, "a write");
        }

        if (exchange.Ineligible is null && !exchange.Attention && durationMs >= _options.SlowMs && exchange.Payload is not null)
        {
            _counters.CountSlow();
            var (payload, length, reserved) = exchange.Take();
            _submit(new SlowRequest
            {
                Type = exchange.Type,
                Payload = payload,
                Length = length,
                Reserved = reserved,
                Context = exchange.Context,
                Session = _session.Id,
                Login = _session.Login ?? "",
                App = _session.AppName ?? "",
                StartedUtc = exchange.StartUtc,
                DurationMs = durationMs,
                FirstByteMs = response.StartTimestamp >= exchange.EndTimestamp
                    ? Stopwatch.GetElapsedTime(exchange.EndTimestamp, response.StartTimestamp).TotalMilliseconds
                    : null,
                ResponseBytes = response.Bytes,
                DoneStatus = done,
                Key = exchange.Key,
            });
        }
        else
        {
            exchange.Release(_budget);
        }
    }

    /// <summary>
    /// The database a USE left the session in: the last database ENVCHANGE in its answer, or the one
    /// it was already in if there is none (the USE failed). Unknown - null - when the answer was too
    /// large to keep or could not be walked to its end, because a later ENVCHANGE cannot be ruled out.
    /// </summary>
    private string? DatabaseAfter(ResponseListener response)
    {
        if (response.Overflow) return null;
        var detail = ResponseReader.Read(response.Buffered, _session.ColumnEncryption, tdsVersion: _session.TdsVersion);
        if (detail.Problem is not null) return null;
        return detail.Database ?? _context.Database;
    }
}
