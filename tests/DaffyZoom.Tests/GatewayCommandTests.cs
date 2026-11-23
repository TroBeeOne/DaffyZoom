using System.Net;
using System.Net.Sockets;
using System.Text;
using DaffyZoom.Commands;
using DaffyZoom.Proxy;
using DaffyZoom.Security;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>Requests as a client sends them, for the tests below.</summary>
internal static class Requests
{
    public static byte[] Packet(byte type, byte status, byte[] payload)
    {
        int length = payload.Length + 8;
        return [type, status, (byte)(length >> 8), (byte)length, 0, 0, 1, 0, .. payload];
    }

    /// <summary>ALL_HEADERS with a transaction descriptor, then the SQL text, as one packet.</summary>
    public static byte[] Batch(string sql, byte status = TdsConstants.PacketStatus.EndOfMessage, ulong transaction = 0) =>
        Packet(TdsConstants.PacketType.SqlBatch, status, BatchPayload(sql, transaction));

    public static byte[] BatchPayload(string sql, ulong transaction = 0)
    {
        var w = new TdsWriter();
        w.WriteUInt32(22); w.WriteUInt32(18); w.WriteUInt16(2); w.WriteUInt64(transaction); w.WriteUInt32(1);
        w.WriteBytes(Encoding.Unicode.GetBytes(sql));
        return w.ToArray();
    }

    /// <summary>A batch too long for one packet, framed as a client frames it.</summary>
    public static byte[] LongBatch(string sql, int packetSize = 4096) =>
        PacketWriter.Frame(TdsConstants.PacketType.SqlBatch, BatchPayload(sql), packetSize, 0);

    /// <summary>A procedure called by name with nvarchar parameters, as CommandType.StoredProcedure sends it.</summary>
    public static byte[] Rpc(string procedure, params (string Name, string Value)[] parameters)
    {
        var w = new TdsWriter();
        w.WriteUInt32(22); w.WriteUInt32(18); w.WriteUInt16(2); w.WriteUInt64(0); w.WriteUInt32(1);
        w.WriteUInt16((ushort)procedure.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(procedure));
        w.WriteUInt16(0);                                       // option flags
        foreach (var (name, value) in parameters)
        {
            w.WriteBVarChar(name);
            w.WriteByte(0);                                     // an input parameter
            w.WriteByte(0xE7);                                  // nvarchar(4000)
            w.WriteUInt16(8000);
            w.WriteBytes(ResponseWriter.DefaultCollation);
            byte[] bytes = Encoding.Unicode.GetBytes(value);
            w.WriteUInt16((ushort)bytes.Length);
            w.WriteBytes(bytes);
        }
        return Packet(TdsConstants.PacketType.Rpc, TdsConstants.PacketStatus.EndOfMessage, w.ToArray());
    }

    /// <summary>
    /// sp_prepexec by id, as ODBC sends it (seen against SQL Server 2022): an unnamed @handle int
    /// OUTPUT that is NULL, the declarations, the statement, then one int value.
    /// </summary>
    public static byte[] PrepExec(string statement, string declarations, int value, ushort optionFlags = 0) =>
        Packet(TdsConstants.PacketType.Rpc, TdsConstants.PacketStatus.EndOfMessage, PrepExecPayload(statement, declarations, value, optionFlags));

    public static byte[] PrepExecPayload(string statement, string declarations, int value, ushort optionFlags = 0)
    {
        var w = new TdsWriter();
        w.WriteUInt32(22); w.WriteUInt32(18); w.WriteUInt16(2); w.WriteUInt64(0); w.WriteUInt32(1);
        w.WriteUInt16(TdsConstants.RpcProcIdSwitch);
        w.WriteUInt16(TdsConstants.ProcId.PrepExec);
        w.WriteUInt16(optionFlags);
        w.WriteByte(0); w.WriteByte(0x01); w.WriteByte(0x26); w.WriteByte(4); w.WriteByte(0);   // @handle int OUTPUT = NULL
        foreach (string text in (string[])[declarations, statement])
        {
            w.WriteByte(0); w.WriteByte(0); w.WriteByte(0xE7); w.WriteUInt16(8000);
            w.WriteBytes(ResponseWriter.DefaultCollation);
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            w.WriteUInt16((ushort)bytes.Length);
            w.WriteBytes(bytes);
        }
        w.WriteByte(0); w.WriteByte(0); w.WriteByte(0x26); w.WriteByte(4); w.WriteByte(4); w.WriteInt32(value);
        return w.ToArray();
    }

    /// <summary>A RETURNVALUE for an int output parameter as SQL Server sends a prepared statement's handle: ordinal 0, no name, INTN(4).</summary>
    public static byte[] HandleValue(int handle)
    {
        var w = new TdsWriter();
        w.WriteByte(TdsConstants.Token.ReturnValue); w.WriteUInt16(0); w.WriteByte(0); w.WriteByte(0x01);
        w.WriteUInt32(0); w.WriteUInt16(0); w.WriteByte(0x26); w.WriteByte(4); w.WriteByte(4); w.WriteInt32(handle);
        return w.ToArray();
    }

