using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DaffyZoom.Commands;
using DaffyZoom.Tds;

namespace DaffyZoom.Proxy;

/// <summary>
/// Watches a session's traffic as it passes. Called synchronously on the relay's own thread with
/// the relay's own buffer - so an observer must be quick, must copy anything it wants to keep, and
/// must never wait on anything.
///
/// Bytes are shown just BEFORE they are forwarded, in both directions. That puts what observers see
/// in causal order: a request is seen before the target can answer it, and an answer is seen before
/// the client can react to it with its next request. Observing after forwarding lets a fast pair
/// overtake itself - a BEGIN TRAN acknowledged in microseconds, the client already sending the next
/// statement before the answer has been noticed - and an observer then pairs responses with the wrong
/// requests. (Found by 200 sessions at 20,000 requests a second.)
/// </summary>
internal interface IRelayObserver
{
    void OnClientBytes(ReadOnlySpan<byte> data, long timestamp);
    void OnServerBytes(ReadOnlySpan<byte> data, long timestamp);
    void OnClosed(string reason);

    /// <summary>
    /// The request about to be shown is a daffyzoom.* command - or a refused attempt at one - which
    /// DaffyZoom answers itself. Its answer follows as server bytes, as any answer would, but neither
    /// ever reaches the target. <paramref name="always"/> marks what belongs on the record whatever
    /// the capture settings: admin commands, and refusals.
    /// </summary>
    void OnGatewayCommand(string command, bool always) { }
}

/// <summary>
/// After login, the whole of a session: two pumps, one each way, that read from one side and write
/// to the other.
///
/// Nothing is parsed, reframed or re-encoded on the way through, and observation - the cache, the
/// counters, the transcript - costs the conversation at most a memory copy: it never waits on a
/// queue, a disk or a broker. The two TLS sessions are independent, but the bytes inside them are
/// the same bytes: the client's packets reach the target exactly as sent, and the target's replies
/// reach the client exactly as sent, SPID, packet numbers, MARS framing and all.
///
/// The one exception is a daffyzoom.* command, which DaffyZoom answers itself (see
/// <see cref="CommandParser"/>). To spot one, the client's side is read packet by packet and the
/// first packet of each request is glanced at: an RPC's procedure name, or a batch's text, searched
/// for "daffyzoom". Only a request that mentions it is held back and read whole; everything else goes
/// straight on. Under MARS, where the client's packets are wrapped in SMP frames whose sequence
/// numbers the target keeps, no answer could be slipped in without rewriting every later frame, so
/// MARS sessions are relayed exactly as before and their commands reach the target, which does not
/// know them.
///
/// When either side closes, both are closed. A client that goes away takes its upstream session
/// with it, so the server rolls back whatever that session left open, exactly as it would have if
/// the client had been connected directly.
/// </summary>
internal sealed class Relay
{
    /// <summary>Four TLS records' worth; one read usually carries a whole packet at the default size.</summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>A request that mentions daffyzoom is read whole before deciding, up to this size. A command is a line or two.</summary>
    private const int InspectLimit = 64 * 1024;

    /// <summary>DaffyZoom's own request is a comment with a reset flag; a target this slow to answer it is not answering.</summary>
    private static readonly TimeSpan OwnRequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly SearchValues<string> SchemaName =
        SearchValues.Create([CommandParser.Schema], StringComparison.OrdinalIgnoreCase);

    private readonly ProxySession _session;
    private readonly Stream _client, _upstream;
    private readonly int _packetSize;
    private readonly Observers _observers;
    private readonly IGatewayCommands? _commands;
    private readonly Cache.ICacheGate? _gate;

    /// <summary>
    /// The client's connection has two writers - the pump relaying the target's answers, and the
    /// answer to a daffyzoom.* command - so each write, and the observing that goes with it, takes a
    /// turn. They never actually meet, because a client sends its next request only after reading
    /// the whole answer to the last; the lock makes that a guarantee rather than an assumption.
    /// </summary>
    private readonly SemaphoreSlim _toClient = new(1, 1);

    private readonly CancellationTokenSource _closing = new();
    private Diversion? _diversion;

