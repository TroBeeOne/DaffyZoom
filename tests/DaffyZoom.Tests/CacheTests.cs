using System.Diagnostics;
using System.Text;
using DaffyZoom.Cache;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>What the relay reads from a request's text about the session: USE, SET, and what taints it.</summary>
public class BatchTextTests
{
    [Theory]
    [InlineData("USE Sales", true)]
    [InlineData("use [Sales]; SELECT 1", true)]
    [InlineData("SELECT * FROM dbo.Users", false)]       // a word boundary: Users is not USE
    [InlineData("SELECT cause FROM dbo.t", false)]
    public void FindsAUse(string sql, bool expected) => Assert.Equal(expected, BatchText.Scan(sql).Use);

    [Theory]
    [InlineData("SET NOCOUNT ON", true)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SNAPSHOT", true)]
    [InlineData("UPDATE dbo.t SET a = 1", true)]          // not a session option, but folding it only costs hits
    [InlineData("SET @x = 1", false)]
    [InlineData("SET   @x = 1", false)]
    [InlineData("SELECT asset FROM dbo.t", false)]
    public void FindsSessionSettings(string sql, bool expected) => Assert.Equal(expected, BatchText.Scan(sql).ChangesSettings);

    [Theory]
    [InlineData("EXECUTE AS USER = 'bob'", "EXECUTE AS")]
    [InlineData("exec as login = 'bob'", "EXECUTE AS")]
    [InlineData("EXEC sp_set_session_context @key = N'tenant', @value = 3", "sp_set_session_context")]
    [InlineData("EXEC sys.sp_setapprole 'role', 'pw'", "sp_setapprole")]
    [InlineData("SET CONTEXT_INFO 0x01", "SET CONTEXT_INFO")]
    [InlineData("SET IMPLICIT_TRANSACTIONS ON", "SET IMPLICIT_TRANSACTIONS ON")]
    [InlineData("SET FMTONLY ON", "SET FMTONLY ON")]
    [InlineData("SET STATISTICS IO ON", "SET STATISTICS ON")]
    [InlineData("SET SHOWPLAN_XML ON", "SET SHOWPLAN_XML ON")]
    [InlineData("SETUSER 'bob'", "SETUSER")]
    [InlineData("SET IMPLICIT_TRANSACTIONS OFF", null)]   // what SSMS sends at every connect
    [InlineData("SET STATISTICS IO OFF", null)]
    [InlineData("EXEC dbo.usp_report", null)]
    public void FindsWhatTaintsTheSession(string sql, string? expected) => Assert.Equal(expected, BatchText.Scan(sql).Taint);

    [Theory]
    [InlineData("SET QUOTED_IDENTIFIER OFF", false)]
    [InlineData("SET QUOTED_IDENTIFIER ON", true)]
    [InlineData("SET ANSI_DEFAULTS ON", true)]
    [InlineData("SELECT 1", null)]
    public void FollowsQuotedIdentifier(string sql, bool? expected) => Assert.Equal(expected, BatchText.Scan(sql).QuotedIdentifier);

    /// <summary>
    /// Comments never run, so their words are not read - but a -- or /* inside a string, "quoted"
    /// text or a [bracketed] name is not a comment, and must not hide the code after it.
    /// </summary>
    [Theory]
    [InlineData("-- set the date range\nSELECT 1", false)]
    [InlineData("/* SET NOCOUNT ON */ SELECT 1", false)]
    [InlineData("/* outer /* nested */ SET NOCOUNT ON still in the outer one */ SELECT 1", false)]   // T-SQL nests them
    [InlineData("/* a */ SET NOCOUNT ON", true)]
    [InlineData("SELECT 1 -- a comment\r\nSET NOCOUNT ON", true)]
    [InlineData("SELECT '--'; SET NOCOUNT ON", true)]
    [InlineData("SELECT '/*'; SET NOCOUNT ON", true)]
    [InlineData("SELECT 'it''s -- not a comment'; SET NOCOUNT ON", true)]
    [InlineData("SELECT N'/* nor this'; SET NOCOUNT ON", true)]
    [InlineData("SELECT [a--b] FROM dbo.t; SET NOCOUNT ON", true)]
    [InlineData("SELECT [a]]/*b] FROM dbo.t; SET NOCOUNT ON", true)]
    [InlineData("SELECT \"x/*\" FROM dbo.t; SET NOCOUNT ON", true)]
    [InlineData("SE/**/T NOCOUNT ON", false)]                // two tokens to SQL Server too
    public void CommentsAreNotRead(string sql, bool changesSettings) => Assert.Equal(changesSettings, BatchText.Scan(sql).ChangesSettings);

    [Fact]
    public void ASettingsValueIsReadAcrossAComment()
    {
        var effects = BatchText.Scan("SET LANGUAGE /* the users' own */ Deutsch");
        Assert.Equal(new SettingAssignment("LANGUAGE", "DEUTSCH"), Assert.Single(effects.Assignments));
        Assert.False(effects.Unclear);
    }

    /// <summary>A write named in a comment flushes nothing; one in a string may be dynamic SQL that runs, and still counts.</summary>
    [Fact]
    public void WritesAndTaintsAreReadInCodeAndStringsButNotComments()
    {
        Assert.False(BatchText.Scan("-- load the rows into staging, then UPDATE the totals\nSELECT a FROM dbo.t").Writes);
        Assert.False(BatchText.Scan("SELECT a FROM dbo.t /* DELETE nothing */").Writes);
        Assert.True(BatchText.Scan("EXEC('INSERT dbo.t VALUES (1)')").Writes);
        Assert.True(BatchText.Scan("SELECT a INTO #t FROM dbo.t -- a temp table").Writes);
        Assert.Equal("SET CONTEXT_INFO", BatchText.Scan("EXEC('SET CONTEXT_INFO 0x01')").Taint);
        Assert.Null(BatchText.Scan("-- SET CONTEXT_INFO 0x01\nSELECT 1").Taint);
    }

    [Fact]
    public void FindsSessionContextInAnRpcAtEitherAlignment()
    {
        byte[] text = Encoding.Unicode.GetBytes("EXEC sp_set_session_context N'k', 1");
        Assert.Equal("sp_set_session_context", BatchText.RpcTaint(text));
        Assert.Equal("sp_set_session_context", BatchText.RpcTaint([0x07, .. text]));   // one byte in, as after a length byte
        Assert.Null(BatchText.RpcTaint(Encoding.Unicode.GetBytes("SELECT a FROM dbo.t")));
    }
}

/// <summary>What may be cached, decided by the T-SQL parser.</summary>
public class ClassifierTests
{
    [Theory]
    [InlineData("SELECT a FROM dbo.t WHERE b = 1")]
    [InlineData("WITH c AS (SELECT a FROM dbo.t) SELECT * FROM c")]
    [InlineData("SET NOCOUNT ON; SELECT 1 AS x FROM dbo.t")]
    [InlineData("DECLARE @y int = 2013; SELECT * FROM dbo.v WHERE y = @y")]
    [InlineData("SELECT TOP (10) * FROM dbo.t ORDER BY a OPTION (MAXDOP 1)")]
    [InlineData("SELECT x FROM dbo.t WITH (NOLOCK)")]
    [InlineData("SELECT SUSER_SNAME(), @@VERSION")]                       // the target login is in the key
    [InlineData("SELECT a FROM dbo.t; PRINT 'done'; SELECT b FROM dbo.u")]
    public void Cacheable(string sql) => Assert.True(Classifier.Batch(sql, true).Cacheable, Classifier.Batch(sql, true).Describe());

