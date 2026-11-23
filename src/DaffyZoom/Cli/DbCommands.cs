using DaffyZoom.Cache;
using DaffyZoom.Repl;
using DaffyZoom.Security;

namespace DaffyZoom.Cli;

/// <summary>
/// `daffyzoom db ...` and `:db` at the prompt: which databases are cached, and how. Policies live in
/// the encrypted store with the users, so a change made here reaches a DaffyZoom running elsewhere
/// on this machine within a second. Changing a database's policy flushes what it had cached; a set
/// that changes no setting does not.
/// </summary>
internal static class DbCommands
{
    public static string Usage => """
            Nothing is cached until its database has a policy. SERVER is the SQL Server a login's target is on,
            as the target names it: host, or host,port off 1433. Not a DaffyZoom login. For example:
                daffyzoom db set sql01/Sales --mode ttl --max-age 4h
                daffyzoom db set sql01,14333/Sales --mode tracked
                daffyzoom db set myserver.database.windows.net/Sales --mode ttl --watermark "SELECT MAX(LoadId) FROM etl.LoadStatus" --watermark-as etl
            At the prompt, the same without "daffyzoom": :db set sql01/Sales --mode ttl

            daffyzoom db list
            daffyzoom db set SERVER/DATABASE --mode ttl|tracked|off [options]
                                        tracked: SQL Server's Change Tracking is checked on every hit (it must be on
                                        for the database and each table; the mapped logins need VIEW DEFINITION
                                        and VIEW CHANGE TRACKING)
                --max-age 4h            the longest any answer is kept (default 4h)
                --idle 1h               let an answer go if it is not used for this long; 0 for never (default 1h)
                --flush-at 02:30,14:00  flush at these times of day (this machine's local time)
                --blackout 02:00-02:45  no serving or filling during these windows - while a load runs -
                                        and a flush as each starts and ends
                --watermark "SQL"       flush when this query's answer changes, e.g. "SELECT MAX(LoadId) FROM etl.LoadStatus"
                --watermark-as LOGIN    the DaffyZoom login whose mapping runs it (it needs SELECT on what it reads)
                --every 60s             how often it runs (default 60s)
                --allow-proc NAME,...   stored procedures whose answers may be cached: your word that they only read
                none clears --flush-at, --blackout, --watermark or --allow-proc. Any change flushes the database;
                the same settings again (or the database spelled another way) keep what is cached.
            daffyzoom db remove SERVER/DATABASE
                The database's name is matched in any case. EXEC daffyzoom.status shows a session's target and database.
        """;

    public static int Run(UserStore store, IReadOnlyList<string> args, TextWriter output, ZoomCache? cache = null)
    {
        try
        {
            switch (args.Count > 0 ? args[0].ToLowerInvariant() : "list")
            {
                case "list":
                    List(store, output);
                    return 0;

                case "set" when args.Count >= 2:
                    Set(store, args[1], args.Skip(2).ToList(), output, cache);
                    return 0;

                case "remove" when args.Count >= 2:
                {
                    var (server, database) = DatabasePolicy.ParseId(args[1]);
                    bool removed = store.RemovePolicy(server, database);
                    cache?.Index.Flush(server, database);
                    cache?.Policies.Refresh(force: true);
                    output.WriteLine(removed ? $"  {server}/{database} is no longer cached" : $"  {server}/{database} had no policy");
                    return removed ? 0 : 1;
                }

                default:
                    output.WriteLine(Usage);
                    return 1;
            }
        }
        catch (FormatException ex)
        {
            output.WriteLine($"  {ex.Message}");
            return 1;
        }
    }

    public static ReplCommand PromptCommand(UserStore store, ZoomCache cache) => new(
        [":db"], ":db [list | set SERVER/DB ... | remove SERVER/DB]", "which databases are cached, and how",
        (args, output) =>
        {
            Run(store, args, output, cache);
            return Task.CompletedTask;
        });

    private static void List(UserStore store, TextWriter output)
    {
        var policies = store.Policies.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        if (policies.Count == 0)
        {
            output.WriteLine("  no database is cached yet: daffyzoom db set SERVER/DATABASE --mode ttl");
            return;
        }

        TextTable.Write(output, ["database", "policy", "changed"],
            policies.Select(p => new[] { p.Id, p.Describe(), p.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }).ToList());
    }

