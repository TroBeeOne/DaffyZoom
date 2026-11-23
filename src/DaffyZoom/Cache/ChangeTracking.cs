using System.Globalization;
using System.Text;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>One object a cached answer depends on: a table, view, function or synonym, as SQL Server named it.</summary>
/// <param name="Id">Its object_id in the database.</param>
/// <param name="Name">[schema].[name], quoted.</param>
/// <param name="Type">sys.objects.type: U (a table), V, FN, IF, TF, P, SN.</param>
/// <param name="Modified">sys.objects.modify_date as CONVERT(char(23), ..., 126): a changed definition changes it.</param>
internal sealed record TrackedObject(int Id, string Name, string Type, string Modified)
{
    public bool IsTable => Type == "U";
}

/// <summary>
/// What a request's answer was resolved to in a tracked database: every object it depends on, down
/// to its tables, each of which has Change Tracking - and the database's Change Tracking version
/// when that was read.
/// </summary>
internal sealed record TrackedTables(IReadOnlyList<TrackedObject> Objects, long Version)
{
    public IEnumerable<TrackedObject> Tables => Objects.Where(o => o.IsTable);
}

/// <summary>The result of resolving a request's objects: its tables, or why it cannot be tracked.</summary>
internal sealed record Resolution(TrackedTables? Tables, string? Problem)
{
    /// <summary>Why, for the watch list: NotTracked, or a rule of tracked mode the request breaks (DirtyRead, Procedure).</summary>
    public Refusal Kind { get; init; } = Refusal.NotTracked;

    /// <summary>The query could not be run at all - no connection, a timeout. Tried again on the request's next slow run.</summary>
    public bool Transient { get; init; }
}

/// <summary>What one freshness check said, or why it could not say.</summary>
/// <param name="DataUnchanged">No tracked table changed since the baseline, and none of them lost the changes since it.</param>
/// <param name="DefinitionsUnchanged">No object the answer depends on was altered, dropped or recreated.</param>
/// <param name="Version">The database's version now, for the next fill's baseline.</param>
/// <param name="Problem">An error from SQL Server, or an answer that could not be read: the hit is a miss.</param>
internal sealed record FreshnessCheck(bool DataUnchanged, bool DefinitionsUnchanged, long? Version, string? Problem)
{
    public bool Fresh => Problem is null && DataUnchanged && DefinitionsUnchanged;

    /// <summary>SQL Server could not be asked at all: no connection, no login. Not an answer about the entry.</summary>
    public bool Unavailable { get; init; }
}

/// <summary>For daffyzoom.tracking: a database's Change Tracking, as one login sees it.</summary>
/// <param name="CurrentVersion">CHANGE_TRACKING_CURRENT_VERSION(): null when Change Tracking is off for the database.</param>
/// <param name="AutoCleanup">Null where the login cannot see sys.change_tracking_databases.</param>
/// <param name="RowLevelSecurity">Null when the login cannot tell (no VIEW DEFINITION).</param>
internal sealed record TrackingDatabase(string Database, string Login, string User, long? CurrentVersion, bool? AutoCleanup,
                                        int? Retention, string? RetentionUnits, bool ViewDefinition, bool? RowLevelSecurity);

/// <summary>One table: whether it is tracked, could be (a primary key), and what this login may do with it.</summary>
/// <param name="MinValidVersion">CHANGE_TRACKING_MIN_VALID_VERSION: changes older than this are cleaned up. Null when not tracked, or not readable.</param>
internal sealed record TrackingTable(int Id, string Schema, string Name, bool Tracked, bool PrimaryKey, long? MinValidVersion,
                                     bool ViewChangeTracking, bool Select)
{
    public string Quoted => $"{ObjectReference.Quote(Schema)}.{ObjectReference.Quote(Name)}";
}

/// <summary>What the status query said, or why it could not say.</summary>
internal sealed record TrackingStatus(TrackingDatabase? Database, IReadOnlyList<TrackingTable> Tables, string? Problem);

/// <summary>daffyzoom.tracking's material: SQL Server's answer, and what DaffyZoom knows of the database.</summary>
internal sealed class TrackingReport
{
    public string Server { get; init; } = "";
    public string Database { get; init; } = "";

    /// <summary>The login SQL Server saw: the entries counted are this login's.</summary>
    public string TargetLogin { get; init; } = "";

    public DatabasePolicy? Policy { get; init; }

    /// <summary>The newest Change Tracking version DaffyZoom has read for the database, from a resolution or a hit's check.</summary>
    public long? KnownVersion { get; init; }

    public required TrackingStatus Status { get; init; }

