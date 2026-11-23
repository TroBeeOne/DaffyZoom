using System.Net;
using System.Net.Sockets;
using DaffyZoom.Cache;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>What a request names, for tracked mode to resolve: every object it reads, never its own CTEs.</summary>
public class ReferenceTests
{
    private static List<string> Names(string sql) => Classifier.Batch(sql, true).References.Select(r => r.ToString()).ToList();

    [Fact]
    public void TablesViewsAndSchemaQualifiedFunctionsAreNamed() => Assert.Equivalent(
        new[] { "[dbo].[FactSales]", "[DimDate]", "[dbo].[vSales]", "[dbo].[fnMargin]" },
                     Names("SELECT dbo.fnMargin(f.a, f.b) FROM dbo.FactSales f JOIN DimDate d ON d.k = f.k WHERE f.k IN (SELECT k FROM dbo.vSales)"));

    [Fact]
    public void BuiltInFunctionsAreNot() =>
        Assert.Equal(["[dbo].[t]"], Names("SELECT UPPER(a), COUNT_BIG(*), STRING_AGG(b, ',') FROM dbo.t GROUP BY a"));

    [Fact]
    public void ACteIsNotAnObject() =>
        Assert.Equal(["[dbo].[t]"], Names("WITH totals AS (SELECT a FROM dbo.t) SELECT * FROM totals"));

    /// <summary>A CTE hides a table of its name only in its own statement: in the next one, the name is the table.</summary>
    [Fact]
    public void ACteNameInAnotherStatementIsTheTable() =>
        Assert.Equal(["[dbo].[t]", "[totals]"], Names("WITH totals AS (SELECT a FROM dbo.t) SELECT * FROM totals; SELECT * FROM totals"));

    [Fact]
    public void ADatabaseNameIsKept()
    {
        var reference = Assert.Single(Classifier.Batch("SELECT a FROM Sales.dbo.t", true).References);
        Assert.Equal("Sales", reference.Database);
        Assert.Equal("[dbo].[t]", reference.Quoted);
    }

    [Fact]
    public void ABracketInANameIsEscaped() => Assert.Equal(["[dbo].[odd]]name]"], Names("SELECT a FROM dbo.[odd]]name]"));

    [Fact]
    public void TableValuedFunctionsAreNamed() => Assert.Equal(["[dbo].[tvf]"], Names("SELECT * FROM dbo.tvf(2013)"));

