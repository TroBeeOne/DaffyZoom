using System.Reflection;
using DaffyZoom.Cli;
using DaffyZoom.Security;

namespace DaffyZoom;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // varchar parameters arrive in their collation's code page (1252 and friends), which .NET
        // Core only knows once the code-page provider is registered.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // --config-dir applies to every command, so it is taken out before anything else looks.
        var rest = new List<string>(args);
        int configIndex = rest.FindIndex(a => a == "--config-dir");
        if (configIndex >= 0 && configIndex + 1 < rest.Count)
        {
            AppPaths.Home = rest[configIndex + 1];
            rest.RemoveRange(configIndex, 2);
        }

        try
        {
            string first = rest.Count > 0 ? rest[0] : "";
            switch (first)
            {
                case "--help" or "-h" or "help":
                    PrintUsage();
                    return 0;

                case "--version" or "-v":
                    Console.WriteLine($"daffyzoom {ToolVersion()}");
                    Console.WriteLine($"dotnet   {Environment.Version}");
                    return 0;

                case "user" or "users":
                {
                    var store = UserStore.Open(AppPaths.EnsureHome());
                    var commands = new UserCommands(store, Console.Out, Prompter.ForConsole(Console.Out));
                    return await commands.RunAsync(rest.Skip(1).ToList(), cts.Token);
                }

                case "db" or "database" or "databases":
                    return DbCommands.Run(UserStore.Open(AppPaths.EnsureHome()), rest.Skip(1).ToList(), Console.Out);

                case "cert":
                    return new CertCommands(Console.Out).Run(rest.Skip(1).ToList());

                // DaffyDB's spelling, so the habit carries over.
                case "--trust-cert" or "--untrust-cert":
                    return new CertCommands(Console.Out).Run([first == "--trust-cert" ? "trust" : "untrust", .. rest.Skip(1)]);

                case "serve":
                    return await Serve.RunAsync(rest.Skip(1).ToList(), cts);

                default:
                    return await Serve.RunAsync(rest, cts);
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
        finally
        {
            Log.Flush();    // the log is written on threads of its own, which end with the process
        }
    }

    public static string ToolVersion()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            daffyzoom {ToolVersion()} - a result cache for SQL Server and Azure SQL, in front of the database as a gateway

              daffyzoom [serve] [options]
                  Listen for SQL Server clients (port 1433 on 127.0.0.1 by default) and relay
                  each session to the target SQL Server its login is mapped to.

            {Serve.Usage}
              daffyzoom user ...
                  Manage who can log in and where they go.

            {UserCommands.Usage}
              daffyzoom db ...
                  Which databases are cached, and how.

            {DbCommands.Usage}
              daffyzoom cert ...
                  The certificate clients see, and whether Windows trusts it.

            {CertCommands.Usage}
              Common:
                --config-dir DIR    keep the user store, key and certificate in DIR
                                    (default {AppPaths.Home})
                --version | --help
            """);
    }
}
