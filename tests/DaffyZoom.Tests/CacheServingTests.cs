using System.Net;
using System.Net.Sockets;
using DaffyZoom.Cache;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>Database policies: how they are named, and their windows of the day.</summary>
public class DatabasePolicyTests
{
    [Theory]
    [InlineData("sql01/Sales", "sql01:1433", "Sales")]
    [InlineData("SQL01,14340/Sales", "sql01:14340", "Sales")]
    [InlineData("tcp:127.0.0.1:14340/AdventureWorksDW2022", "127.0.0.1:14340", "AdventureWorksDW2022")]
    public void IsNamedByServerAndDatabase(string text, string server, string database) =>
        Assert.Equal((server, database), DatabasePolicy.ParseId(text));

    [Theory]
    [InlineData("Sales")]
    [InlineData("sql01/")]
    [InlineData("sql01,99999/Sales")]
    public void RefusesWhatIsNotServerAndDatabase(string text) => Assert.Throws<FormatException>(() => DatabasePolicy.ParseId(text));

    [Fact]
    public void BlackoutsMayCrossMidnight()
    {
        var policy = new DatabasePolicy { Blackouts = ["23:30-00:30", "02:00-02:45"] };
        Assert.True(policy.InBlackout(new DateTime(2026, 11, 9, 23, 45, 0)));
        Assert.True(policy.InBlackout(new DateTime(2026, 11, 10, 0, 15, 0)));
        Assert.True(policy.InBlackout(new DateTime(2026, 11, 10, 2, 44, 0)));
        Assert.False(policy.InBlackout(new DateTime(2026, 11, 10, 2, 45, 0)));
        Assert.False(policy.InBlackout(new DateTime(2026, 11, 10, 12, 0, 0)));
    }

    [Theory]
    [InlineData("4h", 240)]
    [InlineData("90m", 90)]
    [InlineData("2d", 2880)]
    [InlineData("30", 30)]
    [InlineData("45s", 1)]
    public void DurationsAreMinutes(string text, int minutes) => Assert.Equal(minutes, DatabasePolicy.ParseMinutes(text));

    /// <summary>A watermark's interval is seconds: --every 5s is five seconds, not a minute rounded up.</summary>
    [Theory]
    [InlineData("5s", 5)]
    [InlineData("90", 90)]
    [InlineData("2m", 120)]
    [InlineData("1h", 3600)]
    public void IntervalsAreSeconds(string text, int seconds) => Assert.Equal(seconds, DatabasePolicy.ParseSeconds(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5m")]
    public void ADurationMustBeOne(string text) => Assert.Throws<FormatException>(() => DatabasePolicy.ParseMinutes(text));

    [Fact]
    public void ProceduresAreAllowedWithOrWithoutTheirSchema()
    {
        var policy = new DatabasePolicy { Procedures = ["dbo.usp_ZoomTerritoryYear", "rpt.usp_Other"] };
        Assert.True(policy.AllowsProcedure("usp_ZoomTerritoryYear"));
        Assert.True(policy.AllowsProcedure("[dbo].[usp_zoomterritoryyear]"));
        Assert.True(policy.AllowsProcedure("rpt.usp_Other"));
        Assert.False(policy.AllowsProcedure("usp_Other"));          // only dbo may be left out
        Assert.False(policy.AllowsProcedure("dbo.usp_Something"));
    }

    /// <summary>
    /// A session's database is spelled as its client spelled it - Database=adventureworksdw2022 in a
    /// target, AdventureWorksDW2022 from SSMS's list - and a policy covers it in any of them.
    /// </summary>
    [Fact]
    public void APolicyCoversItsDatabaseInAnySpelling()
    {
        var policies = new PolicySet(() => [new DatabasePolicy { Server = "sql01:1433", Database = "AdventureWorksDW2022", Mode = CacheMode.Ttl }], () => 1);
        Assert.NotNull(policies.For("SQL01:1433", "Adventureworksdw2022"));
        Assert.NotNull(policies.For("sql01:1433", "adventureworksdw2022"));
        Assert.Null(policies.For("sql02:1433", "AdventureWorksDW2022"));
        Assert.Null(policies.For("sql01:1433", "AdventureWorks2022"));
    }

    /// <summary>A store from 0.1.0 can hold one database twice, spelled two ways: the newer decides.</summary>
    [Fact]
    public void OfOneDatabaseInTwoSpellingsTheNewerPolicyDecides()
    {
        var older = new DatabasePolicy { Server = "sql01:1433", Database = "sales", Mode = CacheMode.Ttl, UpdatedUtc = DateTime.UtcNow.AddHours(-1) };
        var newer = new DatabasePolicy { Server = "sql01:1433", Database = "Sales", Mode = CacheMode.Off, UpdatedUtc = DateTime.UtcNow };
        var policies = new PolicySet(() => [older, newer], () => 1);
        Assert.Equal(CacheMode.Off, policies.For("sql01:1433", "SALES")!.Mode);
        Assert.Single(policies.All);
    }

    [Theory]
    [InlineData("sql01:1433", "Sales", "sql01/Sales")]
    [InlineData("sql01:14333", "Sales", "sql01,14333/Sales")]
    [InlineData("sql01:1433", "My Sales", "\"sql01/My Sales\"")]
    public void ADatabaseIsNamedAsDbSetTakesIt(string server, string database, string expected)
    {
        Assert.Equal(expected, DatabasePolicy.CommandId(server, database));
        Assert.Equal((server, database), DatabasePolicy.ParseId(expected.Trim('"')));
    }

    [Fact]
    public void ScheduledTimesArePassedOnceAcrossMidnight()
    {
        var edge = new TimeOnly(0, 10);
        Assert.True(ZoomCache.Passed(edge, new DateTime(2026, 11, 9, 23, 59, 0), new DateTime(2026, 11, 10, 0, 11, 0)));
        Assert.False(ZoomCache.Passed(edge, new DateTime(2026, 11, 10, 0, 11, 0), new DateTime(2026, 11, 10, 0, 12, 0)));
        Assert.True(ZoomCache.Passed(new TimeOnly(2, 30), new DateTime(2026, 11, 10, 2, 29, 50), new DateTime(2026, 11, 10, 2, 30, 0)));
    }
}

/// <summary>Cached answers on disk: encrypted, and refused when anything about them is not as written.</summary>
public sealed class CacheStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static byte[] Data(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i % 251 == 0 ? i / 251 : i % 7);    // compressible, but not trivially
        return data;
    }

