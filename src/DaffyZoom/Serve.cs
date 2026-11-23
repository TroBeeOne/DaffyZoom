using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using DaffyZoom.Cache;
using DaffyZoom.Cli;
using DaffyZoom.Commands;
using DaffyZoom.Proxy;
using DaffyZoom.Repl;
using DaffyZoom.Security;
using DaffyZoom.Tds;

namespace DaffyZoom;

/// <summary>`daffyzoom [serve]`: the gateway itself.</summary>
internal static class Serve
{
    private const int DefaultPort = 1433;

    public static string Usage => """
          Listening:
            --port N               listen on N (default 1433)
            --bind ADDRESS         127.0.0.1 (default), 0.0.0.0 / any for every IPv4 interface,
                                   :: for IPv4 and IPv6, or one specific address
            --allow-plaintext      also accept clients that will not encrypt (refused by default)
            --cert-file FILE.pfx   present this certificate instead of the generated self-signed one
            --cert-password PW     its password
            --regen-cert           replace the generated certificate
            --machine-scope        (Windows) protect a NEW store's key for the whole machine, for a
                                   service running under another account

          Cache (databases are cached only once given a policy: daffyzoom db set SERVER/DB --mode ttl):
            --slow-ms N            a request at least this slow is considered (default 5000)
            --min-runs N           the slow run of an exact request that fills the cache: 2 means
                                   the second fills and the third is served (default 2, at least 2)
            --hit-deadline-ms N    the most a hit may take before its first byte; past it the
                                   request goes to SQL Server (default 2000; never more than 10%
                                   of what the query took)
            --cache-dir DIR        where cached answers are kept, encrypted (default: cache under the
                                   config directory). Emptied at every start
            --cache-max-gb N       the cache's size on disk; the least useful entries go first (default 10)
            --cache-min-free-gb N  what the cache always leaves free on its disk, whatever else fills it
                                   (default: the larger of 1 GB and 5% of the disk)
            --max-entry-mb N       the largest answer that is cached (default 256)
            --fill-budget-mb N     memory for answers being filled, all together (default 256)
            --memory-cache-mb N    memory for the answers being hit, kept decrypted and shared by every
                                   hit on them (default: the smaller of 1024 and an eighth of memory; 0 off)
            --check-connections N  tracked mode: DaffyZoom's own connections to each target, at most,
                                   for the check on every hit (default 8)

          Operation:
            --log-file FILE            also write the operational log as JSON lines ({date} rolls daily;
                                       quote the name in PowerShell: "daffyzoom-{date}.log")
            --log-retention 24h        delete DaffyZoom's log files and transcripts once nothing has been
                                       written to them for this long; a log file without {date} is moved
                                       to NAME.old and started afresh instead (default 24h; 7d, 90m; off)
            --no-repl | --repl         no prompt, just listen (automatic when output is redirected)
            --quiet                    log startup and warnings, not every session
            --trace                    print every decoded TDS message, both directions (slow; shows SQL
                                       exactly as sent, passwords included)
            --transcript DIR           one decoded transcript file per session (slow; shows SQL exactly
                                       as sent, passwords included)
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationTokenSource cts)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("try: daffyzoom --help");
            return 1;
        }

        bool repl = options.Repl ?? (!Console.IsOutputRedirected && !Console.IsInputRedirected);
        Log.Quiet = options.Quiet;
        RollingFileWriter? logFile = options.LogFile is null ? null : LogFile.Attach(options.LogFile);
        var janitor = LogJanitor.RunAsync(options.LogRetention, logFile, options.TranscriptDirectory, cts.Token);

        var store = UserStore.Open(AppPaths.EnsureHome(), options.MachineScope ? KeyScope.LocalMachine : KeyScope.CurrentUser);

        // Encryption is mandatory unless --allow-plaintext says otherwise, so a certificate is not
        // optional either: without one no client could connect, and failing now beats failing every login.
        X509Certificate2? certificate = null;
        string? certificateProblem = null;
        try
        {
            certificate = ServerCertificate.Load(options.CertificatePath, options.CertificatePassword, options.RegenerateCertificate);
        }
        catch (Exception ex)
        {
            certificateProblem = $"{ex.GetType().Name}: {ex.Message}";
            if (!options.AllowPlaintext)
            {
                Console.Error.WriteLine($"fatal: encryption is mandatory and no certificate could be loaded: {certificateProblem}");
                Console.Error.WriteLine("       fix the certificate (--cert-file / --regen-cert), or run with --allow-plaintext");
                return 2;
            }
        }

        var sessions = new SessionRegistry();
        var context = new ProxyContext
        {
            Users = store,
            Sessions = sessions,
            Certificate = certificate,
            AllowPlaintext = options.AllowPlaintext,
            Trace = options.Trace,
            TranscriptDirectory = options.TranscriptDirectory,
        };
        var cacheOptions = new CacheOptions
        {
            SlowMs = options.SlowMs, MinRuns = options.MinRuns, HitDeadlineMs = options.HitDeadlineMs,
            CacheMaxBytes = options.CacheMaxGb * 1024L * 1024 * 1024, MaxEntryBytes = options.MaxEntryMb * 1024L * 1024,
            FillBudgetBytes = options.FillBudgetMb * 1024L * 1024,
            CacheMinFreeBytes = options.CacheMinFreeGb * 1024L * 1024 * 1024,
            MemoryCacheBytes = options.MemoryCacheMb * 1024L * 1024,
            CheckConnections = options.CheckConnections,
        };
        await using var cache = new ZoomCache(cacheOptions, new PolicySet(() => store.Policies, () => store.Version),
            options.CacheDirectory ?? Path.Combine(AppPaths.Home, "cache"),
            login => store.Find(login) is { Enabled: true } user ? UserAdmin.TryEndpoint(user) : null);
        context.ObserverFactory = cache.ObserversFor;
        cache.Start();

        context.Commands = new CommandRunner(context, () => Settings(options, store, certificate, logFile?.Template, cache), cache: cache);

        var listener = new ProxyListener(options.Address, options.Port, context);
        var listening = new TaskCompletionSource();
        var serving = listener.RunAsync(cts.Token, () => listening.TrySetResult());

        await Task.WhenAny(listening.Task, serving);
        if (serving.IsFaulted) await serving;   // port taken, bad address: surface it as itself

        ReplLoop.Extensions.AddRange(cache.PromptCommands());
        ReplLoop.Extensions.Add(DbCommands.PromptCommand(store, cache));
        ReplLoop.Extensions.Add(new ReplCommand([":cert"], ":cert [trust|untrust]",
            "the certificate clients see, and whether Windows trusts it",
            (args, output) =>
            {
                new CertCommands(output, certificate, options.CertificatePath).Run(args);
                return Task.CompletedTask;
            }));

        // Whether Windows trusts the certificate is only known, and only matters, for the one
        // DaffyZoom generated: a --cert-file certificate is trusted, or not, by its own issuer.
        bool generated = certificate is not null && options.CertificatePath is null;
        bool? trusted = generated && OperatingSystem.IsWindows() ? CertificateTrust.IsTrusted(certificate!) : null;

        Log.Flush();    // what startup logged comes before the banner, not in the middle of it
        StartupBanner.Print(new BannerInfo(
            options.Address, options.Port, store,
            certificate is null ? null : ServerCertificate.PublicPath(options.CertificatePath),
            certificateProblem, options.AllowPlaintext, repl, options.TranscriptDirectory, options.Trace,
            logFile?.Template, trusted, LogJanitor.Describe(options.LogRetention)));

        // Asked at the prompt, once the listener is already up, and only there: a copy started
        // where nobody can answer must not sit waiting for one.
        Action<TextReader, TextWriter>? question = null;
        string certificatePath = ServerCertificate.ResolvePath(null);
        if (generated && CertificateTrust.ShouldOffer(certificate!, certificatePath))
            question = (input, output) => CertificateTrust.Offer(certificate!, certificatePath, input, output);

        try
        {
            if (repl)
            {
                var log = new LogBuffer();
                var console = Console.Out;
                Log.Flush();    // what was logged before the prompt stays on the terminal
                Console.SetOut(log);
                try
                {
                    var loop = new ReplLoop(context, log, console, Console.In, cts, interactive: !Console.IsInputRedirected, question);
                    await loop.RunAsync();
                }
                finally
                {
                    Console.SetOut(console);
                }

                await cts.CancelAsync();
            }

            try { await serving; } catch (OperationCanceledException) { }
        }
        finally
        {
            sessions.AbortAll("DaffyZoom is shutting down");
            Log.Info("proxy.stopped", $"stopped; {sessions.Accepted:N0} session(s) accepted");
            await cts.CancelAsync();
            try { await janitor.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            Log.Flush();
            logFile?.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// daffyzoom.settings: how this DaffyZoom is set up, read when asked. Nothing secret.
    /// </summary>
    private static IReadOnlyList<SettingRow> Settings(Options options, UserStore store, X509Certificate2? certificate, string? logFile,
                                                      ZoomCache cache)
    {
        const string nextStart = "at the next start";
        bool generated = certificate is not null && options.CertificatePath is null;

        var rows = new List<SettingRow>
        {
            new("version", Program.ToolVersion(), "dotnet tool update -g DaffyZoom"),
            new("listening", $"{options.Address}:{options.Port}", $"--port, --bind ({nextStart})"),
            new("client_encryption", options.AllowPlaintext ? "optional (--allow-plaintext)" : "mandatory", $"--allow-plaintext ({nextStart})"),
            new("certificate",
                certificate is null ? "none"
                : $"{(generated ? "generated by DaffyZoom" : options.CertificatePath)}, expires {certificate.NotAfter:yyyy-MM-dd}, thumbprint {certificate.Thumbprint}",
                $"--cert-file, --regen-cert ({nextStart})"),
        };
        if (generated && OperatingSystem.IsWindows())
            rows.Add(new("certificate_trusted", CertificateTrust.IsTrusted(certificate!) ? "yes, on DaffyZoom's machine" : "no",
                         "daffyzoom cert trust | untrust, on DaffyZoom's host"));

        rows.AddRange(
        [
            new("user_store", $"{store.FilePath} ({store.KeyDescription})", $"--config-dir ({nextStart})"),
            new("admins", string.Join(", ", store.Users.Where(u => u.Admin).Select(u => u.Login)),
                "daffyzoom.set_admin, or daffyzoom user admin NAME on|off on the host"),
            new("log_file", logFile ?? "none", $"--log-file ({nextStart})"),
            new("log_retention", LogJanitor.Describe(options.LogRetention), $"--log-retention ({nextStart})"),
            new("trace", options.Trace ? "on" : "off", $"--trace ({nextStart})"),
            new("transcript", options.TranscriptDirectory ?? "off", $"--transcript ({nextStart})"),
            new("serving", cache.Serving ? "on" : "off (kill switch)", "daffyzoom.set_serving, or :serve on|off at the prompt"),
            new("cache_dir", cache.Store.Directory, $"--cache-dir ({nextStart})"),
            new("cache_max_gb", (cache.Options.CacheMaxBytes / 1024 / 1024 / 1024).ToString(CultureInfo.InvariantCulture), $"--cache-max-gb ({nextStart})"),
            new("cache_min_free_gb", cache.Options.CacheMinFreeBytes is { } minFree
                    ? (minFree / 1024 / 1024 / 1024).ToString(CultureInfo.InvariantCulture)
                    : "the larger of 1 GB and 5% of the disk", $"--cache-min-free-gb ({nextStart})"),
            new("cache_size", cache.Space.Describe(), "--cache-max-gb, --cache-min-free-gb"),
            new("max_entry_mb", (cache.Options.MaxEntryBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture), $"--max-entry-mb ({nextStart})"),
            new("memory_cache_mb", (cache.Hot.Capacity / 1024 / 1024).ToString(CultureInfo.InvariantCulture), $"--memory-cache-mb ({nextStart})"),
            new("check_connections", cache.Options.CheckConnections.ToString(CultureInfo.InvariantCulture), $"--check-connections ({nextStart})"),
            new("hit_deadline_ms", cache.Options.HitDeadlineMs.ToString(CultureInfo.InvariantCulture), $"--hit-deadline-ms ({nextStart})"),
            new("databases", string.Join("; ", cache.Policies.All.Where(p => p.Mode != CacheMode.Off).Select(p => $"{p.Id}: {p.Describe()}")),
                "daffyzoom db set SERVER/DB on the host, or :db at the prompt"),
            new("slow_ms", cache.Options.SlowMs.ToString(CultureInfo.InvariantCulture), $"--slow-ms ({nextStart})"),
            new("min_runs", cache.Options.MinRuns.ToString(CultureInfo.InvariantCulture), $"--min-runs ({nextStart})"),
        ]);
        return rows;
    }

    private sealed record Options(
        int Port, IPAddress Address, bool AllowPlaintext, string? CertificatePath, string? CertificatePassword,
        bool RegenerateCertificate, bool MachineScope, bool? Repl, bool Quiet, bool Trace, string? TranscriptDirectory,
        string? LogFile, int SlowMs, int MinRuns, int HitDeadlineMs, string? CacheDirectory, int CacheMaxGb, int MaxEntryMb,
        int FillBudgetMb, int? CacheMinFreeGb, TimeSpan LogRetention, int? MemoryCacheMb, int CheckConnections)
    {
        public static Options? Parse(IReadOnlyList<string> args)
        {
            int port = DefaultPort;
            IPAddress address = IPAddress.Loopback;
            bool allowPlaintext = false, regenerate = false, machineScope = false, quiet = false, trace = false;
            bool? repl = null;
            int slowMs = 5000, minRuns = 2, hitDeadlineMs = 2000, cacheMaxGb = 10, maxEntryMb = 256, fillBudgetMb = 256;
            string? cacheDirectory = null;
            int? cacheMinFreeGb = null, memoryCacheMb = null;
            int checkConnections = 8;
            string? certificatePath = null, certificatePassword = null, transcript = null, logFile = null;
            var logRetention = LogJanitor.DefaultRetention;

            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                bool hasValue = i + 1 < args.Count;
                string Next() => args[++i];

                switch (arg)
                {
                    case "--port" when hasValue:
                        if (!TryInt(Next(), 1, 65535, out port)) return Bad(arg, args[i]);
                        break;
                    case "--bind" when hasValue:
                    {
                        string value = Next();
                        IPAddress? parsed = value.ToLowerInvariant() switch
                        {
                            "any" or "0.0.0.0" or "*" => IPAddress.Any,
                            "::" or "[::]" => IPAddress.IPv6Any,
                            "localhost" => IPAddress.Loopback,
                            _ => IPAddress.TryParse(value, out var ip) ? ip : null,
                        };
                        if (parsed is null) return Bad(arg, value);
                        address = parsed;
                        break;
                    }
                    case "--allow-plaintext": allowPlaintext = true; break;
                    case "--cert-file" when hasValue: certificatePath = Next(); break;
                    case "--cert-password" when hasValue: certificatePassword = Next(); break;
                    case "--regen-cert": regenerate = true; break;
                    case "--machine-scope": machineScope = true; break;
                    case "--repl": repl = true; break;
                    case "--no-repl": repl = false; break;
                    case "--quiet" or "-q": quiet = true; break;
                    case "--trace": trace = true; break;
                    case "--transcript" when hasValue: transcript = Path.GetFullPath(Next()); break;
                    case "--slow-ms" when hasValue:
                        if (!TryInt(Next(), 1, 86_400_000, out slowMs)) return Bad(arg, args[i]);
                        break;
                    case "--min-runs" when hasValue:
                        if (!TryInt(Next(), 2, 100, out minRuns)) return Bad(arg, args[i]);
                        break;
                    case "--hit-deadline-ms" when hasValue:
                        if (!TryInt(Next(), 50, 60_000, out hitDeadlineMs)) return Bad(arg, args[i]);
                        break;
                    case "--cache-dir" when hasValue: cacheDirectory = Path.GetFullPath(Next()); break;
                    case "--cache-max-gb" when hasValue:
                        if (!TryInt(Next(), 1, 100_000, out cacheMaxGb)) return Bad(arg, args[i]);
                        break;
                    case "--cache-min-free-gb" when hasValue:
                        if (!TryInt(Next(), 0, 100_000, out int minFree)) return Bad(arg, args[i]);
                        cacheMinFreeGb = minFree;
                        break;
                    case "--max-entry-mb" when hasValue:
                        if (!TryInt(Next(), 1, 4096, out maxEntryMb)) return Bad(arg, args[i]);
                        break;
                    case "--fill-budget-mb" when hasValue:
                        if (!TryInt(Next(), 16, 64 * 1024, out fillBudgetMb)) return Bad(arg, args[i]);
                        break;
                    case "--memory-cache-mb" when hasValue:
                        if (!TryInt(Next(), 0, 1024 * 1024, out int memory)) return Bad(arg, args[i]);
                        memoryCacheMb = memory;
                        break;
                    case "--check-connections" when hasValue:
                        if (!TryInt(Next(), 1, 256, out checkConnections)) return Bad(arg, args[i]);
                        break;
                    case "--log-file" when hasValue:
                        if ((logFile = FileName(arg, Next())) is null) return null;
                        break;
                    case "--log-retention" when hasValue:
                    {
                        if (LogJanitor.ParseRetention(Next()) is not { } retention)
                        {
                            Console.Error.WriteLine($"{arg}: '{args[i]}' is not a duration: say 24h, 7d or 90m, or off to keep everything");
                            return null;
                        }
                        logRetention = retention;
                        break;
                    }

                    default:
                        Console.Error.WriteLine($"unknown or incomplete argument: {arg}");
                        return null;
                }
            }

            return new Options(port, address, allowPlaintext, certificatePath, certificatePassword, regenerate,
                               machineScope, repl, quiet, trace, transcript, logFile, slowMs, minRuns, hitDeadlineMs,
                               cacheDirectory, cacheMaxGb, maxEntryMb, fillBudgetMb, cacheMinFreeGb, logRetention,
                               memoryCacheMb, checkConnections);
        }

        private static bool TryInt(string text, int min, int max, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

        /// <summary>
        /// PowerShell reads an unquoted {date} as a script block and silently drops it and everything
        /// after it: --log-file zoom-{date}.log arrives here as "zoom-", and {date}.log arrives as
        /// nothing, so the option takes the next option as its value. Refuse the second; warn about
        /// the first, whose only tell is the missing extension.
        /// </summary>
        private static string? FileName(string option, string value)
        {
            string hint = $"  in PowerShell, quote names that contain {{date}}: {option} \"name-{{date}}.log\"";
            if (value.StartsWith('-'))
            {
                Console.Error.WriteLine($"{option} needs a file name, not '{value}'");
                Console.Error.WriteLine(hint);
                return null;
            }

            if (Path.GetExtension(value).Length == 0)
            {
                Console.Error.WriteLine($"warning: {option} '{value}' has no file extension - was part of the name lost?");
                Console.Error.WriteLine(hint);
            }

            return value;
        }

        private static Options? Bad(string option, string value)
        {
            Console.Error.WriteLine($"{option}: '{value}' is not valid");
            return null;
        }
    }
}
