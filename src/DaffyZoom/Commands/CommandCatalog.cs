using System.Globalization;

namespace DaffyZoom.Commands;

/// <summary>
/// One argument of a command. A <paramref name="Secret"/> is masked in events and never logged; a
/// <paramref name="ConnectionString"/> is not logged either, and in events only its password is masked.
/// </summary>
internal sealed record CommandParameter(string Name, string Description, bool Required = true, bool Secret = false,
                                        bool ConnectionString = false);

/// <summary>One daffyzoom.* command: who may run it, what it takes, and how daffyzoom.help describes it.</summary>
internal sealed record CommandDefinition(string Name, bool Admin, string Summary, string Example, params CommandParameter[] Parameters)
{
    public string Arguments => string.Join(", ", Parameters.Select(p => p.Required ? $"@{p.Name}" : $"[@{p.Name}]"));

    public string Usage => Parameters.Length == 0 ? $"EXEC daffyzoom.{Name}" : $"EXEC daffyzoom.{Name} {Arguments}";
}

/// <summary>
/// Every daffyzoom.* command. Names put the verb first so they read as sentences, and none is a
/// T-SQL reserved word - daffyzoom.kill would not even parse - so every command is valid T-SQL that
/// tools accept as it stands. (The pattern, the binding and the help are DaffyDuo's, by way of
/// DaffyTee.)
///
/// Anything that needs a restart or the host itself - the port, the certificate, the cache
/// directory - is not here: daffyzoom.settings shows it, and it is changed on the host.
/// </summary>
internal static class CommandCatalog
{
    private static readonly CommandParameter LoginName = new("login", "a DaffyZoom login");

    public static readonly IReadOnlyList<CommandDefinition> All =
    [
        new("help", false, "What you can run here, with examples.", "EXEC daffyzoom.help 'sessions'",
            new CommandParameter("command", "one command, to see just that one", Required: false)),
        new("status", false, "Your session: its id here, where it is relayed to, its SPID there, and how it is encrypted.",
            "EXEC daffyzoom.status"),

        new("sessions", true, "Every connected session: who, relayed where, SPID, database, application, traffic, idle time.",
            "EXEC daffyzoom.sessions"),
        new("kill_session", true,
            "Disconnect a session, by its id in daffyzoom.sessions (not its SPID). The target rolls back whatever it had open.",
            "EXEC daffyzoom.kill_session 12", new CommandParameter("session", "the session's id from daffyzoom.sessions")),
        new("health", true, "How DaffyZoom is doing: uptime, sessions, logins, and what the cache has seen.", "EXEC daffyzoom.health"),
        new("watchlist", true,
            "Slow queries that repeat: how often, how slow, whether they are cached (or why not), and the time saved.",
            "EXEC daffyzoom.watchlist 20", new CommandParameter("count", "how many query shapes (default 20)", Required: false)),
        new("entries", true, "What is cached now, most useful first: the query, its size, its hits and the time they saved.",
            "EXEC daffyzoom.entries 20", new CommandParameter("count", "how many entries (default 20)", Required: false)),
        new("databases", true, "Which databases are cached, and how (their policies). Set on the host: daffyzoom db set.",
            "EXEC daffyzoom.databases"),
        new("tracking", false,
            "Change Tracking in a database, as your login sees it: on or off, its version and retention, each tracked table " +
            "(or with 'all', every table, and whether it could be tracked), what your login may read - and the GRANT or ALTER that fixes what is missing.",
            "EXEC daffyzoom.tracking 'AdventureWorksDW2022', 'all'",
            new CommandParameter("database", "the database (default: your session's)", Required: false),
            new CommandParameter("show", "'tracked' tables (default) or 'all'", Required: false)),
        new("flush", false,
            "Drop a database's cached answers - from an ETL job when a load has finished. Flushes it on the server your login maps to; " +
            "an admin may name another server, or flush everything with '*'.",
            "EXEC daffyzoom.flush 'AdventureWorksDW2022'",
            new CommandParameter("database", "the database, or '*' for everything (admins)"),
            new CommandParameter("server", "host:port, when not your own target's (admins)", Required: false)),
        new("set_serving", true, "The kill switch: 0 sends every request to SQL Server and fills nothing; 1 serves again.",
            "EXEC daffyzoom.set_serving 0", new CommandParameter("on", "1 or 0")),
        new("settings", true, "How this DaffyZoom is configured, and where each setting is changed.", "EXEC daffyzoom.settings"),
        new("log", true, "DaffyZoom's own log: sessions, refused logins, admin commands.", "EXEC daffyzoom.log 50",
            new CommandParameter("count", "how many lines (default 50)", Required: false)),

        new("users", true, "Every DaffyZoom login: admin or not, enabled or not, its target, and when that was last logged in to.",
            "EXEC daffyzoom.users"),
        new("add_user", true,
            "Create a DaffyZoom login and its target. DaffyZoom logs in to the target first, and saves nothing if that fails.",
            "EXEC daffyzoom.add_user 'bob', 'Pa55word!', 'Server=tcp:sql01,1433;Database=Sales;User Id=svc_app;Password=...;TrustServerCertificate=True'",
            LoginName, new CommandParameter("password", "its DaffyZoom password", Secret: true),
            new CommandParameter("target", "the target connection string, its password included", ConnectionString: true)),
        new("set_user_password", true, "Change a login's DaffyZoom password. Its target is unchanged.",
            "EXEC daffyzoom.set_user_password 'bob', '...'", LoginName, new CommandParameter("password", "the new password", Secret: true)),
        new("set_target", true,
            "Change where a login's sessions go. The target's password is kept if the new string has none and names the same login. Logged in to first.",
            "EXEC daffyzoom.set_target 'bob', 'Server=tcp:sql02,1433;Database=Sales;User Id=svc_app;TrustServerCertificate=True'",
            LoginName, new CommandParameter("target", "the new target connection string", ConnectionString: true)),
        new("set_target_password", true,
            "Change the password (or Entra client secret) DaffyZoom logs in to a login's target with. Logged in to first.",
            "EXEC daffyzoom.set_target_password 'bob', '...'", LoginName,
            new CommandParameter("password", "the target's new password", Secret: true)),
        new("test_user", true, "Log in to a login's target now, and say how it went.", "EXEC daffyzoom.test_user 'bob'", LoginName),
        new("enable_user", true, "Let a disabled login log in again.", "EXEC daffyzoom.enable_user 'bob'", LoginName),
        new("disable_user", true, "Stop a login logging in. Its open sessions continue (daffyzoom.kill_session ends them).",
            "EXEC daffyzoom.disable_user 'bob'", LoginName),
        new("set_admin", true, "Make a login an admin (1), or not (0).", "EXEC daffyzoom.set_admin 'alice', 1",
            LoginName, new CommandParameter("admin", "1 or 0")),
        new("remove_user", true, "Remove a login and its target. Its open sessions continue until they disconnect.",
            "EXEC daffyzoom.remove_user 'bob'", LoginName),
    ];