    private Relay(ProxySession session, Stream client, Stream upstream, int packetSize,
                  IReadOnlyList<IRelayObserver> observers, IGatewayCommands? commands, Cache.ICacheGate? gate)
    {
        _session = session;
        _client = client;
        _upstream = upstream;
        _packetSize = packetSize;
        _observers = new Observers(session, observers);
        _commands = commands;
        _gate = gate;
    }

    /// <summary>
    /// Relays a session. <paramref name="packetSize"/> is what client and target agreed at login, and
    /// caps how much is written in one go - see <see cref="WriteInPacketSizedPiecesAsync"/>.
    /// <paramref name="commands"/>, when given, answers daffyzoom.* commands; leave it out for a MARS
    /// session. <paramref name="early"/> is whatever the login handshake read past LOGIN7.
    /// </summary>
    public static Task RunAsync(ProxySession session, Stream client, Stream upstream, int packetSize,
                                IReadOnlyList<IRelayObserver> observers, IGatewayCommands? commands = null,
                                ReadOnlyMemory<byte> early = default, Cache.ICacheGate? gate = null) =>
        new Relay(session, client, upstream, packetSize, observers, commands, gate).RunAsync(early);

    private async Task RunAsync(ReadOnlyMemory<byte> early)
    {
        var fromClient = _commands is null && _gate is null ? PumpFromClientAsync(early) : RequestsFromClientAsync(early);
        var fromServer = PumpFromServerAsync();

        var first = await Task.WhenAny(fromClient, fromServer);
        string reason = first == fromClient ? "client disconnected" : "target disconnected";
        if (first.IsFaulted) reason += $" ({Describe(first.Exception!.InnerException!)})";

        _session.Abort(reason);
        _closing.Cancel();
        try { await Task.WhenAll(fromClient, fromServer); } catch { /* the second one fails by design */ }

        _observers.Closed(_session.CloseReason ?? reason);
    }

    /// <summary>
    /// Writes at most one packet's worth per call. Over TLS each write becomes its own record(s), and
    /// SQL Server sends every TDS packet as its own record, so a client never sees a record bigger
    /// than the packet size it negotiated. SqlClient's native SNI on Windows relies on that: handed a
    /// full 16 KB record on an 8000-byte packet size it fails fast inside SNI.dll (0xC0000409) -
    /// found by relaying a 400 KB nvarchar(max). Matching the server's record sizes costs nothing.
    /// </summary>
    public static async ValueTask WriteInPacketSizedPiecesAsync(Stream destination, ReadOnlyMemory<byte> data, int packetSize,
                                                                CancellationToken cancellationToken = default)
    {
        while (data.Length > packetSize)
        {
            await destination.WriteAsync(data[..packetSize], cancellationToken);
            data = data[packetSize..];
        }

        if (!data.IsEmpty) await destination.WriteAsync(data, cancellationToken);
    }

    // ---- client to target ----------------------------------------------------------------------

    /// <summary>The plain pump, for sessions whose commands are not DaffyZoom's to answer (MARS).</summary>
    private async Task PumpFromClientAsync(ReadOnlyMemory<byte> early)
    {
        if (!early.IsEmpty) await ForwardAsync(early, Stopwatch.GetTimestamp());

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                int read = await _client.ReadAsync(buffer.AsMemory(0, BufferSize));
                if (read == 0) return;
                await ForwardAsync(buffer.AsMemory(0, read), Stopwatch.GetTimestamp());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask ForwardAsync(ReadOnlyMemory<byte> data, long timestamp)
    {
        // Observed before forwarding - see IRelayObserver for why the order matters. The cost is at
        // most a memory copy of what is kept, microseconds.
        _session.AddClientBytes(data.Length);
        _observers.Client(data.Span, timestamp);
        await WriteInPacketSizedPiecesAsync(_upstream, data, _packetSize);
    }

    /// <summary>The pump that knows where each request begins, so daffyzoom.* commands can be answered here.</summary>
    private async Task RequestsFromClientAsync(ReadOnlyMemory<byte> early)
    {
        var reader = new ClientPacketReader(_client, early.Span);
        bool startsMessage = true;

        while (await reader.ReadAsync() is { } packet)
        {
            long timestamp = Stopwatch.GetTimestamp();

            if (startsMessage && _commands is not null && MightBeCommand(packet.Span))
            {
                _session.AddClientBytes(packet.Length);
                startsMessage = await InspectAsync(reader, packet, timestamp);
                continue;
            }

            if (startsMessage && _gate is { } gate && gate.MightServe(packet.Span))
            {
                _session.AddClientBytes(packet.Length);
                startsMessage = await ServeOrForwardAsync(reader, packet, timestamp, gate);
                continue;
            }

            await ForwardAsync(packet, timestamp);
            startsMessage = IsEndOfMessage(packet.Span);
        }
    }

    /// <summary>
    /// Whether a request's first packet mentions the daffyzoom schema: an RPC's procedure name, or a
    /// batch's text, searched without regard to case. This is all that ordinary traffic pays for
    /// commands - a vectorised search of one packet - and a false positive costs only a closer look.
    /// </summary>
    internal static bool MightBeCommand(ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= TdsConstants.HeaderLength) return false;
        byte type = packet[0];
        if (type is not (TdsConstants.PacketType.SqlBatch or TdsConstants.PacketType.Rpc)) return false;

        var payload = packet[TdsConstants.HeaderLength..];
        var text = payload[AllHeaders.BodyOffset(payload)..];

        if (type == TdsConstants.PacketType.Rpc)
        {
            if (text.Length < 2) return false;
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(text);
            if (nameLength == TdsConstants.RpcProcIdSwitch) return false;   // a system procedure by number
            text = text.Slice(2, Math.Min(nameLength * 2, text.Length - 2));
        }

        // The text is UTF-16, little-endian, as .NET's own strings are.
        return MemoryMarshal.Cast<byte, char>(text[..(text.Length & ~1)]).ContainsAny(SchemaName);
    }