    /// <summary>RETURNSTATUS 0 and the final DONEPROC: how every RPC's answer ends.</summary>
    public static byte[] ProcEnd()
    {
        var w = new TdsWriter();
        w.WriteByte(TdsConstants.Token.ReturnStatus); w.WriteInt32(0);
        w.WriteByte(TdsConstants.Token.DoneProc); w.WriteUInt16(TdsConstants.DoneStatus.Final); w.WriteUInt16(0xE0); w.WriteUInt64(0);
        return w.ToArray();
    }

    public static byte[] Done(ushort status = TdsConstants.DoneStatus.Final, ulong rows = 0)
    {
        var w = new TdsWriter();
        w.WriteByte(TdsConstants.Token.Done); w.WriteUInt16(status); w.WriteUInt16(0xC1); w.WriteUInt64(rows);
        return w.ToArray();
    }
}

/// <summary>Recognising daffyzoom.* commands: whole statements, never text that happens to contain one. (From DaffyDuo.)</summary>
public class CommandParserTests
{
    [Theory]
    [InlineData("EXEC daffyzoom.help 'sessions'", "help", "sessions")]
    [InlineData("exec DAFFYZOOM.HELP N'sessions';", "help", "sessions")]
    [InlineData("EXECUTE [daffyzoom].[help] 'sessions'", "help", "sessions")]
    [InlineData("EXEC daffyzoom.help sessions", "help", "sessions")]
    [InlineData("daffyzoom.help 'sessions'", "help", "sessions")]
    [InlineData("-- who is on\r\nEXEC daffyzoom.kill_session 12\r\n", "kill_session", 12L)]
    [InlineData("/* first /* nested */ still a comment */ EXEC daffyzoom.test_user 'o''brien'", "test_user", "o'brien")]
    public void FindsTheCommandAndItsArgument(string sql, string name, object argument)
    {
        var command = CommandParser.FromBatch(sql);
        Assert.NotNull(command);
        Assert.Equal(name, command.Name);
        Assert.Equal(argument, Assert.Single(command.Arguments).Value);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT 'EXEC daffyzoom.status'")]
    [InlineData("-- EXEC daffyzoom.status\nSELECT 1")]
    [InlineData("SELECT * FROM daffyzoom.settings")]
    [InlineData("EXEC dbo.daffyzoom_status")]
    [InlineData("EXEC sp_executesql N'EXEC daffyzoom.status'")]
    [InlineData("EXEC daffy.connect 'reporting'")]          // DaffyDuo's: DaffyZoom in front of it passes it through
    public void LeavesEverythingElseToTheTarget(string sql) => Assert.Null(CommandParser.FromBatch(sql));

    [Theory]
    [InlineData("SELECT 1; EXEC daffyzoom.status", "status")]
    [InlineData("EXEC daffyzoom.status; SELECT 1", "status")]
    [InlineData("BEGIN TRAN EXEC daffyzoom.sessions", "sessions")]
    public void RefusesAMixedBatch(string sql, string command)
    {
        var refusal = Assert.Throws<GatewayException>(() => CommandParser.FromBatch(sql));
        Assert.Equal(GatewayErrors.NotAlone, refusal.Number);
        Assert.Equal(command, refusal.Command);
        Assert.StartsWith("DaffyZoom: ", refusal.Message);
    }

    [Theory]
    [InlineData("daffyzoom.status", "status")]
    [InlineData("[daffyzoom].[status]", "status")]
    [InlineData("Sales.daffyzoom.Sessions", "sessions")]
    [InlineData("daffy.connect", null)]
    [InlineData("dbo.status", null)]
    public void RecognisesProcedureNamesInRpcs(string procName, string? expected) =>
        Assert.Equal(expected, CommandParser.ProcedureName(procName));
}

public class CommandBindingTests
{
    private static readonly CommandDefinition AddUser = CommandCatalog.Find("add_user")!;

    [Fact]
    public void BindsByPositionByNameOrBoth()
    {
        var mixed = CommandCatalog.Bind(AddUser, [new(null, "bob"), new("target", "Server=x"), new("password", " pw ")]);
        Assert.Equal("bob", mixed.Text("login"));
        Assert.Equal(" pw ", mixed.Secret("password"));         // passwords exactly as given
        Assert.Equal("Server=x", mixed.Text("target"));
    }

    [Fact]
    public void SaysWhatIsMissingAndHowToCallIt()
    {
        var error = Assert.Throws<GatewayException>(() => CommandCatalog.Bind(AddUser, [new(null, "bob")]));
        Assert.Equal(GatewayErrors.BadCommand, error.Number);
        Assert.Contains("needs @password", error.Message);
        Assert.Contains("Usage: EXEC daffyzoom.add_user @login, @password, @target", error.Message);
    }

