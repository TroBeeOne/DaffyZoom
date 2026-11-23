using System.Globalization;
using System.Text;
using DaffyZoom.Tds;

namespace DaffyZoom.Commands;

/// <summary>One argument as written: <c>'sales'</c>, <c>@connection = 'sales'</c>, <c>1</c>, <c>NULL</c>.</summary>
internal sealed record CommandArgument(string? Name, object? Value);

/// <summary>A daffyzoom.* command, recognised in a batch or an RPC. <see cref="Name"/> is lower case, without the schema.</summary>
internal sealed record DaffyCommand(string Name, IReadOnlyList<CommandArgument> Arguments);

/// <summary>
/// Recognises daffyzoom.* commands. Anything in the daffyzoom schema is DaffyZoom's own: it is
/// answered by DaffyZoom and never reaches the target. (From DaffyDuo, whose schema is daffy - which
/// is why this one is not: DaffyZoom in front of DaffyDuo passes daffy.* through untouched.)
///
/// A batch is matched with a tokenizer over the whole statement, never by searching the text, so a
/// command inside a comment or a string literal is not one. The command must be the batch's only
/// statement - with or without EXEC, with or without a trailing semicolon - because answering half
/// a batch here and sending the other half to the target would mean splitting it and merging two
/// streams of results. A batch that mixes a daffyzoom.* call with other SQL is refused, not guessed at.
///
/// An RPC is matched by procedure name, which is how code calls a stored procedure
/// (CommandType.StoredProcedure). Calls hidden inside sp_executesql are not looked into; they reach
/// the target, which answers "Could not find stored procedure 'daffyzoom....'".
/// </summary>
internal static class CommandParser
{
    public const string Schema = "daffyzoom";

    /// <summary>
    /// The command a SQL batch is, or null when it is not one. Throws <see cref="GatewayException"/>
    /// for a daffyzoom.* call that shares its batch with other SQL, or whose arguments cannot be read.
    /// </summary>
    public static DaffyCommand? FromBatch(string sql)
    {
        // Cheap test first: most batches never mention the schema at all.
        if (sql.IndexOf(Schema, StringComparison.OrdinalIgnoreCase) < 0) return null;

        List<Token> tokens;
        try
        {
            tokens = Lex(sql);
        }
        catch (FormatException)
        {
            return null;   // an unterminated string or comment: the server will say so better
        }

        int position = 0;
        if (position < tokens.Count && IsWord(tokens[position], "EXEC", "EXECUTE")) position++;

        if (TryReadName(tokens, ref position, out string? name))
        {
            var arguments = ReadArguments(tokens, ref position, name!);
            while (position < tokens.Count && tokens[position].Is(";")) position++;
            if (position < tokens.Count) throw NotAlone(name!);
            return new DaffyCommand(name!, arguments);
        }

        // Not a command at the start - but one further in means a mixed batch.
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (!IsWord(tokens[i], "EXEC", "EXECUTE")) continue;
            int at = i + 1;
            if (TryReadName(tokens, ref at, out string? inner)) throw NotAlone(inner!);
        }