    private static void Set(UserStore store, string id, List<string> options, TextWriter output, ZoomCache? cache)
    {
        var (server, database) = DatabasePolicy.ParseId(id);
        var existing = store.Policies.Where(p => p.Server.Equals(server, StringComparison.OrdinalIgnoreCase) && DatabasePolicy.SameDatabase(p.Database, database))
                                     .MaxBy(p => p.UpdatedUtc);
        bool isNew = existing is null;

        // A copy: the stored policy is the one a running DaffyZoom is serving by, and must not change
        // half-way through these options if one of them turns out to be wrong.
        var policy = existing?.Copy() ?? new DatabasePolicy { Server = server, Mode = CacheMode.Off };
        policy.Database = database;     // as typed this time; saving replaces it in any spelling
        bool modeGiven = false;

        for (int i = 0; i < options.Count; i++)
        {
            string option = options[i];
            string Next() => i + 1 < options.Count ? options[++i] : throw new FormatException($"{option} needs a value");
            // "none" clears a list, since an empty argument is hard to pass from some shells (Windows PowerShell drops it).
            List<string> List() => Next() is var text && text.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? [] : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

            switch (option)
            {
                case "--mode":
                    policy.Mode = Next().ToLowerInvariant() switch
                    {
                        "ttl" => CacheMode.Ttl,
                        "off" => CacheMode.Off,
                        "tracked" => CacheMode.Tracked,
                        var other => throw new FormatException($"--mode is ttl, tracked or off, not '{other}'"),
                    };
                    modeGiven = true;
                    break;
                case "--max-age":
                    policy.MaxAgeMinutes = Math.Max(1, DatabasePolicy.ParseMinutes(Next()));
                    break;
                case "--idle":
                    policy.IdleMinutes = DatabasePolicy.ParseMinutes(Next());
                    break;
                case "--flush-at":
                    policy.FlushAt = List();
                    foreach (var t in policy.FlushAt)
                        if (!DatabasePolicy.TryParseTime(t, out _)) throw new FormatException($"--flush-at: '{t}' is not a time of day (HH:mm)");
                    break;
                case "--blackout":
                    policy.Blackouts = List();
                    foreach (var w in policy.Blackouts)
                        if (!DatabasePolicy.TryParseWindow(w, out _, out _)) throw new FormatException($"--blackout: '{w}' is not a window (HH:mm-HH:mm)");
                    break;
                case "--watermark":
                    policy.WatermarkSql = Next() is { Length: > 0 } sql && !sql.Equals("none", StringComparison.OrdinalIgnoreCase) ? sql : null;
                    break;
                case "--watermark-as":
                {
                    string login = Next();
                    if (store.Find(login) is null) throw new FormatException($"--watermark-as: there is no DaffyZoom login '{login}'");
                    policy.WatermarkLogin = login;
                    break;
                }
                case "--every":
                    policy.WatermarkSeconds = Math.Max(5, DatabasePolicy.ParseSeconds(Next()));
                    break;
                case "--allow-proc":
                    policy.Procedures = List();
                    break;
                default:
                    throw new FormatException($"unknown option {option}; daffyzoom db shows them");
            }
        }

        if (!modeGiven && isNew) throw new FormatException("a new database needs --mode ttl (or tracked, or off)");
        if (policy.WatermarkSql is not null && policy.WatermarkLogin is null)
            throw new FormatException("--watermark needs --watermark-as LOGIN: the mapping whose login runs the query");

        // The same settings again - a setup script run twice, or the database spelled another way -
        // change nothing that decides what is cached, so what is cached stays. A new spelling is saved,
        // keeping the policy's change time, which is what the cached answers are checked against.
        if (existing is not null && policy.SameSettings(existing))
        {
            if (existing.Database != policy.Database) store.SavePolicy(policy, keepChangeTime: true);
            cache?.Policies.Refresh(force: true);
            output.WriteLine($"  {policy.Id}: {policy.Describe()} (unchanged{(policy.Mode != CacheMode.Off ? ": what is cached for it is kept" : "")})");
            if (policy.Mode != CacheMode.Off && ServerProblem(store, policy.Server) is { } unchangedProblem)
                output.WriteLine($"  note: {unchangedProblem}");
            return;
        }

        store.SavePolicy(policy);
        cache?.Index.Flush(policy.Server, policy.Database);
        cache?.Policies.Refresh(force: true);
        output.WriteLine($"  {policy.Id}: {policy.Describe()}");
        if (policy.Mode != CacheMode.Off && ServerProblem(store, policy.Server) is { } problem)
            output.WriteLine($"  note: {problem}");
        if (policy.Mode != CacheMode.Off && cache is null)
            output.WriteLine("  a running DaffyZoom picks this up within a second; what it had cached for this database is no longer served");
    }

    /// <summary>The servers the logins' targets are on, as policies name them: host:port, in lower case.</summary>
    internal static List<string> TargetServers(UserStore store) =>
        store.Users.Select(UserAdmin.TryEndpoint).OfType<Proxy.TargetEndpoint>()
             .Select(e => $"{e.Host}:{e.Port}".ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Why a policy for this server caches nothing - no login's target is on it - or null. Most often
    /// SERVER was given as a DaffyZoom login, or as a name other than the one the targets use.
    /// </summary>
    internal static string? ServerProblem(UserStore store, string server)
    {
        var servers = TargetServers(store);
        if (servers.Contains(server, StringComparer.OrdinalIgnoreCase)) return null;

        string host = server.LastIndexOf(':') is var colon and > 0 ? server[..colon] : server;
        string login = store.Find(host) is not null
            ? $" '{host}' is a DaffyZoom login; SERVER is the SQL Server a login's target is on."
            : " SERVER is the SQL Server as the targets name it.";
        return servers.Count == 0
            ? $"no DaffyZoom login has a target yet, so this caches nothing until one is on {server}."
            : $"no DaffyZoom login's target is on {server}, so this caches nothing. The targets are on {string.Join(", ", servers)}.{login}";
    }
}