    private static async Task<byte[]> ReadAllAsync(EntryContent content)
    {
        var all = new List<byte>();
        await foreach (var chunk in content.ChunksAsync()) all.AddRange(chunk.ToArray());
        return [.. all];
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(CacheStore.ChunkSize)]
    [InlineData(CacheStore.ChunkSize * 3 + 12345)]
    public async Task WhatIsWrittenIsReadBackExactly(int length)
    {
        var store = CacheStore.Open(_directory.Path);
        var key = new CacheKey(7);
        byte[] data = Data(length);
        var (path, _) = store.Write(key, data);

        foreach (int inMemory in new[] { -1, int.MaxValue })
        {
            var content = await store.OpenVerifiedAsync(key, path, length, inMemory, CancellationToken.None);
            Assert.Equal(inMemory > 0, content.InMemory);
            Assert.Equal(data, await ReadAllAsync(content));
        }
    }

    [Fact]
    public void TheFileIsNeitherPlainNorTheSameTwice()
    {
        var store = CacheStore.Open(_directory.Path);
        byte[] data = System.Text.Encoding.Unicode.GetBytes(string.Concat(Enumerable.Repeat("Grace Hopper, 1906-1992. ", 200)));
        var (first, _) = store.Write(new CacheKey(1), data);
        var (second, _) = store.Write(new CacheKey(1), data);

        byte[] a = File.ReadAllBytes(first), b = File.ReadAllBytes(second);
        Assert.DoesNotContain("Grace", System.Text.Encoding.Unicode.GetString(a));
        Assert.NotEqual(a, b);                                                 // a fresh nonce every write
        Assert.NotEqual(first, second);                                        // and a file name of its own
    }

    [Fact]
    public async Task ATamperedByteIsCaught()
    {
        var store = CacheStore.Open(_directory.Path);
        var key = new CacheKey(9);
        var (path, _) = store.Write(key, Data(200_000));
        byte[] file = File.ReadAllBytes(path);
        file[^100] ^= 0x01;
        File.WriteAllBytes(path, file);

        await Assert.ThrowsAsync<CacheCorruptException>(() => store.OpenVerifiedAsync(key, path, 200_000, int.MaxValue, CancellationToken.None));
    }

    [Fact]
    public async Task ATruncatedFileIsCaught()
    {
        var store = CacheStore.Open(_directory.Path);
        var key = new CacheKey(9);
        var (path, _) = store.Write(key, Data(CacheStore.ChunkSize * 2 + 10));
        byte[] file = File.ReadAllBytes(path);
        File.WriteAllBytes(path, file[..(file.Length / 2)]);

        await Assert.ThrowsAsync<CacheCorruptException>(() => store.OpenVerifiedAsync(key, path, CacheStore.ChunkSize * 2 + 10, 0, CancellationToken.None));
    }

    [Fact]
    public async Task AnotherEntrysFileOrAnotherRunsIsCaught()
    {
        var store = CacheStore.Open(_directory.Path);
        var (path, _) = store.Write(new CacheKey(1), Data(1000));
        await Assert.ThrowsAsync<CacheCorruptException>(() => store.OpenVerifiedAsync(new CacheKey(2), path, 1000, 0, CancellationToken.None));

        // A restart has a new key: what the last run wrote cannot be opened, and is cleared away.
        var copy = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dzc");
        File.Copy(path, copy);
        var restarted = CacheStore.Open(_directory.Path);
        Assert.Empty(Directory.EnumerateFiles(_directory.Path, "*.dzc"));
        await Assert.ThrowsAsync<CacheCorruptException>(() => restarted.OpenVerifiedAsync(new CacheKey(1), copy, 1000, 0, CancellationToken.None));
        File.Delete(copy);
    }
}

/// <summary>Which filled answers may be replayed.</summary>
public class ReplayCheckTests
{
    private static byte[] ResultSet()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("a", SqlColumnType.Int), new ColumnDef("b", SqlColumnType.NVarChar)],
                                               [[1, "one"], [2, "two"]]));
        return reply.Encode(rpc: false);
    }

    private static byte[] EnvChange(byte type, byte[] newValue)
    {
        var w = new TdsWriter();
        w.WriteByte(TdsConstants.Token.EnvChange);
        w.WriteUInt16((ushort)(1 + newValue.Length + 1));
        w.WriteByte(type);
        w.WriteBytes(newValue);
        w.WriteByte(0);
        return w.ToArray();
    }

    private static (string? Refused, int Start) Check(byte[] response, bool reset) =>
        ReplayCheck.Check(ResponseReader.Read(response, columnEncryption: false), reset);

    [Fact]
    public void AnOrdinaryResultSetMayBeReplayed() => Assert.Equal((null, 0), Check(ResultSet(), reset: false));

    [Fact]
    public void AResetsEnvChangesAreLeftOutOfTheReplay()
    {
        byte[] reset = EnvChange(TdsConstants.EnvChangeType.ResetConnectionAck, []);
        var (refused, start) = Check([.. reset, .. ResultSet()], reset: true);
        Assert.Null(refused);
        Assert.Equal(reset.Length, start);
    }

    [Fact]
    public void AnEnvChangeWithoutAResetIsTheRequestChangingTheSession() =>
        Assert.Equal("it changed the session", Check([.. EnvChange(TdsConstants.EnvChangeType.ResetConnectionAck, []), .. ResultSet()], reset: false).Refused);

    [Fact]
    public void AnEnvChangeAfterTheResultsIsRefused() =>
        Assert.Equal("it changed the session", Check([.. ResultSet(), .. EnvChange(TdsConstants.EnvChangeType.ResetConnectionAck, [])], reset: true).Refused);

    [Fact]
    public void AnErrorIsRefused()
    {
        var reply = Reply.Failed(new Commands.GatewayException(50000, "no"));
        Assert.StartsWith("it carried an error", Check(reply.Encode(rpc: false), reset: false).Refused);
    }

    private static byte[] SessionState(byte sequence)
    {
        var w = new TdsWriter();
        w.WriteByte(TdsConstants.Token.SessionState);
        w.WriteUInt32(6);
        w.WriteUInt32(sequence);   // SeqNo
        w.WriteByte(1);            // Status: recoverable
        w.WriteByte(0);            // (no state data)
        return w.ToArray();
    }

    [Fact]
    public void SessionStateIsAllowedOnlyWhereItIsExplainedAndIsNeverStored()
    {
        // What SqlClient's connection resiliency gets for "SET NOCOUNT ON; SELECT ...": the SET's state
        // change reported in a SESSIONSTATE token, numbered for this session.
        byte[] response = [.. SessionState(7), .. ResultSet(), .. SessionState(8)];
        var detail = ResponseReader.Read(response, columnEncryption: false);

        Assert.Equal("it changed the session (session recovery state)", ReplayCheck.Check(detail, reset: false).Refused);
        var (refused, start) = ReplayCheck.Check(detail, reset: false, stateExplained: true);
        Assert.Null(refused);
        Assert.Equal(ResultSet(), Filler.WithoutSessionStates(response, start, detail.SessionStates));
    }

    [Fact]
    public void ResultsWithoutTheirMetadataAreRefused()
    {
        var w = new TdsWriter();
        w.WriteByte(TdsConstants.Token.ColMetadata);
        w.WriteUInt16(0xFFFF);
        w.WriteBytes(Requests.Done());
        Assert.Equal("its results came without column metadata", Check(w.ToArray(), reset: false).Refused);
    }
}

