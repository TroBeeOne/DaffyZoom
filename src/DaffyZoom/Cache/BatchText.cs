using System.Runtime.InteropServices;

namespace DaffyZoom.Cache;

/// <summary>One SET option and what follows it, normalised: "NOCOUNT" = "ON".</summary>
internal readonly record struct SettingAssignment(string Option, string Value);

/// <summary>What a request does to its session, as far as the cache is concerned.</summary>
/// <param name="Use">It may change the database (a top-level USE), so its answer has to be read for where it went.</param>
/// <param name="Assignments">The SET options it sets, in order.</param>
/// <param name="Unclear">A SET whose value could not be read plainly (a comment in the way): the whole batch then counts.</param>
/// <param name="Taint">Something after which the session must not be served from the cache, until it is reset.</param>
/// <param name="QuotedIdentifier">The QUOTED_IDENTIFIER setting it leaves, when it sets one.</param>
/// <param name="Writes">It may write: in a ttl database, that flushes the cache.</param>
internal readonly record struct SessionEffects(
    bool Use, IReadOnlyList<SettingAssignment> Assignments, bool Unclear, string? Taint, bool? QuotedIdentifier, bool Writes = false)
{
    /// <summary>It sets session options, so later requests see a different settings hash.</summary>
    public bool ChangesSettings => Assignments.Count > 0 || Unclear;
}

