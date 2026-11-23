using System.Text;

namespace DaffyZoom.Cli;

/// <summary>
/// Asks the questions `user add` and friends need answered.
///
/// Secrets are typed, never passed as arguments: a password on the command line lands in shell
/// history and in the process list. At a real console, secret input is masked. When stdin is
/// redirected, each question simply consumes the next line - which is how scripts and tests drive
/// these commands without a terminal - and nothing is echoed.
/// </summary>
internal sealed class Prompter(TextWriter output, TextReader input, bool interactive)
{
    public static Prompter ForConsole(TextWriter output) =>
        new(output, Console.In, interactive: !Console.IsInputRedirected);

    public string Ask(string question, string? defaultValue = null)
    {
        output.Write(defaultValue is null ? $"  {question}: " : $"  {question} [{defaultValue}]: ");
        output.Flush();

        string? line = input.ReadLine();
        if (line is null) throw new EndOfStreamException($"no answer for '{question}'");

        line = line.TrimStart('﻿').Trim();
        if (!interactive) output.WriteLine();
        return line.Length == 0 && defaultValue is not null ? defaultValue : line;
    }

    public string AskSecret(string question)
    {
        output.Write($"  {question}: ");
        output.Flush();

        if (!interactive)
        {
            string? line = input.ReadLine() ?? throw new EndOfStreamException($"no answer for '{question}'");
            output.WriteLine();
            return line.TrimStart('﻿');
        }

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length == 0) continue;
                secret.Length--;
                output.Write("\b \b");
                continue;
            }

            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar)) continue;
            secret.Append(key.KeyChar);
            output.Write('*');
        }

        output.WriteLine();
        return secret.ToString();
    }

    public bool Confirm(string question, bool defaultValue)
    {
        string answer = Ask($"{question} (y/n)", defaultValue ? "y" : "n");
        return answer.StartsWith('y') || answer.StartsWith('Y');
    }

    /// <summary>Asks twice, so a typo cannot lock someone out of a password they never meant to set.</summary>
    public string AskNewPassword(string what)
    {
        while (true)
        {
            string first = AskSecret(what);
            if (first.Length == 0)
            {
                output.WriteLine("  a password is required");
                if (!interactive) throw new ArgumentException("a password is required");
                continue;
            }

            string second = AskSecret("again, to confirm");
            if (first == second) return first;

            output.WriteLine("  those did not match");
            if (!interactive) throw new ArgumentException("the two passwords did not match");
        }
    }
}