    /// <summary>The live tracked entries of this database and target login.</summary>
    public IReadOnlyList<CacheEntry> Entries { get; init; } = [];

    /// <summary>How many live entries depend on a table, and the oldest version any of them was filled at (its baseline).</summary>
    public (int Count, long? OldestBaseline) EntriesOn(int tableId)
    {
        var on = Entries.Where(e => e.Tracked!.Tables.Any(t => t.Id == tableId)).ToList();
        return (on.Count, on.Count == 0 ? null : on.Min(e => e.Tracked!.Version));
    }
}

/// <summary>
/// The queries tracked mode sends, on a connection of DaffyZoom's own logged in as the client's
/// own mapped login, and how their answers are read.
///
/// Every condition is written so that anything SQL Server cannot answer - a NULL, an object that is
/// gone, a permission it lacks - comes out as "changed" or as an error, never as "unchanged".
/// </summary>
internal static class ChangeTracking
{
    private const int MaxDepth = 32;
    private const int MaxProblems = 5;

    /// <summary>
    /// Resolves the names a request uses, as its own login would (OBJECT_ID, with the login's default
    /// schema), then follows views, functions and synonyms through sys.sql_expression_dependencies to
    /// the tables underneath. Three result sets: problems, objects, the database's version.
    /// </summary>
    public static string ResolveBatch(IReadOnlyList<string> names)
    {
        var sql = new StringBuilder("""
            SET NOCOUNT ON;
            DECLARE @names TABLE (name nvarchar(800) NOT NULL);
            DECLARE @objects TABLE (id int PRIMARY KEY, depth int NOT NULL);
            DECLARE @problems TABLE (problem nvarchar(1000) NOT NULL);
            DECLARE @depth int = 0, @added int = 1;

            """);
        foreach (var chunk in names.Chunk(500))
            sql.Append("INSERT @names (name) VALUES ").AppendJoin(", ", chunk.Select(n => $"({Literal(n)})")).AppendLine(";");

        sql.Append($$"""
            IF ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0) = 0
                INSERT @problems VALUES (N'the login lacks VIEW DEFINITION in the database');
            IF EXISTS (SELECT 1 FROM sys.security_policies WHERE is_enabled = 1)
                INSERT @problems VALUES (N'the database has an enabled row-level security policy');
            IF CHANGE_TRACKING_CURRENT_VERSION() IS NULL
                INSERT @problems VALUES (N'Change Tracking is not on for the database');
            INSERT @problems SELECT N'cannot resolve ' + name FROM @names WHERE OBJECT_ID(name) IS NULL;
            INSERT @objects SELECT DISTINCT OBJECT_ID(name), 0 FROM @names WHERE OBJECT_ID(name) IS NOT NULL;

            WHILE @added > 0 AND @depth < {{MaxDepth}}
            BEGIN
                INSERT @problems
                SELECT N'synonym ' + QUOTENAME(SCHEMA_NAME(s.schema_id)) + N'.' + QUOTENAME(s.name) + N' points to ' + s.base_object_name
                FROM sys.synonyms s JOIN @objects o ON o.id = s.object_id AND o.depth = @depth
                WHERE PARSENAME(s.base_object_name, 3) IS NOT NULL OR OBJECT_ID(s.base_object_name) IS NULL;

                INSERT @problems
                SELECT DISTINCT QUOTENAME(OBJECT_SCHEMA_NAME(d.referencing_id)) + N'.' + QUOTENAME(OBJECT_NAME(d.referencing_id)) + N' refers to '
                     + CASE WHEN d.referenced_server_name IS NOT NULL THEN N'another server: '
                            WHEN d.referenced_database_name IS NOT NULL THEN N'another database: '
                            WHEN d.is_caller_dependent = 1 THEN N'a name resolved per caller: '
                            WHEN d.is_ambiguous = 1 THEN N'an ambiguous name: '
                            ELSE N'something that does not resolve: ' END
                     + ISNULL(d.referenced_schema_name + N'.', N'') + d.referenced_entity_name
                FROM sys.sql_expression_dependencies d JOIN @objects o ON o.id = d.referencing_id AND o.depth = @depth
                WHERE d.referenced_class = 1
                  AND (d.referenced_id IS NULL OR d.is_caller_dependent = 1 OR d.is_ambiguous = 1
                       OR d.referenced_server_name IS NOT NULL OR d.referenced_database_name IS NOT NULL);

                INSERT @objects
                SELECT DISTINCT n.id, @depth + 1
                FROM (SELECT d.referenced_id AS id FROM sys.sql_expression_dependencies d JOIN @objects o ON o.id = d.referencing_id AND o.depth = @depth
                      WHERE d.referenced_class = 1 AND d.referenced_id IS NOT NULL
                        AND d.referenced_server_name IS NULL AND d.referenced_database_name IS NULL
                      UNION
                      SELECT OBJECT_ID(s.base_object_name) FROM sys.synonyms s JOIN @objects o ON o.id = s.object_id AND o.depth = @depth
                      WHERE PARSENAME(s.base_object_name, 3) IS NULL AND OBJECT_ID(s.base_object_name) IS NOT NULL) n
                WHERE NOT EXISTS (SELECT 1 FROM @objects x WHERE x.id = n.id);
                SET @added = @@ROWCOUNT;
                SET @depth += 1;
            END
            IF @added > 0 INSERT @problems VALUES (N'its objects nest more than {{MaxDepth}} levels deep');

            INSERT @problems
            SELECT QUOTENAME(OBJECT_SCHEMA_NAME(o.id)) + N'.' + QUOTENAME(OBJECT_NAME(o.id)) + N' ' +
                   CASE WHEN so.type = 'U' AND ct.object_id IS NULL THEN N'is a table without Change Tracking'
                        WHEN so.type = 'U' THEN N'needs VIEW CHANGE TRACKING for this login'
                        WHEN so.type IN ('V', 'FN', 'IF', 'TF', 'P') THEN N'has a definition this login cannot see (VIEW DEFINITION)'
                        WHEN so.type IS NULL THEN N'cannot be seen by this login'
                        ELSE N'is a ' + so.type_desc + N', which cannot be tracked' END
            FROM @objects o
            LEFT JOIN sys.objects so ON so.object_id = o.id
            LEFT JOIN sys.change_tracking_tables ct ON ct.object_id = o.id
            WHERE NOT ((so.type = 'U' AND ct.object_id IS NOT NULL
                        AND HAS_PERMS_BY_NAME(QUOTENAME(OBJECT_SCHEMA_NAME(o.id)) + N'.' + QUOTENAME(OBJECT_NAME(o.id)), 'OBJECT', 'VIEW CHANGE TRACKING') = 1)
                    OR (so.type IN ('V', 'FN', 'IF', 'TF', 'P') AND OBJECT_DEFINITION(o.id) IS NOT NULL)
                    OR so.type = 'SN');

            SELECT TOP ({{MaxProblems}}) problem FROM @problems;
            SELECT o.id, QUOTENAME(OBJECT_SCHEMA_NAME(o.id)) + N'.' + QUOTENAME(OBJECT_NAME(o.id)), RTRIM(so.type),
                   CONVERT(char(23), so.modify_date, 126)
            FROM @objects o JOIN sys.objects so ON so.object_id = o.id ORDER BY o.id;
            SELECT CHANGE_TRACKING_CURRENT_VERSION();
            """);
        return sql.ToString();
    }