/// <summary>Framing a stored answer for the session it is replayed to.</summary>
public class ReplayFramerTests
{
    [Fact]
    public async Task FramesAtThePacketSizeWithTheSessionsSpidAndEndsOnce()
    {
        var packets = new List<byte[]>();
        var framer = new ReplayFramer(512, spid: 63, packet => { packets.Add(packet.ToArray()); return ValueTask.CompletedTask; });
        byte[] data = Enumerable.Range(0, 1300).Select(i => (byte)i).ToArray();
        await framer.AppendAsync(data.AsMemory(0, 700));
        await framer.AppendAsync(data.AsMemory(700));
        await framer.FinishAsync();

        Assert.Equal([512, 512, 1300 - 2 * 504 + 8], packets.Select(p => p.Length));
        Assert.All(packets, p => Assert.Equal(TdsConstants.PacketType.TabularResult, p[0]));
        Assert.Equal([0, 0, 1], packets.Select(p => (int)(p[1] & TdsConstants.PacketStatus.EndOfMessage)));
        Assert.Equal([1, 2, 3], packets.Select(p => (int)p[6]));
        Assert.All(packets, p => Assert.Equal(63, (p[4] << 8) | p[5]));
        Assert.Equal(data, packets.SelectMany(p => p[8..]).ToArray());
    }

    [Fact]
    public async Task AnEmptyStreamIsOnePacketWithEndOfMessage()
    {
        var packets = new List<byte[]>();
        var framer = new ReplayFramer(4096, 51, packet => { packets.Add(packet.ToArray()); return ValueTask.CompletedTask; });
        await framer.FinishAsync();
        Assert.Equal(8, Assert.Single(packets).Length);
    }
}

/// <summary>The index: the prefilter, fills claimed once, and flushes by epoch.</summary>
public class CacheIndexTests
{
    private static CacheEntry Entry(CacheKey key, ulong firstPacket, long epoch = 0) => new()
    {
        Key = key, Path = Path.Combine(Path.GetTempPath(), "nope.dzc"), Length = 10, FileBytes = 100, Server = "sql01:1433", Database = "Sales",
        Epoch = epoch, CreatedUtc = DateTime.UtcNow, OriginalMs = 10_000, FirstPacketHash = firstPacket, ShapeHash = 1, Shape = "SELECT ?",
    };

    [Fact]
    public void ThePrefilterHoldsWhatIsCachedAndNothingElse()
    {
        var index = new CacheIndex();
        Assert.False(index.MightHit(5));
        var one = Entry(new CacheKey(1), 5);
        var two = Entry(new CacheKey(2), 5);                // another context, the same first packet
        index.Add(one);
        index.Add(two);
        Assert.True(index.MightHit(5));
        index.Remove(one);
        Assert.True(index.MightHit(5));
        index.Remove(two);
        Assert.False(index.MightHit(5));
        Assert.Equal(0, index.FileBytes);
    }

    /// <summary>A client with a small packet size sends a request in several packets; its first still gives the same prefix hash.</summary>
    [Fact]
    public void ThePrefixHashDoesNotDependOnThePacketSize()
    {
        string sql = "SELECT a FROM dbo.t WHERE b IN (" + string.Join(",", Enumerable.Range(1, 400)) + ")";
        byte[] whole = Requests.BatchPayload(sql);
        byte[] firstOf512 = Requests.LongBatch(sql, packetSize: 512)[TdsConstants.HeaderLength..512];
        const byte batch = TdsConstants.PacketType.SqlBatch;
        Assert.Equal(CacheIndex.PrefixHash(batch, whole), CacheIndex.PrefixHash(batch, firstOf512));
        Assert.NotEqual(CacheIndex.PrefixHash(batch, whole), CacheIndex.PrefixHash(batch, Requests.BatchPayload("SELECT 1")));
    }

    /// <summary>The prefilter ignores what the key ignores, or a request the key would serve would never be tried.</summary>
    [Fact]
    public void ThePrefixHashIgnoresWhatTheKeyIgnores()
    {
        const byte batch = TdsConstants.PacketType.SqlBatch;
        ulong plain = CacheIndex.PrefixHash(batch, Requests.BatchPayload("SELECT a FROM dbo.t"));
        Assert.Equal(plain, CacheIndex.PrefixHash(batch, Requests.BatchPayload("SELECT a FROM dbo.t\r\n")));
        Assert.Equal(plain, CacheIndex.PrefixHash(batch, Requests.BatchPayload("    SELECT a FROM dbo.t  ")));
        Assert.NotEqual(plain, CacheIndex.PrefixHash(batch, Requests.BatchPayload("\r\nSELECT a FROM dbo.t")));
    }

    [Fact]
    public void AKeyIsFilledByOneSessionAtATime()
    {
        var index = new CacheIndex();
        Assert.True(index.TryBeginFill(new CacheKey(1)));
        Assert.False(index.TryBeginFill(new CacheKey(1)));
        index.EndFill(new CacheKey(1));
        index.Add(Entry(new CacheKey(1), 5));
        Assert.False(index.TryBeginFill(new CacheKey(1)));   // cached: nothing to fill
    }

