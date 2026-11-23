using System.Net;
using DaffyZoom.Security;

namespace DaffyZoom;

internal sealed record BannerInfo(
    IPAddress Address, int Port, UserStore Store, string? PublicCertificatePath, string? CertificateProblem,
    bool AllowPlaintext, bool Repl, string? TranscriptDirectory, bool Trace,
    string? LogFile, bool? CertificateTrusted = null, string? LogRetention = null);

/// <summary>
/// What to connect with, printed once the port is bound. As in DaffyDB, every string shown is one
/// that will actually connect as the proxy stands, and whatever is not switched on gets the flag
/// that would switch it on.
/// </summary>
internal static class StartupBanner
{
    private static string Kept(string? retention) =>
        retention is null || retention.StartsWith("off", StringComparison.Ordinal) ? "kept for good: --log-retention off" : $"kept {retention}: --log-retention";

    public static void Print(BannerInfo info)
    {
        string host = info.Address.Equals(IPAddress.Loopback) ? "127.0.0.1"
            : info.Address.Equals(IPAddress.Any) || info.Address.Equals(IPAddress.IPv6Any) ? Dns.GetHostName()
            : info.Address.ToString();
        string endpoint = $"tcp:{host},{info.Port}";

        Console.WriteLine();
        Console.WriteLine($"  DaffyZoom {Program.ToolVersion()} - relays SQL Server sessions to mapped targets, and caches slow repeats.");
        Console.WriteLine();
        Console.WriteLine($"  Listening on {Describe(info.Address)}:{info.Port}" +
                          (info.Address.Equals(IPAddress.Loopback) ? "   (this machine only; --bind 0.0.0.0 to open it up)" : ""));
        Console.WriteLine();

        const string tail = "User Id=<DaffyZoom login>;Password=<its DaffyZoom password>";
        if (info.PublicCertificatePath is not null)
        {
            Console.WriteLine("    ADO.NET, SSMS, sqlcmd");
            Console.WriteLine($"      Server={endpoint};Encrypt=Mandatory;TrustServerCertificate=True;{tail}");
            Console.WriteLine();
            Console.WriteLine("    ADO.NET with Encrypt=Strict, which needs the certificate file");
            Console.WriteLine($"      Server={endpoint};Encrypt=Strict;ServerCertificate={info.PublicCertificatePath};{tail}");
            Console.WriteLine();
            Console.WriteLine("    JDBC, DBeaver");
            Console.WriteLine($"      jdbc:sqlserver://{host}:{info.Port};encrypt=true;trustServerCertificate=true;user=...;password=...");
            Console.WriteLine();

            // Known only on Windows, for the generated certificate. A client that checks the
            // certificate cannot use the strings above until Windows trusts it, so say which. The
            // name has to be one the certificate covers, which a specific address is not.
            if (info.CertificateTrusted is { } trusted)
            {
                string named = info.Address.Equals(IPAddress.Loopback) ? "127.0.0.1" : Dns.GetHostName();
                Console.WriteLine("    Power BI's SQL Server connector, and other clients that check the certificate");
                Console.WriteLine($"      Server=tcp:{named},{info.Port};Encrypt=Mandatory;{tail}");
                Console.WriteLine(trusted
                    ? "      (the certificate is trusted on this machine)"
                    : $"      once the certificate is trusted: {(info.Repl ? ":cert trust" : "daffyzoom cert trust")}");
                Console.WriteLine();
            }
            Console.WriteLine(info.AllowPlaintext
                ? "    Unencrypted clients are also accepted (--allow-plaintext)."
                : "    Encryption is mandatory: Encrypt=Optional clients are upgraded, clients that cannot encrypt are refused.");
        }
        else
        {
            Console.WriteLine($"    Encryption is UNAVAILABLE ({info.CertificateProblem}); plaintext only:");
            Console.WriteLine($"      Server={endpoint};Encrypt=False;{tail}");
        }
        Console.WriteLine();

        var users = info.Store.Users;
        Console.WriteLine($"  Users                : {(users.Count == 0 ? "NONE YET - nobody can log in until you add one" : $"{users.Count} ({users.Count(u => u.Enabled)} enabled)")}");
        Console.WriteLine($"  User store           : {info.Store.FilePath}   (key: {info.Store.KeyDescription})");
        var admins = users.Where(u => u.Admin && u.Enabled).Select(u => u.Login).ToList();
        Console.WriteLine($"  In T-SQL             : EXEC daffyzoom.help   (admins: " +
                          $"{(admins.Count == 0 ? "none yet - daffyzoom user admin NAME on" : string.Join(", ", admins))})");
        var cached = info.Store.Policies.Where(p => p.Mode != Cache.CacheMode.Off).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"  Cached databases     : {(cached.Count == 0 ? "NONE YET - nothing is cached until a database has a policy" : string.Join(", ", cached.Select(p => $"{p.Id} ({p.Mode.ToString().ToLowerInvariant()})")))}");
        Console.WriteLine("  Cache                : :db shows which databases are cached, :watchlist what is slow, :entries what is cached");
        if (info.LogFile is not null)
            Console.WriteLine($"  Log file             : {info.LogFile}   ({Kept(info.LogRetention)})");
        if (info.TranscriptDirectory is not null)
            Console.WriteLine($"  Transcripts          : {info.TranscriptDirectory}   (decoded SQL in the clear; slows sessions; {Kept(info.LogRetention)})");
        if (info.Trace)
            Console.WriteLine("  Trace                : ON - every message is decoded into the log");
        Console.WriteLine();

        if (users.Count == 0)
        {
            Console.WriteLine(info.Repl
                ? "  Add a user now with  :user add NAME  - it asks for a password and the target to map to."
                : "  Add a user with  daffyzoom user add NAME  (in another terminal; this server picks it up).");
            Console.WriteLine();
        }

        if (cached.Count == 0)
        {
            // Made from a login's own target, so it is a line that would work as it stands.
            var target = users.Select(UserAdmin.TryEndpoint).OfType<Proxy.TargetEndpoint>().FirstOrDefault();
            string example = target is null ? "SERVER/DATABASE"
                : Cache.DatabasePolicy.CommandId($"{target.Host}:{target.Port}", target.Database ?? "DATABASE");
            Console.WriteLine(info.Repl
                ? $"  Cache a database with  :db set {example} --mode ttl --max-age 4h"
                : $"  Cache a database with  daffyzoom db set {example} --mode ttl --max-age 4h  (in another terminal; this server picks it up).");
            Console.WriteLine("  SERVER is the SQL Server a login's target is on; :db set alone shows the options.");
            Console.WriteLine();
        }
        var notes = cached.Select(p => Cli.DbCommands.ServerProblem(info.Store, p.Server) is { } problem ? $"  Note: {p.Id}: {problem}" : null)
                          .OfType<string>().ToList();
        if (notes.Count > 0)
        {
            foreach (var note in notes) Console.WriteLine(note);
            Console.WriteLine();
        }

        Console.WriteLine(info.Repl
            ? "  Ctrl-C or :quit to stop. Log lines are held while you type; :log shows them."
            : "  Ctrl-C to stop. Sessions are logged below.");
        Console.WriteLine();
    }

    private static string Describe(IPAddress address) =>
        address.Equals(IPAddress.Any) ? "0.0.0.0 (all IPv4 interfaces)"
        : address.Equals(IPAddress.IPv6Any) ? "[::] (all interfaces)"
        : address.ToString();
}
