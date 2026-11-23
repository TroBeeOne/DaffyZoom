using System.Globalization;
using System.Text.Json.Serialization;

namespace DaffyZoom.Cache;

/// <summary>How a database's cached answers are kept fresh.</summary>
internal enum CacheMode
{
    /// <summary>Nothing is cached. The default for every database.</summary>
    Off,

    /// <summary>The admin says the tables change only through loads that can be signalled: max age, flushes, blackouts.</summary>
    Ttl,

    /// <summary>Change Tracking, checked on every hit (phase 3).</summary>
    Tracked,
}

/// <summary>
/// One database's cache policy, kept in the encrypted store beside the users so the command line,
/// the prompt and every running DaffyZoom see the same one. A database is named by the server its
/// mappings point at - host:port, as in the mapping - and its name exactly as SQL Server gives it
/// (case matters on a case-sensitive server).
/// </summary>
internal sealed class DatabasePolicy
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public CacheMode Mode { get; set; }

    /// <summary>The longest any answer is kept, whatever else happens. Every mode has one.</summary>
    public int MaxAgeMinutes { get; set; } = 240;

    /// <summary>An answer not served for this long is let go. 0: no idle expiry.</summary>
    public int IdleMinutes { get; set; } = 60;

    /// <summary>A query whose answer changes when a load finishes; a change flushes the database.</summary>
    public string? WatermarkSql { get; set; }

    /// <summary>The DaffyZoom login whose mapping the watermark query runs as.</summary>
    public string? WatermarkLogin { get; set; }

    public int WatermarkSeconds { get; set; } = 60;

    /// <summary>Times of day (HH:mm, this machine's local time) to flush at.</summary>
    public List<string> FlushAt { get; set; } = [];

    /// <summary>Windows of the day (HH:mm-HH:mm, local) when nothing is served or filled - while a load runs.</summary>
    public List<string> Blackouts { get; set; } = [];

    /// <summary>Stored procedures whose answers may be cached: the admin's word that they only read.</summary>
    public List<string> Procedures { get; set; } = [];

    public DateTime UpdatedUtc { get; set; }

    [JsonIgnore]
    public string Id => $"{Server}/{Database}";

    public DatabasePolicy Copy()
    {
        var copy = (DatabasePolicy)MemberwiseClone();
        copy.FlushAt = [.. FlushAt];
        copy.Blackouts = [.. Blackouts];
        copy.Procedures = [.. Procedures];
        return copy;
    }

    /// <summary>
    /// Whether two policies cache in the same way: every setting alike, as typed. The database's
    /// spelling and when the policy was saved are not settings. A setting added to this class must be
    /// added here too (a test checks every property).
    /// </summary>
    public bool SameSettings(DatabasePolicy other) =>
        Mode == other.Mode && MaxAgeMinutes == other.MaxAgeMinutes && IdleMinutes == other.IdleMinutes
        && WatermarkSql == other.WatermarkSql && WatermarkLogin == other.WatermarkLogin && WatermarkSeconds == other.WatermarkSeconds
        && FlushAt.SequenceEqual(other.FlushAt) && Blackouts.SequenceEqual(other.Blackouts) && Procedures.SequenceEqual(other.Procedures);

    [JsonIgnore]
    public TimeSpan MaxAge => TimeSpan.FromMinutes(MaxAgeMinutes);

    [JsonIgnore]
    public TimeSpan? Idle => IdleMinutes > 0 ? TimeSpan.FromMinutes(IdleMinutes) : null;

    public bool InBlackout(DateTime local)
    {
        var now = TimeOnly.FromDateTime(local);
        foreach (string window in Blackouts)
        {
            if (!TryParseWindow(window, out var from, out var to)) continue;
            bool inside = from <= to ? now >= from && now < to : now >= from || now < to;   // a window may cross midnight
            if (inside) return true;
        }
        return false;
    }

    /// <summary>Whether a procedure is allowed: named as in the policy, with or without its schema.</summary>
    public bool AllowsProcedure(string name)
    {
        string bare = Strip(name);
        return Procedures.Any(p => string.Equals(Strip(p), bare, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(p.Replace("[", "").Replace("]", ""), name.Replace("[", "").Replace("]", ""),
                                                 StringComparison.OrdinalIgnoreCase));

        static string Strip(string n)
        {
            string plain = n.Replace("[", "").Replace("]", "");
            return plain.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase) ? plain[4..] : plain;
        }
    }

    /// <summary>"sql01/Sales", "sql01,1433/Sales" or "sql01:1433/Sales" as (sql01:1433, Sales).</summary>
    public static (string Server, string Database) ParseId(string text)
    {
        int slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1)
            throw new FormatException($"'{text}' is not SERVER/DATABASE - for example sql01,1433/Sales");

        string server = text[..slash].Trim();
        string database = text[(slash + 1)..].Trim();
        string host = server, port = "1433";
        int separator = server.LastIndexOfAny([',', ':']);
        if (separator > 0)
        {
            host = server[..separator];
            port = server[(separator + 1)..];
            if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) || p is <= 0 or > 65535)
                throw new FormatException($"'{server}': the port is not a valid port number");
        }
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return ($"{host.ToLowerInvariant()}:{port}", database);
    }

    /// <summary>
    /// Whether two names are the same database. In any case: a session's database is spelled as its
    /// client spelled it (a login's Database=, a USE), and SQL Server takes any case for a name under
    /// the case-insensitive collations nearly every server has. On a case-sensitive server two
    /// databases differing only in case count as one: they share a policy and each other's flushes,
    /// which can cost a flush too many, never one too few. Cached answers stay apart: the cache key
    /// holds the database as spelled.
    /// </summary>
    public static bool SameDatabase(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A database as `daffyzoom db set` takes it: sql01/Sales, or sql01,14333/Sales off the default port.</summary>
    public static string CommandId(string server, string database)
    {
        string id = server.EndsWith(":1433", StringComparison.Ordinal) ? $"{server[..^5]}/{database}"
                  : server.LastIndexOf(':') is var colon and > 0 ? $"{server[..colon]},{server[(colon + 1)..]}/{database}"
                  : $"{server}/{database}";
        return id.Contains(' ') ? $"\"{id}\"" : id;
    }

    public static bool TryParseWindow(string text, out TimeOnly from, out TimeOnly to)
    {
        from = to = default;
        var parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && TryParseTime(parts[0], out from) && TryParseTime(parts[1], out to);
    }

    public static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>"4h", "90m", "45s", "2d", or plain minutes.</summary>
    /// <summary>A duration in whole minutes, rounded up: 4h, 90m, 2d, 30s; a bare number is minutes.</summary>
    public static int ParseMinutes(string text) => (int)Math.Ceiling(Seconds(text, bareUnit: 60) / 60.0);

    /// <summary>A duration in whole seconds, rounded up: 30s, 5m, 1h; a bare number is seconds.</summary>
    public static int ParseSeconds(string text) => (int)Math.Ceiling(Seconds(text, bareUnit: 1));

    private static double Seconds(string text, double bareUnit)
    {
        string value = text.Trim().ToLowerInvariant();
        if (value.Length == 0) throw new FormatException("an empty duration: say 4h, 90m, 2d or 30s");
        double factor = value[^1] switch { 'd' => 86400, 'h' => 3600, 'm' => 60, 's' => 1, _ => bareUnit };
        string number = char.IsLetter(value[^1]) ? value[..^1] : value;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount < 0 || double.IsInfinity(amount))
            throw new FormatException($"'{text}' is not a duration: 4h, 90m, 2d, 30s");
        return amount * factor;
    }

    public string Describe()
    {
        if (Mode == CacheMode.Off) return "off";
        var parts = new List<string> { Mode.ToString().ToLowerInvariant(), $"max age {Duration(MaxAgeMinutes)}" };
        if (IdleMinutes > 0) parts.Add($"idle {Duration(IdleMinutes)}");
        if (WatermarkSql is not null) parts.Add($"watermark every {WatermarkSeconds}s as {WatermarkLogin}");
        if (FlushAt.Count > 0) parts.Add($"flush at {string.Join(", ", FlushAt)}");
        if (Blackouts.Count > 0) parts.Add($"blackout {string.Join(", ", Blackouts)}");
        if (Procedures.Count > 0) parts.Add($"procedures {string.Join(", ", Procedures)}");
        return string.Join("; ", parts);
    }

    public static string Duration(int minutes) =>
        minutes % 1440 == 0 ? $"{minutes / 1440}d" : minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}m";
}

