using System.Text;

namespace DaffyZoom.Tds;

/// <summary>One RPC parameter as it arrived.</summary>
internal sealed record RpcParameter(string Name, TypeInfo Type, string DeclaredType, byte Status, object? Value)
{
    public bool IsOutput => (Status & 0x01) != 0;
    public bool IsDefault => (Status & 0x02) != 0;
    public bool IsEncrypted => (Status & 0x08) != 0;

    public override string ToString() =>
        $"{(Name.Length == 0 ? "(unnamed)" : Name)} {DeclaredType}{(IsOutput ? " OUTPUT" : "")} = {Describe(Value)}";

    public static string Describe(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{(s.Length > 200 ? s[..200] + "..." : s)}'",
        byte[] b => $"0x{Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 32)))}{(b.Length > 32 ? "..." : "")}",
        TableValue t => $"<{t.Type.TypeName}: {t.Rows.Count} row(s)>",
        RawValue r => $"<{r.Reason}: {r.Bytes.Length} bytes>",
        EncryptedValue e => $"<encrypted: {e.Ciphertext.Length} bytes>",
        _ => value.ToString() ?? "",
    };
}

/// <summary>
/// One call within an RPC message. <see cref="Sql"/> and <see cref="ParamDeclarations"/> are
/// lifted out of the arguments for the procedures that carry a statement (sp_executesql,
/// sp_prepexec, sp_prepare, sp_cursoropen ...); <see cref="Parameters"/> are then only the actual
/// parameter values. <see cref="Handle"/> is the prepared-statement handle an sp_execute or
/// sp_unprepare refers to.
///
/// <see cref="Remainder"/> is set when decoding stopped part-way (a type DaffyZoom does not know):
/// the undecoded bytes, kept so that nothing the client sent is lost.
/// </summary>
internal sealed record RpcCall(
    string ProcName, ushort ProcId, string? Sql, string? ParamDeclarations, int? Handle,
    IReadOnlyList<RpcParameter> Parameters, byte[]? Remainder = null, string? RemainderReason = null, ushort OptionFlags = 0)
{
    /// <summary>fNoMetaData: the client asked for results without their COLMETADATA, which it already has from an earlier call.</summary>
    public bool NoMetadata => (OptionFlags & 0x02) != 0;

    /// <summary>
    /// Where each argument decoded - the statement, declarations and handle as well as the values - lies
    /// in the payload <see cref="RequestParser.ParseRpc"/> was given: start and length, name to value.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> ArgumentSpans { get; init; } = [];
}

/// <summary>Decodes the body of SQL_BATCH and RPC requests.</summary>
internal static class RequestParser
{
    /// <summary>
    /// Between batched RPC calls: BatchFlag (0xFF on TDS 7.2+) and NoExecFlag (0xFE). Neither can be
    /// the length byte of a parameter name, which tops out at 128. (TDS 7.1 used 0x80 as BatchFlag,
    /// but 0x80 is also a legal name length, and nothing older than 7.2 is relayed.)
    /// </summary>
    private static bool IsCallSeparator(byte value) => value is 0xFF or 0xFE;

    /// <summary>
    /// Where the well-known procedures keep their statement, declarations and handle, by argument
    /// position; the values follow from <c>FirstValue</c>. -1 means "not carried".
    /// </summary>
    private static readonly Dictionary<ushort, (int Statement, int Declarations, int Handle, int FirstValue)> Shapes = new()
    {
        [TdsConstants.ProcId.ExecuteSql] = (0, 1, -1, 2),
        [TdsConstants.ProcId.PrepExec] = (2, 1, 0, 3),
        [TdsConstants.ProcId.Prepare] = (2, 1, 0, 4),
        [TdsConstants.ProcId.Execute] = (-1, -1, 0, 1),
        [TdsConstants.ProcId.Unprepare] = (-1, -1, 0, 1),
        // sp_cursoropen cursor OUT, stmt, scrollopt, ccopt, rowcount [, paramdef, values...]
        [TdsConstants.ProcId.CursorOpen] = (1, 5, -1, 6),
        // sp_cursorprepexec handle OUT, cursor OUT, paramdef, stmt, options, scrollopt, ccopt, rowcount, values...
        [TdsConstants.ProcId.CursorPrepExec] = (3, 2, 0, 8),
        // sp_cursorprepare handle OUT, paramdef, stmt, options [, scrollopt, ccopt]
        [TdsConstants.ProcId.CursorPrepare] = (2, 1, 0, 6),
        // sp_cursorexecute handle, cursor OUT, scrollopt, ccopt, rowcount, values...
        [TdsConstants.ProcId.CursorExecute] = (-1, -1, 0, 5),
    };

    public static string ParseBatchText(ReadOnlySpan<byte> payload)
    {
        int offset = AllHeaders.BodyOffset(payload);
        var body = payload[offset..];
        return Encoding.Unicode.GetString(body[..(body.Length & ~1)]);
    }

    public static IReadOnlyList<RpcCall> ParseRpc(ReadOnlySpan<byte> payload)
    {
        int offset = AllHeaders.BodyOffset(payload);
        var cursor = new TdsCursor(payload[offset..]);
        var calls = new List<RpcCall>();

        while (cursor.Remaining > 0)
        {
            var call = ParseCall(ref cursor, offset);
            calls.Add(call);
            if (call.Remainder is not null) break;   // lost our place: nothing after this can be read
            if (cursor.Remaining > 0 && IsCallSeparator(cursor.Rest[0])) cursor.Skip(1);
        }

        return calls;
    }