/// <summary>
/// A quick look at a request's text, on the relay, for what it does to the session. It reads words,
/// not T-SQL, with one piece of T-SQL's grammar: comments are blanked out first, since they never
/// run (<see cref="WithoutComments"/>). String literals are not skipped - dynamic SQL in one runs -
/// and neither are identifiers. Either way it errs only one way: a word in a string or a column name
/// can make DaffyZoom think the session changed, or the database was written, when it was not, which
/// costs a cache hit, never a wrong answer.
///
/// Only top-level statements of a batch outlast it. SET options, USE and EXECUTE AS inside a stored
/// procedure or dynamic SQL revert when it ends. Session context, application roles and CONTEXT_INFO
/// do not, wherever they are set.
/// </summary>
internal static class BatchText
{
    public static SessionEffects Scan(ReadOnlySpan<char> text)
    {
        if (text.IndexOfAny('-', '/') < 0) return ScanCode(text);

        char[] rented = System.Buffers.ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            var code = rented.AsSpan(0, text.Length);
            WithoutComments(text, code);
            return ScanCode(code);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The text as SQL Server runs it: every comment - <c>-- to the end of the line</c> and
    /// <c>/* ... */</c>, which nest in T-SQL - written over with spaces, so offsets and the tokens on
    /// either side of a comment stay apart, as SQL Server keeps them. String literals ('...', N'...'),
    /// "quoted" text and [bracketed] identifiers are kept as they are, and followed to their end, so
    /// that a -- or /* inside one is not taken for a comment and code after it hidden. Unterminated,
    /// either runs to the end of the text: SQL Server would refuse such a batch whole.
    /// </summary>
    internal static void WithoutComments(ReadOnlySpan<char> text, Span<char> code)
    {
        text.CopyTo(code);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '\'' or '"' or '[')
            {
                i = AfterQuoted(text, i, c == '[' ? ']' : c);
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                int end = text[i..].IndexOfAny('\r', '\n');
                end = end < 0 ? text.Length : i + end;
                code[i..end].Fill(' ');
                i = end;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = AfterBlockComment(text, i);
                code[i..end].Fill(' ');
                i = end;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>Just past the quote that closes the one at <paramref name="start"/>; a doubled closing quote is one inside it.</summary>
    private static int AfterQuoted(ReadOnlySpan<char> text, int start, char close)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] != close) { i++; continue; }
            if (i + 1 < text.Length && text[i + 1] == close) { i += 2; continue; }
            return i + 1;
        }
        return text.Length;
    }

    /// <summary>Just past the */ that closes the /* at <paramref name="start"/>, counting the ones nested in it.</summary>
    private static int AfterBlockComment(ReadOnlySpan<char> text, int start)
    {
        int depth = 0, i = start;
        while (i + 1 < text.Length)
        {
            if (text[i] == '/' && text[i + 1] == '*') { depth++; i += 2; }
            else if (text[i] == '*' && text[i + 1] == '/') { i += 2; if (--depth == 0) return i; }
            else i++;
        }
        return text.Length;
    }

    private static SessionEffects ScanCode(ReadOnlySpan<char> text)
    {
        bool use = false, unclear = false, writes = false;
        bool? quoted = null;
        string? taint = null;
        var assignments = new List<SettingAssignment>();

        int position = 0;
        while (NextWord(text, ref position) is (var start, var length) and (_, > 0))
        {
            var word = text.Slice(start, length);

            if (IsWriteWord(word)) writes = true;

            if (Is(word, "USE"))
            {
                use = true;
            }
            else if (Is(word, "SET"))
            {
                int after = position;
                SkipSpace(text, ref after);
                if (after < text.Length && text[after] == '@') continue;   // SET @variable = ...: not a session option

                var option = WordAt(text, ref after);
                int optionEnd = after;
                var next = WordAt(text, ref after);
                var nextButOne = WordAt(text, ref after);
                bool on = Is(next, "ON") || Is(nextButOne, "ON");

                if (Is(option, "QUOTED_IDENTIFIER") || Is(option, "ANSI_DEFAULTS"))
                {
                    if (Is(next, "ON")) quoted = true;
                    else if (Is(next, "OFF")) quoted = false;
                    assignments.Add(new SettingAssignment(option.ToString().ToUpperInvariant(), ValueAfter(text, optionEnd, out bool hidden)));
                    unclear |= hidden;
                }
                else if (Is(option, "IMPLICIT_TRANSACTIONS") && on)
                {
                    taint ??= "SET IMPLICIT_TRANSACTIONS ON";
                }
                else if ((Is(option, "FMTONLY") || Is(option, "NOEXEC") || Is(option, "PARSEONLY") || Is(option, "STATISTICS")
                          || option.StartsWith("SHOWPLAN", StringComparison.OrdinalIgnoreCase)) && on)
                {
                    taint ??= $"SET {option.ToString().ToUpperInvariant()} ON";
                }
                else if (Is(option, "CONTEXT_INFO"))
                {
                    taint ??= "SET CONTEXT_INFO";
                }
                else
                {
                    assignments.Add(new SettingAssignment(option.ToString().ToUpperInvariant(), ValueAfter(text, optionEnd, out bool hidden)));
                    unclear |= hidden;
                }
            }
            else if (Is(word, "SETUSER"))
            {
                taint ??= "SETUSER";
            }
            else if (Is(word, "EXECUTE") || Is(word, "EXEC"))
            {
                int after = position;
                if (Is(WordAt(text, ref after), "AS")) taint ??= "EXECUTE AS";
            }
            else if (Is(word, "sp_setapprole"))
            {
                taint ??= "sp_setapprole";
            }
            else if (Is(word, "sp_set_session_context"))
            {
                taint ??= "sp_set_session_context";
            }
        }

        return new SessionEffects(use, assignments, unclear, taint, quoted, writes);
    }

    /// <summary>
    /// What follows a SET option, as its value: up to the next semicolon, and no further than 64
    /// characters, upper case, spacing collapsed. Past the value it may take in some of the next
    /// statement too, which only makes two otherwise equal sessions look different - never two
    /// different ones alike. A comment in that stretch could hide the value itself, so it makes the
    /// SET <paramref name="unclear"/>.
    /// </summary>
    private static string ValueAfter(ReadOnlySpan<char> text, int start, out bool unclear)
    {
        var window = text[start..Math.Min(text.Length, start + 64)];
        int semicolon = window.IndexOf(';');
        if (semicolon >= 0) window = window[..semicolon];
        unclear = window.Contains("/*", StringComparison.Ordinal) || window.Contains("--", StringComparison.Ordinal);
        return string.Join(' ', window.ToString().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The words a write cannot be made without - DML, DDL, SELECT ... INTO, bulk loads. A word in a
    /// comment, a string or a column name counts too: a needless flush costs hits, never a stale answer.
    /// </summary>
    private static readonly string[] WriteWords =
        ["INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "CREATE", "ALTER", "DROP", "INTO", "BULK", "WRITETEXT", "UPDATETEXT"];

    private static bool IsWriteWord(ReadOnlySpan<char> word)
    {
        foreach (string write in WriteWords)
            if (word.Equals(write, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Whether an RPC may write: its text - an sp_executesql statement, mostly - mentions a write word. Procedures by name are not seen into.</summary>
    public static bool RpcWrites(ReadOnlySpan<byte> payload)
    {
        for (int shift = 0; shift <= 1 && payload.Length > shift + 1; shift++)
        {
            var chars = MemoryMarshal.Cast<byte, char>(payload[shift..][..((payload.Length - shift) & ~1)]);
            int position = 0;
            while (NextWord(chars, ref position) is (var start, var length) and (_, > 0))
                if (IsWriteWord(chars.Slice(start, length))) return true;
        }
        return false;
    }

    /// <summary>
    /// An RPC cannot change SET options, the database or the security context beyond its own call,
    /// but session context, an application role and CONTEXT_INFO outlast it, whether the procedure
    /// called is one of those or something that calls them. The text is UTF-16 at whatever offset its
    /// length prefix leaves it, so both alignments are searched.
    /// </summary>
    public static string? RpcTaint(ReadOnlySpan<byte> payload)
    {
        foreach (string word in RpcTaintWords)
        {
            for (int shift = 0; shift <= 1 && payload.Length > shift + 1; shift++)
            {
                var chars = MemoryMarshal.Cast<byte, char>(payload[shift..][..((payload.Length - shift) & ~1)]);
                if (chars.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return word;
            }
        }
        return null;
    }

    private static readonly string[] RpcTaintWords = ["sp_set_session_context", "sp_setapprole", "context_info"];

    private static bool Is(ReadOnlySpan<char> word, string expected) => word.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>The next word from <paramref name="position"/>, which then points just past it.</summary>
    private static (int Start, int Length) NextWord(ReadOnlySpan<char> text, ref int position)
    {
        while (position < text.Length && !IsWordChar(text[position])) position++;
        int start = position;
        while (position < text.Length && IsWordChar(text[position])) position++;
        return (start, position - start);
    }

    /// <summary>The word right after the whitespace at <paramref name="position"/>, or empty if something else is there.</summary>
    private static ReadOnlySpan<char> WordAt(ReadOnlySpan<char> text, ref int position)
    {
        SkipSpace(text, ref position);
        int start = position;
        while (position < text.Length && IsWordChar(text[position])) position++;
        return text[start..position];
    }

    private static void SkipSpace(ReadOnlySpan<char> text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
    }
}