    /// <summary>
    /// The first packet of a request mentions daffyzoom. Reads the rest of the request - up to
    /// <see cref="InspectLimit"/> - before anything is sent, then answers it here if it is a command,
    /// or sends it on unchanged if it is not. Returns whether the next packet starts a request.
    /// </summary>
    private async Task<bool> InspectAsync(ClientPacketReader reader, ReadOnlyMemory<byte> first, long timestamp)
    {
        var held = new List<byte[]> { first.ToArray() };
        int total = first.Length;
        bool complete = IsEndOfMessage(first.Span);

        while (!complete && total <= InspectLimit)
        {
            var next = await reader.ReadAsync() ?? throw new EndOfStreamException("the client disconnected part-way through a request");
            _session.AddClientBytes(next.Length);
            held.Add(next.ToArray());
            total += next.Length;
            complete = IsEndOfMessage(next.Span);
        }

        if (complete && Recognise(held) is { } recognised)
        {
            await AnswerAsync(held, recognised, timestamp);
            return true;
        }

        // Not a command after all - "daffyzoom" in a comment or a string - so it may still have an
        // answer in the cache.
        if (complete && _gate is { } gate && gate.MightServe(held[0]) && await TryServeHeldAsync(held, timestamp, gate)) return true;

        await ForwardHeldAsync(held, timestamp);
        return complete;
    }

    /// <summary>Sends held packets on to the target, shown to the observers first, exactly as if they had never been held.</summary>
    private async Task ForwardHeldAsync(List<byte[]> held, long timestamp)
    {
        foreach (var packet in held)
        {
            _observers.Client(packet, timestamp);
            await _upstream.WriteAsync(packet);
        }
    }

    /// <summary>A request DaffyZoom will answer: a command to run, or one it has to refuse.</summary>
    internal sealed record Recognised(string Name, DaffyCommand? Command, GatewayException? Refusal, bool Rpc, byte ResetFlags, ulong Transaction);

    /// <summary>The command a whole request is, a refusal of it, or null when it is the target's after all.</summary>
    internal static Recognised? Recognise(IReadOnlyList<byte[]> packets)
    {
        byte[] payload = Join(packets);
        bool rpc = packets[0][0] == TdsConstants.PacketType.Rpc;
        byte reset = (byte)(packets[0][1] & (TdsConstants.PacketStatus.ResetConnection | TdsConstants.PacketStatus.ResetConnectionSkipTran));
        ulong transaction = AllHeaders.TransactionDescriptor(payload) ?? 0;

        try
        {
            var command = rpc ? CommandParser.FromRpc(RequestParser.ParseRpc(payload))
                              : CommandParser.FromBatch(RequestParser.ParseBatchText(payload));
            return command is null ? null : new Recognised(command.Name, command, null, rpc, reset, transaction);
        }
        catch (GatewayException ex)
        {
            return new Recognised(ex.Command ?? "?", null, ex, rpc, reset, transaction);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException or FormatException)
        {
            return null;   // not something DaffyZoom can read; whether it is valid is the target's to say
        }
    }