    [Theory]
    [InlineData("SELECT GETDATE()", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT * FROM dbo.t WHERE d < CURRENT_TIMESTAMP", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT TOP 5 * FROM dbo.t ORDER BY NEWID()", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT a FROM dbo.t WHERE d > DATEADD(day, -7, SYSDATETIME())", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT @@SPID", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT @@ROWCOUNT", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT {fn NOW()}", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT NEXT VALUE FOR dbo.seq", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT * FROM dbo.t TABLESAMPLE (10 PERCENT)", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT * FROM CHANGETABLE(CHANGES dbo.t, 0) AS c", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT HAS_PERMS_BY_NAME('dbo.t', 'OBJECT', 'SELECT')", nameof(Refusal.NonDeterministic))]
    [InlineData("SELECT * FROM #t", nameof(Refusal.TempTable))]
    [InlineData("SELECT a INTO #t FROM dbo.t", nameof(Refusal.TempTable))]
    [InlineData("SELECT * FROM tempdb.dbo.t", nameof(Refusal.TempTable))]
    [InlineData("DECLARE @t TABLE (a int); SELECT * FROM @t", nameof(Refusal.TableVariable))]
    [InlineData("SELECT a INTO dbo.copy FROM dbo.t", nameof(Refusal.Write))]
    [InlineData("INSERT dbo.t VALUES (1)", nameof(Refusal.Write))]
    [InlineData("UPDATE dbo.t SET a = 1", nameof(Refusal.Write))]
    [InlineData("DELETE dbo.t", nameof(Refusal.Write))]
    [InlineData("MERGE dbo.t AS x USING dbo.u AS y ON x.a = y.a WHEN MATCHED THEN DELETE;", nameof(Refusal.Write))]
    [InlineData("CREATE TABLE dbo.x (a int)", nameof(Refusal.Write))]
    [InlineData("TRUNCATE TABLE dbo.t", nameof(Refusal.Write))]
    [InlineData("EXEC dbo.usp_report 1", nameof(Refusal.Procedure))]
    [InlineData("EXEC ('SELECT 1')", nameof(Refusal.DynamicSql))]
    [InlineData("EXEC sp_executesql N'SELECT 1'", nameof(Refusal.DynamicSql))]
    [InlineData("SELECT * FROM OPENQUERY(srv, 'SELECT 1')", nameof(Refusal.ExternalData))]
    [InlineData("SELECT * FROM srv.db.dbo.t", nameof(Refusal.ExternalData))]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')", nameof(Refusal.ExternalData))]
    [InlineData("SELECT * FROM sys.objects", nameof(Refusal.SystemObject))]
    [InlineData("SELECT * FROM sys.dm_exec_requests", nameof(Refusal.SystemObject))]
    [InlineData("SELECT * FROM sysobjects", nameof(Refusal.SystemObject))]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES", nameof(Refusal.SystemObject))]
    [InlineData("USE master", nameof(Refusal.SessionState))]
    [InlineData("BEGIN TRAN; SELECT 1 FROM dbo.t", nameof(Refusal.SessionState))]
    [InlineData("SELECT 1 FROM dbo.t; SET NOCOUNT ON", nameof(Refusal.SessionState))]
    [InlineData("SET FMTONLY ON; SELECT * FROM dbo.t", nameof(Refusal.SessionState))]
    [InlineData("SET STATISTICS IO ON; SELECT * FROM dbo.t", nameof(Refusal.SessionState))]
    [InlineData("SET IMPLICIT_TRANSACTIONS ON; SELECT * FROM dbo.t", nameof(Refusal.SessionState))]
    [InlineData("WAITFOR DELAY '00:00:01'; SELECT 1 FROM dbo.t", nameof(Refusal.NotSelect))]
    [InlineData("IF 1 = 1 SELECT 1 FROM dbo.t", nameof(Refusal.NotSelect))]
    [InlineData("PRINT 'hello'", nameof(Refusal.NotSelect))]
    [InlineData("SELECT * FROM dbo.t FOR BROWSE", nameof(Refusal.NotSelect))]
    [InlineData("DECLARE c CURSOR FOR SELECT a FROM dbo.t", nameof(Refusal.Cursor))]
    [InlineData("SELECT FROM WHERE", nameof(Refusal.ParseFailed))]
    public void Refused(string sql, string expected)
    {
        var classification = Classifier.Batch(sql, true);
        Assert.Equal(expected, classification.Refusal.ToString());
        Assert.False(classification.Cacheable);
    }

