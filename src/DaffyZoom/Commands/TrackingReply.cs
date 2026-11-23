using DaffyZoom.Cache;
using DaffyZoom.Tds;

namespace DaffyZoom.Commands;

/// <summary>
/// daffyzoom.tracking's answer: one row for the database, one per table, and messages that say what
/// stands in tracked mode's way and the statement that removes it. What the login may not see is
/// said to be unseen, never reported as off: a missing permission is part of the answer, not an error.
/// </summary>
internal static class TrackingReply
{
    public static Reply Build(TrackingReport report, bool allTables)
    {
        var reply = new Reply();
        string database = ObjectReference.Quote(report.Database);

        if (report.Status.Database is not { } db)
        {
            reply.Messages.Add($"SQL Server could not be asked about {database} as {Login(report)}: {report.Status.Problem}");
            if (report.Status.Problem is { } problem && (problem.Contains("4060") || problem.Contains("Cannot open database", StringComparison.OrdinalIgnoreCase)))
                reply.Messages.Add($"The login cannot open {database}: it needs a user there (CREATE USER ... FOR LOGIN ...), or the name is wrong.");
            reply.Messages.Add(PolicyLine(report));
            return reply;
        }

        var tables = report.Status.Tables;
        var tracked = tables.Where(t => t.Tracked).ToList();
        bool on = db.CurrentVersion is not null;
        string user = ObjectReference.Quote(db.User);

        reply.ResultSets.Add(new ResultSet(
            [Text("server", 256), Text("database", 128), Text("change_tracking", 8), BigInt("current_version"), BigInt("daffyzoom_last_saw"),
             Text("retention", 64), Text("auto_cleanup", 8), Text("daffyzoom_mode", 16), Text("login", 128), Text("database_user", 128),
             Text("view_definition", 8), Text("row_level_security", 16), Int("tables_tracked"), Int("tables_readable"), Int("entries")],
            [[report.Server, db.Database, on ? "on" : "off", db.CurrentVersion, report.KnownVersion,
              !on ? null : db.Retention is { } days ? $"{days} {db.RetentionUnits?.ToLowerInvariant()}" : "not visible to this login",
              db.AutoCleanup is { } cleanup ? YesNo(cleanup) : null,
              report.Policy?.Mode.ToString().ToLowerInvariant() ?? "not cached", db.Login, db.User,
              YesNo(db.ViewDefinition), db.RowLevelSecurity is { } rls ? (rls ? "enabled" : "none") : "cannot tell",
              tracked.Count, tracked.Count(Readable), report.Entries.Count]]));

        reply.ResultSets.Add(new ResultSet(
            [Text("table", 300), Text("tracked", 8), Text("primary_key", 8), BigInt("min_valid_version"), Text("view_change_tracking", 8),
             Text("select", 8), Int("entries"), BigInt("oldest_entry_version"), Text("state", 400)],
            tables.Select(t =>
            {
                var (count, oldest) = report.EntriesOn(t.Id);
                return new object?[]
                {
                    t.Quoted, YesNo(t.Tracked), YesNo(t.PrimaryKey), t.MinValidVersion, YesNo(t.ViewChangeTracking), YesNo(t.Select),
                    count, oldest, State(t, oldest),
                };
            }).ToList()));

        // What stands in the way, most fundamental first, each with what removes it.
        if (!on)
            reply.Messages.Add($"Change Tracking is off in {database}; tracked mode needs it: ALTER DATABASE {database} SET CHANGE_TRACKING = ON " +
                               "(CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON); then ALTER TABLE ... ENABLE CHANGE_TRACKING for each table the queries read (each needs a primary key).");
        if (!db.ViewDefinition)
            reply.Messages.Add($"{db.Login} has no VIEW DEFINITION in {database}, so tracked mode refuses every query for it (it cannot follow views to their tables), " +
                               $"this list shows only the tables it has permissions on, and row-level security cannot be checked: GRANT VIEW DEFINITION TO {user};");
        if (db.RowLevelSecurity == true)
            reply.Messages.Add($"{database} has an enabled row-level security policy: tracked mode refuses it, since answers could differ from one user to the next.");

        foreach (var schema in tracked.Where(t => !t.ViewChangeTracking).GroupBy(t => t.Schema))
            reply.Messages.Add($"{db.Login} cannot read the changes of {Count(schema.Count(), "tracked table")} in {ObjectReference.Quote(schema.Key)} " +
                               $"({Names(schema)}), so their queries are never served: GRANT VIEW CHANGE TRACKING ON SCHEMA::{ObjectReference.Quote(schema.Key)} TO {user}; (or ON each table)");
        if (tracked.Where(t => !t.Select).ToList() is { Count: > 0 } unreadable)
            reply.Messages.Add($"{db.Login} cannot SELECT from {Count(unreadable.Count, "tracked table")} ({Names(unreadable)}): queries on them fail at SQL Server, as they would without DaffyZoom.");

        var passed = tracked.Where(t => report.EntriesOn(t.Id).OldestBaseline is { } oldest && t.MinValidVersion > oldest).ToList();
        if (passed.Count > 0)
            reply.Messages.Add($"Change Tracking's cleanup has passed the version some entries were filled at ({Names(passed)}): they go to SQL Server on their next hit, " +
                               "and are cached again. A retention shorter than the time between loads makes this happen.");

        reply.Messages.Add(PolicyLine(report));
        if (on && db.ViewDefinition && db.RowLevelSecurity != true && tracked.Count > 0 && tracked.All(Readable))
            reply.Messages.Add($"{db.Login} can use tracked mode in {database}: {Count(tracked.Count, "table")} tracked, all readable by it" +
                               (report.Entries.Count > 0 ? $"; {Count(report.Entries.Count, "live entry", "live entries")} depend on them." : "."));
        else if (on && tracked.Count == 0)
            reply.Messages.Add($"No table in {database} has Change Tracking{(db.ViewDefinition ? "" : " that this login can see")}: ALTER TABLE [schema].[table] ENABLE CHANGE_TRACKING; (it needs a primary key).");

        if (!allTables)
            reply.Messages.Add($"Only tracked tables are listed. EXEC daffyzoom.tracking '{report.Database.Replace("'", "''")}', 'all' lists every table, and whether it could be tracked.");
        return reply;
    }

    private static bool Readable(TrackingTable table) => table.ViewChangeTracking && table.Select;

    private static string State(TrackingTable table, long? oldestEntry) =>
        !table.Tracked && !table.PrimaryKey ? "not tracked; it needs a primary key before it can be"
        : !table.Tracked ? $"not tracked: ALTER TABLE {table.Quoted} ENABLE CHANGE_TRACKING;"
        : !table.Select ? "this login cannot read it (SELECT)"
        : !table.ViewChangeTracking ? "this login cannot read its changes (VIEW CHANGE TRACKING)"
        : table.MinValidVersion is null ? "its changes cannot be read"
        : oldestEntry is { } oldest && table.MinValidVersion > oldest ? "cleanup has passed an entry's version: that entry goes to SQL Server on its next hit"
        : "ok";

    private static string PolicyLine(TrackingReport report) => report.Policy?.Mode switch
    {
        CacheMode.Tracked => $"DaffyZoom caches {ObjectReference.Quote(report.Database)} on {report.Server} in tracked mode.",
        CacheMode.Ttl => $"DaffyZoom caches {ObjectReference.Quote(report.Database)} on {report.Server} in ttl mode, which does not use Change Tracking.",
        _ => $"DaffyZoom does not cache {ObjectReference.Quote(report.Database)} on {report.Server}. On DaffyZoom's host: daffyzoom db set {report.Server}/{report.Database} --mode tracked",
    };

    private static string Login(TrackingReport report) => report.TargetLogin.Length > 0 ? report.TargetLogin : "your mapped login";

    private static string Names(IEnumerable<TrackingTable> tables)
    {
        var list = tables.ToList();
        string shown = string.Join(", ", list.Take(5).Select(t => t.Quoted));
        return list.Count > 5 ? $"{shown} and {list.Count - 5} more" : shown;
    }

    private static string Count(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

    private static ColumnDef Text(string name, int maxChars) => new(name, SqlColumnType.NVarChar, MaxLength: maxChars * 2);

    private static ColumnDef Int(string name) => new(name, SqlColumnType.Int);

    private static ColumnDef BigInt(string name) => new(name, SqlColumnType.BigInt);

    private static string YesNo(bool value) => value ? "yes" : "no";
}