    private async Task AnswerAsync(List<byte[]> held, Recognised recognised, long timestamp)
    {
        bool always = recognised.Command is null || CommandCatalog.Find(recognised.Name) is not { Admin: false };
        _observers.GatewayCommand($"{CommandParser.Schema}.{recognised.Name}", always);
        foreach (var packet in held) _observers.Client(packet, timestamp);

        // A pooled connection's reset can ride in on a command, which the target never sees. It still
        // has to happen - the next user of the pooled connection expects a fresh session - so it goes
        // to the target on a request of DaffyZoom's own, and what the target says about it leads the reply.
        var prelude = recognised.ResetFlags != 0 ? await ResetTargetAsync(recognised.ResetFlags, recognised.Transaction) : [];

        var reply = recognised.Refusal is { } refusal ? Reply.Failed(refusal) : await RunAsync(recognised.Command!);
        reply.Prelude.InsertRange(0, prelude);

        byte[] answer = PacketWriter.Frame(TdsConstants.PacketType.TabularResult, reply.Encode(recognised.Rpc, _session.ColumnEncryption),
                                           _packetSize, _session.Spid);
        await ToClientAsync(answer, Stopwatch.GetTimestamp());
    }

    private async Task<Reply> RunAsync(DaffyCommand command)
    {
        try
        {
            return await _commands!.RunAsync(_session, command, _closing.Token);
        }
        catch (GatewayException ex)
        {
            return Reply.Failed(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("command.failed", $"daffyzoom.{command.Name} failed: {ex}", _session.Id);
            return Reply.Failed(new GatewayException(GatewayErrors.Refused, $"daffyzoom.{command.Name} failed inside DaffyZoom: {ex.Message}"));
        }
    }

    /// <summary>
    /// Sends the target a request of DaffyZoom's own carrying the reset, and reads the answer here
    /// instead of relaying it. Returns the ENVCHANGE tokens the target answered with - the reset's
    /// acknowledgement, the database and language it went back to - for the client to hear first.
    /// </summary>
    private Task<List<byte[]>> ResetTargetAsync(byte resetFlags, ulong transaction) =>
        OwnRequestAsync("-- DaffyZoom: pooled connection reset", resetFlags, transaction, "the reset of a pooled connection");

    /// <summary>
    /// Sends the target a batch of DaffyZoom's own on the session's connection - a pooled connection's
    /// reset, or a cached batch's leading SET statements - and reads the answer here instead of
    /// relaying it. Returns the ENVCHANGE tokens it held, for the client to hear first. Its errors are
    /// logged; a target that does not answer at all ends the session, because what state the server
    /// session is in can then no longer be known.
    /// </summary>
    private async Task<List<byte[]>> OwnRequestAsync(string sql, byte statusFlags, ulong transaction, string purpose)
    {
        byte[] answer = await OwnAnswerAsync(BuildBatch(sql, statusFlags, transaction, _packetSize), purpose);
        var scanned = TokenScanner.Scan(answer);
        foreach (var error in scanned.Errors)
            Log.Warn("session.own_request_error", $"the target answered {purpose} with {error}", _session.Id);
        Log.Debug("session.own_request", $"{purpose} sent to the target on DaffyZoom's own request", _session.Id);
        return scanned.ForClient;
    }

    /// <summary>
    /// A cached sp_prepexec's statement, prepared on the session by DaffyZoom's own sp_prepare
    /// (<see cref="Cache.PreparedStatements"/>), framed at the session's packet size and carrying the
    /// request's reset: the handle SQL Server issued, for the replay to carry, or why there is none.
    /// </summary>
    private async Task<Cache.PreparedStatements.Prepared> PrepareAsync(byte[] payload, byte resetFlags)
    {
        byte[] request = PacketWriter.Frame(TdsConstants.PacketType.Rpc, payload, _packetSize, 0);
        request[1] |= resetFlags;                               // the first packet carries a reset
        byte[] answer = await OwnAnswerAsync(request, "the prepare of a cached prepared statement");
        var prepared = Cache.PreparedStatements.Read(answer, _session.ColumnEncryption);
        if (prepared.Problem is not null) Log.Debug("session.prepare_fallback", $"a cached prepared statement went to the target: {prepared.Problem}", _session.Id);
        return prepared;
    }

    /// <summary>
    /// Sends the target a request of DaffyZoom's own, already framed, and returns its answer, read
    /// here instead of relayed.
    /// </summary>
    private async Task<byte[]> OwnAnswerAsync(byte[] request, string purpose)
    {
        var diversion = new Diversion();
        Volatile.Write(ref _diversion, diversion);
        try
        {
            await _upstream.WriteAsync(request);
            return await diversion.Answer.WaitAsync(OwnRequestTimeout, _closing.Token);
        }
        catch (TimeoutException)
        {
            // The client believes the server did what it asked; if that cannot be confirmed, it must not carry on.
            _session.Abort($"the target did not answer {purpose} within {OwnRequestTimeout.TotalSeconds:F0}s");
            throw new IOException(_session.CloseReason);
        }
        finally
        {
            Interlocked.CompareExchange(ref _diversion, null, diversion);
        }
    }

    // ---- answering from the cache ---------------------------------------------------------------

    /// <summary>Whether a batch of DaffyZoom's own fits in one packet, as <see cref="BuildBatch"/> needs.</summary>
    private bool FitsOnePacket(string sql) => TdsConstants.HeaderLength + 22 + sql.Length * 2 <= _packetSize;

    /// <summary>
    /// The first packet of a request matches something in the cache. Reads the rest of the request
    /// - up to the gate's hold limit - before anything is sent, and asks the cache for the hit. Answers
    /// it from the cache if there is one; otherwise sends it on unchanged, exactly as if it had never
    /// been held. Returns whether the next packet starts a request.
    /// </summary>
    private async Task<bool> ServeOrForwardAsync(ClientPacketReader reader, ReadOnlyMemory<byte> first, long timestamp, Cache.ICacheGate gate)
    {
        var held = new List<byte[]> { first.ToArray() };
        int total = first.Length;
        bool complete = IsEndOfMessage(first.Span);

        while (!complete && total <= gate.HoldLimit)
        {
            var next = await reader.ReadAsync() ?? throw new EndOfStreamException("the client disconnected part-way through a request");
            _session.AddClientBytes(next.Length);
            held.Add(next.ToArray());
            total += next.Length;
            complete = IsEndOfMessage(next.Span);
        }

        if (complete && await TryServeHeldAsync(held, timestamp, gate)) return true;

        await ForwardHeldAsync(held, timestamp);
        return complete;
    }

    /// <summary>Answers a whole held request from the cache if there is a hit that may be served; false sends it to the target.</summary>
    private async Task<bool> TryServeHeldAsync(List<byte[]> held, long timestamp, Cache.ICacheGate gate)
    {
        if (await gate.TryHitAsync(held, _closing.Token) is not { } hit) return false;

        if (hit.OwnBatch is { } own && !FitsOnePacket(own))
        {
            gate.NotUsed(hit, "its leading SET statements do not fit one packet");
            return false;
        }

        return await ServeAsync(held, hit, timestamp, gate);
    }

    /// <summary>
    /// Answers a request from the cache, or returns false and the request goes to the target as it
    /// came. The server is first told what it must still do for the session, on a request of
    /// DaffyZoom's own: the request's reset and the batch's leading SETs - or, for an sp_prepexec,
    /// the prepare of its statement, whose handle the replay then carries; if the server will not
    /// prepare it, nothing has been sent to the client yet, and the request goes on. The observers see
    /// the request, then the answer, as if the target had sent it: the stored answer, framed at this
    /// session's packet size and SPID and written, one packet per write.
    ///
    /// Everything that could fail without consequence was checked before this was called. A failure
    /// from here on - the disk dying mid-stream - ends the session, as a server crash would.
    /// </summary>
    private async Task<bool> ServeAsync(List<byte[]> held, Cache.CacheHit hit, long timestamp, Cache.ICacheGate gate)
    {
        List<byte[]> prelude;
        Cache.HandlePatch? patch = null;
        if (hit.Prepare is { } prepare)
        {
            var prepared = await PrepareAsync(prepare, hit.ResetFlags);
            if (prepared.Handle is not { } handle)
            {
                gate.NotUsed(hit, $"a cached prepared statement could not be prepared: {prepared.Problem}", stale: true);
                return false;
            }
            prelude = prepared.ForClient;
            patch = new Cache.HandlePatch(hit.Entry.HandleOffset!.Value, handle);
        }
        else
        {
            prelude = hit.OwnBatch is { } own
                ? await OwnRequestAsync(own, hit.ResetFlags, hit.Transaction,
                                        hit.Entry.LeadingSetText is null ? "the reset of a pooled connection" : "a cached batch's leading SET statements")
                : [];
        }

        foreach (var packet in held) _observers.Client(packet, timestamp);

        var clock = Stopwatch.StartNew();
        bool completed = false;
        await _toClient.WaitAsync();
        try
        {
            var framer = new Cache.ReplayFramer(_packetSize, _session.Spid, async packet =>
            {
                _observers.Server(packet.Span, Stopwatch.GetTimestamp());
                _session.AddServerBytes(packet.Length);
                await _client.WriteAsync(packet, _closing.Token);
            });

            foreach (var token in prelude) await framer.AppendAsync(token);
            long at = 0;
            await foreach (var chunk in hit.Content.ChunksAsync(_closing.Token))
            {
                await framer.AppendAsync(patch is { } handle ? handle.Apply(chunk, at) : chunk);
                at += chunk.Length;
            }
            await framer.FinishAsync();
            completed = true;
        }
        finally
        {
            _toClient.Release();
            gate.Served(hit, clock.Elapsed.TotalMilliseconds, completed);
        }
        return true;
    }

    /// <summary>A SQL_BATCH of DaffyZoom's own: ALL_HEADERS (transaction descriptor, one outstanding request), then the text. (From DaffyDuo.)</summary>
    internal static byte[] BuildBatch(string sql, byte statusFlags, ulong transaction, int packetSize)
    {
        byte[] text = Encoding.Unicode.GetBytes(sql);
        int payloadLength = 22 + text.Length;
        if (payloadLength + TdsConstants.HeaderLength > packetSize)
            throw new ArgumentException("DaffyZoom's own requests fit in one packet", nameof(sql));

        var packet = new byte[TdsConstants.HeaderLength + payloadLength];
        packet[0] = TdsConstants.PacketType.SqlBatch;
        packet[1] = (byte)(TdsConstants.PacketStatus.EndOfMessage | statusFlags);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[6] = 1;

        var body = packet.AsSpan(TdsConstants.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(body, 22);                        // ALL_HEADERS total
        BinaryPrimitives.WriteUInt32LittleEndian(body[4..], 18);                   // this header's length
        BinaryPrimitives.WriteUInt16LittleEndian(body[8..], TdsConstants.HeaderType.TransactionDescriptor);
        BinaryPrimitives.WriteUInt64LittleEndian(body[10..], transaction);
        BinaryPrimitives.WriteUInt32LittleEndian(body[18..], 1);                   // outstanding requests
        text.CopyTo(body[22..]);
        return packet;
    }

    // ---- target to client ----------------------------------------------------------------------

    private async Task PumpFromServerAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                int read = await _upstream.ReadAsync(buffer.AsMemory(0, BufferSize));
                if (read == 0) return;

                long timestamp = Stopwatch.GetTimestamp();
                _session.AddServerBytes(read);

                // The answer to DaffyZoom's own request is DaffyZoom's, not the client's.
                if (Volatile.Read(ref _diversion) is { } diversion)
                {
                    if (diversion.Feed(buffer.AsSpan(0, read)))
                    {
                        Interlocked.CompareExchange(ref _diversion, null, diversion);
                        diversion.Complete();
                    }
                    continue;
                }

                await ToClientAsync(buffer.AsMemory(0, read), timestamp);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Observes and writes as one step, in turn with the other writer - see <see cref="_toClient"/>.</summary>
    private async ValueTask ToClientAsync(ReadOnlyMemory<byte> data, long timestamp)
    {
        await _toClient.WaitAsync();
        try
        {
            _observers.Server(data.Span, timestamp);
            await WriteInPacketSizedPiecesAsync(_client, data, _packetSize);
        }
        finally
        {
            _toClient.Release();
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static bool IsEndOfMessage(ReadOnlySpan<byte> packet) => (packet[1] & TdsConstants.PacketStatus.EndOfMessage) != 0;

    private static byte[] Join(IReadOnlyList<byte[]> packets)
    {
        var payload = new byte[packets.Sum(p => p.Length - TdsConstants.HeaderLength)];
        int offset = 0;
        foreach (var packet in packets)
        {
            packet.AsSpan(TdsConstants.HeaderLength).CopyTo(payload.AsSpan(offset));
            offset += packet.Length - TdsConstants.HeaderLength;
        }
        return payload;
    }

    private static string Describe(Exception ex) => ex switch
    {
        IOException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    /// <summary>Collects the target's answer to DaffyZoom's own request, packet by packet, until its last.</summary>
    private sealed class Diversion : IPacketListener
    {
        private readonly PacketTracker _tracker = new();
        private readonly MemoryStream _payload = new();
        private readonly TaskCompletionSource<byte[]> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _ended;

        public Task<byte[]> Answer => _answer.Task;

        /// <summary>True once the answer is complete (or cannot be read): the pump stops diverting.</summary>
        public bool Feed(ReadOnlySpan<byte> data)
        {
            _tracker.Feed(data, this);
            if (_tracker.Desynced)
            {
                _answer.TrySetException(new InvalidDataException("the target's answer to DaffyZoom's own request was not TDS"));
                return true;
            }
            return _ended;
        }

        public void Complete() => _answer.TrySetResult(_payload.ToArray());

        public void OnPacketStart(in PacketHeader header) { }

        public void OnPacketPayload(ReadOnlySpan<byte> payload) => _payload.Write(payload);

        public void OnPacketEnd(in PacketHeader header)
        {
            if (header.IsEndOfMessage) _ended = true;
        }
    }

    /// <summary>
    /// Fans bytes out to the observers and quarantines any that throw: a bug in watching the traffic
    /// must cost the watching, never the session.
    /// </summary>
    private sealed class Observers(ProxySession session, IReadOnlyList<IRelayObserver> observers)
    {
        private readonly bool[] _failed = new bool[observers.Count];

        public void Client(ReadOnlySpan<byte> data, long timestamp)
        {
            for (int i = 0; i < observers.Count; i++)
            {
                if (_failed[i]) continue;
                try { observers[i].OnClientBytes(data, timestamp); }
                catch (Exception ex) { Quarantine(i, ex); }
            }
        }

        public void Server(ReadOnlySpan<byte> data, long timestamp)
        {
            for (int i = 0; i < observers.Count; i++)
            {
                if (_failed[i]) continue;
                try { observers[i].OnServerBytes(data, timestamp); }
                catch (Exception ex) { Quarantine(i, ex); }
            }
        }

        public void GatewayCommand(string command, bool always)
        {
            for (int i = 0; i < observers.Count; i++)
            {
                if (_failed[i]) continue;
                try { observers[i].OnGatewayCommand(command, always); }
                catch (Exception ex) { Quarantine(i, ex); }
            }
        }

        public void Closed(string reason)
        {
            for (int i = 0; i < observers.Count; i++)
            {
                try { observers[i].OnClosed(reason); }
                catch (Exception ex) { Quarantine(i, ex); }
            }
        }

        private void Quarantine(int index, Exception ex)
        {
            _failed[index] = true;
            Log.Error("observer.failed",
                $"{observers[index].GetType().Name} failed and is switched off for this session; relaying continues: {ex}",
                session.Id);
        }
    }
}

/// <summary>
/// Counts requests - one per complete message from the client - for :sessions and :status. Under
/// MARS the client's bytes are SMP frames rather than TDS packets, so it counts bytes only.
/// </summary>
internal sealed class RequestCounter(ProxySession session) : IRelayObserver, IPacketListener
{
    private readonly PacketTracker _tracker = new();

    public void OnClientBytes(ReadOnlySpan<byte> data, long timestamp)
    {
        if (!session.Mars) _tracker.Feed(data, this);
    }

    public void OnServerBytes(ReadOnlySpan<byte> data, long timestamp) { }

    public void OnClosed(string reason) { }

    public void OnPacketStart(in PacketHeader header) { }

    public void OnPacketPayload(ReadOnlySpan<byte> payload) { }

    public void OnPacketEnd(in PacketHeader header)
    {
        if (header.IsEndOfMessage && header.Type != TdsConstants.PacketType.Attention) session.CountRequest();
    }
}