    [Fact]
    public void LeadingSetsAreCountedForTheHitToReapply()
    {
        Assert.Equal(2, Classifier.Batch("SET NOCOUNT ON; SET ARITHABORT ON; SELECT a FROM dbo.t", true).LeadingSets);
        Assert.Equal(0, Classifier.Batch("SELECT a FROM dbo.t", true).LeadingSets);
    }

    [Fact]
    public void InsideSpExecuteSqlASetAnywhereIsItsOwn()
    {
        Assert.True(Classifier.Statement("SELECT a FROM dbo.t; SET NOCOUNT ON; SELECT b FROM dbo.u", true).Cacheable);
        Assert.Equal(Refusal.SessionState, Classifier.Batch("SELECT a FROM dbo.t; SET NOCOUNT ON; SELECT b FROM dbo.u", true).Refusal);
    }

    [Fact]
    public void RunsWithDifferentValuesShareAShape()
    {
        var one = Classifier.Batch("SELECT a FROM dbo.t WHERE b = 1 AND c = N'x' -- first", true);
        var two = Classifier.Batch("select A\n  from DBO.T where B=2 and c = N'yy'", true);
        Assert.Equal(one.Shape, two.Shape);
        Assert.Equal(one.ShapeHash, two.ShapeHash);
        Assert.Equal("SELECT a FROM dbo . t WHERE b = ? AND c = ?", one.Shape);
    }

    /// <summary>The test database's own queries: the timed ones may be cached, the r* ones say how they must not be.</summary>
    [Theory]
    [InlineData("q01_category_year.sql", nameof(Refusal.None))]
    [InlineData("q02_monthly_view.sql", nameof(Refusal.None))]
    [InlineData("q03_customer_value.sql", nameof(Refusal.None))]
    [InlineData("q04_running_totals.sql", nameof(Refusal.None))]
    [InlineData("q05_cohort_retention.sql", nameof(Refusal.None))]
    [InlineData("q06_market_basket.sql", nameof(Refusal.None))]
    [InlineData("q07_reseller_medians.sql", nameof(Refusal.None))]
    [InlineData("q08_procedure.sql", nameof(Refusal.Procedure))]
    [InlineData("q09_big_result.sql", nameof(Refusal.None))]
    [InlineData("q10_internet_medians.sql", nameof(Refusal.None))]
    [InlineData("q11_distribution_profile.sql", nameof(Refusal.None))]
    [InlineData("r01_getdate.sql", nameof(Refusal.NonDeterministic))]
    [InlineData("r02_temp_table.sql", nameof(Refusal.TempTable))]
    [InlineData("r03_leading_set.sql", nameof(Refusal.None))]
    [InlineData("r04_write.sql", nameof(Refusal.Write))]
    public void TheTestQueries(string file, string expected)
    {
        var classification = Classifier.Batch(File.ReadAllText(Path.Combine(Repository.Root, "docker", "queries", file)), true);
        Assert.True(expected == classification.Refusal.ToString(), $"{file}: {classification.Describe()}");
    }
}

/// <summary>The repository's root, found from the test's own location.</summary>
internal static class Repository
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DaffyZoom.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("the repository root (DaffyZoom.slnx) is not above the test's directory");
    }
}

/// <summary>Which requests may share an answer.</summary>
public class CacheKeyTests
{
    private static readonly ContextSnapshot Context = new("sql01:1433", "svc_reports", 7, "Sales", 7, true, null);

    private static CacheKey Key(ContextSnapshot context, string sql, ulong transaction = 0) =>
        CacheKey.Compute(context, TdsConstants.PacketType.SqlBatch, Requests.BatchPayload(sql, transaction));