    private static RpcCall ParseCall(ref TdsCursor cursor, int bodyOffset)
    {
        ushort nameLengthOrSwitch = cursor.ReadUInt16();
        ushort procId = 0;
        string procName;

        if (nameLengthOrSwitch == TdsConstants.RpcProcIdSwitch)
        {
            procId = cursor.ReadUInt16();
            procName = TdsConstants.ProcId.Name(procId);
        }
        else
        {
            procName = Encoding.Unicode.GetString(cursor.ReadBytes(nameLengthOrSwitch * 2));
            procId = WellKnownByName(procName);
        }

        ushort optionFlags = cursor.ReadUInt16();

        var arguments = new List<RpcParameter>();
        var spans = new List<(int Start, int Length)>();
        byte[]? remainder = null;
        string? reason = null;

        while (cursor.Remaining > 0 && !IsCallSeparator(cursor.Rest[0]))
        {
            int start = cursor.Position;
            try
            {
                byte nameLength = cursor.ReadByte();
                string name = nameLength == 0 ? "" : Encoding.Unicode.GetString(cursor.ReadBytes(nameLength * 2));
                byte status = cursor.ReadByte();

                var type = TypeInfoReader.ReadTypeInfo(ref cursor);
                object? value = (status & 0x08) != 0
                    ? TypeInfoReader.ReadEncrypted(ref cursor, type)
                    : TypeInfoReader.ReadValue(ref cursor, type);

                arguments.Add(new RpcParameter(name, type, type.SqlName, status, value));
                spans.Add((bodyOffset + start, cursor.Position - start));
            }
            catch (Exception ex) when (ex is TdsUnsupportedException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                cursor.Seek(start);
                remainder = cursor.Rest.ToArray();
                reason = ex is TdsUnsupportedException ? $"undecoded {ex.Message}" : "malformed parameter";
                cursor.Skip(cursor.Remaining);
                break;
            }
        }

        if (!Shapes.TryGetValue(procId, out var shape))
            return new RpcCall(procName, procId, null, null, null, arguments, remainder, reason, optionFlags) { ArgumentSpans = spans };

        string? sql = Argument(arguments, shape.Statement) as string;
        string? declarations = Argument(arguments, shape.Declarations) as string;
        int? handle = Argument(arguments, shape.Handle) switch { int i => i, _ => null };

        var declared = ParseDeclarations(declarations ?? "");
        var values = arguments.Skip(shape.FirstValue)
            .Select(p => p with { DeclaredType = declared.GetValueOrDefault(p.Name, p.DeclaredType) })
            .ToList();

        return new RpcCall(procName, procId, sql, declarations, handle, values, remainder, reason, optionFlags) { ArgumentSpans = spans };
    }

    private static object? Argument(List<RpcParameter> arguments, int index) =>
        index >= 0 && index < arguments.Count ? arguments[index].Value : null;

    /// <summary>Some clients call the system procedures by name rather than by id.</summary>
    private static ushort WellKnownByName(string name) => name.ToLowerInvariant().Replace("[", "").Replace("]", "") switch
    {
        "sp_executesql" or "sys.sp_executesql" => TdsConstants.ProcId.ExecuteSql,
        "sp_prepexec" or "sys.sp_prepexec" => TdsConstants.ProcId.PrepExec,
        "sp_prepare" or "sys.sp_prepare" => TdsConstants.ProcId.Prepare,
        "sp_execute" or "sys.sp_execute" => TdsConstants.ProcId.Execute,
        "sp_unprepare" or "sys.sp_unprepare" => TdsConstants.ProcId.Unprepare,
        "sp_cursoropen" or "sys.sp_cursoropen" => TdsConstants.ProcId.CursorOpen,
        "sp_cursorprepexec" or "sys.sp_cursorprepexec" => TdsConstants.ProcId.CursorPrepExec,
        "sp_cursorprepare" or "sys.sp_cursorprepare" => TdsConstants.ProcId.CursorPrepare,
        "sp_cursorexecute" or "sys.sp_cursorexecute" => TdsConstants.ProcId.CursorExecute,
        _ => 0,
    };

    /// <summary>
    /// Splits an sp_executesql @params string such as "@year int,@total decimal(18,2)" into a
    /// name/type map. Commas inside parentheses belong to the type, not the separator.
    /// </summary>
    public static Dictionary<string, string> ParseDeclarations(string declarations)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int depth = 0, start = 0;

        for (int i = 0; i <= declarations.Length; i++)
        {
            if (i < declarations.Length)
            {
                char c = declarations[i];
                if (c == '(') { depth++; continue; }
                if (c == ')') { depth--; continue; }
                if (c != ',' || depth != 0) continue;
            }

            string entry = declarations[start..i].Trim();
            start = i + 1;
            if (entry.Length == 0) continue;

            int space = entry.IndexOfAny([' ', '\t', '\r', '\n']);
            if (space < 0) map[entry] = "";
            else map[entry[..space]] = entry[(space + 1)..].Trim();
        }

        return map;
    }
}