    /// <summary>
    /// A flush is per database, in any case: one spelled SALES ends what a session spelling it Sales
    /// filled. (0.1.0 matched the case exactly, and such a flush ended nothing.)
    /// </summary>
    [Fact]
    public void AFlushIsPerDatabaseInAnyCase()
    {
        var index = new CacheIndex();
        index.Flush("SQL01:1433", "Sales");
        Assert.Equal(1, index.Epoch("sql01:1433", "Sales"));
        Assert.Equal(1, index.Epoch("sql01:1433", "SALES"));
        Assert.Equal(0, index.Epoch("sql01:1433", "Other"));
        Assert.Equal(0, index.Epoch("sql02:1433", "Sales"));
    }
}

/// <summary>
/// The whole of serving, on a real relay: a slow query is seen, filled on its next run, then
/// answered from the cache without the target hearing of it - with resets, leading SETs, flushes and
/// writes. The target is the test, answering by hand.
/// </summary>
/// <summary>
/// A database's watermark: any change of its answer flushes. Until it is first read, and while it
/// cannot be read, a load could finish unseen - so nothing of the database is served then, and
/// reading it again flushes.
/// </summary>
public sealed class WatermarkTests : IAsyncLifetime
{
    private const string Server = "sql01:1433";
    private const string Database = "Sales";
    private readonly TempDirectory _directory = new();
    private readonly DatabasePolicy _policy = new()
    {
        Server = Server, Database = Database, Mode = CacheMode.Ttl,
        WatermarkSql = "SELECT MAX(LoadId) FROM etl.LoadStatus", WatermarkLogin = "etl",
    };
    private Func<byte[]> _answer = () => Answer(1);
    private string _watermarkLogin = "etl";
    private ZoomCache _cache = null!;

    public Task InitializeAsync()
    {
        var target = TargetEndpoint.Parse("Server=sql01;User Id=svc_etl;Password=p");
        _cache = new ZoomCache(new CacheOptions { SweepInterval = TimeSpan.FromHours(1) }, new PolicySet(() => [_policy], () => 1),
                               Path.Combine(_directory.Path, "cache"), login => login == _watermarkLogin ? target : null)
        {
            WatermarkQuery = (_, _, _) => Task.FromResult(_answer()),
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _cache.DisposeAsync();
        _directory.Dispose();
    }

    private static byte[] Answer(int loadId)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("LoadId", SqlColumnType.Int)], [[loadId]]));
        return reply.Encode(rpc: false);
    }

    /// <summary>An entry filled now, under the database's current flush count.</summary>
    private CacheEntry Filled()
    {
        var entry = new CacheEntry
        {
            Key = new CacheKey((UInt128)Random.Shared.NextInt64()), Path = Path.Combine(_directory.Path, "nope.dzc"), Length = 10, FileBytes = 100,
            Server = Server, Database = Database, Epoch = _cache.Index.Epoch(Server, Database), CreatedUtc = DateTime.UtcNow,
            OriginalMs = 10_000, FirstPacketHash = 1, ShapeHash = 1, Shape = "SELECT ?",
        };
        _cache.Index.Add(entry);
        return entry;
    }

    private bool Live(CacheEntry entry) => _cache.Invalid(entry, _policy, DateTime.UtcNow) is null;

    private Task PollAsync() => _cache.PollWatermarkAsync(_policy, CancellationToken.None);

    [Fact]
    public async Task NothingIsServedUntilItIsReadAndTheFirstReadFlushes()
    {
        var before = Filled();
        Assert.False(Live(before));
        Assert.Contains("watermark", _cache.Invalid(before, _policy, DateTime.UtcNow));
        Assert.Contains("has not been read", _cache.StateOf(_policy));

        await PollAsync();
        Assert.False(Live(before));                       // filled before the first read: it may predate a load
        Assert.Equal("serving", _cache.StateOf(_policy));
        Assert.True(Live(Filled()));
    }

    [Fact]
    public async Task AChangedAnswerFlushesAndTheSameAnswerDoesNot()
    {
        await PollAsync();
        var entry = Filled();
        await PollAsync();
        Assert.True(Live(entry));

        _answer = () => Answer(2);
        await PollAsync();
        Assert.False(Live(entry));
        Assert.True(Live(Filled()));
    }

    [Fact]
    public async Task WhileItFailsNothingIsServedAndWorkingAgainFlushes()
    {
        await PollAsync();
        var entry = Filled();

        _answer = () => throw new IOException("the server is restarting");
        await PollAsync();
        Assert.Contains("the server is restarting", _cache.StateOf(_policy));
        await PollAsync();
        Assert.False(Live(entry));

        _answer = () => Answer(1);                         // the same answer: a load may still have come and gone
        await PollAsync();
        Assert.False(Live(entry));
        Assert.True(Live(Filled()));
    }

    [Fact]
    public async Task AnErrorAnswerIsAFailure()
    {
        await PollAsync();
        var entry = Filled();
        _answer = () => Reply.Failed(new Commands.GatewayException(208, "Invalid object name 'etl.LoadStatus'.")).Encode(rpc: false);
        await PollAsync();
        Assert.False(Live(entry));
    }

    [Fact]
    public async Task AWatermarkLoginWithoutAMappingIsAFailure()
    {
        _watermarkLogin = "someone else";
        await PollAsync();
        Assert.False(Live(Filled()));
    }

    [Fact]
    public void ADatabaseWithoutAWatermarkNeedsNone()
    {
        _policy.WatermarkSql = null;
        Assert.True(Live(Filled()));
    }
}

public sealed class CacheServingTests : IAsyncLifetime
{
    private const string Server = "sql01:1433";
    private const string Database = "Sales";
    private readonly TempDirectory _directory = new();
    private readonly List<DatabasePolicy> _policies = [new() { Server = Server, Database = Database, Mode = CacheMode.Ttl }];
    private ZoomCache _cache = null!;
    private Stream _client = null!, _target = null!;
    private TcpClient _targetSocket = null!;
    private ProxySession _session = null!;
    private Task _relaying = null!;
    private readonly List<TcpClient> _sockets = [];