    [Fact]
    public void TheSameRequestInTheSameContextHasTheSameKey()
    {
        Assert.Equal(Key(Context, "SELECT 1"), Key(Context, "SELECT 1"));
        Assert.NotEqual(Key(Context, "SELECT 1"), Key(Context, "SELECT 2"));
    }

    [Fact]
    public void AllHeadersAreNotPartOfIt() => Assert.Equal(Key(Context, "SELECT 1"), Key(Context, "SELECT 1", transaction: 0x1234));

    /// <summary>
    /// Whitespace that cannot change the answer is not part of it: at the end of a batch (SSMS sends
    /// the line break before a GO, or a blank line at the end of the window), and spaces or tabs before
    /// its first line's text (a selection started at the indent).
    /// </summary>
    [Theory]
    [InlineData("SELECT a FROM dbo.t\r\n")]
    [InlineData("SELECT a FROM dbo.t\r\n\r\n\t  \r\n")]
    [InlineData("SELECT a FROM dbo.t \t")]
    [InlineData("SELECT a FROM dbo.t\n")]
    [InlineData("    SELECT a FROM dbo.t")]
    [InlineData("\t  SELECT a FROM dbo.t  \r\n")]
    public void WhitespaceAroundABatchIsNotPartOfIt(string sql) => Assert.Equal(Key(Context, "SELECT a FROM dbo.t"), Key(Context, sql));

    /// <summary>
    /// Line breaks before the text are: they move every line number SQL Server reports, and a cached
    /// answer replays its messages with theirs. Whitespace inside the batch is too, byte for byte.
    /// </summary>
    [Theory]
    [InlineData("\r\nSELECT a FROM dbo.t")]
    [InlineData("  \r\n  SELECT a FROM dbo.t")]
    [InlineData("SELECT a  FROM dbo.t")]
    [InlineData("SELECT a\r\nFROM dbo.t")]
    [InlineData("SELECT a FROM dbo.t -- note")]
    public void LineBreaksBeforeItAndWhitespaceInsideItAre(string sql) => Assert.NotEqual(Key(Context, "SELECT a FROM dbo.t"), Key(Context, sql));

    /// <summary>An RPC is hashed whole: a parameter value can end in a space that matters.</summary>
    [Fact]
    public void AnRpcIsHashedWhole()
    {
        byte[] withSpace = Requests.BatchPayload("SELECT 1 ");
        Assert.NotEqual(CacheKey.Compute(Context, TdsConstants.PacketType.Rpc, Requests.BatchPayload("SELECT 1")),
                        CacheKey.Compute(Context, TdsConstants.PacketType.Rpc, withSpace));
    }

    /// <summary>A batch of nothing but whitespace is an empty batch, and nothing is trimmed past its start.</summary>
    [Fact]
    public void ABatchOfOnlyWhitespaceIsEmpty()
    {
        Assert.Equal(0, CacheKey.Body(TdsConstants.PacketType.SqlBatch, Requests.BatchPayload(" \t\r\n ")).Length);
        Assert.Equal(Key(Context, ""), Key(Context, "   "));
    }

    [Fact]
    public void EveryPartOfTheContextIs()
    {
        var key = Key(Context, "SELECT 1");
        Assert.NotEqual(key, Key(Context with { Database = "Other" }, "SELECT 1"));
        Assert.NotEqual(key, Key(Context with { Database = "sales" }, "SELECT 1"));          // a case-sensitive server has both
        Assert.NotEqual(key, Key(Context with { TargetLogin = "svc_other" }, "SELECT 1"));
        Assert.NotEqual(key, Key(Context with { SettingsHash = 8 }, "SELECT 1"));
        Assert.NotEqual(key, Key(Context with { LoginHash = 8 }, "SELECT 1"));
        Assert.Equal(key, Key(Context with { Target = "SQL01:1433" }, "SELECT 1"));        // host names are not case-sensitive
    }

    [Fact]
    public void ABatchAndAnRpcNeverShare()
    {
        var payload = Requests.BatchPayload("SELECT 1");
        Assert.NotEqual(CacheKey.Compute(Context, TdsConstants.PacketType.SqlBatch, payload),
                        CacheKey.Compute(Context, TdsConstants.PacketType.Rpc, payload));
    }
}

/// <summary>The observer on the relay: pairing, timing, and the session context it follows.</summary>
public class CacheObserverTests
{
    private static readonly long Tick = Stopwatch.Frequency / 1000;

    private sealed class Harness
    {
        public List<SlowRequest> Submitted { get; } = [];
        public CacheCounters Counters { get; } = new();
        public ByteBudget Budget { get; } = new(64 * 1024 * 1024);
        public CacheObserver Observer { get; }
        public ProxySession Session { get; }
        private long _now = 1_000_000;

        public Harness(CacheOptions? options = null, string database = "Sales")
        {
            var session = new ProxySession(1, "10.0.0.1:5000")
            {
                Target = "sql01:1433", TargetLogin = "svc_reports", Database = database, TdsVersion = 0x74000004,
                LoginDefaults = new LoginDefaults(0xE0, 0x03, 0, 0, 0x409, ""),
            };
            Session = session;
            Observer = new CacheObserver(options ?? new CacheOptions(), Counters, Budget, r => { Submitted.Add(r); return true; },
                                         session, SessionContext.For(session));
        }