    public static Resolution ParseResolution(byte[] answer)
    {
        var detail = ResponseReader.Read(answer, columnEncryption: false, keepRows: true);
        if (detail.Errors.Count > 0) return new Resolution(null, $"resolving its objects failed: {detail.Errors[0]}");
        if (detail.Problem is not null || detail.Tables.Count != 3) return new Resolution(null, $"the answer to resolving its objects could not be read{(detail.Problem is null ? "" : $": {detail.Problem}")}");

        var problems = detail.Tables[0].Select(r => r[0] as string).OfType<string>().ToList();
        if (problems.Count > 0) return new Resolution(null, string.Join("; ", problems));

        var objects = new List<TrackedObject>();
        foreach (var row in detail.Tables[1])
        {
            if (row is not [int id, string name, string type, string modified])
                return new Resolution(null, "the answer to resolving its objects could not be read: an object row");
            objects.Add(new TrackedObject(id, name, type.Trim(), modified));
        }
        if (!objects.Any(o => o.IsTable)) return new Resolution(null, "it reads no table");
        if (detail.Tables[2] is not [[long version]]) return new Resolution(null, "Change Tracking is not on for the database");
        return new Resolution(new TrackedTables(objects, version), null);
    }

    /// <summary>
    /// The per-hit check: for each table, no change since <paramref name="baseline"/> and the changes
    /// since it still on record; for each object, the same definition. One row: data unchanged,
    /// definitions unchanged, the version now. A condition SQL Server cannot decide is false.
    /// </summary>
    public static string CheckBatch(IReadOnlyList<TrackedObject> objects, long baseline)
    {
        string v = baseline.ToString(CultureInfo.InvariantCulture);
        var data = objects.Where(o => o.IsTable).Select(t =>
            $"CHANGE_TRACKING_MIN_VALID_VERSION({t.Id}) <= {v} AND NOT EXISTS (SELECT 1 FROM CHANGETABLE(CHANGES {t.Name}, {v}) AS c)");
        var definitions = objects.Select(o =>
            $"(SELECT CONVERT(char(23), modify_date, 126) FROM sys.objects WHERE object_id = {o.Id}) = {Literal(o.Modified)}");

        return $"""
            SET NOCOUNT ON;
            SELECT CASE WHEN {string.Join("\n    AND ", data)} THEN 1 ELSE 0 END,
                   CASE WHEN {string.Join("\n    AND ", definitions)} THEN 1 ELSE 0 END,
                   CHANGE_TRACKING_CURRENT_VERSION();
            """;
    }