    [Fact]
    public void NumbersFlagsAndDefaults()
    {
        var log = CommandCatalog.Find("log")!;
        Assert.Equal(50, CommandCatalog.Bind(log, []).Number("count", 50));
        Assert.Equal(7, CommandCatalog.Bind(log, [new(null, 7L)]).Number("count", 50));
        Assert.Equal(7, CommandCatalog.Bind(log, [new(null, "7")]).Number("count", 50));

        var admin = CommandCatalog.Find("set_admin")!;
        Assert.True(CommandCatalog.Bind(admin, [new(null, "bob"), new(null, true)]).Flag("admin"));
        Assert.False(CommandCatalog.Bind(admin, [new(null, "bob"), new(null, "OFF")]).Flag("admin"));
        Assert.Throws<GatewayException>(() => CommandCatalog.Bind(admin, [new(null, "bob"), new(null, "maybe")]).Flag("admin"));
    }

    [Fact]
    public void NoCommandIsAReservedWordAndEveryExampleRuns()
    {
        // KILL, GRANT, REVOKE and USER are T-SQL reserved words: daffyzoom.kill would not even parse.
        string[] reserved = ["kill", "grant", "revoke", "deny", "use", "user", "exec", "execute", "select", "set", "current", "default"];
        Assert.DoesNotContain(CommandCatalog.All, c => reserved.Contains(c.Name));

        foreach (var command in CommandCatalog.All)
        {
            var parsed = CommandParser.FromBatch(command.Example);
            Assert.Equal(command.Name, parsed?.Name);
            CommandCatalog.Bind(command, parsed!.Arguments);
        }
    }
}

public class ReplyTests
{
    [Fact]
    public void ABatchWithOnlyMessagesEndsInAFinalDone()
    {
        var reply = new Reply();
        reply.Messages.Add("hello");
        byte[] bytes = reply.Encode(rpc: false);

        var scanned = TokenScanner.Scan(bytes);
        Assert.Null(scanned.Problem);
        Assert.Equal("hello", Assert.Single(scanned.Messages).Text);
        Assert.Equal(TdsConstants.Token.Done, bytes[^13]);
        Assert.Equal(0, bytes[^12] | bytes[^11] << 8);
    }

    [Fact]
    public void AnRpcEndsInReturnStatusAndDoneProc()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet([new ColumnDef("n", SqlColumnType.Int)], [[1]]));
        byte[] bytes = reply.Encode(rpc: true);

        Assert.Equal(TdsConstants.Token.ColMetadata, bytes[0]);
        Assert.Equal(TdsConstants.Token.ReturnStatus, bytes[^18]);
        Assert.Equal(TdsConstants.Token.DoneProc, bytes[^13]);
    }

    [Fact]
    public void AnErrorIsSeverity16AndCarriesItsNumber()
    {
        var bytes = Reply.Failed(new GatewayException(GatewayErrors.NeedsAdmin, "not for you")).Encode(rpc: false);

        var error = Assert.Single(TokenScanner.Scan(bytes).Errors);
        Assert.Equal(50303, error.Number);
        Assert.Equal(16, error.Severity);
        Assert.Equal("DaffyZoom: not for you", error.Text);
        Assert.Equal("DaffyZoom", error.Server);
        Assert.Equal(TdsConstants.DoneStatus.Error, bytes[^12] | bytes[^11] << 8);
    }

    /// <summary>
    /// Microsoft.Data.SqlClient asks for column encryption on every connection and SQL Server grants
    /// it; from then on the client reads a key table after every COLMETADATA's column count.
    /// </summary>
    [Fact]
    public void WithColumnEncryptionEveryResultCarriesAnEmptyKeyTable()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet([new ColumnDef("n", SqlColumnType.Int)], [[1]]));

        byte[] plain = reply.Encode(rpc: false, columnEncryption: false);
        byte[] encrypted = reply.Encode(rpc: false, columnEncryption: true);

        Assert.Equal(plain.Length + 2, encrypted.Length);
        Assert.Equal([TdsConstants.Token.ColMetadata, 1, 0, 0, 0], encrypted[..5]);   // one column, then no keys
        Assert.Equal(plain[3..], encrypted[5..]);
    }

    [Fact]
    public void ServerTokensGoFirstUnchanged()
    {
        byte[] envChange = [TdsConstants.Token.EnvChange, 3, 0, TdsConstants.EnvChangeType.ResetConnectionAck, 0, 0];
        var reply = new Reply();
        reply.Prelude.Add(envChange);
        Assert.Equal(envChange, reply.Encode(rpc: false)[..6]);
    }

    [Fact]
    public void FramingSplitsAtThePacketSizeAndEndsOnce()
    {
        byte[] payload = new byte[10_000];
        Random.Shared.NextBytes(payload);
        byte[] framed = PacketWriter.Frame(TdsConstants.PacketType.TabularResult, payload, 4096, spid: 63);

        var packets = new List<byte[]>();
        for (int at = 0; at < framed.Length;)
        {
            int length = framed[at + 2] << 8 | framed[at + 3];
            packets.Add(framed[at..(at + length)]);
            at += length;
        }

        Assert.Equal(3, packets.Count);
        Assert.All(packets[..2], p => Assert.Equal(4096, p.Length));
        Assert.Equal([0, 0, 1], packets.Select(p => p[1] & TdsConstants.PacketStatus.EndOfMessage));
        Assert.Equal([1, 2, 3], packets.Select(p => (int)p[6]));
        Assert.All(packets, p => Assert.Equal(63, p[4] << 8 | p[5]));
        Assert.Equal(payload, packets.SelectMany(p => p[8..]).ToArray());
    }
}