        /// <summary>A request, and after <paramref name="milliseconds"/> its whole answer.</summary>
        public void Exchange(byte[] request, double milliseconds, params byte[][] responseTokens)
        {
            Observer.OnClientBytes(request, _now);
            _now += (long)(milliseconds * Tick);
            byte[] payload = responseTokens.Length == 0 ? Requests.Done() : responseTokens.SelectMany(t => t).ToArray();
            Observer.OnServerBytes(Requests.Packet(TdsConstants.PacketType.TabularResult, TdsConstants.PacketStatus.EndOfMessage, payload), _now);
            _now += Tick;
        }
    }

    internal static byte[] EnvChangeDatabase(string to, string from)
    {
        var w = new TdsWriter();
        w.WriteByte(TdsConstants.Token.EnvChange);
        w.WriteUInt16((ushort)(1 + 1 + to.Length * 2 + 1 + from.Length * 2));
        w.WriteByte(TdsConstants.EnvChangeType.Database);
        w.WriteBVarChar(to);
        w.WriteBVarChar(from);
        return w.ToArray();
    }

    private static byte[] Reset(byte[] packet)
    {
        packet[1] |= TdsConstants.PacketStatus.ResetConnection;
        return packet;
    }

    [Fact]
    public void ASlowRequestIsHandedOverWithItsContextAndAFastOneIsNot()
    {
        var h = new Harness();
        h.Exchange(Requests.Batch("SELECT a FROM dbo.t"), 6000);
        h.Exchange(Requests.Batch("SELECT b FROM dbo.t"), 100);

        var slow = Assert.Single(h.Submitted);
        Assert.Equal("Sales", slow.Context.Database);
        Assert.Equal("svc_reports", slow.Context.TargetLogin);
        Assert.InRange(slow.DurationMs, 5999, 6001);
        Assert.Equal("SELECT a FROM dbo.t", RequestParser.ParseBatchText(slow.Span));
        Assert.Equal(2, h.Counters.Eligible);
    }

    [Fact]
    public void ARequestInATransactionIsNotConsidered()
    {
        var h = new Harness();
        h.Exchange(Requests.Batch("SELECT a FROM dbo.t", transaction: 0x0000_0042_0000_0001), 6000);
        Assert.Empty(h.Submitted);
        Assert.Equal(1, h.Counters.Bypassed["in a transaction"]);
    }

    [Fact]
    public void AUseMovesTheSessionAndItsKey()
    {
        var h = new Harness();
        h.Exchange(Requests.Batch("USE Other"), 1, EnvChangeDatabase("Other", "Sales"), Requests.Done());
        h.Exchange(Requests.Batch("SELECT a FROM dbo.t"), 6000);
        Assert.Equal("Other", Assert.Single(h.Submitted).Context.Database);
    }

    [Fact]
    public void AUseWhoseAnswerCannotBeReadLeavesTheDatabaseUnknownUntilAReset()
    {
        var h = new Harness(new CacheOptions { ResponseScanBytes = 16 });
        h.Exchange(Requests.Batch("USE Other; SELECT a FROM dbo.t"), 1, EnvChangeDatabase("Other", "Sales"), Requests.Done());
        h.Exchange(Requests.Batch("SELECT a FROM dbo.t"), 6000);
        Assert.Empty(h.Submitted);
        Assert.Equal(1, h.Counters.Bypassed["database unknown"]);
        Assert.Null(h.Session.Database);

        h.Exchange(Reset(Requests.Batch("SELECT a FROM dbo.t")), 6000);
        Assert.Equal("Sales", Assert.Single(h.Submitted).Context.Database);
        Assert.Equal("Sales", h.Session.Database);
    }

    /// <summary>daffyzoom.status and :sessions show the database the session is in now, not the one it logged in to.</summary>
    [Fact]
    public void TheSessionShowsTheDatabaseAUseMovedItTo()
    {
        var h = new Harness();
        h.Exchange(Requests.Batch("USE master"), 1, EnvChangeDatabase("master", "Sales"), Requests.Done());
        Assert.Equal("master", h.Session.Database);
        h.Exchange(Reset(Requests.Batch("SELECT 1")), 1);
        Assert.Equal("Sales", h.Session.Database);
    }

    [Fact]
    public void ASetChangesTheSettingsAndSessionsThatSendTheSameAgree()
    {
        var one = new Harness();
        var two = new Harness();
        var before = one.Observer.Context.SettingsHash;

        one.Exchange(Requests.Batch("SET NOCOUNT ON"), 1);
        two.Exchange(Requests.Batch("SET NOCOUNT ON"), 1);
        Assert.NotEqual(before, one.Observer.Context.SettingsHash);
        Assert.Equal(one.Observer.Context.SettingsHash, two.Observer.Context.SettingsHash);

        one.Exchange(Reset(Requests.Batch("SELECT 1")), 1);
        Assert.Equal(before, one.Observer.Context.SettingsHash);
    }

