using DaffyZoom.Proxy;
using DaffyZoom.Security;
using DaffyZoom.Tds;

namespace DaffyZoom.Commands;

/// <summary>One row of daffyzoom.settings: a setting, its value now, and how it is changed.</summary>
internal sealed record SettingRow(string Setting, string Value, string ChangedWith);

/// <summary>
/// Carries out daffyzoom.* commands. Everyone can ask for help and for their own session; the rest
/// needs a login that is a DaffyZoom admin when the command runs - checked against the store every
/// time, so a change made on the host counts at once.
///
/// It works on the same things the prompt does - the session registry, the user store through
/// <see cref="UserAdmin"/> - so a session killed here is exactly what :kill would have done, logged
/// the same way, with the login that did it named instead of "the console".
/// </summary>
internal sealed class CommandRunner(
    ProxyContext context,
    Func<IReadOnlyList<SettingRow>> settings,
    Func<TargetEndpoint, CancellationToken, Task<TargetCheck>>? checkTarget = null,
    Cache.ZoomCache? cache = null) : IGatewayCommands
{
    private const int MaxLog = 1000;

    private readonly Func<TargetEndpoint, CancellationToken, Task<TargetCheck>> _checkTarget = checkTarget ?? UserAdmin.CheckTargetAsync;

    private UserStore Store => context.Users;

    public async Task<Reply> RunAsync(ProxySession caller, DaffyCommand command, CancellationToken cancellationToken)
    {
        var definition = CommandCatalog.Find(command.Name)
            ?? throw new GatewayException(GatewayErrors.BadCommand, $"there is no command daffyzoom.{command.Name}. EXEC daffyzoom.help lists them.");

        string login = caller.Login ?? "";
        bool admin = Store.Find(login) is { Admin: true, Enabled: true };
        if (definition.Admin && !admin)
        {
            Log.Warn("admin.refused", $"'{login}': daffyzoom.{definition.Name} refused, not an admin", caller.Id);
            throw new GatewayException(GatewayErrors.NeedsAdmin,
                $"daffyzoom.{definition.Name} needs a DaffyZoom admin login. An admin is made on DaffyZoom's host: daffyzoom user admin NAME on");
        }

        var args = CommandCatalog.Bind(definition, command.Arguments);
        if (definition.Admin)
            Log.Info("admin.command", $"'{login}': daffyzoom.{definition.Name} {Describe(definition, args)}".TrimEnd(), caller.Id);

        var by = new Caller(caller, login);
        return definition.Name switch
        {
            "help" => Help(by, admin, args.OptionalText("command")),
            "status" => Status(by, admin),

            "sessions" => Sessions(by),
            "kill_session" => Kill(by, args.Number("session")),
            "health" => Health(),
            "watchlist" => WatchList(Math.Clamp(args.Number("count", 20), 1, 500)),
            "entries" => Entries(Math.Clamp(args.Number("count", 20), 1, 500)),
            "databases" => Databases(),
            "tracking" => await TrackingAsync(by, args.OptionalText("database"), args.OptionalText("show"), cancellationToken),
            "flush" => Flush(by, admin, args.Text("database"), args.OptionalText("server")),
            "set_serving" => SetServing(by, args.Flag("on")),
            "settings" => Settings(),
            "log" => LogLines(Math.Clamp(args.Number("count", 50), 1, MaxLog)),

            "users" => Users(),
            "add_user" => await AddUserAsync(args.Text("login"), args.Secret("password"), args.Text("target"), cancellationToken),
            "set_user_password" => SetUserPassword(args.Text("login"), args.Secret("password")),
            "set_target" => await SetTargetAsync(args.Text("login"), args.Text("target"), cancellationToken),
            "set_target_password" => await SetTargetPasswordAsync(args.Text("login"), args.Secret("password"), cancellationToken),
            "test_user" => await TestUserAsync(args.Text("login"), cancellationToken),
            "enable_user" => SetEnabled(by, args.Text("login"), true),
            "disable_user" => SetEnabled(by, args.Text("login"), false),
            "set_admin" => SetAdmin(by, args.Text("login"), args.Flag("admin")),
            "remove_user" => RemoveUser(by, args.Text("login")),

            _ => throw new GatewayException(GatewayErrors.BadCommand, $"daffyzoom.{definition.Name} is not implemented."),
        };
    }

    /// <summary>Who is asking: the session, and the DaffyZoom login it logged in with.</summary>
    private sealed record Caller(ProxySession Session, string Login)
    {
        public bool Is(string login) => string.Equals(login, Login, StringComparison.OrdinalIgnoreCase);
    }

    // ---- everyone -------------------------------------------------------------------------------

    private static Reply Help(Caller caller, bool admin, string? only)
    {
        var commands = CommandCatalog.All.Where(c => admin || !c.Admin).ToList();

        if (only is not null)
        {
            string name = only.StartsWith(CommandParser.Schema + ".", StringComparison.OrdinalIgnoreCase) ? only[(CommandParser.Schema.Length + 1)..] : only;
            var found = CommandCatalog.Find(name)
                ?? throw new GatewayException(GatewayErrors.BadCommand, $"there is no command daffyzoom.{name}. EXEC daffyzoom.help lists them.");
            if (found.Admin && !admin)
                throw new GatewayException(GatewayErrors.NeedsAdmin, $"daffyzoom.{found.Name} needs a DaffyZoom admin login.");
            commands = [found];
        }

        var reply = new Reply();
        if (only is null)
        {
            var s = caller.Session;
            reply.Messages.Add($"DaffyZoom {Program.ToolVersion()}. You are '{caller.Login}', session {s.Id}, relayed to {s.TargetLogin}@{s.Target} as SPID {s.Spid}.");
            reply.Messages.Add("Run each daffyzoom command on its own, not in a batch with other SQL. DaffyZoom answers them itself; they never reach the target.");
            reply.Messages.Add(admin
                ? "Passwords typed here can stay behind in SSMS's query history. On DaffyZoom's host, 'daffyzoom user passwd NAME' and 'daffyzoom user target NAME' ask for them at a masked prompt instead."
                : "DaffyZoom admins can run more: who is connected, how DaffyZoom is doing, and the logins. An admin is made on DaffyZoom's host with 'daffyzoom user admin NAME on'.");
        }

        reply.ResultSets.Add(new ResultSet(
            [Text("command", 128), Text("arguments", 256), Text("what_it_does", 512), Text("example", 512)],
            commands.Select(c => new object?[] { $"daffyzoom.{c.Name}", c.Arguments, c.Summary, c.Example }).ToList()));
        return reply;
    }

    private static Reply Status(Caller caller, bool admin)
    {
        var s = caller.Session;
        var reply = new Reply();
        reply.Messages.Add($"You are session {s.Id} ('{caller.Login}'), relayed to {s.TargetLogin}@{s.Target} as SPID {s.Spid}.");
        reply.ResultSets.Add(new ResultSet(
            [Int("session"), Text("login", 128), Text("target_login", 256), Text("target", 256), Text("redirected_to", 256),
             Int("spid"), Text("database", 128), Text("application", 256), Text("host", 128), Text("client_address", 64),
             Text("client_encryption", 128), Text("target_encryption", 128), Time("connected_since"), BigInt("requests"),
             Text("admin", 3), Text("daffyzoom_version", 32)],
            [[s.Id, caller.Login, s.TargetLogin, s.Target, s.RoutedTo, (int)s.Spid, s.Database, s.AppName, s.HostName, s.ClientAddress,
              s.FrontSecurity, s.UpstreamSecurity, s.StartedUtc.ToLocalTime(), s.Requests, YesNo(admin), Program.ToolVersion()]]));
        return reply;
    }

    // ---- what is going on -----------------------------------------------------------------------

    private Reply Sessions(Caller caller)
    {
        var reply = new Reply();
        reply.Messages.Add($"You are session {caller.Session.Id}.");
        reply.ResultSets.Add(new ResultSet(
            [Int("session"), Text("login", 128), Text("target_login", 256), Text("target", 256), Text("redirected_to", 256),
             Int("spid"), Text("database", 128), Text("application", 256), Text("host", 128), Text("client_address", 64),
             Text("client_encryption", 128), Text("target_encryption", 128), Text("mars", 3), Text("state", 16),
             BigInt("requests"), BigInt("bytes_from_client"), BigInt("bytes_to_client"), Time("connected_since"), Int("idle_seconds")],
            context.Sessions.Snapshot().Select(s => new object?[]
            {
                s.Id, s.Login, s.TargetLogin, s.Target, s.RoutedTo, s.Spid == 0 ? null : (int)s.Spid, s.Database, s.AppName, s.HostName,
                s.ClientAddress, s.FrontSecurity, s.UpstreamSecurity, YesNo(s.Mars), s.State.ToString().ToLowerInvariant(),
                s.Requests, s.BytesFromClient, s.BytesFromServer, s.StartedUtc.ToLocalTime(),
                (int)(DateTime.UtcNow - s.LastActivityUtc).TotalSeconds,
            }).ToList()));
        return reply;
    }

    private Reply Kill(Caller caller, int id)
    {
        if (id == caller.Session.Id)
            throw new GatewayException(GatewayErrors.Refused, $"session {id} is your own; disconnect instead.");

        var killed = context.Sessions.Kill(id, $"killed by '{caller.Login}' (session {caller.Session.Id}) with daffyzoom.kill_session")
            ?? throw new GatewayException(GatewayErrors.NotFound, $"there is no session {id}. EXEC daffyzoom.sessions lists them (by id, not SPID).");

        var reply = new Reply();
        reply.Messages.Add($"Disconnected session {id} ('{killed.Login}', SPID {killed.Spid}). The target rolls back whatever it had open.");
        return reply;
    }

    private Reply Health()
    {
        var sessions = context.Sessions;
        var c = cache?.Counters;
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet(
            [Text("version", 32), Time("started"), BigInt("uptime_seconds"), Int("sessions_active"), BigInt("sessions_accepted"),
             BigInt("logins_succeeded"), BigInt("logins_refused"), BigInt("refused_by_target"),
             Text("cache", 64), BigInt("queries_seen"), BigInt("queries_cacheable"), BigInt("queries_slow"), BigInt("queries_analyzed"),
             BigInt("analyzer_dropped"), Int("exact_requests_watched"),
             Int("entries"), BigInt("cache_bytes"), BigInt("hits"), BigInt("hit_bytes"), BigInt("fills_started"), BigInt("fills_kept"),
             BigInt("fell_back"), BigInt("invalidated")],
            [[Program.ToolVersion(), sessions.StartedUtc.ToLocalTime(), (long)(DateTime.UtcNow - sessions.StartedUtc).TotalSeconds,
              sessions.Active, sessions.Accepted, sessions.LoginsSucceeded, sessions.LoginsFailed, sessions.UpstreamFailures,
              cache?.State ?? "off",
              c?.Requests, c?.Eligible, c?.Slow, c?.Analyzed, c?.DroppedQueueFull, cache?.WatchList.KeyCount,
              cache?.Index.Count, cache?.Index.FileBytes, c?.Hits, c?.HitBytes, c?.FillsStarted, c?.Filled, c?.Fallbacks, c?.Invalidated]]));
        return reply;
    }

    private Reply Entries(int count)
    {
        var reply = new Reply();
        if (cache is null) { reply.Messages.Add("The cache is off."); return reply; }

        var now = DateTime.UtcNow;
        var entries = cache.LiveEntries().OrderByDescending(e => e.SavedMs).ThenByDescending(e => e.OriginalMs).Take(count).ToList();
        if (entries.Count == 0) reply.Messages.Add("Nothing is cached.");
        reply.ResultSets.Add(new ResultSet(
            [Text("key", 32), Text("server", 256), Text("database", 128), Number("took_seconds", 1), BigInt("bytes"), BigInt("bytes_on_disk"),
             BigInt("hits"), Number("saved_seconds", 1), Time("cached_at"), Time("last_used"), Text("query")],
            entries.Select(e => new object?[]
            {
                e.Key.ToString(), e.Server, e.Database, Math.Round((decimal)e.OriginalMs / 1000, 1), e.Length, e.FileBytes, e.Hits,
                Math.Round((decimal)e.SavedMs / 1000, 1), e.CreatedUtc.ToLocalTime(), e.LastUsedUtc.ToLocalTime(), e.Shape,
            }).ToList()));
        return reply;
    }

    private Reply Databases()
    {
        var reply = new Reply();
        if (cache is null) { reply.Messages.Add("The cache is off."); return reply; }

        var policies = cache.Policies.All;
        if (policies.Count == 0)
            reply.Messages.Add("No database is cached: nothing is, until its database has a policy. On DaffyZoom's host: daffyzoom db set SERVER/DATABASE --mode ttl, " +
                               "where SERVER is the SQL Server a login's target is on (EXEC daffyzoom.status shows yours, as target).");
        reply.ResultSets.Add(new ResultSet(
            [Text("server", 256), Text("database", 128), Text("mode", 16), Text("state", 1024), Int("max_age_minutes"), Int("idle_minutes"),
             Text("policy", 1024), Int("entries"), Time("changed")],
            policies.Select(p => new object?[]
            {
                p.Server, p.Database, p.Mode.ToString().ToLowerInvariant(),
                p.Mode != Cache.CacheMode.Off && Cli.DbCommands.ServerProblem(Store, p.Server) is { } problem ? $"caches nothing: {problem}" : cache.StateOf(p),
                p.MaxAgeMinutes, p.IdleMinutes, p.Describe(),
                cache.LiveEntries().Count(e => e.Server.Equals(p.Server, StringComparison.OrdinalIgnoreCase) && Cache.DatabasePolicy.SameDatabase(e.Database, p.Database)),
                p.UpdatedUtc.ToLocalTime(),
            }).ToList()));
        return reply;
    }

    /// <summary>
    /// Change Tracking in a database, asked of SQL Server as the caller's own mapped login - what tracked
    /// mode would see for them - beside the entries of that login DaffyZoom holds on its tables.
    /// </summary>
    private async Task<Reply> TrackingAsync(Caller caller, string? database, string? show, CancellationToken cancellationToken)
    {
        var definition = CommandCatalog.Find("tracking")!;
        bool all = (show ?? "").ToLowerInvariant() switch
        {
            "" or "tracked" => false,
            "all" => true,
            _ => throw CommandCatalog.Usage(definition, $"@show is 'tracked' or 'all', not '{show}'"),
        };
        if (cache is null)
        {
            var off = new Reply();
            off.Messages.Add("The cache is off.");
            return off;
        }

        database = string.IsNullOrEmpty(database) ? caller.Session.Database : database;
        if (string.IsNullOrEmpty(database)) throw CommandCatalog.Usage(definition, "your session's database is not known: name one");
        return TrackingReply.Build(await cache.TrackingAsync(caller.Login, database, all, cancellationToken), all);
    }

    /// <summary>
    /// Anyone may flush a database on the server their own login maps to - an ETL job needs no admin
    /// rights to say its load has finished. Another server, or everything, takes an admin.
    /// </summary>
    private Reply Flush(Caller caller, bool admin, string database, string? server)
    {
        var reply = new Reply();
        if (cache is null) { reply.Messages.Add("The cache is off; there is nothing to flush."); return reply; }

        if ((database == "*" || server is not null) && !admin)
            throw new GatewayException(GatewayErrors.NeedsAdmin,
                "flushing everything, or a database on another server, needs a DaffyZoom admin login. Your own target's databases you can flush: EXEC daffyzoom.flush 'Database'");

        string who = $"'{caller.Login}' (session {caller.Session.Id}) with daffyzoom.flush";
        if (database == "*")
        {
            reply.Messages.Add($"Flushed everything: {cache.FlushAll(who)} cached answer(s).");
            return reply;
        }

        string target = (server ?? caller.Session.Target ?? "").ToLowerInvariant();
        cache.Flush(target, database, who);
        reply.Messages.Add($"Flushed {target}/{database}: none of its answers cached until now will be served.");
        if (cache.Policies.For(target, database) is not { Mode: not Cache.CacheMode.Off })
            reply.Messages.Add($"No policy caches {database} on {target}, so nothing of it was cached here. " +
                               "EXEC daffyzoom.databases lists the databases that are; check the name if this one should be.");
        return reply;
    }

    private Reply SetServing(Caller caller, bool on)
    {
        var reply = new Reply();
        if (cache is null) { reply.Messages.Add("The cache is off."); return reply; }
        cache.Serving = on;
        Log.Info("admin.serving", $"serving from the cache switched {(on ? "on" : "off")} by '{caller.Login}' (session {caller.Session.Id})", caller.Session.Id);
        reply.Messages.Add(on ? "Serving from the cache is on." : "Serving from the cache is off: every request goes to SQL Server, and nothing is filled.");
        return reply;
    }

    private Reply WatchList(int count)
    {
        var reply = new Reply();
        if (cache is null)
        {
            reply.Messages.Add("The cache is off.");
            return reply;
        }

        var rows = cache.WatchList.Top(count);
        var cached = cache.CachedByShape();
        if (rows.Count == 0) reply.Messages.Add($"Nothing slower than {cache.Options.SlowMs / 1000.0:0.#} s has run through DaffyZoom yet.");
        reply.ResultSets.Add(new ResultSet(
            [Text("id", 16), Text("state", 1024), BigInt("runs"), Int("exact_requests"), Number("avg_seconds", 1), Number("p95_seconds", 1),
             BigInt("last_bytes"), Int("cached_entries"), BigInt("hits"), BigInt("would_serve"), Number("saved_seconds", 1),
             Text("database", 128), Text("target_login", 256), Time("last_seen"), Text("query")],
            rows.Select(r =>
            {
                var (entries, hits, saved) = cached.GetValueOrDefault((r.ShapeHash, r.TargetLogin));
                return new object?[]
                {
                    r.Id, entries > 0 ? "cached" : cache.RowState(r), r.Runs, r.Keys, Math.Round((decimal)r.AverageMs / 1000, 1), Math.Round((decimal)r.P95Ms / 1000, 1),
                    r.LastBytes, entries, hits, r.WouldServe, Math.Round((decimal)(entries > 0 ? saved : r.SavedMs) / 1000, 1),
                    r.Database, r.TargetLogin, r.LastSeenUtc.ToLocalTime(), r.Shape,
                };
            }).ToList()));
        return reply;
    }

    private Reply Settings()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet(
            [Text("setting", 64), Text("value", 1024), Text("changed_with", 512)],
            settings().Select(s => new object?[] { s.Setting, s.Value, s.ChangedWith }).ToList()));
        return reply;
    }

    private static Reply LogLines(int count)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet(
            [Time("time"), Text("level", 8), Text("event", 64), Int("session"), Text("message")],
            Log.Recent.Tail(count).Select(e => new object?[]
            {
                e.TimestampUtc.ToLocalTime(), e.Level.ToString().ToLowerInvariant(), e.Code, e.Session == 0 ? null : e.Session, e.Message,
            }).ToList()));
        return reply;
    }

    // ---- logins ---------------------------------------------------------------------------------

    private Reply Users()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new ResultSet(
            [Text("login", 128), Text("admin", 3), Text("enabled", 3), Text("target", 512), Text("authentication", 32),
             Text("encryption", 128), Time("last_validated"), Text("validation", 256), Time("created"), Time("updated")],
            Store.Users.Select(u =>
            {
                var endpoint = UserAdmin.TryEndpoint(u);
                return new object?[]
                {
                    u.Login, YesNo(u.Admin), YesNo(u.Enabled), endpoint?.Describe() ?? "(unreadable target)",
                    endpoint is null ? null : endpoint.IsEntra ? "Entra application" : "SQL login", endpoint?.DescribeSecurity(),
                    u.LastValidatedUtc?.ToLocalTime(), u.LastValidation, u.CreatedUtc.ToLocalTime(), u.UpdatedUtc.ToLocalTime(),
                };
            }).ToList()));
        return reply;
    }

    private async Task<Reply> AddUserAsync(string login, string password, string target, CancellationToken cancellationToken)
    {
        try
        {
            UserStore.ValidateLogin(login);
        }
        catch (ArgumentException ex)
        {
            throw new GatewayException(GatewayErrors.BadCommand, $"'{login}' cannot be a login: {ex.Message}.");
        }

        if (Store.Find(login) is { } existing)
            throw new GatewayException(GatewayErrors.NotFound,
                $"there is already a login '{existing.Login}'. daffyzoom.set_target and daffyzoom.set_user_password change it.");

        var endpoint = ParseTarget(target);
        if (endpoint.Password.Length == 0)
            throw new GatewayException(GatewayErrors.BadCommand,
                $"the target connection string has no {(endpoint.IsEntra ? "client secret" : "password")}, and DaffyZoom logs in to the target before saving. " +
                $"Include Password=..., or add the login on DaffyZoom's host, which asks for it at a masked prompt: daffyzoom user add {login}");

        var check = await CheckOrThrowAsync(endpoint, "Nothing was saved.", cancellationToken);
        UserAdmin.Create(Store, login, password, endpoint, check.Summary!);

        var reply = new Reply();
        reply.Messages.Add($"Created '{login}' -> {endpoint.Describe()}: {check.Detail}. It can log in now.");
        Redirected(reply, check);
        WeakPassword(reply, password);
        return reply;
    }

    private Reply SetUserPassword(string login, string password)
    {
        var user = RequireUser(login);
        UserAdmin.SetPassword(Store, user, password);

        var reply = new Reply();
        reply.Messages.Add($"Changed the DaffyZoom password of '{user.Login}'. Its target is unchanged, and sessions already open carry on.");
        WeakPassword(reply, password);
        return reply;
    }

    private async Task<Reply> SetTargetAsync(string login, string target, CancellationToken cancellationToken)
    {
        var user = RequireUser(login);
        var endpoint = UserAdmin.KeepPassword(ParseTarget(target), UserAdmin.TryEndpoint(user));
        if (endpoint.Password.Length == 0)
            throw new GatewayException(GatewayErrors.BadCommand,
                $"the new target connection string has no {(endpoint.IsEntra ? "client secret" : "password")}, and it names a different login from " +
                $"the current target, so the current one cannot be kept. Include Password=..., or change it on DaffyZoom's host: daffyzoom user target {user.Login}");

        var check = await CheckOrThrowAsync(endpoint, "Nothing was changed.", cancellationToken);
        UserAdmin.SetTarget(Store, user, endpoint, check.Summary!);

        var reply = new Reply();
        reply.Messages.Add($"'{user.Login}' now goes to {endpoint.Describe()}: {check.Detail}.");
        reply.Messages.Add("New sessions go there; sessions already open stay where they are.");
        Redirected(reply, check);
        return reply;
    }

    private async Task<Reply> SetTargetPasswordAsync(string login, string password, CancellationToken cancellationToken)
    {
        var user = RequireUser(login);
        var current = UserAdmin.TryEndpoint(user)
            ?? throw new GatewayException(GatewayErrors.BadCommand, $"the target of '{user.Login}' cannot be read; set a whole new one with daffyzoom.set_target.");

        var endpoint = current with { Password = password };
        var check = await CheckOrThrowAsync(endpoint, "Nothing was changed.", cancellationToken);
        UserAdmin.SetTarget(Store, user, endpoint, check.Summary!);

        var reply = new Reply();
        reply.Messages.Add($"Changed the {(endpoint.IsEntra ? "client secret" : "password")} DaffyZoom logs in to {endpoint.Describe()} with, for '{user.Login}': {check.Detail}.");
        return reply;
    }

    private async Task<Reply> TestUserAsync(string login, CancellationToken cancellationToken)
    {
        var user = RequireUser(login);
        var endpoint = UserAdmin.TryEndpoint(user)
            ?? throw new GatewayException(GatewayErrors.BadCommand, $"the target of '{user.Login}' cannot be read; set a whole new one with daffyzoom.set_target.");

        var check = await CheckOrThrowAsync(endpoint, "", cancellationToken);
        UserAdmin.RecordValidation(Store, user, check.Summary!);

        var reply = new Reply();
        reply.Messages.Add($"'{user.Login}' -> {endpoint.Describe()}: ok - {check.Detail}.");
        Redirected(reply, check);
        return reply;
    }

    private Reply SetEnabled(Caller caller, string login, bool enabled)
    {
        var user = RequireUser(login);
        if (!enabled && caller.Is(user.Login))
            throw new GatewayException(GatewayErrors.Refused, "you cannot disable your own login here. Another admin can, or DaffyZoom's host can: daffyzoom user disable NAME");

        UserAdmin.SetEnabled(Store, user, enabled);
        var reply = new Reply();
        reply.Messages.Add(enabled
            ? $"'{user.Login}' can log in again."
            : $"'{user.Login}' can no longer log in. Sessions already open carry on; daffyzoom.kill_session ends one.");
        return reply;
    }

    private Reply SetAdmin(Caller caller, string login, bool admin)
    {
        var user = RequireUser(login);
        if (!admin && caller.Is(user.Login))
            throw new GatewayException(GatewayErrors.Refused, "you cannot take away your own admin rights here. Another admin can, or DaffyZoom's host can: daffyzoom user admin NAME off");

        UserAdmin.SetAdmin(Store, user, admin);
        var reply = new Reply();
        reply.Messages.Add(admin ? $"'{user.Login}' is now an admin." : $"'{user.Login}' is no longer an admin.");
        return reply;
    }

    private Reply RemoveUser(Caller caller, string login)
    {
        var user = RequireUser(login);
        if (caller.Is(user.Login))
            throw new GatewayException(GatewayErrors.Refused, "you cannot remove your own login here. Another admin can, or DaffyZoom's host can: daffyzoom user remove NAME");

        Store.Remove(user.Login);
        var reply = new Reply();
        reply.Messages.Add($"Removed '{user.Login}' and its target. Sessions already open carry on until they disconnect; daffyzoom.kill_session ends one.");
        return reply;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private UserRecord RequireUser(string login) =>
        Store.Find(login) ?? throw new GatewayException(GatewayErrors.NotFound, $"there is no login '{login}'. EXEC daffyzoom.users lists them.");

    private static TargetEndpoint ParseTarget(string connectionString)
    {
        try
        {
            return TargetEndpoint.Parse(connectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new GatewayException(GatewayErrors.BadCommand, $"the target connection string cannot be used: {ex.Message}.");
        }
    }

    /// <summary>Logs in to the target the way the relay would; a failure is the client's error, and nothing is saved.</summary>
    private async Task<TargetCheck> CheckOrThrowAsync(TargetEndpoint endpoint, string consequence, CancellationToken cancellationToken)
    {
        var check = await _checkTarget(endpoint, cancellationToken);
        if (check.Ok) return check;

        string message = $"could not log in to the target {endpoint.Describe()} ({check.Failure}): {check.Problem}";
        if (check.Resuming) message += " (An Azure SQL database that paused itself says this while it resumes, which takes about a minute: try again.)";
        throw new GatewayException(GatewayErrors.TargetFailed, $"{message.TrimEnd('.')}. {consequence}".TrimEnd());
    }

    private static void Redirected(Reply reply, TargetCheck check)
    {
        if (check.RoutedTo is { } routedTo)
            reply.Messages.Add($"The target redirected the login to {routedTo}; DaffyZoom follows the redirect itself.");
    }

    private static void WeakPassword(Reply reply, string password)
    {
        if (password.Length < 8) reply.Messages.Add("Note: that password is shorter than 8 characters.");
    }

    /// <summary>What an admin command was given, for the log - with passwords and connection strings left out.</summary>
    private static string Describe(CommandDefinition definition, BoundArguments args) =>
        string.Join(", ", definition.Parameters
            .Where(p => !p.Secret && !p.ConnectionString)
            .Select(p => args.OptionalText(p.Name) is { } value ? $"@{p.Name}='{value}'" : null)
            .Where(s => s is not null));

    private static ColumnDef Text(string name, int? maxChars = null) =>
        new(name, SqlColumnType.NVarChar, MaxLength: maxChars * 2);

    private static ColumnDef Int(string name) => new(name, SqlColumnType.Int);

    private static ColumnDef BigInt(string name) => new(name, SqlColumnType.BigInt);

    private static ColumnDef Time(string name) => new(name, SqlColumnType.DateTime2);

    private static ColumnDef Number(string name, byte scale) => new(name, SqlColumnType.Decimal, Precision: 18, Scale: scale);

    private static string YesNo(bool value) => value ? "yes" : "no";
}