        return null;
    }

    /// <summary>The command an RPC request is, or null. Batched RPCs that include one are refused.</summary>
    public static DaffyCommand? FromRpc(IReadOnlyList<RpcCall> calls)
    {
        DaffyCommand? found = null;
        foreach (var call in calls)
        {
            if (ProcedureName(call.ProcName) is not { } name) continue;
            if (calls.Count > 1) throw NotAlone(name);

            if (call.Remainder is not null)
                throw new GatewayException(GatewayErrors.BadCommand, $"the arguments of daffyzoom.{name} could not be read ({call.RemainderReason}).")
                {
                    Command = name,
                };

            var arguments = call.Parameters
                .Select(p => new CommandArgument(p.Name.Length == 0 ? null : p.Name.TrimStart('@').ToLowerInvariant(), p.Value))
                .ToList();
            found = new DaffyCommand(name, arguments);
        }

        return found;
    }

    /// <summary>"daffyzoom.status", "[daffyzoom].[status]", "Sales.daffyzoom.status" - the command name, or null.</summary>
    internal static string? ProcedureName(string procName)
    {
        var parts = SplitName(procName);
        if (parts.Count < 2 || !parts[^2].Equals(Schema, StringComparison.OrdinalIgnoreCase)) return null;
        return parts[^1].ToLowerInvariant();
    }

    private static List<string> SplitName(string procName)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < procName.Length; i++)
        {
            char c = procName[i];
            if (c == '[')
            {
                int end = procName.IndexOf(']', i + 1);
                if (end < 0) end = procName.Length;
                current.Append(procName, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else if (!char.IsWhiteSpace(c))
            {
                current.Append(c);
            }
        }
        parts.Add(current.ToString());
        return parts;
    }

    private static GatewayException NotAlone(string name) =>
        new(GatewayErrors.NotAlone,
            $"EXEC daffyzoom.{name} must be the only statement in its batch. Run it on its own (in SSMS: select just that line, or put GO before and after it).")
        {
            Command = name,
        };

    // ---- the command itself -----------------------------------------------------------------

    /// <summary>daffyzoom . name, in any mix of plain, [bracketed] and "quoted" spellings.</summary>
    private static bool TryReadName(List<Token> tokens, ref int position, out string? name)
    {
        name = null;
        if (position + 2 >= tokens.Count) return false;

        var schema = tokens[position];
        if (schema.Kind is not (TokenKind.Word or TokenKind.Identifier)
            || !schema.Text.Equals(Schema, StringComparison.OrdinalIgnoreCase)) return false;
        if (!tokens[position + 1].Is(".")) return false;

        var procedure = tokens[position + 2];
        if (procedure.Kind is not (TokenKind.Word or TokenKind.Identifier)) return false;

        name = procedure.Text.ToLowerInvariant();
        position += 3;
        return true;
    }

    /// <summary>
    /// value [, value ...] where value is [@name =] 'text' | N'text' | number | NULL | bare_word.
    /// A bare word is a string, as T-SQL allows for procedure arguments: EXEC daffyzoom.help sessions.
    /// </summary>
    private static List<CommandArgument> ReadArguments(List<Token> tokens, ref int position, string command)
    {
        var arguments = new List<CommandArgument>();
        if (position >= tokens.Count || tokens[position].Is(";")) return arguments;

        while (true)
        {
            string? name = null;
            if (tokens[position].Kind == TokenKind.Variable && position + 1 < tokens.Count && tokens[position + 1].Is("="))
            {
                name = tokens[position].Text.TrimStart('@').ToLowerInvariant();
                position += 2;
            }

            if (position >= tokens.Count) throw BadArgument(command, "an argument is missing after '='");
            arguments.Add(new CommandArgument(name, ReadValue(tokens, ref position, command)));

            if (position >= tokens.Count || !tokens[position].Is(",")) return arguments;
            position++;
            if (position >= tokens.Count) throw BadArgument(command, "an argument is missing after ','");
        }
    }

    private static object? ReadValue(List<Token> tokens, ref int position, string command)
    {
        var token = tokens[position];
        bool negative = false;
        if (token.Is("-") && position + 1 < tokens.Count && tokens[position + 1].Kind == TokenKind.Number)
        {
            negative = true;
            token = tokens[++position];
        }

        position++;
        switch (token.Kind)
        {
            case TokenKind.String:
            case TokenKind.Identifier:
                return token.Text;

            case TokenKind.Number:
                if (long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                    return negative ? -whole : whole;
                if (decimal.TryParse(token.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
                    return negative ? -number : number;
                break;

            case TokenKind.Word:
                return token.Text.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : token.Text;

            case TokenKind.Variable:
                throw BadArgument(command,
                    $"{token.Text} is a variable, and variables would need a DECLARE in the same batch. Pass the value itself, e.g. 'bob'");
        }

        throw BadArgument(command, $"'{token.Text}' is not a value");
    }

    private static GatewayException BadArgument(string command, string detail) =>
        new(GatewayErrors.BadCommand, $"the arguments of daffyzoom.{command} could not be read: {detail}. EXEC daffyzoom.help '{command}' shows how to call it.")
        {
            Command = command,
        };

    // ---- tokenizer --------------------------------------------------------------------------

    private enum TokenKind { Word, Identifier, String, Number, Variable, Symbol }

    private readonly record struct Token(TokenKind Kind, string Text)
    {
        public bool Is(string symbol) => Kind == TokenKind.Symbol && Text == symbol;
    }

    private static bool IsWord(Token token, params string[] words) =>
        token.Kind == TokenKind.Word && words.Any(w => w.Equals(token.Text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// T-SQL tokens, comments dropped. Block comments nest, as they do in SQL Server. A string's
    /// value is unescaped ('' becomes '); an N prefix is accepted and makes no difference here.
    /// </summary>
    private static List<Token> Lex(string sql)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }

            if (c == '-' && At(sql, i + 1, '-'))
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            if (c == '/' && At(sql, i + 1, '*'))
            {
                int depth = 0;
                do
                {
                    if (i + 1 >= sql.Length) throw new FormatException("unterminated comment");
                    if (sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                while (depth > 0);
                continue;
            }

            if ((c is 'N' or 'n') && At(sql, i + 1, '\''))
            {
                i++;
                continue;   // the quote is read next, as a string
            }

            if (c == '\'') { tokens.Add(new Token(TokenKind.String, ReadDelimited(sql, ref i, '\''))); continue; }
            if (c == '[') { tokens.Add(new Token(TokenKind.Identifier, ReadDelimited(sql, ref i, ']'))); continue; }
            if (c == '"') { tokens.Add(new Token(TokenKind.Identifier, ReadDelimited(sql, ref i, '"'))); continue; }

            if (c == '@')
            {
                int start = i++;
                while (i < sql.Length && IsWordChar(sql[i])) i++;
                tokens.Add(new Token(TokenKind.Variable, sql[start..i]));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
            {
                int start = i;
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                tokens.Add(new Token(TokenKind.Number, sql[start..i]));
                continue;
            }

            if (char.IsLetter(c) || c is '_' or '#')
            {
                int start = i;
                while (i < sql.Length && IsWordChar(sql[i])) i++;
                tokens.Add(new Token(TokenKind.Word, sql[start..i]));
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
            i++;
        }

        return tokens;
    }

    private static bool At(string sql, int index, char expected) => index < sql.Length && sql[index] == expected;

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '$' or '@';

    /// <summary>Reads from an opening delimiter through its close, honouring the doubled-close escape.</summary>
    private static string ReadDelimited(string sql, ref int i, char close)
    {
        var text = new StringBuilder();
        i++;   // the opening delimiter
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == close)
            {
                if (At(sql, i + 1, close))
                {
                    text.Append(close);
                    i += 2;
                    continue;
                }
                i++;
                return text.ToString();
            }
            text.Append(c);
            i++;
        }

        throw new FormatException("unterminated string or identifier");
    }
}