    [Fact]
    public void TheSameSetBatchAgainLeavesTheSameSettings()
    {
        var h = new Harness();
        h.Exchange(Requests.Batch("SET NOCOUNT ON; SET ARITHABORT ON; SELECT a FROM dbo.t"), 1);
        var once = h.Observer.Context.SettingsHash;
        h.Exchange(Requests.Batch("SET NOCOUNT ON; SET ARITHABORT ON; SELECT a FROM dbo.t"), 1);
        Assert.Equal(once, h.Observer.Context.SettingsHash);
        h.Exchange(Requests.Batch("set nocount   on"), 1);                  // the same option and value, written differently
        Assert.NotEqual(once, h.Observer.Context.SettingsHash);             // NOCOUNT moved to the end: a different order
        h.Exchange(Requests.Batch("SET ARITHABORT ON"), 1);
        Assert.Equal(once, h.Observer.Context.SettingsHash);                // and back
    }

    [Fact]
    public void TheSameOptionsInAnotherOrderAreNotAssumedTheSame()
    {
        // ANSI_DEFAULTS ON then ANSI_NULLS OFF leaves ANSI_NULLS off; the other way round leaves it on.
        var one = new Harness();
        var two = new Harness();
        one.Exchange(Requests.Batch("SET ANSI_DEFAULTS ON; SET ANSI_NULLS OFF"), 1);
        two.Exchange(Requests.Batch("SET ANSI_NULLS OFF; SET ANSI_DEFAULTS ON"), 1);
        Assert.NotEqual(one.Observer.Context.SettingsHash, two.Observer.Context.SettingsHash);
    }

    [Fact]
    public void AValueOnTheNextLineIsStillTheValue()
    {
        var on = new Harness();
        var off = new Harness();
        on.Exchange(Requests.Batch("SET NOCOUNT\nON\nSELECT 1"), 1);
        off.Exchange(Requests.Batch("SET NOCOUNT\nOFF\nSELECT 1"), 1);
        Assert.NotEqual(on.Observer.Context.SettingsHash, off.Observer.Context.SettingsHash);
    }

    [Fact]
    public void ExecuteAsTaintsTheSessionUntilItIsReset()
    {
        var h = new Harness();
        h.Exchange(Requests.Batch("EXECUTE AS USER = 'bob'"), 1);
        h.Exchange(Requests.Batch("SELECT a FROM dbo.t"), 6000);
        Assert.Empty(h.Submitted);
        Assert.Equal("EXECUTE AS", h.Observer.Context.Taint);

        h.Exchange(Reset(Requests.Batch("SELECT a FROM dbo.t")), 6000);
        Assert.Single(h.Submitted);
    }

    [Fact]
    public void SessionContextSetByAnRpcTaintsToo()
    {
        var h = new Harness();
        h.Exchange(Requests.Rpc("sp_set_session_context", ("@key", "tenant"), ("@value", "3")), 1);
        Assert.Equal("sp_set_session_context", h.Observer.Context.Taint);
    }

    [Fact]
    public void ABatchTooLargeToReadTaintsTheSession()
    {
        var h = new Harness(new CacheOptions { MaxRequestBytes = 64 });
        h.Exchange(Requests.Batch("SELECT a FROM dbo.t WHERE b IN (" + string.Join(",", Enumerable.Range(1, 100)) + ")"), 6000);
        Assert.Empty(h.Submitted);
        Assert.Equal("a batch too large to read", h.Observer.Context.Taint);
    }

    [Fact]
    public void ACancelledRequestIsNotConsidered()
    {
        var h = new Harness();
        h.Observer.OnClientBytes(Requests.Batch("SELECT a FROM dbo.t"), 0);
        h.Observer.OnClientBytes(Requests.Packet(TdsConstants.PacketType.Attention, TdsConstants.PacketStatus.EndOfMessage, []), 10 * Tick);
        h.Observer.OnServerBytes(Requests.Packet(TdsConstants.PacketType.TabularResult, TdsConstants.PacketStatus.EndOfMessage,
                                                 Requests.Done(TdsConstants.DoneStatus.Attention)), 7000 * Tick);
        Assert.Empty(h.Submitted);
    }

    [Fact]
    public void AnOldClientIsNeverConsidered()
    {
        var session = new ProxySession(1, "c") { Target = "t", TargetLogin = "l", Database = "d", TdsVersion = 0x72090002 };
        var counters = new CacheCounters();
        var submitted = new List<SlowRequest>();
        var observer = new CacheObserver(new CacheOptions(), counters, new ByteBudget(1 << 20), r => { submitted.Add(r); return true; },
                                         session, SessionContext.For(session));
        observer.OnClientBytes(Requests.Batch("SELECT a FROM dbo.t"), 0);
        observer.OnServerBytes(Requests.Packet(TdsConstants.PacketType.TabularResult, TdsConstants.PacketStatus.EndOfMessage, Requests.Done()), 7000 * Tick);
        Assert.Empty(submitted);
        Assert.Equal(1, counters.Bypassed["TDS older than 7.3"]);
    }

    [Fact]
    public void EveryCopyIsGivenBack()
    {
        var h = new Harness();
        h.Exchange(Requests.Batch("SELECT a FROM dbo.t"), 100);
        h.Exchange(Requests.Batch("SELECT a FROM dbo.t"), 6000);
        Assert.True(h.Budget.Bytes > 0);                 // the slow one's copy now belongs to the analyzer
        foreach (var request in h.Submitted) request.Release(h.Budget);
        h.Observer.OnClosed("done");
        Assert.Equal(0, h.Budget.Bytes);
    }
}