/// <summary>
/// The relay itself, on real sockets: one pair for the client, one for the target. What reaches
/// the target is compared byte for byte with what the client sent.
/// </summary>
public class GatewayRelayTests
{
    private sealed class FakeCommands : IGatewayCommands
    {
        public List<string> Ran { get; } = [];

        public Task<Reply> RunAsync(ProxySession caller, DaffyCommand command, CancellationToken cancellationToken)
        {
            Ran.Add(command.Name);
            var reply = new Reply();
            reply.Messages.Add($"hello from {command.Name}");
            return Task.FromResult(reply);
        }
    }

    private sealed class Recorder : IRelayObserver
    {
        public List<string> Seen { get; } = [];

        public void OnClientBytes(ReadOnlySpan<byte> data, long timestamp) { lock (Seen) Seen.Add($"client {data[0]:X2}"); }
        public void OnServerBytes(ReadOnlySpan<byte> data, long timestamp) { lock (Seen) Seen.Add($"server {data[0]:X2}"); }
        public void OnClosed(string reason) { }
        public void OnGatewayCommand(string command, bool always) { lock (Seen) Seen.Add($"command {command} {always}"); }
    }

    /// <summary>A running relay, and the far ends of its two connections.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required Stream Client { get; init; }
        public required Stream Target { get; init; }
        public required ProxySession Session { get; init; }
        public required Task Relaying { get; init; }
        public required List<TcpClient> Sockets { get; init; }