    [Theory]
    [InlineData("SELECT a FROM dbo.t WITH (NOLOCK)", true)]
    [InlineData("SELECT a FROM dbo.t WITH (READUNCOMMITTED)", true)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SELECT a FROM dbo.t", true)]
    [InlineData("SELECT a FROM dbo.t WITH (INDEX(1))", false)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SNAPSHOT; SELECT a FROM dbo.t", false)]
    public void DirtyReadsAreMarked(string sql, bool dirty)
    {
        var classification = Classifier.Batch(sql, true);
        Assert.True(classification.Cacheable, classification.Describe());      // ttl mode still caches them
        Assert.Equal(dirty, classification.DirtyRead);
    }
}

/// <summary>Reading tracked mode's two answers, where anything unexpected means "cannot be tracked" or "changed".</summary>
public class ChangeTrackingTests
{
    internal static byte[] Resolution(string[] problems, (int Id, string Name, string Type, string Modified)[] objects, long? version)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("problem", SqlColumnType.NVarChar)], problems.Select(p => new object?[] { p }).ToList()));
        reply.ResultSets.Add(new Tds.ResultSet(
            [new ColumnDef("id", SqlColumnType.Int), new ColumnDef("name", SqlColumnType.NVarChar), new ColumnDef("type", SqlColumnType.VarChar),
             new ColumnDef("modified", SqlColumnType.VarChar)],
            objects.Select(o => new object?[] { o.Id, o.Name, o.Type, o.Modified }).ToList()));
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("version", SqlColumnType.BigInt)], [[version]]));
        return reply.Encode(rpc: false);
    }

    internal static byte[] Check(int data, int definitions, long? version)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet(
            [new ColumnDef("data", SqlColumnType.Int), new ColumnDef("definitions", SqlColumnType.Int), new ColumnDef("version", SqlColumnType.BigInt)],
            [[data, definitions, version]]));
        return reply.Encode(rpc: false);
    }

    internal static readonly (int, string, string, string)[] SalesObjects =
        [(10, "[dbo].[FactSales]", "U", "2026-11-10T06:20:23.057"), (11, "[dbo].[vSales]", "V", "2026-11-10T03:41:00.683")];

    [Fact]
    public void AResolutionGivesTheObjectsAndTheVersion()
    {
        var resolution = ChangeTracking.ParseResolution(Resolution([], SalesObjects, 42));
        Assert.Null(resolution.Problem);
        Assert.Equal(42, resolution.Tables!.Version);
        Assert.Equal(["[dbo].[FactSales]"], resolution.Tables.Tables.Select(t => t.Name));
        Assert.Equal(2, resolution.Tables.Objects.Count);
    }

    [Fact]
    public void AnyProblemRefuses()
    {
        var resolution = ChangeTracking.ParseResolution(Resolution(["[dbo].[FactResellerSales] is a table without Change Tracking"], SalesObjects, 42));
        Assert.Null(resolution.Tables);
        Assert.Contains("without Change Tracking", resolution.Problem);
    }

    [Fact]
    public void NoVersionMeansChangeTrackingIsOff() =>
        Assert.Contains("not on", ChangeTracking.ParseResolution(Resolution([], SalesObjects, null)).Problem);

    [Fact]
    public void NoTableRefuses() =>
        Assert.Contains("no table", ChangeTracking.ParseResolution(Resolution([], [(11, "[dbo].[vSales]", "V", "x")], 42)).Problem);

    [Fact]
    public void AnErrorRefuses() =>
        Assert.Contains("failed", ChangeTracking.ParseResolution(Reply.Failed(new Commands.GatewayException(229, "denied")).Encode(rpc: false)).Problem);

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    public void ACheckIsFreshOnlyWhenBothSayUnchanged(int data, int definitions, bool fresh)
    {
        var check = ChangeTracking.ParseCheck(Check(data, definitions, 50));
        Assert.Equal(fresh, check.Fresh);
        Assert.Equal(50, check.Version);
    }

    [Fact]
    public void ACheckThatFailedIsNotFresh()
    {
        var check = ChangeTracking.ParseCheck(Reply.Failed(new Commands.GatewayException(229, "The VIEW CHANGE TRACKING permission was denied")).Encode(rpc: false));
        Assert.False(check.Fresh);
        Assert.Contains("VIEW CHANGE TRACKING", check.Problem);
    }

    [Fact]
    public void AnAnswerOfAnotherShapeIsNotFresh()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("x", SqlColumnType.Int)], [[1]]));
        Assert.False(ChangeTracking.ParseCheck(reply.Encode(rpc: false)).Fresh);
    }

    [Fact]
    public void TheCheckNamesEveryTableAndObject()
    {
        string sql = ChangeTracking.CheckBatch(ChangeTracking.ParseResolution(Resolution([], SalesObjects, 42)).Tables!.Objects, 42);
        Assert.Contains("CHANGETABLE(CHANGES [dbo].[FactSales], 42)", sql);
        Assert.Contains("CHANGE_TRACKING_MIN_VALID_VERSION(10) <= 42", sql);
        Assert.Contains("object_id = 11) = N'2026-11-10T03:41:00.683'", sql);
        Assert.DoesNotContain("CHANGES [dbo].[vSales]", sql);
    }

    [Fact]
    public void TheResolutionQuotesTheNames() =>
        Assert.Contains("(N'[dbo].[it''s]')", ChangeTracking.ResolveBatch(["[dbo].[it's]"]));

    [Fact]
    public void ABatchLongerThanAPacketIsFramedInSeveral()
    {
        byte[] packets = BackgroundConnections.Frame(new string('x', 5000), 4096);
        Assert.Equal(TdsConstants.PacketType.SqlBatch, packets[0]);
        Assert.Equal(TdsConstants.PacketStatus.Normal, packets[1]);
        Assert.Equal(4096, (packets[2] << 8) | packets[3]);
        Assert.Equal(TdsConstants.PacketStatus.Normal, packets[4096 + 1]);
        Assert.Equal(TdsConstants.PacketStatus.EndOfMessage, packets[2 * 4096 + 1]);
        Assert.Equal(22 + 10_000 + 3 * TdsConstants.HeaderLength, packets.Length);
    }
}

