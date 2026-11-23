using DaffyZoom.Proxy;
using DaffyZoom.Security;
using DaffyZoom.Tds;

namespace DaffyZoom.Cli;

/// <summary>
/// `daffyzoom user ...` - creating and maintaining user mappings. The same commands run at the
/// server's prompt as `:user ...`, against the same store, while clients stay connected.
///
/// A mapping is only saved once its target login has actually logged in, through the same
/// connector the relay uses. "Validated" therefore means "the relay will be able to do this", not
/// merely "the connection string parsed". The changes themselves are <see cref="UserAdmin"/>'s,
/// shared with the daffyzoom.* commands in T-SQL; this is the asking and the printing.
/// </summary>
internal sealed class UserCommands(UserStore store, TextWriter output, Prompter prompter)
{
    public static string Usage => """
          daffyzoom user add NAME [--target "CONNECTION STRING"]
              Create a mapping. Prompts for NAME's DaffyZoom password, then for the target
              (or takes it from --target; a password missing from it is prompted for), logs
              in to the target to prove it works, and only then saves. For Azure SQL as an
              Entra ID application: Authentication=ActiveDirectoryServicePrincipal, the
              application (client) id as User Id; its client secret is prompted for.
          daffyzoom user list
          daffyzoom user show NAME
          daffyzoom user test NAME             log in to NAME's target again, now
          daffyzoom user passwd NAME           change NAME's DaffyZoom password
          daffyzoom user target NAME [--target "CONNECTION STRING"]   change and re-validate the target
          daffyzoom user enable NAME | disable NAME
          daffyzoom user admin NAME on|off      let NAME run DaffyZoom's admin commands in T-SQL
                                               (EXEC daffyzoom.help lists them)
          daffyzoom user remove NAME
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Count == 0 ? 1 : 0;
        }

        string command = args[0].ToLowerInvariant();
        string? name = args.Count > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string? target = Option(args, "--target");

        try
        {
            switch (command)
            {
                case "list" or "ls":
                    List();
                    return 0;

                case "add" when name is not null:
                    return await AddAsync(name, target, cancellationToken);

                case "show" when name is not null:
                    return Show(name);

                case "test" when name is not null:
                    return await TestAsync(name, cancellationToken);

                case "passwd" or "password" when name is not null:
                    return ChangePassword(name);

                case "target" when name is not null:
                    return await ChangeTargetAsync(name, target, cancellationToken);

                case "enable" or "disable" when name is not null:
                    return SetEnabled(name, command == "enable");

                case "admin" when name is not null:
                    return SetAdmin(name, args.Count > 2 ? args[2] : null);

                case "remove" or "rm" or "delete" when name is not null:
                    return Remove(name);

                default:
                    output.WriteLine($"  unknown or incomplete command: user {string.Join(' ', args)}");
                    output.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or EndOfStreamException)
        {
            output.WriteLine($"  {ex.Message}");
            return 1;
        }
    }

    private void List()
    {
        var users = store.Users;
        if (users.Count == 0)
        {
            output.WriteLine("  no users yet - add one with: daffyzoom user add NAME");
            return;
        }

        var rows = users.Select(u => new[]
        {
            u.Login,
            u.Enabled ? "yes" : "no",
            u.Admin ? "yes" : "",
            UserAdmin.DescribeTarget(u),
            u.LastValidatedUtc is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never",
            u.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        }).ToList();

        TextTable.Write(output, ["login", "enabled", "admin", "target", "validated", "updated"], rows);
    }

    private int Show(string name)
    {
        var user = Require(name);
        output.WriteLine();
        output.WriteLine($"  login      : {user.Login}{(user.Enabled ? "" : "   (DISABLED)")}");
        output.WriteLine($"  admin      : {(user.Admin ? "yes - may run DaffyZoom's admin commands in T-SQL" : "no")}");
        output.WriteLine($"  target     : {UserAdmin.DescribeTarget(user)}");
        if (UserAdmin.TryEndpoint(user) is { } endpoint)
            output.WriteLine($"  encryption : {endpoint.DescribeSecurity()}");
        output.WriteLine($"  validated  : {(user.LastValidatedUtc is { } v ? $"{v.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {user.LastValidation}" : "never")}");
        output.WriteLine($"  created    : {user.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        output.WriteLine($"  updated    : {user.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        output.WriteLine();
        return 0;
    }

    private async Task<int> AddAsync(string name, string? targetText, CancellationToken cancellationToken)
    {
        UserStore.ValidateLogin(name);
        if (store.Find(name) is not null)
        {
            output.WriteLine($"  '{name}' already exists - use 'user target' or 'user passwd' to change it");
            return 1;
        }

        output.WriteLine($"  new DaffyZoom login '{name}'");
        string password = prompter.AskNewPassword($"DaffyZoom password for {name}");
        WarnIfWeak(password);

        var endpoint = targetText is not null ? CompleteTarget(targetText) : AskTarget(null);

        var validation = await ValidateAsync(endpoint, cancellationToken);
        if (validation is null)
        {
            output.WriteLine($"  '{name}' was NOT saved.");
            return 1;
        }

        UserAdmin.Create(store, name, password, endpoint, validation);
        output.WriteLine($"  saved '{name}' -> {endpoint.Describe()}");
        return 0;
    }

    private async Task<int> TestAsync(string name, CancellationToken cancellationToken)
    {
        var user = Require(name);
        var endpoint = user.Endpoint;

        var validation = await ValidateAsync(endpoint, cancellationToken);
        if (validation is null) return 1;

        UserAdmin.RecordValidation(store, user, validation);
        return 0;
    }

    private int ChangePassword(string name)
    {
        var user = Require(name);
        string password = prompter.AskNewPassword($"new DaffyZoom password for {user.Login}");
        WarnIfWeak(password);

        UserAdmin.SetPassword(store, user, password);
        output.WriteLine($"  DaffyZoom password changed for '{user.Login}'. Its target login is unchanged.");
        return 0;
    }

    private async Task<int> ChangeTargetAsync(string name, string? targetText, CancellationToken cancellationToken)
    {
        var user = Require(name);
        var current = UserAdmin.TryEndpoint(user);
        var endpoint = targetText is not null ? CompleteTarget(targetText, current) : AskTarget(current);

        var validation = await ValidateAsync(endpoint, cancellationToken);
        if (validation is null)
        {
            output.WriteLine($"  '{user.Login}' was NOT changed.");
            return 1;
        }

        UserAdmin.SetTarget(store, user, endpoint, validation);
        output.WriteLine($"  '{user.Login}' now goes to {endpoint.Describe()} (new sessions only)");
        return 0;
    }

    private int SetEnabled(string name, bool enabled)
    {
        var user = Require(name);
        UserAdmin.SetEnabled(store, user, enabled);
        output.WriteLine($"  '{user.Login}' {(enabled ? "enabled" : "disabled - new logins are refused; connected sessions carry on")}");
        return 0;
    }

    private int SetAdmin(string name, string? onOff)
    {
        if (onOff?.ToLowerInvariant() is not ("on" or "off"))
        {
            output.WriteLine("  usage: daffyzoom user admin NAME on|off");
            return 1;
        }

        var user = Require(name);
        bool admin = onOff.Equals("on", StringComparison.OrdinalIgnoreCase);
        UserAdmin.SetAdmin(store, user, admin);
        output.WriteLine(admin
            ? $"  '{user.Login}' is an admin: it may run DaffyZoom's admin commands in T-SQL (EXEC daffyzoom.help lists them)"
            : $"  '{user.Login}' is no longer an admin");
        return 0;
    }

    private int Remove(string name)
    {
        var user = Require(name);
        if (!prompter.Confirm($"remove '{user.Login}' and its target credentials", defaultValue: false))
        {
            output.WriteLine("  not removed");
            return 1;
        }

        store.Remove(user.Login);
        output.WriteLine($"  removed '{user.Login}'");
        return 0;
    }

    /// <summary>
    /// Logs in to the target and reports what answered. Returns a one-line summary to store, or
    /// null after explaining the failure.
    /// </summary>
    public async Task<string?> ValidateAsync(TargetEndpoint endpoint, CancellationToken cancellationToken)
    {
        output.WriteLine($"  validating {endpoint.Describe()} ({endpoint.DescribeSecurity()}) ...");
        var check = await UserAdmin.CheckTargetAsync(endpoint, cancellationToken);
        if (check.Ok)
        {
            output.WriteLine($"  ok: {check.Detail}");
            if (check.RoutedTo is { } routedTo)
                output.WriteLine($"  the target redirected the login to {routedTo}; DaffyZoom follows the redirect itself");
            return check.Summary;
        }

        output.WriteLine($"  FAILED ({check.Failure}): {check.Problem}");
        if (check.Resuming)
            output.WriteLine("  (an Azure SQL database that paused itself says this while it resumes, which takes about a minute: try again)");
        return null;
    }

    /// <summary>
    /// A connection string given with --target. A password missing from it is kept from the current
    /// target if that names the same login, as in T-SQL's daffyzoom.set_target; otherwise it is asked for.
    /// </summary>
    private TargetEndpoint CompleteTarget(string connectionString, TargetEndpoint? current = null)
    {
        var endpoint = UserAdmin.KeepPassword(TargetEndpoint.Parse(connectionString), current);
        if (endpoint.Password.Length == 0)
            endpoint = endpoint with { Password = prompter.AskSecret(endpoint.SecretName) };
        return endpoint;
    }

    private TargetEndpoint AskTarget(TargetEndpoint? current)
    {
        output.WriteLine("  target SQL Server:");
        string server = prompter.Ask("server (host or host,port)", current is null ? null : $"{current.Host},{current.Port}");
        var (host, port) = TargetEndpoint.ParseServer(server);

        var authentication = current?.Authentication ?? TargetAuthentication.SqlPassword;
        bool entra = authentication == TargetAuthentication.EntraServicePrincipal;

        string user = prompter.Ask(entra ? "Entra application (client) id" : "target login", current?.UserName);
        if (user.Length == 0) throw new ArgumentException(entra ? "an application id is required" : "a target login is required");

        string password = prompter.AskSecret(entra ? $"client secret for Entra application {user}" : $"password for target login {user}");
        if (password.Length == 0) throw new ArgumentException(entra ? "the application's client secret is required" : "the target login's password is required");

        string database = prompter.Ask("default database (blank: the login's default)", current?.Database ?? "");
        string encryptText = prompter.Ask("encrypt: Mandatory, Optional or Strict", (current?.Encrypt ?? TargetEncrypt.Mandatory).ToString());
        var encrypt = Enum.TryParse<TargetEncrypt>(encryptText, ignoreCase: true, out var e)
            ? e
            : throw new ArgumentException($"'{encryptText}' is not Mandatory, Optional or Strict");

        bool trust = encrypt != TargetEncrypt.Optional &&
                     prompter.Confirm("trust the server certificate without validating it", current?.TrustServerCertificate ?? false);

        return new TargetEndpoint(host, port, user, password, database.Length == 0 ? null : database,
                                  encrypt, trust, current?.HostNameInCertificate, current?.ConnectTimeoutSeconds ?? TargetEndpoint.DefaultConnectTimeout,
                                  authentication);
    }

    private UserRecord Require(string name) =>
        store.Find(name) ?? throw new ArgumentException($"no user '{name}' - 'daffyzoom user list' shows who exists");

    private void WarnIfWeak(string password)
    {
        if (password.Length < 8)
            output.WriteLine("  note: that password is shorter than 8 characters");
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