        public async Task<ReceivedMessage> FromRelayAsync(Stream stream) =>
            await new TdsMessageReader(stream).ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))
            ?? throw new EndOfStreamException();

        public async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            Session.Abort("test over");
            foreach (var socket in Sockets) socket.Dispose();
            try { await Relaying.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }

    private static async Task<(TcpClient Near, TcpClient Far)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var near = new TcpClient();
        var connecting = near.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var far = await listener.AcceptTcpClientAsync();
        await connecting;
        listener.Stop();
        return (near, far);
    }

    private static async Task<Harness> StartAsync(IGatewayCommands? commands, IRelayObserver? observer = null, byte[]? early = null,
                                                  bool columnEncryption = false)
    {
        var (client, relayClient) = await PairAsync();
        var (relayTarget, target) = await PairAsync();

        var session = new ProxySession(1, "127.0.0.1:1") { Spid = 63, State = SessionState.Relaying };
        if (columnEncryption) session.AcknowledgedFeatures = [TdsConstants.FeatureId.ColumnEncryption];
        session.Attach(relayClient);
        session.AttachUpstream(relayTarget);

        var relaying = Relay.RunAsync(session, relayClient.GetStream(), relayTarget.GetStream(), 4096,
                                      observer is null ? [] : [observer], commands, early);
        return new Harness
        {
            Client = client.GetStream(),
            Target = target.GetStream(),
            Session = session,
            Relaying = relaying,
            Sockets = [client, relayClient, relayTarget, target],
        };
    }

    [Fact]
    public async Task OrdinaryRequestsAndAnswersPassByteForByte()
    {
        await using var relay = await StartAsync(new FakeCommands());

        byte[] request = Requests.Batch("SELECT 'daffyzoom' AS word");   // mentions it, and is not a command
        await relay.Client.WriteAsync(request);
        Assert.Equal(request, await relay.ReadExactlyAsync(relay.Target, request.Length));

        byte[] answer = Requests.Packet(TdsConstants.PacketType.TabularResult, 1, Requests.Done());
        await relay.Target.WriteAsync(answer);
        Assert.Equal(answer, await relay.ReadExactlyAsync(relay.Client, answer.Length));
    }

    [Fact]
    public async Task ACommandIsAnsweredHereAndNeverReachesTheTarget()
    {
        var commands = new FakeCommands();
        await using var relay = await StartAsync(commands);

        await relay.Client.WriteAsync(Requests.Batch("EXEC daffyzoom.status"));
        var reply = await relay.FromRelayAsync(relay.Client);

        Assert.Equal(TdsConstants.PacketType.TabularResult, reply.Message.Type);
        Assert.Equal(63, reply.Spid);
        Assert.Equal("hello from status", Assert.Single(TokenScanner.Scan(reply.Message.Payload).Messages).Text);
        Assert.Equal(["status"], commands.Ran);

        // The first thing the target hears is the next ordinary request, not the command.
        byte[] next = Requests.Batch("SELECT 1");
        await relay.Client.WriteAsync(next);
        Assert.Equal(next, await relay.ReadExactlyAsync(relay.Target, next.Length));
    }

    [Fact]
    public async Task AProcedureCallIsACommandToo()
    {
        var commands = new FakeCommands();
        await using var relay = await StartAsync(commands);

        await relay.Client.WriteAsync(Requests.Rpc("daffyzoom.help", ("@command", "status")));
        var reply = await relay.FromRelayAsync(relay.Client);

        byte[] payload = reply.Message.Payload.ToArray();
        Assert.Equal(TdsConstants.Token.DoneProc, payload[^13]);
        Assert.Equal(["help"], commands.Ran);
    }

    [Fact]
    public async Task ARefusalIsAnErrorAndTheSessionCarriesOn()
    {
        var commands = new FakeCommands();
        await using var relay = await StartAsync(commands);

        await relay.Client.WriteAsync(Requests.Batch("SELECT 1; EXEC daffyzoom.status"));
        var reply = await relay.FromRelayAsync(relay.Client);
        Assert.Equal(GatewayErrors.NotAlone, Assert.Single(TokenScanner.Scan(reply.Message.Payload).Errors).Number);
        Assert.Empty(commands.Ran);

        byte[] next = Requests.Batch("SELECT 2");
        await relay.Client.WriteAsync(next);
        Assert.Equal(next, await relay.ReadExactlyAsync(relay.Target, next.Length));
    }

    [Fact]
    public async Task APooledResetOnACommandGoesToTheTargetOnARequestOfDaffyZoomsOwn()
    {
        await using var relay = await StartAsync(new FakeCommands());

        await relay.Client.WriteAsync(Requests.Batch("EXEC daffyzoom.status",
            TdsConstants.PacketStatus.EndOfMessage | TdsConstants.PacketStatus.ResetConnection, transaction: 0x42));

        var own = await relay.FromRelayAsync(relay.Target);
        Assert.Equal(TdsConstants.PacketType.SqlBatch, own.Message.Type);
        Assert.Equal(TdsConstants.PacketStatus.EndOfMessage | TdsConstants.PacketStatus.ResetConnection, own.RawPackets[1]);
        Assert.Equal("-- DaffyZoom: pooled connection reset", RequestParser.ParseBatchText(own.Message.Payload));
        Assert.Equal(0x42UL, AllHeaders.TransactionDescriptor(own.Message.Payload));

        // The target acknowledges the reset; the client hears that first, then DaffyZoom's answer.
        byte[] acknowledgement = [TdsConstants.Token.EnvChange, 3, 0, TdsConstants.EnvChangeType.ResetConnectionAck, 0, 0];
        await relay.Target.WriteAsync(Requests.Packet(TdsConstants.PacketType.TabularResult, 1, [.. acknowledgement, .. Requests.Done()]));

        var reply = await relay.FromRelayAsync(relay.Client);
        byte[] payload = reply.Message.Payload.ToArray();
        Assert.Equal(acknowledgement, payload[..6]);
        Assert.Equal("hello from status", Assert.Single(TokenScanner.Scan(payload).Messages).Text);
    }

    [Fact]
    public async Task WithoutCommandsEverythingReachesTheTarget()
    {
        await using var relay = await StartAsync(commands: null);   // a MARS session, or no commands at all

        byte[] request = Requests.Batch("EXEC daffyzoom.status");
        await relay.Client.WriteAsync(request);
        Assert.Equal(request, await relay.ReadExactlyAsync(relay.Target, request.Length));
    }

    [Fact]
    public async Task WhatTheHandshakeReadAheadIsRelayedFirst()
    {
        byte[] early = Requests.Batch("SELECT 'early'");
        await using var relay = await StartAsync(new FakeCommands(), early: early);
        Assert.Equal(early, await relay.ReadExactlyAsync(relay.Target, early.Length));
    }

    [Fact]
    public async Task ALongRequestThatMentionsDaffyteeIsHeldThenSentOnWhole()
    {
        await using var relay = await StartAsync(new FakeCommands());

        // Past the 64 KB DaffyZoom holds back to look at: the first part waits, the rest follows.
        byte[] request = Requests.LongBatch("SELECT N'daffyzoom' + N'" + new string('y', 50_000) + "'");
        Assert.True(request.Length > 64 * 1024);
        await relay.Client.WriteAsync(request);
        Assert.Equal(request, await relay.ReadExactlyAsync(relay.Target, request.Length));
    }

    [Fact]
    public async Task ObserversSeeTheCommandThenItsRequestThenTheAnswer()
    {
        var recorder = new Recorder();
        await using var relay = await StartAsync(new FakeCommands(), recorder);

        await relay.Client.WriteAsync(Requests.Batch("EXEC daffyzoom.users"));
        await relay.FromRelayAsync(relay.Client);
        await relay.Client.WriteAsync(Requests.Batch("EXEC daffyzoom.status"));
        await relay.FromRelayAsync(relay.Client);

        lock (recorder.Seen)
            Assert.Equal(["command daffyzoom.users True", "client 01", "server 04", "command daffyzoom.status False", "client 01", "server 04"],
                         recorder.Seen);
    }

    [Fact]
    public async Task UnderColumnEncryptionTheAnswersCarryTheKeyTable()
    {
        await using var relay = await StartAsync(new ResultCommands(), columnEncryption: true);

        await relay.Client.WriteAsync(Requests.Batch("EXEC daffyzoom.status"));
        var reply = await relay.FromRelayAsync(relay.Client);
        Assert.Equal([TdsConstants.Token.ColMetadata, 1, 0, 0, 0], reply.Message.Payload.ToArray()[..5]);
    }

    private sealed class ResultCommands : IGatewayCommands
    {
        public Task<Reply> RunAsync(ProxySession caller, DaffyCommand command, CancellationToken cancellationToken)
        {
            var reply = new Reply();
            reply.ResultSets.Add(new ResultSet([new ColumnDef("session", SqlColumnType.Int)], [[caller.Id]]));
            return Task.FromResult(reply);
        }
    }

    [Theory]
    [InlineData("EXEC daffyzoom.status", true)]
    [InlineData("select * from DAFFYZOOM_LOG", true)]          // only a closer look; not a command
    [InlineData("SELECT 1", false)]
    public void OnlyARequestThatMentionsDaffyteeIsLookedAt(string sql, bool expected) =>
        Assert.Equal(expected, Relay.MightBeCommand(Requests.Batch(sql)));

    [Fact]
    public void AProcedureCallIsLookedAtByItsNameOnly()
    {
        Assert.True(Relay.MightBeCommand(Requests.Rpc("daffyzoom.status")));
        Assert.False(Relay.MightBeCommand(Requests.Rpc("dbo.orders", ("@note", "daffyzoom"))));
    }

    [Fact]
    public void DaffyZoomsOwnBatchesAreWellFormed()
    {
        byte[] packet = Relay.BuildBatch("-- reset", TdsConstants.PacketStatus.ResetConnectionSkipTran, transaction: 0x1122, packetSize: 4096);
        var payload = packet.AsSpan(TdsConstants.HeaderLength);

        Assert.Equal(TdsConstants.PacketType.SqlBatch, packet[0]);
        Assert.Equal(TdsConstants.PacketStatus.EndOfMessage | TdsConstants.PacketStatus.ResetConnectionSkipTran, packet[1]);
        Assert.Equal(packet.Length, packet[2] << 8 | packet[3]);
        Assert.Equal(0x1122UL, AllHeaders.TransactionDescriptor(payload));
        Assert.Equal("-- reset", RequestParser.ParseBatchText(payload));
    }
}