/// <summary>
/// The policies, as the relay asks for them: by server and database, many times a second. A copy of
/// the store's list, refreshed when the store has changed - looked at no more than once a second.
/// </summary>
internal sealed class PolicySet(Func<IReadOnlyList<DatabasePolicy>> load, Func<long> version)
{
    private Dictionary<(string, string), DatabasePolicy> _byId = new(DatabaseId.Comparer);
    private long _loadedVersion = -1;
    private long _lastCheck;

    public DatabasePolicy? For(string server, string? database)
    {
        if (database is null) return null;
        Refresh();
        return Volatile.Read(ref _byId).GetValueOrDefault((server, database));
    }

    public IReadOnlyList<DatabasePolicy> All
    {
        get
        {
            Refresh();
            return Volatile.Read(ref _byId).Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public void Refresh(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastCheck) < 1000) return;
        Interlocked.Exchange(ref _lastCheck, now);

        long current = version();
        if (current == Interlocked.Read(ref _loadedVersion)) return;
        // A store written by 0.1.0 may hold one database twice, in two spellings: the newer decides.
        var map = load().GroupBy(p => (p.Server, p.Database), DatabaseId.Comparer)
                        .ToDictionary(g => g.Key, g => g.MaxBy(p => p.UpdatedUtc)!, DatabaseId.Comparer);
        Volatile.Write(ref _byId, map);
        Interlocked.Exchange(ref _loadedVersion, current);
    }
}

/// <summary>A server and database as one key, both in any case (<see cref="DatabasePolicy.SameDatabase"/>).</summary>
internal sealed class DatabaseId : IEqualityComparer<(string Server, string Database)>
{
    public static readonly DatabaseId Comparer = new();

    public bool Equals((string Server, string Database) x, (string Server, string Database) y) =>
        string.Equals(x.Server, y.Server, StringComparison.OrdinalIgnoreCase) && DatabasePolicy.SameDatabase(x.Database, y.Database);

    public int GetHashCode((string Server, string Database) id) =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(id.Server), StringComparer.OrdinalIgnoreCase.GetHashCode(id.Database));
}