    public static FreshnessCheck ParseCheck(byte[] answer)
    {
        var detail = ResponseReader.Read(answer, columnEncryption: false, keepRows: true);
        if (detail.Errors.Count > 0) return new FreshnessCheck(false, false, null, detail.Errors[0].ToString());
        if (detail.Problem is not null || detail.Tables is not [[[int data, int definitions, var version]]])
            return new FreshnessCheck(false, false, null, "the answer to the freshness check could not be read");
        return new FreshnessCheck(data == 1, definitions == 1, version as long?, null);
    }

    /// <summary>
    /// For daffyzoom.tracking: the database's Change Tracking and the login's permissions in it, then
    /// its tables - the tracked ones, or with <paramref name="allTables"/> every user table. What the
    /// login may not see comes back NULL rather than as a "no": row-level security without VIEW
    /// DEFINITION, the retention where sys.change_tracking_databases is hidden from it.
    /// </summary>
    public static string StatusBatch(bool allTables) => $"""
        SET NOCOUNT ON;
        DECLARE @viewDefinition int = ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0);
        SELECT DB_NAME(), SUSER_SNAME(), USER_NAME(), CHANGE_TRACKING_CURRENT_VERSION(),
               CAST(d.is_auto_cleanup_on AS int), d.retention_period, d.retention_period_units_desc, @viewDefinition,
               CASE WHEN @viewDefinition = 0 THEN NULL WHEN EXISTS (SELECT 1 FROM sys.security_policies WHERE is_enabled = 1) THEN 1 ELSE 0 END
        FROM (SELECT 1 AS one) AS x LEFT JOIN sys.change_tracking_databases AS d ON d.database_id = DB_ID();
        SELECT t.object_id, s.name, t.name, CASE WHEN c.object_id IS NULL THEN 0 ELSE 1 END,
               CASE WHEN EXISTS (SELECT 1 FROM sys.indexes AS i WHERE i.object_id = t.object_id AND i.is_primary_key = 1) THEN 1 ELSE 0 END,
               CHANGE_TRACKING_MIN_VALID_VERSION(t.object_id),
               ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(t.name), 'OBJECT', 'VIEW CHANGE TRACKING'), 0),
               ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(t.name), 'OBJECT', 'SELECT'), 0)
        FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.change_tracking_tables AS c ON c.object_id = t.object_id
        WHERE t.is_ms_shipped = 0{(allTables ? "" : " AND c.object_id IS NOT NULL")}
        ORDER BY 4 DESC, 2, 3;
        """;

    public static TrackingStatus ParseStatus(byte[] answer)
    {
        var detail = ResponseReader.Read(answer, columnEncryption: false, keepRows: true);
        if (detail.Errors.Count > 0) return new TrackingStatus(null, [], detail.Errors[0].Text);
        if (detail.Problem is not null || detail.Tables is not [[var database], var tables])
            return new TrackingStatus(null, [], "SQL Server's answer could not be read");

        if (database is not [string name, string login, string user, var version, var cleanup, var retention, var units, int viewDefinition, var rls])
            return new TrackingStatus(null, [], "SQL Server's answer could not be read: the database row");
        var db = new TrackingDatabase(name, login, user, version as long?, cleanup is int c ? c == 1 : null, retention as int?, units as string,
                                      viewDefinition == 1, rls is int r ? r == 1 : null);

        var rows = new List<TrackingTable>();
        foreach (var row in tables)
        {
            if (row is not [int id, string schema, string table, int tracked, int key, var minValid, int viewChanges, int select])
                return new TrackingStatus(db, [], "SQL Server's answer could not be read: a table row");
            rows.Add(new TrackingTable(id, schema, table, tracked == 1, key == 1, minValid as long?, viewChanges == 1, select == 1));
        }
        return new TrackingStatus(db, rows, null);
    }

    /// <summary>An N'...' literal; a name or a date as SQL Server gave it, quotes doubled.</summary>
    private static string Literal(string text) => "N'" + text.Replace("'", "''") + "'";
}