/// <summary>The packet reader under the relay: whole packets, whatever pieces the socket delivers them in.</summary>
public class ClientPacketReaderTests
{
    private sealed class Trickle(byte[] data, int piece) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(piece, count), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(65536)]
    public async Task HandsOnWholePacketsInOrder(int piece)
    {
        byte[] first = Requests.Batch("SELECT 1"), second = Requests.LongBatch(new string('z', 9000)), third = Requests.Batch("SELECT 3");
        var reader = new ClientPacketReader(new Trickle([.. second, .. third], piece), early: first);

        var packets = new List<byte[]>();
        while (await reader.ReadAsync() is { } packet) packets.Add(packet.ToArray());

        Assert.Equal([.. first, .. second, .. third], packets.SelectMany(p => p).ToArray());
        Assert.Equal(first, packets[0]);
        Assert.Equal(third, packets[^1]);
        Assert.All(packets, p => Assert.Equal(p.Length, p[2] << 8 | p[3]));
    }

    [Fact]
    public async Task AConnectionThatEndsPartWayThroughAPacketIsAnError()
    {
        byte[] cut = Requests.Batch("SELECT 1")[..12];
        var reader = new ClientPacketReader(new Trickle(cut, 100));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }
}

/// <summary>The commands themselves, against a real user store and session registry.</summary>
public class CommandRunnerTests : IAsyncLifetime
{
    private readonly TempDirectory _directory = new();
    private readonly SessionRegistry _sessions = new();
    private ProxyContext _context = null!;
    private TargetCheck _nextCheck = new("Microsoft SQL Server 16.0.4295", "Microsoft SQL Server 16.0.4295, database 'Sales', 3 ms", null, null, null, false);
    private readonly List<TargetEndpoint> _checked = [];

    private const string Target = "Server=tcp:sql01,1433;User Id=svc_app;Password=Svc!1;Initial Catalog=Sales;TrustServerCertificate=True";

    public Task InitializeAsync()
    {
        _context = new ProxyContext { Users = UserStore.Open(_directory.Path), Sessions = _sessions };

        AddUser("alice", admin: true);
        AddUser("bob", admin: false);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _directory.Dispose();
        return Task.CompletedTask;
    }