    public static CommandDefinition? Find(string name) =>
        All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Matches arguments to parameters as T-SQL does for a procedure: by position, or by
    /// @name = value, in any mix.
    /// </summary>
    public static BoundArguments Bind(CommandDefinition definition, IReadOnlyList<CommandArgument> arguments)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        int position = 0;

        foreach (var argument in arguments)
        {
            CommandParameter parameter;
            if (argument.Name is { } name)
            {
                parameter = definition.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw Usage(definition, $"daffyzoom.{definition.Name} has no parameter @{name}");
            }
            else
            {
                if (position >= definition.Parameters.Length)
                    throw Usage(definition, $"daffyzoom.{definition.Name} takes {Count(definition.Parameters.Length)}");
                parameter = definition.Parameters[position++];
            }

            if (values.ContainsKey(parameter.Name)) throw Usage(definition, $"@{parameter.Name} is given twice");
            values[parameter.Name] = argument.Value;
        }

        var missing = definition.Parameters.FirstOrDefault(p => p.Required && (!values.TryGetValue(p.Name, out var v) || v is null));
        if (missing is not null) throw Usage(definition, $"daffyzoom.{definition.Name} needs @{missing.Name} ({missing.Description})");

        return new BoundArguments(definition, values);
    }

    private static string Count(int n) => n switch { 0 => "no arguments", 1 => "one argument", _ => $"{n} arguments" };

    internal static GatewayException Usage(CommandDefinition definition, string problem) =>
        new(GatewayErrors.BadCommand, $"{problem}. Usage: {definition.Usage}   Example: {definition.Example}");
}

/// <summary>A command's arguments, matched to its parameters, read as the types the command needs.</summary>
internal sealed class BoundArguments(CommandDefinition definition, Dictionary<string, object?> values)
{
    public string Text(string name) =>
        OptionalText(name) is { Length: > 0 } text ? text : throw CommandCatalog.Usage(definition, $"@{name} cannot be empty");

    public string? OptionalText(string name) => values.GetValueOrDefault(name) switch
    {
        null => null,
        string s => s.Trim(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };

    /// <summary>The value exactly as given, for passwords, where spaces count.</summary>
    public string Secret(string name) =>
        values.GetValueOrDefault(name) is string { Length: > 0 } s ? s : throw CommandCatalog.Usage(definition, $"@{name} cannot be empty");

    public bool Flag(string name, bool defaultValue = false) => values.GetValueOrDefault(name) switch
    {
        null => defaultValue,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        short s => s != 0,
        byte b => b != 0,
        decimal d => d != 0,
        string s when s.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on" => true,
        string s when s.Trim().ToLowerInvariant() is "0" or "false" or "no" or "off" => false,
        var other => throw CommandCatalog.Usage(definition, $"@{name} must be 1 or 0, not '{other}'"),
    };

    public int Number(string name, int? defaultValue = null) => values.GetValueOrDefault(name) switch
    {
        null when defaultValue is { } fallback => fallback,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        int i => i,
        short s => s,
        byte b => b,
        decimal d when d == Math.Truncate(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        var other => throw CommandCatalog.Usage(definition, $"@{name} must be a whole number, not '{other}'"),
    };
}
