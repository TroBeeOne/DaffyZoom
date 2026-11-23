using DaffyZoom.Cli;
using DaffyZoom.Proxy;

namespace DaffyZoom.Repl;

/// <summary>
/// The prompt attached to a running proxy: what is connected, what the queue is doing, who can log
/// in - and the switches to change it, all without dropping a session.
///
/// Unlike DaffyDB's prompt it runs no SQL; that is what the target is for. Commands start with a
/// colon, as there, so the muscle memory carries over. While it owns the terminal the running log is
/// held in a <see cref="LogBuffer"/>, and :log shows it on demand.
/// </summary>
internal sealed class ReplLoop(
    ProxyContext context,
    LogBuffer log,
    TextWriter output,
    TextReader input,
    CancellationTokenSource shutdown,
    bool interactive,
    Action<TextReader, TextWriter>? question = null)
{
    /// <summary>Extra commands contributed by other parts of DaffyZoom (the cache, the certificate).</summary>
    public static readonly List<ReplCommand> Extensions = [];

    public async Task RunAsync()
    {
        output.WriteLine("  Type :help for commands, :quit to stop.");
        output.WriteLine();

        // Something to settle before the first command, such as whether to trust the certificate.
        // Only asked of someone typing: piped input is a script, and would answer with its first line.
        if (interactive) question?.Invoke(input, output);

        while (!shutdown.IsCancellationRequested)
        {
            output.Write("daffyzoom> ");
            output.Flush();

            string? line = input.ReadLine();
            if (line is null || shutdown.IsCancellationRequested) break;

            // PowerShell pipes a byte order mark ahead of the first line, and U+FEFF is not whitespace.
            line = line.TrimStart('﻿').Trim();
            if (line.Length == 0) continue;

            if (!line.StartsWith(':'))
            {
                output.WriteLine("  commands start with ':' - try :help. (SQL goes to the target through a client, not here.)");
                continue;
            }

            try
            {
                if (await RunCommandAsync(line)) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.WriteLine($"  error: {ex.Message}");
            }
        }
    }

    /// <summary>Returns true when the loop should end.</summary>
    private async Task<bool> RunCommandAsync(string line)
    {
        string[] parts = Tokenize(line);
        string command = parts[0].ToLowerInvariant();
        string[] arguments = parts[1..];

        switch (command)
        {
            case ":quit" or ":q" or ":exit":
                output.WriteLine($"  stopping; {context.Sessions.Active} session(s) will be disconnected.");
                shutdown.Cancel();
                return true;

            case ":help" or ":?":
                WriteHelp();
                return false;

            case ":status":
                WriteStatus();
                return false;

            case ":sessions" or ":s":
                WriteSessions();
                return false;

            case ":kill":
                Kill(arguments);
                return false;

            case ":users":
                await RunUserCommandAsync(["list"]);
                return false;

            case ":user":
                await RunUserCommandAsync(arguments);
                return false;

            case ":log":
                ShowLog(arguments.Length > 0 ? arguments[0] : "");
                return false;
        }

        var extension = Extensions.FirstOrDefault(e => e.Names.Contains(command));
        if (extension is not null)
        {
            await extension.Run(arguments, output);
            return false;
        }

        output.WriteLine($"  unknown command '{command}' - :help for the list");
        return false;
    }

    /// <summary>
    /// Splits a command line on whitespace, keeping "double-quoted text" as one argument, so
    /// :user add NAME --target "Server=...;User Id=..." works as it does on the command line.
    /// </summary>
    internal static string[] Tokenize(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, started = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) parts.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started) parts.Add(current.ToString());
        return [.. parts];
    }

    private void WriteHelp()
    {
        output.WriteLine("""

              :status              listener, sessions, logins, and the cache
              :sessions            who is connected, where to, and how much
              :kill ID             disconnect a session by its id in :sessions (not the SPID)
              :users               the user mappings
              :user add NAME       create a mapping (prompts; validates the target before saving)
              :user test|show|passwd|target|enable|disable|remove NAME
              :user admin NAME on|off   let NAME run the admin commands in T-SQL
              :log [n|follow]      the last n log lines (default 20), or stream them until a key
            """);

        foreach (var extension in Extensions)
            output.WriteLine($"  {extension.Usage.PadRight(20)} {extension.Description}");

        output.WriteLine("""
              :help                this
              :quit                disconnect everyone and stop

              Admin logins can do much of this in T-SQL too, from SSMS or any client: EXEC daffyzoom.help.

            """);
    }

    private void WriteStatus()
    {
        var sessions = context.Sessions;
        var uptime = DateTime.UtcNow - sessions.StartedUtc;

        output.WriteLine();
        output.WriteLine($"  up {(int)uptime.TotalDays}d {uptime:hh\\:mm\\:ss}, since {sessions.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        output.WriteLine($"  sessions : {sessions.Active} active, {sessions.Accepted} accepted in total");
        output.WriteLine($"  logins   : {sessions.LoginsSucceeded} succeeded, {sessions.LoginsFailed} refused " +
                         $"({sessions.UpstreamFailures} because the target could not be reached or refused)");

        foreach (var extension in Extensions) extension.Status?.Invoke(output);
        output.WriteLine();
    }

    private void WriteSessions()
    {
        var list = context.Sessions.Snapshot();
        if (list.Count == 0)
        {
            output.WriteLine("  no sessions");
            return;
        }

        var rows = list.Select(s => new[]
        {
            s.Id.ToString(),
            s.Login ?? "?",
            s.DescribeTarget(),
            s.Spid == 0 ? "" : s.Spid.ToString(),
            s.Database ?? "",
            s.AppName ?? "",
            s.ClientAddress,
            s.Requests.ToString(),
            $"{(s.BytesFromClient + s.BytesFromServer) / 1024.0:F0} KB",
            Ago(s.LastActivityUtc),
        }).ToList();

        TextTable.Write(output, ["id", "login", "target", "spid", "db", "app", "from", "requests", "traffic", "idle"], rows);
    }

    private void Kill(string[] arguments)
    {
        if (arguments.Length != 1 || !int.TryParse(arguments[0], out int id))
        {
            output.WriteLine("  usage: :kill ID   (ids are in :sessions)");
            return;
        }

        var session = context.Sessions.Kill(id, "killed from the console");
        if (session is null)
        {
            output.WriteLine($"  no session {id}");
            return;
        }

        output.WriteLine($"  session {id} ({session.Login}) disconnected");
    }

    private async Task RunUserCommandAsync(string[] arguments)
    {
        var prompter = new Prompter(output, input, interactive);
        var commands = new UserCommands(context.Users, output, prompter);
        await commands.RunAsync(arguments, shutdown.Token);
    }

    private void ShowLog(string argument)
    {
        if (argument.Equals("follow", StringComparison.OrdinalIgnoreCase))
        {
            if (!interactive)
            {
                output.WriteLine("  :log follow needs an interactive console");
                return;
            }

            output.WriteLine("  following the log - press any key to stop");
            log.Follower = line => output.WriteLine("  " + line);
            try { Console.ReadKey(intercept: true); }
            finally { log.Follower = null; }
            output.WriteLine("  stopped following");
            return;
        }

        int count = 20;
        if (argument.Length > 0 && !int.TryParse(argument, out count))
        {
            output.WriteLine("  usage: :log [n|follow]");
            return;
        }

        var lines = log.Tail(count);
        if (lines.Count == 0)
        {
            output.WriteLine("  (log is empty)");
            return;
        }

        output.WriteLine();
        foreach (string line in lines) output.WriteLine("  " + line);
        output.WriteLine($"  {lines.Count} of {log.Count} line(s)");
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalSeconds < 60 ? $"{span.TotalSeconds:F0}s"
             : span.TotalMinutes < 60 ? $"{span.TotalMinutes:F0}m"
             : span.TotalHours < 48 ? $"{span.TotalHours:F0}h"
             : $"{span.TotalDays:F0}d";
    }
}

/// <summary>A prompt command added by another component, so the loop does not need to know about it.</summary>
internal sealed record ReplCommand(
    string[] Names, string Usage, string Description, Func<string[], TextWriter, Task> Run, Action<TextWriter>? Status = null);