/// <summary>Admission, played out on paper.</summary>
public class WatchListTests
{
    private static readonly ContextSnapshot Context = new("sql01:1433", "svc_reports", 7, "Sales", 7, true, null);

    private static SlowRequest Run(double ms = 60_000, long bytes = 10_000) => new()
    {
        Type = TdsConstants.PacketType.SqlBatch, Payload = new byte[16], Length = 0, Reserved = 0, Context = Context,
        Session = 1, Login = "bob", App = "test", StartedUtc = DateTime.UtcNow, DurationMs = ms, ResponseBytes = bytes, DoneStatus = 0,
    };

    private static readonly Classification Cacheable = Classifier.Batch("SELECT a FROM dbo.t", true);
    private static readonly CacheKey Key = new(42);

    [Fact]
    public void TheSecondSlowRunFillsAndTheThirdWouldBeServed()
    {
        var list = new WatchList(new CacheOptions());
        var t = DateTime.UtcNow;
        Assert.StartsWith("candidate", list.Record(Key, Cacheable, null, Run(), t));
        Assert.Equal("would fill", list.Record(Key, Cacheable, null, Run(), t.AddMinutes(1)));
        Assert.Equal("would serve", list.Record(Key, Cacheable, null, Run(), t.AddMinutes(2)));

        var row = Assert.Single(list.Top(10));
        Assert.Equal(3, row.Runs);
        Assert.Equal(1, row.WouldServe);
        Assert.InRange(row.SavedMs, 59_000, 60_000);
    }

    /// <summary>GO 4: the second run comes in before the first has been analyzed. In ttl mode it may fill early, unconfirmed.</summary>
    [Fact]
    public void ASlowRunWaitingForTheAnalyzerLetsTheNextFillEarly()
    {
        var list = new WatchList(new CacheOptions());
        var t = DateTime.UtcNow;
        Assert.Null(list.FillWanted(Key, _ => true, t, early: true));      // never seen

        list.Pending(Key);
        Assert.Null(list.FillWanted(Key, _ => true, t));                     // tracked mode, or a request that may write: not early
        var early = Assert.IsType<FillCandidate>(list.FillWanted(Key, _ => true, t, early: true));
        Assert.False(early.Confirmed);
        Assert.Null(early.Classification);

        // Its answer ends before the run before it is recorded: let go.
        Assert.Contains("not been analyzed", list.Confirm(Key, _ => true).Refused);

        list.Record(Key, Cacheable, null, Run(), t);
        list.NoLongerPending(Key);
        Assert.Equal(Cacheable, list.Confirm(Key, _ => true).Classification);
        Assert.True(list.FillWanted(Key, _ => true, t)!.Confirmed);            // and from now on, an ordinary fill
    }

    [Fact]
    public void AnEarlyFillIsConfirmedOnlyByTheRulesOfAnOrdinaryOne()
    {
        var list = new WatchList(new CacheOptions());
        var t = DateTime.UtcNow;

        // The run before it was not cacheable after all.
        var clock = new CacheKey(43);
        list.Pending(clock);
        Assert.NotNull(list.FillWanted(clock, _ => true, t, early: true));
        list.Record(clock, Classifier.Batch("SELECT GETDATE()", true), null, Run(), t);
        list.NoLongerPending(clock);
        Assert.StartsWith("it may not be cached", list.Confirm(clock, c => c.Cacheable).Refused);

        // The run before it ended in an error, so it does not count.
        list.Pending(Key);
        list.Record(Key, Cacheable, Refusal.ResponseHadError, Run(), t);
        list.NoLongerPending(Key);
        Assert.Contains("had not been seen slow 1 time(s)", list.Confirm(Key, _ => true).Refused);
        Assert.Null(list.FillWanted(Key, _ => true, t, early: true));          // nothing pending now, and no slow run counted
    }

    [Fact]
    public void ARefusedRequestSaysWhy()
    {
        var list = new WatchList(new CacheOptions());
        var refused = Classifier.Batch("SELECT GETDATE()", true);
        Assert.Equal("refused: NonDeterministic: GETDATE()", list.Record(Key, refused, null, Run(), DateTime.UtcNow));
    }

    [Fact]
    public void ARunWithAnErrorNeitherFillsNorCounts()
    {
        var list = new WatchList(new CacheOptions { MinRuns = 1 });
        Assert.Equal("not this run: ResponseHadError", list.Record(Key, Cacheable, Refusal.ResponseHadError, Run(), DateTime.UtcNow));
        Assert.Equal("would fill", list.Record(Key, Cacheable, null, Run(), DateTime.UtcNow));
    }

    [Fact]
    public void ACandidateNotSeenAgainInTimeStartsOver()
    {
        var list = new WatchList(new CacheOptions());
        var t = DateTime.UtcNow;
        list.Record(Key, Cacheable, null, Run(), t);
        Assert.StartsWith("candidate (1 of 2", list.Record(Key, Cacheable, null, Run(), t.AddMinutes(61)));
    }