    private void AddUser(string login, bool admin)
    {
        var user = UserAdmin.Create(_context.Users, login, login + "-password", TargetEndpoint.Parse(Target), "test");
        UserAdmin.SetAdmin(_context.Users, user, admin);
    }

    private CommandRunner Runner() => new(_context, () => [new SettingRow("listening", "127.0.0.1:1433", "--port")],
        (endpoint, _) =>
        {
            _checked.Add(endpoint);
            return Task.FromResult(_nextCheck);
        });

    private ProxySession Connected(string login)
    {
        var session = _sessions.Open("10.0.0.1:5000");
        session.Login = login;
        session.TargetLogin = "svc_app";
        session.Target = "sql01:1433";
        session.Spid = 63;
        session.State = SessionState.Relaying;
        return session;
    }

    private async Task<Reply> RunAsync(string login, string sql) =>
        await Runner().RunAsync(Connected(login), CommandParser.FromBatch(sql)!, CancellationToken.None);

    private async Task<GatewayException> RefusedAsync(string login, string sql) =>
        await Assert.ThrowsAsync<GatewayException>(() => RunAsync(login, sql));

    [Fact]
    public async Task AdminCommandsNeedAnAdminAndHelpShowsOnlyWhatYouMayRun()
    {
        Assert.Equal(GatewayErrors.NeedsAdmin, (await RefusedAsync("bob", "EXEC daffyzoom.sessions")).Number);

        var bobs = await RunAsync("bob", "EXEC daffyzoom.help");
        Assert.Equal(["daffyzoom.help", "daffyzoom.status", "daffyzoom.tracking", "daffyzoom.flush"], bobs.ResultSets[0].Rows.Select(r => (string)r[0]!));

        var alices = await RunAsync("alice", "EXEC daffyzoom.help");
        Assert.Equal(CommandCatalog.All.Count, alices.ResultSets[0].Rows.Count);
    }

    [Fact]
    public async Task AnAdminWhoIsDisabledIsNoLongerAnAdmin()
    {
        UserAdmin.SetEnabled(_context.Users, _context.Users.Find("alice")!, false);
        Assert.Equal(GatewayErrors.NeedsAdmin, (await RefusedAsync("alice", "EXEC daffyzoom.users")).Number);
    }

    /// <summary>Every answer, decoded again by DaffyZoom's own response reader - the same reading a client gives it.</summary>
    [Fact]
    public async Task EveryAnswerDecodesAsItWasMeant()
    {
        Connected("bob");
        Log.Info("test.line", "something to show");

        foreach (string sql in new[] { "EXEC daffyzoom.status", "EXEC daffyzoom.sessions", "EXEC daffyzoom.health", "EXEC daffyzoom.settings",
                                       "EXEC daffyzoom.log 5", "EXEC daffyzoom.users", "EXEC daffyzoom.help 'add_user'" })
        {
            var reply = await RunAsync("alice", sql);
            Assert.NotEmpty(reply.ResultSets);

            foreach (bool encrypted in new[] { false, true })
            {
                foreach (bool rpc in new[] { false, true })
                {
                    var decoded = ResponseReader.Read(reply.Encode(rpc, encrypted), encrypted);
                    Assert.True(decoded.Problem is null, $"{sql} (rpc {rpc}, encrypted {encrypted}): {decoded.Problem}");
                    Assert.Equal(reply.ResultSets.Select(r => (long)r.Rows.Count), decoded.ResultSetRows);
                    if (rpc) Assert.Equal(0, decoded.ReturnStatus);
                }
            }
        }
    }

    [Fact]
    public async Task SessionsAndKilling()
    {
        var bob = Connected("bob");
        var me = Connected("alice");

        var listed = await Runner().RunAsync(me, CommandParser.FromBatch("EXEC daffyzoom.sessions")!, CancellationToken.None);
        Assert.Contains(listed.ResultSets[0].Rows, r => (int)r[0]! == bob.Id && (string)r[1]! == "bob");

        var own = await Assert.ThrowsAsync<GatewayException>(() =>
            Runner().RunAsync(me, CommandParser.FromBatch($"EXEC daffyzoom.kill_session {me.Id}")!, CancellationToken.None));
        Assert.Equal(GatewayErrors.Refused, own.Number);
        Assert.Equal(GatewayErrors.NotFound, (await RefusedAsync("alice", "EXEC daffyzoom.kill_session 9999")).Number);

        await RunAsync("alice", $"EXEC daffyzoom.kill_session {bob.Id}");
        Assert.StartsWith("killed by 'alice'", bob.CloseReason);
    }

