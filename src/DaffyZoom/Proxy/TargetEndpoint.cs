using System.Data.Common;

namespace DaffyZoom.Proxy;

/// <summary>How DaffyZoom encrypts its own connection to the target.</summary>
internal enum TargetEncrypt
{
    /// <summary>TDS 7.4: PRELOGIN in the clear, then TLS for the whole session. The default, as in SqlClient 4+.</summary>
    Mandatory,

    /// <summary>No TLS to the target. The target login's password crosses the network only scrambled.</summary>
    Optional,

    /// <summary>TDS 8.0: TLS from the first byte. Needs SQL Server 2022 or later.</summary>
    Strict,
}

/// <summary>How DaffyZoom proves who it is to the target.</summary>
internal enum TargetAuthentication
{
    /// <summary>A SQL login and its password.</summary>
    SqlPassword,

    /// <summary>
    /// Entra ID, as an application: User Id is the application (client) id - optionally
    /// "id@tenant", as sqlcmd writes it - and Password is one of its client secrets. DaffyZoom asks
    /// Entra for an access token and presents that; the secret itself never reaches the target.
    /// </summary>
    EntraServicePrincipal,
}

/// <summary>
/// Where one user's sessions go, and as whom: the target half of a user mapping.
///
/// It is written and stored as an ordinary SQL Server connection string, because that is the form
/// people already have to hand, and parsed with the BCL's DbConnectionStringBuilder rather than
/// SqlClient's - DaffyZoom never opens an ADO.NET connection, so it carries no SqlClient at all.
/// Only the keywords that mean something to a relay are accepted; anything else is refused by
/// name rather than silently ignored.
/// </summary>
internal sealed record TargetEndpoint(
    string Host,
    int Port,
    string UserName,
    string Password,
    string? Database,
    TargetEncrypt Encrypt,
    bool TrustServerCertificate,
    string? HostNameInCertificate,
    int ConnectTimeoutSeconds,
    TargetAuthentication Authentication = TargetAuthentication.SqlPassword)
{
    public const int DefaultPort = 1433;
    public const int DefaultConnectTimeout = 15;

    private static readonly string[] ServerKeys = ["server", "data source", "address", "addr", "network address"];
    private static readonly string[] UserKeys = ["user id", "uid", "user"];
    private static readonly string[] PasswordKeys = ["password", "pwd"];
    private static readonly string[] DatabaseKeys = ["initial catalog", "database"];
    private static readonly string[] EncryptKeys = ["encrypt"];
    private static readonly string[] TrustKeys = ["trustservercertificate", "trust server certificate"];
    private static readonly string[] HostNameKeys = ["hostnameincertificate", "host name in certificate"];
    private static readonly string[] TimeoutKeys = ["connect timeout", "connection timeout", "timeout"];

    /// <summary>Keywords accepted but meaningless for a relay, because the client already chose them.</summary>
    private static readonly string[] IgnoredKeys =
        ["pooling", "min pool size", "max pool size", "multipleactiveresultsets", "multiple active result sets",
         "application name", "app", "workstation id", "wsid", "persist security info", "persistsecurityinfo",
         "trusted_connection", "integrated security", "authentication", "packet size", "command timeout",
         "applicationintent", "application intent", "multisubnetfailover", "multi subnet failover",
         "connectretrycount", "connect retry count", "connectretryinterval", "connect retry interval"];

    public static TargetEndpoint Parse(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var values = builder.Keys.Cast<string>()
            .ToDictionary(k => k.ToLowerInvariant(), k => builder[k]?.ToString() ?? "");

        string? Get(string[] keys) => keys.Select(k => values.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);

        string? integrated = values.GetValueOrDefault("integrated security") ?? values.GetValueOrDefault("trusted_connection");
        if (integrated is not null && IsTrue(integrated))
            throw new FormatException("Integrated Security is not supported: DaffyZoom logs in to the target with a SQL login and password");

        var authentication = ParseAuthentication(values.GetValueOrDefault("authentication"));

        var known = ServerKeys.Concat(UserKeys).Concat(PasswordKeys).Concat(DatabaseKeys).Concat(EncryptKeys)
            .Concat(TrustKeys).Concat(HostNameKeys).Concat(TimeoutKeys).Concat(IgnoredKeys).ToHashSet();
        var unknown = values.Keys.Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new FormatException($"unsupported connection string keyword(s): {string.Join(", ", unknown)}");

        string server = Get(ServerKeys) ?? throw new FormatException("the target connection string needs Server=host[,port]");
        var (host, port) = ParseServer(server);

        string user = Get(UserKeys) ?? throw new FormatException("the target connection string needs User Id=");
        string password = Get(PasswordKeys) ?? "";

        string? database = Get(DatabaseKeys);
        if (string.IsNullOrWhiteSpace(database)) database = null;

        var encrypt = ParseEncrypt(Get(EncryptKeys));
        bool trust = Get(TrustKeys) is { } t && IsTrue(t);
        string? hostName = Get(HostNameKeys) is { Length: > 0 } h ? h : null;

        int timeout = DefaultConnectTimeout;
        if (Get(TimeoutKeys) is { } timeoutText && (!int.TryParse(timeoutText, out timeout) || timeout <= 0))
            throw new FormatException($"Connect Timeout must be a positive number of seconds, not '{timeoutText}'");

        return new TargetEndpoint(host, port, user, password, database, encrypt, trust, hostName, timeout, authentication);
    }

    private static TargetAuthentication ParseAuthentication(string? value) =>
        value?.Replace(" ", "").ToLowerInvariant() switch
        {
            null or "" or "sqlpassword" => TargetAuthentication.SqlPassword,
            "activedirectoryserviceprincipal" => TargetAuthentication.EntraServicePrincipal,
            _ => throw new FormatException(
                $"Authentication={value} is not supported: SqlPassword (a SQL login) and ActiveDirectoryServicePrincipal " +
                "(an Entra ID application and its client secret) are"),
        };

    public bool IsEntra => Authentication == TargetAuthentication.EntraServicePrincipal;

    /// <summary>For an Entra application: its client id, without any "@tenant".</summary>
    public string ClientId => IsEntra && UserName.IndexOf('@') is > 0 and var at ? UserName[..at] : UserName;

    /// <summary>
    /// For an Entra application written "id@tenant": the tenant - used only when the target does
    /// not name its own, which Azure SQL always does.
    /// </summary>
    public string? TenantId => IsEntra && UserName.IndexOf('@') is > 0 and var at && at < UserName.Length - 1 ? UserName[(at + 1)..] : null;

    /// <summary>What to call the secret when asking for it.</summary>
    public string SecretName => IsEntra ? $"client secret for Entra application {ClientId}" : $"password for target login {UserName}";

    /// <summary>
    /// Accepts host, host,port, tcp:host,port and the local shorthands. A named instance needs the
    /// SQL Browser service to find its port, which is not implemented yet, so it is refused with the
    /// fix spelled out rather than failing later as a timeout.
    /// </summary>
    public static (string Host, int Port) ParseServer(string server)
    {
        string value = server.Trim();
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (value.StartsWith("np:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"'{server}': only TCP targets are supported");

        int port = DefaultPort;
        int comma = value.LastIndexOf(',');
        if (comma >= 0)
        {
            if (!int.TryParse(value[(comma + 1)..].Trim(), out port) || port is <= 0 or > 65535)
                throw new FormatException($"'{server}': the port after the comma is not a valid port number");
            value = value[..comma].Trim();
        }

        if (value.Contains('\\'))
            throw new FormatException($"'{server}': named instances are not supported yet; give the instance's TCP port instead, as host,port");

        if (value is "." or "(local)" or "(localdb)") value = "localhost";
        if (value.Length == 0) throw new FormatException("the target server name is empty");

        return (value, port);
    }

    private static TargetEncrypt ParseEncrypt(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "true" or "yes" or "mandatory" => TargetEncrypt.Mandatory,
        "false" or "no" or "optional" => TargetEncrypt.Optional,
        "strict" => TargetEncrypt.Strict,
        _ => throw new FormatException($"Encrypt={value} is not recognised (Mandatory, Optional or Strict)"),
    };

    private static bool IsTrue(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "sspi" or "1";

    /// <summary>The canonical form that is stored. Contains the password.</summary>
    public string ToConnectionString()
    {
        var builder = new DbConnectionStringBuilder
        {
            ["Server"] = Port == DefaultPort ? $"tcp:{Host}" : $"tcp:{Host},{Port}",
            ["User Id"] = UserName,
            ["Password"] = Password,
        };
        if (Database is not null) builder["Initial Catalog"] = Database;
        if (IsEntra) builder["Authentication"] = "ActiveDirectoryServicePrincipal";
        builder["Encrypt"] = Encrypt.ToString();
        if (TrustServerCertificate) builder["TrustServerCertificate"] = "True";
        if (HostNameInCertificate is not null) builder["HostNameInCertificate"] = HostNameInCertificate;
        if (ConnectTimeoutSeconds != DefaultConnectTimeout) builder["Connect Timeout"] = ConnectTimeoutSeconds.ToString();
        return builder.ConnectionString;
    }

    /// <summary>Safe to show and log: who, where, how - never the password.</summary>
    public string Describe() =>
        $"{UserName}@{Host}:{Port}{(Database is null ? "" : "/" + Database)}";

    public string DescribeSecurity() =>
        Encrypt switch
        {
            TargetEncrypt.Optional => "unencrypted",
            TargetEncrypt.Strict => "Strict (TDS 8.0)",
            _ => "Mandatory (TDS 7.4)",
        } + (Encrypt != TargetEncrypt.Optional && TrustServerCertificate ? ", certificate trusted without validation" : "")
          + (IsEntra ? ", Entra ID application" : "");

    /// <summary>Records print every member, and this one has a password in it.</summary>
    public override string ToString() => Describe();
}
