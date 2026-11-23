using DaffyZoom.Cache;
using DaffyZoom.Commands;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>daffyzoom.tracking: SQL Server's Change Tracking as the caller's own login sees it, and what stands in tracked mode's way.</summary>
public sealed class TrackingCommandTests : IAsyncLifetime
{
    private const string Database = "Sales";
    private readonly TempDirectory _directory = new();
    private readonly List<DatabasePolicy> _policies = [new() { Server = "sql01:1433", Database = Database, Mode = CacheMode.Tracked }];
    private readonly List<string> _queries = [];
    private Func<byte[]> _answer = () => throw new InvalidOperationException("no answer set");
    private ZoomCache _cache = null!;

    public Task InitializeAsync()
    {
        var mapping = TargetEndpoint.Parse("Server=sql01;User Id=svc_reports;Password=p");
        _cache = new ZoomCache(new CacheOptions(), new PolicySet(() => _policies, () => 1), Path.Combine(_directory.Path, "cache"),
                               login => login == "bob" ? mapping : null)
        {
            TrackedQuery = (target, sql, _) =>
            {
                Assert.Equal(Database, target.Database);
                _queries.Add(sql);
                return Task.FromResult(_answer());
            },
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _cache.DisposeAsync();
        _directory.Dispose();
    }

    private static ColumnDef Text(string name) => new(name, SqlColumnType.NVarChar);
    private static ColumnDef Int(string name) => new(name, SqlColumnType.Int);
    private static ColumnDef Big(string name) => new(name, SqlColumnType.BigInt);

    /// <summary>The status query's answer: the database row, then one row per table, in the columns it selects.</summary>
    private static byte[] Status(object?[] database, params object?[][] tables)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet(
            [Text("db"), Text("login"), Text("user"), Big("version"), Int("cleanup"), Int("retention"), Text("units"), Int("vd"), Int("rls")], [database]));
        reply.ResultSets.Add(new Tds.ResultSet(
            [Int("id"), Text("schema"), Text("name"), Int("tracked"), Int("pk"), Big("min"), Int("vct"), Int("select")], tables.ToList()));
        return reply.Encode(rpc: false);
    }

    private static readonly object?[] Granted = [Database, "svc_reports", "reports", 42L, 1, 2, "DAYS", 1, 0];
    private static readonly object?[] FactSales = [10, "dbo", "FactSales", 1, 1, 40L, 1, 1];

    private async Task<Reply> RunAsync(bool all = false) => TrackingReply.Build(await _cache.TrackingAsync("bob", Database, all, CancellationToken.None), all);

    private static object? Cell(Reply reply, int set, string column, int row = 0) =>
        reply.ResultSets[set].Rows[row][reply.ResultSets[set].Columns.ToList().FindIndex(c => c.Name == column)];

    [Fact]
    public async Task AllGrantedAndTrackedSaysSo()
    {
        _answer = () => Status(Granted, FactSales);
        var reply = await RunAsync();

        Assert.Equal("on", Cell(reply, 0, "change_tracking"));
        Assert.Equal(42L, Cell(reply, 0, "current_version"));
        Assert.Equal("2 days", Cell(reply, 0, "retention"));
        Assert.Equal("tracked", Cell(reply, 0, "daffyzoom_mode"));
        Assert.Equal(1, Cell(reply, 0, "tables_readable"));
        Assert.Equal("[dbo].[FactSales]", Cell(reply, 1, "table"));
        Assert.Equal("ok", Cell(reply, 1, "state"));
        Assert.Contains(reply.Messages, m => m.Contains("svc_reports can use tracked mode in [Sales]: 1 table tracked"));
        Assert.Contains(reply.Messages, m => m.Contains("'Sales', 'all'"));
        Assert.Contains("AND c.object_id IS NOT NULL", Assert.Single(_queries));
    }

    /// <summary>A login without the two grants: the answer says what it cannot see and gives the GRANTs, rather than failing.</summary>
    [Fact]
    public async Task MissingPermissionsAreSaidWithTheGrantsThatFixThem()
    {
        _answer = () => Status([Database, "svc_reports", "reports", 42L, null, null, null, 0, null], [10, "dbo", "FactSales", 1, 1, null, 0, 1]);
        var reply = await RunAsync();

        Assert.Equal("no", Cell(reply, 0, "view_definition"));
        Assert.Equal("cannot tell", Cell(reply, 0, "row_level_security"));
        Assert.Equal("not visible to this login", Cell(reply, 0, "retention"));
        Assert.Equal("this login cannot read its changes (VIEW CHANGE TRACKING)", Cell(reply, 1, "state"));
        Assert.Contains(reply.Messages, m => m.Contains("GRANT VIEW DEFINITION TO [reports];"));
        Assert.Contains(reply.Messages, m => m.Contains("GRANT VIEW CHANGE TRACKING ON SCHEMA::[dbo] TO [reports];"));
        Assert.DoesNotContain(reply.Messages, m => m.Contains("can use tracked mode"));
    }

    [Fact]
    public async Task ChangeTrackingOffSaysHowToTurnItOn()
    {
        _answer = () => Status([Database, "svc_reports", "reports", null, null, null, null, 1, 0]);
        var reply = await RunAsync();

        Assert.Equal("off", Cell(reply, 0, "change_tracking"));
        Assert.Null(Cell(reply, 0, "retention"));
        Assert.Empty(reply.ResultSets[1].Rows);
        Assert.Contains(reply.Messages, m => m.Contains("ALTER DATABASE [Sales] SET CHANGE_TRACKING = ON"));
    }

    /// <summary>'all' lists every table: which are tracked, and for the rest, whether they could be (a primary key).</summary>
    [Fact]
    public async Task AllTablesShowWhichAreTrackedAndWhichCouldBe()
    {
        _answer = () => Status(Granted, FactSales, [11, "dbo", "DimRegion", 0, 1, null, 0, 1], [12, "dbo", "FactHeap", 0, 0, null, 0, 1]);
        var reply = await RunAsync(all: true);

        Assert.DoesNotContain("AND c.object_id IS NOT NULL", Assert.Single(_queries));
        Assert.Equal(3, reply.ResultSets[1].Rows.Count);
        Assert.Equal("not tracked: ALTER TABLE [dbo].[DimRegion] ENABLE CHANGE_TRACKING;", Cell(reply, 1, "state", row: 1));
        Assert.Equal("not tracked; it needs a primary key before it can be", Cell(reply, 1, "state", row: 2));
        Assert.Equal(1, Cell(reply, 0, "tables_tracked"));
        Assert.DoesNotContain(reply.Messages, m => m.Contains("Only tracked tables"));
    }

    /// <summary>SQL Server cannot be asked - the login fails, the database cannot be opened: a message, not an error, and DaffyZoom's own side still said.</summary>
    [Fact]
    public async Task ASqlServerThatCannotBeAskedIsAMessage()
    {
        _answer = () => throw new IOException("Cannot open database \"Sales\" requested by the login (4060)");
        var reply = await RunAsync();

        Assert.Empty(reply.ResultSets);
        Assert.Contains(reply.Messages, m => m.Contains("could not be asked about [Sales] as svc_reports"));
        Assert.Contains(reply.Messages, m => m.Contains("needs a user there"));
        Assert.Contains(reply.Messages, m => m.Contains("in tracked mode"));
    }

    [Fact]
    public async Task ALoginWithoutAMappingIsTold()
    {
        var report = await _cache.TrackingAsync("nobody", Database, false, CancellationToken.None);
        Assert.Contains("no mapping", report.Status.Problem);
        Assert.Empty(_queries);
    }
}