/// <summary>
/// Tracked mode on a real relay, with SQL Server's answers stood in for: the first slow run is
/// resolved to its tables, the second fills with the version known before it was sent, and each
/// hit is served only if the check says nothing changed.
/// </summary>
public sealed class TrackedServingTests : IAsyncLifetime
{
    private const string Server = "sql01:1433";
    private const string Database = "Sales";
    private readonly TempDirectory _directory = new();
    private readonly List<DatabasePolicy> _policies = [new() { Server = Server, Database = Database, Mode = CacheMode.Tracked }];
    private readonly List<string> _queries = [];
    private Func<byte[]> _resolution = () => ChangeTrackingTests.Resolution([], ChangeTrackingTests.SalesObjects, 42);
    private Func<byte[]> _check = () => ChangeTrackingTests.Check(1, 1, 42);
    private ZoomCache _cache = null!;
    private Stream _client = null!, _target = null!;
    private TcpClient _targetSocket = null!;
    private ProxySession _session = null!;
    private Task _relaying = null!;
    private readonly List<TcpClient> _sockets = [];

    public async Task InitializeAsync()
    {
        var mapping = TargetEndpoint.Parse("Server=sql01;User Id=svc_reports;Password=p");
        _cache = new ZoomCache(new CacheOptions { SlowMs = 50, MinRuns = 2, SweepInterval = TimeSpan.FromHours(1) },
                               new PolicySet(() => _policies, () => 1), Path.Combine(_directory.Path, "cache"),
                               login => login == "bob" ? mapping : null)
        {
            TrackedQuery = (target, sql, _) =>
            {
                lock (_queries) _queries.Add(sql);
                Assert.Equal(Database, target.Database);
                return Task.FromResult(sql.Contains("@names") ? _resolution() : _check());
            },
        };
        _cache.Start();

        var (client, relayClient) = await PairAsync();
        var (relayTarget, target) = await PairAsync();
        _sockets.AddRange([client, relayClient, relayTarget, target]);
        _session = new ProxySession(1, "127.0.0.1:1")
        {
            Spid = 63, State = SessionState.Relaying, Target = Server, TargetLogin = "svc_reports", Database = Database, Login = "bob",
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

    private static async Task<ReceivedMessage> ReadAsync(Stream stream) =>
        await new TdsMessageReader(stream).ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new EndOfStreamException();

    private static byte[] Answer(params int[] values)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("a", SqlColumnType.Int)], values.Select(v => new object?[] { v }).ToList()));
        return reply.Encode(rpc: false);
    }

    private async Task<byte[]> ThroughTargetAsync(byte[] request, byte[] answer, int delayMs = 120)
    {
        await _client.WriteAsync(request);
        var heard = await ReadAsync(_target);
        Assert.Equal(request[0], heard.Message.Type);
        await Task.Delay(delayMs);
        await _target.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, answer, 4096, 63));
        return (await ReadAsync(_client)).Message.Payload;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), what);
    }

    private int Resolutions { get { lock (_queries) return _queries.Count(q => q.Contains("@names")); } }
    private int Checks { get { lock (_queries) return _queries.Count(q => q.Contains("CHANGETABLE")); } }

    /// <summary>A slow run (analyzed, then resolved), then the run that fills.</summary>
    private async Task FillAsync(byte[] request, byte[] answer)
    {
        int resolutions = Resolutions;
        await ThroughTargetAsync(request, answer);
        await WaitForAsync(() => Resolutions > resolutions, "the first slow run was resolved to its tables");
        await Task.Delay(50);
        long filled = _cache.Counters.Filled;
        await ThroughTargetAsync(request, answer);
        await WaitForAsync(() => _cache.Counters.Filled > filled, "the second run filled");
    }

    private async Task AssertTargetHearsNothingAsync()
    {
        await Task.Delay(300);
        Assert.Equal(0, _targetSocket.Available);
    }

    [Fact]
    public async Task AHitIsServedWhenTheCheckSaysNothingChanged()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.FactSales WHERE b = 1");
        await FillAsync(request, Answer(1, 2, 3));
        var entry = Assert.Single(_cache.Index.Entries);
        Assert.Equal(42, entry.Tracked!.Version);

        await _client.WriteAsync(request);
        Assert.Equal(Answer(1, 2, 3), (await ReadAsync(_client)).Message.Payload);
        await AssertTargetHearsNothingAsync();
        Assert.Equal(1, Checks);
        Assert.Contains("CHANGETABLE(CHANGES [dbo].[FactSales], 42)", _queries.Last());
    }

    [Fact]
    public async Task AChangeSinceTheBaselineSendsTheRequestToTheTarget()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.FactSales WHERE b = 2");
        await FillAsync(request, Answer(4));
        _check = () => ChangeTrackingTests.Check(0, 1, 43);

        Assert.Equal(Answer(5), await ThroughTargetAsync(request, Answer(5)));        // slow, as a miss is
        Assert.Empty(_cache.Index.Entries);

        // The run that went to the target fills again, with the version the check saw as its baseline.
        await WaitForAsync(() => _cache.Index.Count == 1, "the miss filled again");
        Assert.Equal(43, Assert.Single(_cache.Index.Entries).Tracked!.Version);
    }

    [Fact]
    public async Task AChangedDefinitionResolvesTheRequestAgain()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.vSales WHERE b = 3");
        await FillAsync(request, Answer(6));
        _check = () => ChangeTrackingTests.Check(1, 0, 42);
        int resolutions = Resolutions;

        Assert.Equal(Answer(7), await ThroughTargetAsync(request, Answer(7)));
        await WaitForAsync(() => Resolutions > resolutions, "the request was resolved again");
    }

    [Fact]
    public async Task ACheckThatCannotRunIsAFallbackAndKeepsTheEntry()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.FactSales WHERE b = 4");
        await FillAsync(request, Answer(8));
        _check = () => throw new IOException("the server went away");

        Assert.Equal(Answer(9), await ThroughTargetAsync(request, Answer(9), delayMs: 0));
        Assert.Equal(1, _cache.Counters.Fallbacks);
        Assert.Single(_cache.Index.Entries);
    }

    [Fact]
    public async Task ARequestThatCannotBeTrackedIsNeverFilled()
    {
        _resolution = () => ChangeTrackingTests.Resolution(["[dbo].[FactResellerSales] is a table without Change Tracking"], [], 42);
        byte[] request = Requests.Batch("SELECT a FROM dbo.FactResellerSales WHERE b = 5");
        await ThroughTargetAsync(request, Answer(1));
        await WaitForAsync(() => Resolutions == 1, "the request was resolved");
        await ThroughTargetAsync(request, Answer(1));
        await ThroughTargetAsync(request, Answer(1));
        await Task.Delay(200);

        Assert.Equal(0, _cache.Counters.FillsStarted);
        Assert.Contains(_cache.WatchList.Top(10), r => r.State.Contains("NotTracked") && r.State.Contains("without Change Tracking"));
    }

    [Fact]
    public async Task ADirtyReadIsNeverFilled()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.FactSales WITH (NOLOCK) WHERE b = 6");
        await ThroughTargetAsync(request, Answer(1));
        await WaitForAsync(() => _cache.Counters.Analyzed > 0, "the analyzer saw it");
        await ThroughTargetAsync(request, Answer(1));
        await ThroughTargetAsync(request, Answer(1));
        await Task.Delay(200);

        Assert.Equal(0, Resolutions);
        Assert.Contains(_cache.WatchList.Top(10), r => r.State.StartsWith("refused: DirtyRead"));
        Assert.Equal(0, _cache.Counters.FillsStarted);
    }

    [Fact]
    public async Task AWriteThroughDaffyZoomDoesNotFlushATrackedDatabase()
    {
        byte[] request = Requests.Batch("SELECT a FROM dbo.FactSales WHERE b = 7");
        await FillAsync(request, Answer(10));
        // (Not UPDATE ... SET: a SET counts as a session setting, which would change the key.)
        await ThroughTargetAsync(Requests.Batch("INSERT dbo.FactSales (a, b) VALUES (2, 7)"), Requests.Done(), delayMs: 0);

        // Change Tracking sees the write; the check decides.
        _check = () => ChangeTrackingTests.Check(0, 1, 44);
        Assert.Equal(Answer(11), await ThroughTargetAsync(request, Answer(11), delayMs: 0));
        Assert.Equal(1, Checks);
    }
}