    public async Task InitializeAsync()
    {
        long version = 1;
        _cache = new ZoomCache(new CacheOptions { SlowMs = 50, MinRuns = 2, SweepInterval = TimeSpan.FromHours(1) },
                               new PolicySet(() => _policies, () => version), Path.Combine(_directory.Path, "cache"));
        _cache.Start();

        var (client, relayClient) = await PairAsync();
        var (relayTarget, target) = await PairAsync();
        _sockets.AddRange([client, relayClient, relayTarget, target]);
        _session = new ProxySession(1, "127.0.0.1:1")
        {
            Spid = 63, State = SessionState.Relaying, Target = Server, TargetLogin = "svc_reports", Database = Database,
            TdsVersion = 0x74000004, LoginDefaults = new LoginDefaults(0xE0, 0x03, 0, 0, 0x409, ""),
        };
        _session.Attach(relayClient);
        _session.AttachUpstream(relayTarget);

        var observer = (CacheObserver)_cache.ObserversFor(_session).Single();
        _relaying = Relay.RunAsync(_session, relayClient.GetStream(), relayTarget.GetStream(), 4096, [observer], commands: null, gate: observer);
        _client = client.GetStream();
        _target = target.GetStream();
        _targetSocket = target;
    }

    public async Task DisposeAsync()
    {
        _session.Abort("test over");
        foreach (var socket in _sockets) socket.Dispose();
        try { await _relaying.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        await _cache.DisposeAsync();
        _directory.Dispose();
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

    private static async Task<ReceivedMessage> ReadAsync(Stream stream, int seconds = 10) =>
        await new TdsMessageReader(stream).ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(seconds))
        ?? throw new EndOfStreamException();

    private static byte[] Answer(params int[] values)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("a", SqlColumnType.Int)], values.Select(v => new object?[] { v }).ToList()));
        return reply.Encode(rpc: false);
    }

    /// <summary>One request through the relay to the target, answered by it after <paramref name="delayMs"/>.</summary>
    private async Task<byte[]> ThroughTargetAsync(byte[] request, byte[] answer, int delayMs = 120)
    {
        await _client.WriteAsync(request);
        var heard = await ReadAsync(_target);
        Assert.Equal(request[0], heard.Message.Type);
        await Task.Delay(delayMs);
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, answer, 4096, 63));
        return (await ReadAsync(_client)).Message.Payload;
    }

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), what);
    }

    /// <summary>
    /// Slow runs seen by the analyzer (one, normally), then the run that fills; after this the request
    /// is cached.
    /// </summary>
    private async Task FillAsync(byte[] request, byte[] answer, int runsBeforeFill = 1)
    {
        for (int i = 0; i < runsBeforeFill; i++)
        {
            long analyzed = _cache.Counters.Analyzed;
            await ThroughTargetAsync(request, answer);
            await WaitForAsync(() => _cache.Counters.Analyzed > analyzed, "the analyzer saw the run");
        }

        long filled = _cache.Counters.Filled;
        await ThroughTargetAsync(request, answer);
        await WaitForAsync(() => _cache.Counters.Filled > filled, "the run was filled");
    }

    /// <summary>Nothing has reached the target - looked at without reading, so nothing is taken from what comes next.</summary>
    private async Task AssertTargetHearsNothingAsync()
    {
        await Task.Delay(300);
        Assert.Equal(0, _targetSocket.Available);
    }

    private static byte[] WithReset(byte[] packet)
    {
        packet[1] |= TdsConstants.PacketStatus.ResetConnection;
        return packet;
    }

    private static readonly byte[] ResetAck = [TdsConstants.Token.EnvChange, 3, 0, TdsConstants.EnvChangeType.ResetConnectionAck, 0, 0];

    [Fact]
    public async Task TheThirdRunIsAnsweredFromTheCacheAndNeverReachesTheTarget()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 1");
        byte[] answer = Answer(1, 2, 3);
        await FillAsync(request, answer);
        Assert.Equal(1, _cache.Index.Count);

        await _client.WriteAsync(request);
        var served = await ReadAsync(_client);
        Assert.Equal(answer, served.Message.Payload);
        Assert.Equal(63, served.Spid);
        await AssertTargetHearsNothingAsync();
        Assert.Equal(1, _cache.Counters.Hits);

        // And the session carries on with the target as before.
        Assert.Equal(Answer(9), await ThroughTargetAsync(Requests.Batch("SELECT 9"), Answer(9), delayMs: 0));
    }

    /// <summary>The first hit leaves its answer decrypted in memory; the next is sent from there, its file not read again. A flush lets it go.</summary>
    [Fact]
    public async Task HitsAfterTheFirstAreSentFromMemory()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 31");
        byte[] answer = Answer(31, 32);
        await FillAsync(request, answer);
        await _client.WriteAsync(request);
        Assert.Equal(answer, (await ReadAsync(_client)).Message.Payload);
        Assert.Equal(1, _cache.Hot.Count);

        File.Delete(Assert.Single(_cache.Index.Entries).Path);
        await _client.WriteAsync(request);
        Assert.Equal(answer, (await ReadAsync(_client)).Message.Payload);
        await AssertTargetHearsNothingAsync();
        Assert.Equal(2, _cache.Counters.Hits);

        _cache.FlushAll("the test");
        Assert.Equal(0, _cache.Hot.Count);
        Assert.Equal(0, _cache.Hot.Bytes);
    }

    /// <summary>The same query from another SSMS window: a line break at its end, the selection started at its indent.</summary>
    [Fact]
    public async Task TheSameQueryWithOtherWhitespaceAroundItIsServed()
    {
        byte[] answer = Answer(1, 2);
        await FillAsync(Requests.Batch("SELECT a FROM dbo.t WHERE b = 15"), answer);

        await _client.WriteAsync(Requests.Batch("    SELECT a FROM dbo.t WHERE b = 15\r\n"));
        Assert.Equal(answer, (await ReadAsync(_client)).Message.Payload);
        await AssertTargetHearsNothingAsync();
        Assert.Equal(1, _cache.Counters.Hits);

        // A line break before it is another request: the line numbers in its messages would differ.
        Assert.Equal(Answer(3), await ThroughTargetAsync(Requests.Batch("\r\nSELECT a FROM dbo.t WHERE b = 15"), Answer(3), delayMs: 0));
    }

    private Task AnswerAsync(byte[] answer) => _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, answer, 4096, 63)).AsTask();

    /// <summary>
    /// GO 4 on a new query: each run sent the moment the one before it ends. The second is copied
    /// before the first has been analyzed, and the third waits the moment its copy takes to be saved.
    /// </summary>
    [Fact]
    public async Task BackToBackRunsAreServedFromTheThird()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 21");
        byte[] answer = Answer(21);
        var analyzer = new TaskCompletionSource();
        var filler = new TaskCompletionSource();
        _cache.Analyzer.Held = analyzer.Task;
        _cache.Filler.Held = filler.Task;

        await ThroughTargetAsync(request, answer);                  // run 1: slow, and waiting for the analyzer

        await _client.WriteAsync(request);                          // run 2, at once
        await ReadAsync(_target);
        Assert.Equal(1, _cache.Counters.FillsStarted);             // copied early
        analyzer.SetResult();                                       // run 1 is analyzed while run 2 runs
        await WaitForAsync(() => _cache.Counters.Analyzed > 0, "the analyzer saw run 1");
        await Task.Delay(120);
        await AnswerAsync(answer);
        Assert.Equal(answer, (await ReadAsync(_client)).Message.Payload);

        await _client.WriteAsync(request);                          // run 3, at once: run 2's copy is still being saved
        await Task.Delay(30);
        Assert.Equal(0, _cache.Index.Count);
        filler.SetResult();
        Assert.Equal(answer, (await ReadAsync(_client)).Message.Payload);
        await AssertTargetHearsNothingAsync();
        Assert.Equal(1, _cache.Counters.Hits);
    }

    /// <summary>A copy not saved within a tenth of what its query took leaves the next run to SQL Server, as before - not a failure.</summary>
    [Fact]
    public async Task ARunWaitsForItsCopyToBeSavedOnlySoLong()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 22");
        await ThroughTargetAsync(request, Answer(22));
        await WaitForAsync(() => _cache.Counters.Analyzed > 0, "the analyzer saw run 1");
        var filler = new TaskCompletionSource();
        _cache.Filler.Held = filler.Task;
        await ThroughTargetAsync(request, Answer(22));              // run 2 fills; its copy waits for the filler

        Assert.Equal(Answer(23), await ThroughTargetAsync(request, Answer(23), delayMs: 0));
        Assert.Contains(_cache.Counters.FallbackReasons.Keys, k => k.Contains("still being saved after 100 ms"));
        Assert.False(_cache.Breaker.IsOpen);
        filler.SetResult();
        await WaitForAsync(() => _cache.Counters.Filled > 0, "run 2's copy was kept in the end");
    }

    [Fact]
    public async Task AnEarlyCopyIsLetGoWhenTheRunBeforeItIsNotAnalyzedByItsEnd()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 23");
        var analyzer = new TaskCompletionSource();
        _cache.Analyzer.Held = analyzer.Task;
        await ThroughTargetAsync(request, Answer(1));
        await ThroughTargetAsync(request, Answer(1));               // copied early; run 1 is still waiting as it ends

        await WaitForAsync(() => _cache.Counters.FillsAbandoned.Keys.Any(k => k.Contains("not been analyzed")), "the early copy was let go");
        Assert.Equal(1, _cache.Counters.FillsStarted);
        Assert.Equal(0, _cache.Index.Count);
        analyzer.SetResult();
    }

    [Fact]
    public async Task AnEarlyCopyOfWhatMayNotBeCachedIsLetGo()
    {
        byte[] request = Requests.Batch("SELECT a, GETDATE() FROM dbo.t WHERE b = 24");
        var analyzer = new TaskCompletionSource();
        _cache.Analyzer.Held = analyzer.Task;
        await ThroughTargetAsync(request, Answer(1));

        await _client.WriteAsync(request);
        await ReadAsync(_target);
        analyzer.SetResult();
        await WaitForAsync(() => _cache.Counters.Analyzed > 0, "the analyzer saw run 1");
        await AnswerAsync(Answer(1));
        await ReadAsync(_client);

        await WaitForAsync(() => _cache.Counters.FillsAbandoned.Keys.Any(k => k.StartsWith("it may not be cached")), "the early copy was let go");
        Assert.Equal(0, _cache.Index.Count);
    }

    [Fact]
    public async Task ABatchThatMayWriteIsNeverCopiedEarly()
    {
        byte[] request = Requests.Batch("UPDATE dbo.t SET a = 1 WHERE b = 25; SELECT a FROM dbo.t WHERE b = 25");
        var analyzer = new TaskCompletionSource();
        _cache.Analyzer.Held = analyzer.Task;
        await ThroughTargetAsync(request, Answer(1));
        await ThroughTargetAsync(request, Answer(1));
        Assert.Equal(0, _cache.Counters.FillsStarted);
        analyzer.SetResult();
    }

    [Fact]
    public async Task AResetOnAHitGoesToTheTargetOnARequestOfDaffyZoomsOwn()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 2");
        await FillAsync(request, Answer(4));

        byte[] reset = Requests.Batch("SELECT a FROM dbo.t WHERE b = 2");
        reset[1] |= TdsConstants.PacketStatus.ResetConnection;
        await _client.WriteAsync(reset);

        var own = await ReadAsync(_target);
        Assert.Equal("-- DaffyZoom: pooled connection reset", RequestParser.ParseBatchText(own.Message.Payload));
        byte[] ack = [TdsConstants.Token.EnvChange, 3, 0, TdsConstants.EnvChangeType.ResetConnectionAck, 0, 0];
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, [.. ack, .. Requests.Done()], 4096, 63));

        var served = await ReadAsync(_client);
        Assert.Equal([.. ack, .. Answer(4)], served.Message.Payload);
    }

    /// <summary>
    /// The pooled case: every use of the connection resets it, so every run has the same context. The
    /// answers the target gives start with the reset's acknowledgement, which is not stored; the hit
    /// sends the leading SETs with the reset flag, and passes on the target's fresh acknowledgement.
    /// </summary>
    [Fact]
    public async Task ABatchsLeadingSetsAreSentToTheTargetOnAHitWithTheReset()
    {
        const string sql = "SET NOCOUNT ON; SET ARITHABORT ON; SELECT a FROM dbo.t WHERE b = 3";
        await FillAsync(WithReset(Requests.Batch(sql)), [.. ResetAck, .. Answer(5)]);

        await _client.WriteAsync(WithReset(Requests.Batch(sql)));
        var own = await ReadAsync(_target);
        Assert.Equal("SET NOCOUNT ON; SET ARITHABORT ON", RequestParser.ParseBatchText(own.Message.Payload).TrimEnd(';', ' '));
        Assert.NotEqual(0, own.RawPackets[1] & TdsConstants.PacketStatus.ResetConnection);
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, [.. ResetAck, .. Requests.Done()], 4096, 63));

        byte[] expected = [.. ResetAck, .. Answer(5)];
        Assert.Equal(expected, (await ReadAsync(_client)).Message.Payload);
    }

    /// <summary>
    /// One connection running the same SET batch again and again: its first run is in the login's
    /// settings, every later one in the settings the batch leaves - which are the same each time, so
    /// from the second run on the request repeats exactly, and is cached.
    /// </summary>
    [Fact]
    public async Task TheSameSetBatchAgainOnOneConnectionRepeatsFromItsSecondRun()
    {
        byte[] request = Requests.Batch("SET NOCOUNT ON; SELECT a FROM dbo.t WHERE b = 10");
        await FillAsync(request, Answer(11), runsBeforeFill: 2);

        await _client.WriteAsync(request);
        var own = await ReadAsync(_target);
        Assert.Equal("SET NOCOUNT ON", RequestParser.ParseBatchText(own.Message.Payload).TrimEnd(';', ' '));
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, Requests.Done(), 4096, 63));
        Assert.Equal(Answer(11), (await ReadAsync(_client)).Message.Payload);
    }

    private const string PreparedStatement = "SELECT a FROM dbo.t WHERE b > @P1";

    private static byte[] PrepareAnswer(int handle, params byte[][] before) =>
        [.. before.SelectMany(b => b), .. Requests.HandleValue(handle), .. Requests.ProcEnd()];

    /// <summary>
    /// The hit first prepares the statement on the session - DaffyZoom's own sp_prepare - and the
    /// replay carries the handle the server issued for it, not the one the answer was filled with:
    /// the client's later sp_execute or sp_unprepare of it then finds it on its own session.
    /// </summary>
    [Fact]
    public async Task AnSpPrepexecHitCarriesTheHandleThisSessionsServerIssued()
    {
        byte[] request = Requests.PrepExec(PreparedStatement, "@P1 int", 20);
        await FillAsync(request, PreparedTests.PrepExecAnswer(1, 5, 6));

        await _client.WriteAsync(request);
        var own = await ReadAsync(_target);
        var call = Assert.Single(RequestParser.ParseRpc(own.Message.Payload));
        Assert.Equal(TdsConstants.ProcId.Prepare, call.ProcId);
        Assert.Equal(PreparedStatement, call.Sql);
        Assert.Empty(call.Parameters);
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, PrepareAnswer(42), 4096, 63));

        Assert.Equal(PreparedTests.PrepExecAnswer(42, 5, 6), (await ReadAsync(_client)).Message.Payload);
        await AssertTargetHearsNothingAsync();
        Assert.Equal(1, _cache.Counters.Hits);
    }

    /// <summary>The pooled case: the reset rides on the prepare, and the client hears the server's fresh acknowledgement first.</summary>
    [Fact]
    public async Task APooledSpPrepexecHitSendsItsResetWithThePrepare()
    {
        byte[] Request() => WithReset(Requests.PrepExec(PreparedStatement, "@P1 int", 21));
        await FillAsync(Request(), [.. ResetAck, .. PreparedTests.PrepExecAnswer(1, 7)]);

        await _client.WriteAsync(Request());
        var own = await ReadAsync(_target);
        Assert.Equal(TdsConstants.ProcId.Prepare, Assert.Single(RequestParser.ParseRpc(own.Message.Payload)).ProcId);
        Assert.NotEqual(0, own.RawPackets[1] & TdsConstants.PacketStatus.ResetConnection);
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, PrepareAnswer(9, ResetAck), 4096, 63));

        byte[] expected = [.. ResetAck, .. PreparedTests.PrepExecAnswer(9, 7)];
        Assert.Equal(expected, (await ReadAsync(_client)).Message.Payload);
    }

    /// <summary>
    /// SQL Server will not prepare the statement any more (a table dropped, a permission taken away):
    /// nothing has gone to the client, so its request goes to the server as it came, and it hears
    /// SQL Server's own answer. The entry is not tried again.
    /// </summary>
    [Fact]
    public async Task WhenThePrepareFailsTheRequestGoesToTheTargetAsItCame()
    {
        byte[] request = Requests.PrepExec(PreparedStatement, "@P1 int", 22);
        await FillAsync(request, PreparedTests.PrepExecAnswer(1, 8));
        byte[] failed = Reply.Failed(new Commands.GatewayException(208, "Invalid object name 'dbo.t'.")).Encode(rpc: true);

        await _client.WriteAsync(request);
        await ReadAsync(_target);                                   // DaffyZoom's own prepare
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, failed, 4096, 63));

        var forwarded = await ReadAsync(_target);
        Assert.Equal(request[TdsConstants.HeaderLength..], forwarded.Message.Payload);
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, failed, 4096, 63));
        Assert.Equal(failed, (await ReadAsync(_client)).Message.Payload);

        Assert.Equal(0, _cache.Index.Count);
        Assert.Equal(0, _cache.Counters.Hits);
    }

    [Fact]
    public async Task AnSpPrepexecAnswerWithoutItsHandleIsNotKept()
    {
        byte[] request = Requests.PrepExec(PreparedStatement, "@P1 int", 23);
        byte[] noHandle = new Reply { ResultSets = { new Tds.ResultSet([new ColumnDef("a", SqlColumnType.Int)], [[1]]) } }.Encode(rpc: true);
        await ThroughTargetAsync(request, noHandle);
        await WaitForAsync(() => _cache.Counters.Analyzed > 0, "the analyzer saw the first run");
        await ThroughTargetAsync(request, noHandle);
        await WaitForAsync(() => _cache.Counters.FillsAbandoned.Keys.Any(k => k.Contains("handle")), "the fill was refused for its missing handle");
        Assert.Equal(0, _cache.Index.Count);
    }

    [Fact]
    public async Task AfterAFlushTheRequestGoesToTheTargetAgain()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 4");
        await FillAsync(request, Answer(6));
        _cache.Flush(Server, Database, "the test");

        Assert.Equal(Answer(7), await ThroughTargetAsync(request, Answer(7), delayMs: 0));
    }

    /// <summary>An ETL job's flush that spells the database another way than the sessions do still ends their answers.</summary>
    [Fact]
    public async Task AFlushSpelledInAnotherCaseStillEndsTheAnswers()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 12");
        await FillAsync(request, Answer(14));
        _cache.Flush(Server, "SALES", "the test");

        Assert.Equal(Answer(15), await ThroughTargetAsync(request, Answer(15), delayMs: 0));
    }

    /// <summary>A write from a session that spelled the database another way flushes it all the same.</summary>
    [Fact]
    public async Task AWriteFromASessionSpellingTheDatabaseAnotherWayStillFlushesIt()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 13");
        await FillAsync(request, Answer(16));

        await ThroughTargetAsync(Requests.Batch("USE SALES"), [.. CacheObserverTests.EnvChangeDatabase("SALES", Database), .. Requests.Done()], delayMs: 0);
        Assert.Equal("SALES", _session.Database);                    // what daffyzoom.status shows follows the USE
        await ThroughTargetAsync(Requests.Batch("UPDATE dbo.t SET a = 3 WHERE b = 13"), Requests.Done(), delayMs: 0);
        await ThroughTargetAsync(Requests.Batch("USE Sales"), [.. CacheObserverTests.EnvChangeDatabase(Database, "SALES"), .. Requests.Done()], delayMs: 0);

        Assert.Equal(Answer(17), await ThroughTargetAsync(request, Answer(17), delayMs: 0));
    }

    /// <summary>The watch list says why a query that could be cached is not: its database has no policy, and the line that would give it one.</summary>
    [Fact]
    public void TheWatchListNamesTheLineThatWouldCacheADatabaseWithoutAPolicy()
    {
        WatchRow Row(string state, string database, string server = Server) =>
            new("id", state, 3, 1, 9000, 9000, 100, 1, 0, database, "svc_reports", DateTime.UtcNow, "SELECT ?", 1, server);

        Assert.Equal("would serve; not cached, its database has no policy: daffyzoom db set sql01/Other --mode ttl",
                     _cache.RowState(Row("would serve", "Other")));
        Assert.Contains("daffyzoom db set sql01,14333/Sales", _cache.RowState(Row("would fill", "Sales", "sql01:14333")));
        Assert.Equal("would serve", _cache.RowState(Row("would serve", "SALES")));                       // its policy, in another spelling
        Assert.Equal("refused: it writes", _cache.RowState(Row("refused: it writes", "Other")));        // no policy would cache it anyway
    }

    /// <summary>A policy changed by `daffyzoom db set` in another process: what was filled under the old one is not served.</summary>
    [Fact]
    public async Task AChangedPolicyEndsWhatWasFilledUnderTheOldOne()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 11");
        await FillAsync(request, Answer(12));
        _policies[0].UpdatedUtc = DateTime.UtcNow;

        Assert.Equal(Answer(13), await ThroughTargetAsync(request, Answer(13), delayMs: 0));
    }

    [Fact]
    public async Task AWriteThroughDaffyZoomFlushesTheDatabase()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 5");
        await FillAsync(request, Answer(8));

        await ThroughTargetAsync(Requests.Batch("UPDATE dbo.t SET a = 2 WHERE b = 5"), Requests.Done(), delayMs: 0);
        Assert.Equal(Answer(10), await ThroughTargetAsync(request, Answer(10), delayMs: 0));
    }

    [Fact]
    public async Task AnAnswerWithAnErrorIsNotKept()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 6");
        byte[] failed = Reply.Failed(new Commands.GatewayException(208, "Invalid object name")).Encode(rpc: false);
        await ThroughTargetAsync(request, failed);
        await WaitForAsync(() => _cache.Counters.Analyzed > 0, "the analyzer saw the first run");
        await ThroughTargetAsync(request, failed);
        await Task.Delay(300);

        Assert.Equal(0, _cache.Index.Count);
        Assert.Equal(0, _cache.Counters.FillsStarted);         // a run that ended in an error is not one that fills
    }

    [Fact]
    public async Task ADatabaseThatIsNotInTtlModeIsNeverFilled()
    {
        _policies.Clear();
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 7");
        await ThroughTargetAsync(request, Answer(1));
        await WaitForAsync(() => _cache.Counters.Analyzed > 0, "the analyzer saw the first run");
        await ThroughTargetAsync(request, Answer(1));
        await ThroughTargetAsync(request, Answer(1));
        Assert.Equal(0, _cache.Counters.FillsStarted);
    }

    [Fact]
    public async Task WithTheKillSwitchOffNothingIsServed()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 8");
        await FillAsync(request, Answer(2));
        _cache.Serving = false;
        Assert.Equal(Answer(3), await ThroughTargetAsync(request, Answer(3), delayMs: 0));
    }

    [Fact]
    public async Task AnEntryThatWillNotVerifyIsDroppedAndTheRequestGoesToTheTarget()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.t WHERE b = 9");
        await FillAsync(request, Answer(4));
        var entry = Assert.Single(_cache.Index.Entries);
        byte[] file = File.ReadAllBytes(entry.Path);
        file[^20] ^= 0xFF;
        File.WriteAllBytes(entry.Path, file);

        Assert.Equal(Answer(4), await ThroughTargetAsync(request, Answer(4), delayMs: 0));
        Assert.Equal(1, _cache.Counters.Fallbacks);
    }

    [Fact]
    public async Task AFillAbandonedByTheEntryLimitLeavesTheQueryAlone()
    {
        await _cache.DisposeAsync();
        long version = 1;
        _cache = new ZoomCache(new CacheOptions { SlowMs = 50, MinRuns = 2, MaxEntryBytes = 64, SweepInterval = TimeSpan.FromHours(1) },
                               new PolicySet(() => _policies, () => version), Path.Combine(_directory.Path, "cache2"));
        _cache.Start();
        // (The relay still has the first cache's observer; this checks the filler's own rule.)
        var fill = new Fill
        {
            Key = new CacheKey(1), Server = Server, Database = Database, Epoch = 0, FirstPacketHash = 1, Reset = false, ColumnEncryption = false,
            Classification = Classifier.Batch("SELECT 1 AS a", true),
        };
        fill.Append(Answer(Enumerable.Range(0, 100).ToArray()), _cache.Options.MaxEntryBytes, _cache.FillBudget);
        Assert.Equal("larger than the entry size limit", fill.Abandoned);
        Assert.Equal(0, _cache.FillBudget.Bytes);
    }
}