    [Fact]
    public void AnEntryPastItsMaxAgeIsFilledAgain()
    {
        var list = new WatchList(new CacheOptions { DefaultMaxAge = TimeSpan.FromHours(4) });
        var t = DateTime.UtcNow;
        list.Record(Key, Cacheable, null, Run(), t);
        list.Record(Key, Cacheable, null, Run(), t);                       // fills
        Assert.Equal("would serve", list.Record(Key, Cacheable, null, Run(), t.AddHours(1)));
        Assert.StartsWith("candidate", list.Record(Key, Cacheable, null, Run(), t.AddHours(5)));
    }

    [Fact]
    public void ItForgetsTheLeastRecentlySeenPastItsCap()
    {
        var list = new WatchList(new CacheOptions { MaxKeys = 100 });
        var t = DateTime.UtcNow;
        for (int i = 0; i < 150; i++) list.Record(new CacheKey((UInt128)i), Cacheable, null, Run(), t.AddSeconds(i));
        Assert.InRange(list.KeyCount, 90, 100);
        Assert.Null(list.Known(new CacheKey(0)));
        Assert.NotNull(list.Known(new CacheKey(149)));
    }
}

/// <summary>From a request's bytes to its classification, as the analyzer does it.</summary>
public class AnalyzerTests
{
    private static Classification Of(byte[] packet) =>
        Analyzer.Classify(packet[0], packet.AsSpan(TdsConstants.HeaderLength), quotedIdentifier: true);

    [Fact]
    public void ABatch() => Assert.True(Of(Requests.Batch("SELECT a FROM dbo.t")).Cacheable);

    [Fact]
    public void AParameterisedQueryIsJudgedByItsStatement()
    {
        Assert.True(Of(Requests.Rpc("sp_executesql", ("@stmt", "SELECT a FROM dbo.t WHERE b = @b"), ("@params", "@b nvarchar(10)"), ("@b", "x"))).Cacheable);
        Assert.Equal(Refusal.NonDeterministic,
                     Of(Requests.Rpc("sp_executesql", ("@stmt", "SELECT a FROM dbo.t WHERE d < GETDATE()"), ("@params", ""))).Refusal);
    }

    /// <summary>
    /// System.Data.SqlClient sets fNoMetaData on every parameterised query and SQL Server sends the
    /// metadata anyway (seen against SQL Server 2022), so the flag alone refuses nothing.
    /// </summary>
    [Fact]
    public void TheNoMetadataFlagAloneRefusesNothing()
    {
        const string procedure = "sp_executesql";
        byte[] packet = Requests.Rpc(procedure, ("@stmt", "SELECT a FROM dbo.t WHERE b = @b"), ("@params", "@b nvarchar(10)"), ("@b", "x"));
        packet[TdsConstants.HeaderLength + 22 + 2 + procedure.Length * 2] = 0x02;    // OptionFlags, after ALL_HEADERS and the name

        var call = Assert.Single(RequestParser.ParseRpc(packet.AsSpan(TdsConstants.HeaderLength)));
        Assert.True(call.NoMetadata);
        Assert.True(Of(packet).Cacheable);
    }

    [Fact]
    public void AProcedurePreparedStatementOrCursorIsNot()
    {
        Assert.Equal(Refusal.Procedure, Of(Requests.Rpc("dbo.usp_report", ("@year", "2013"))).Refusal);
        Assert.Equal(Refusal.Prepared, Of(Requests.Rpc("sp_execute", ("@handle", "1"))).Refusal);
        Assert.Equal(Refusal.Prepared, Of(Requests.Rpc("sp_prepare", ("@handle", "0"), ("@params", ""), ("@stmt", "SELECT 1"))).Refusal);
        Assert.Equal(Refusal.Cursor, Of(Requests.Rpc("sp_cursoropen", ("@cursor", "0"), ("@stmt", "SELECT 1"))).Refusal);
    }

    [Fact]
    public void TheWholeWayThroughToTheWatchList()
    {
        var options = new CacheOptions();
        var list = new WatchList(options);
        var counters = new CacheCounters();
        var analyzer = new Analyzer(options, list, counters, new ByteBudget(1 << 20));
        var payload = Requests.BatchPayload("SELECT a FROM dbo.t WHERE b = 1");
        var context = new ContextSnapshot("sql01:1433", "svc_reports", 7, "Sales", 7, true, null);

        SlowRequest Slow() => new()
        {
            Type = TdsConstants.PacketType.SqlBatch, Payload = payload, Length = payload.Length, Reserved = 0, Context = context,
            Session = 1, Login = "bob", App = "t", StartedUtc = DateTime.UtcNow, DurationMs = 30_000, ResponseBytes = 1000, DoneStatus = 0,
        };

        var verdicts = new List<string>();
        analyzer.Analyzed += (_, _, _, verdict) => verdicts.Add(verdict);
        for (int i = 0; i < 3; i++) analyzer.Analyze(Slow(), DateTime.UtcNow);

        Assert.Equal(["candidate (1 of 2 slow runs)", "would fill", "would serve"], verdicts);
        Assert.Equal(3, counters.Analyzed);
        Assert.Equal("SELECT a FROM dbo . t WHERE b = ?", Assert.Single(list.Top(5)).Shape);
    }
}