    [Fact]
    public async Task AddingALoginLogsInToItsTargetFirst()
    {
        var reply = await RunAsync("alice", $"EXEC daffyzoom.add_user 'carol', 'carol-pw-1', '{Target}'");
        Assert.Contains("Created 'carol'", reply.Messages[0]);
        Assert.Equal("Svc!1", Assert.Single(_checked).Password);
        Assert.Equal(AuthOutcome.Accepted, _context.Users.Authenticate("carol", "carol-pw-1").Outcome);
        Assert.False(_context.Users.Find("carol")!.Admin);

        _nextCheck = new(null, null, null, "LoginRejected", "Login failed for user 'svc_app'.", false);
        var failed = await RefusedAsync("alice", $"EXEC daffyzoom.add_user 'dave', 'dave-pw-1', '{Target}'");
        Assert.Equal(GatewayErrors.TargetFailed, failed.Number);
        Assert.Contains("Nothing was saved", failed.Message);
        Assert.Null(_context.Users.Find("dave"));

        Assert.Equal(GatewayErrors.NotFound, (await RefusedAsync("alice", $"EXEC daffyzoom.add_user 'bob', 'x', '{Target}'")).Number);
        Assert.Equal(GatewayErrors.BadCommand,
                     (await RefusedAsync("alice", "EXEC daffyzoom.add_user 'erin', 'x', 'Server=sql01;User Id=svc_app'")).Number);
        Assert.Equal(GatewayErrors.BadCommand,
                     (await RefusedAsync("alice", "EXEC daffyzoom.add_user 'erin', 'x', 'Server=sql01;Bogus=1'")).Number);
    }

    [Fact]
    public async Task ANewTargetKeepsThePasswordOnlyForTheSameLogin()
    {
        await RunAsync("alice", "EXEC daffyzoom.set_target 'bob', 'Server=tcp:sql02,1433;User Id=svc_app;Initial Catalog=Sales'");
        Assert.Equal("Svc!1", _checked[^1].Password);
        Assert.Equal("sql02", _context.Users.Find("bob")!.Endpoint.Host);

        var other = await RefusedAsync("alice", "EXEC daffyzoom.set_target 'bob', 'Server=tcp:sql02,1433;User Id=svc_other'");
        Assert.Equal(GatewayErrors.BadCommand, other.Number);
        Assert.Equal("svc_app", _context.Users.Find("bob")!.Endpoint.UserName);

        await RunAsync("alice", "EXEC daffyzoom.set_target_password 'bob', 'Rotated!2'");
        Assert.Equal("Rotated!2", _context.Users.Find("bob")!.Endpoint.Password);
    }

    [Fact]
    public async Task AnAdminCannotLockThemselvesOutFromHere()
    {
        Assert.Equal(GatewayErrors.Refused, (await RefusedAsync("alice", "EXEC daffyzoom.disable_user 'alice'")).Number);
        Assert.Equal(GatewayErrors.Refused, (await RefusedAsync("alice", "EXEC daffyzoom.set_admin 'alice', 0")).Number);
        Assert.Equal(GatewayErrors.Refused, (await RefusedAsync("alice", "EXEC daffyzoom.remove_user 'ALICE'")).Number);

        await RunAsync("alice", "EXEC daffyzoom.set_admin 'bob', 1");
        await RunAsync("bob", "EXEC daffyzoom.set_admin 'alice', 0");
        Assert.False(_context.Users.Find("alice")!.Admin);
    }

    [Fact]
    public async Task PasswordsChangeAndLoginsGo()
    {
        await RunAsync("alice", "EXEC daffyzoom.set_user_password 'bob', 'new-bob-password'");
        Assert.Equal(AuthOutcome.Accepted, _context.Users.Authenticate("bob", "new-bob-password").Outcome);

        await RunAsync("alice", "EXEC daffyzoom.disable_user 'bob'");
        Assert.Equal(AuthOutcome.Disabled, _context.Users.Authenticate("bob", "new-bob-password").Outcome);

        await RunAsync("alice", "EXEC daffyzoom.remove_user 'bob'");
        Assert.Null(_context.Users.Find("bob"));
        Assert.Equal(GatewayErrors.NotFound, (await RefusedAsync("alice", "EXEC daffyzoom.test_user 'bob'")).Number);
    }
}

public class AdminFlagTests
{
    [Fact]
    public void TheAdminFlagIsKeptAndAStoreFromBeforeHasNone()
    {
        using var directory = new TempDirectory();
        var store = UserStore.Open(directory.Path);
        var user = UserAdmin.Create(store, "bob", "bob-password", TargetEndpoint.Parse("Server=x;User Id=y;Password=z"), "test");
        Assert.False(user.Admin);

        UserAdmin.SetAdmin(store, user, true);
        Assert.True(UserStore.Open(directory.Path).Find("bob")!.Admin);
    }

    [Theory]
    [InlineData("Server=b;User Id=svc", "secret")]           // same login: the password is kept
    [InlineData("Server=b;User Id=SVC", "secret")]
    [InlineData("Server=b;User Id=other", "")]               // another login's password is never guessed at
    [InlineData("Server=b;User Id=svc;Password=new", "new")]
    public void ANewTargetKeepsThePasswordOnlyForTheSameLogin(string updated, string expected)
    {
        var current = TargetEndpoint.Parse("Server=a;User Id=svc;Password=secret");
        Assert.Equal(expected, UserAdmin.KeepPassword(TargetEndpoint.Parse(updated), current).Password);
    }
}
