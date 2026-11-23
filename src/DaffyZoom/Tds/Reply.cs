using DaffyZoom.Commands;

namespace DaffyZoom.Tds;

/// <summary>Column types the response writer can encode (from DaffyDB).</summary>
internal enum SqlColumnType
{
    Int, BigInt, Double, Decimal, NVarChar, Bit, Date, DateTime2,
    TinyInt, SmallInt, Real, Time, VarBinary, UniqueIdentifier, VarChar, DateTime, Binary,
}

/// <summary>
/// One column of a result set DaffyZoom writes itself. <paramref name="MaxLength"/> is the declared
/// size in bytes of a character column; left null, it is sized from the widest value sent.
/// </summary>
internal sealed record ColumnDef(string Name, SqlColumnType Type, bool Nullable = true, byte Precision = 38, byte Scale = 4,
                                 int? MaxLength = null);

internal sealed record ResultSet(IReadOnlyList<ColumnDef> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>
/// What DaffyZoom answers to a daffyzoom.* command, shaped the way SQL Server answers a stored
/// procedure: anything the target said that the client must hear first (the ENVCHANGE tokens of a
/// pooled connection's reset), then messages, then result sets - or an error. (From DaffyDuo.)
/// </summary>
internal sealed class Reply
{
    private const ushort ProcCurCmd = 0x00E0;
    private const ushort SelectCurCmd = 0x00C1;

    /// <summary>Raw tokens from the target, sent ahead of everything else, unchanged.</summary>
    public List<byte[]> Prelude { get; } = [];

    /// <summary>Informational messages - SSMS's Messages tab, SqlClient's InfoMessage.</summary>
    public List<string> Messages { get; } = [];

    public List<ResultSet> ResultSets { get; } = [];

    public GatewayException? Error { get; private set; }

    public static Reply Failed(GatewayException error, IEnumerable<byte[]>? prelude = null)
    {
        var reply = new Reply { Error = error };
        if (prelude is not null) reply.Prelude.AddRange(prelude);
        return reply;
    }

    /// <summary>
    /// A batch ends in DONE; an RPC in RETURNSTATUS and DONEPROC, with each result set closed by
    /// DONEINPROC. Every result set's DONE but the last carries MORE. An error is ERROR followed by
    /// a DONE (or DONEPROC) with the error bit, at severity 16 so SqlClient raises a SqlException.
    /// <paramref name="columnEncryption"/> is whether the target acknowledged Always Encrypted at
    /// login, which changes what every COLMETADATA on the session looks like.
    /// </summary>
    public byte[] Encode(bool rpc, bool columnEncryption = false, string serverName = ServerMessages.ServerName)
    {
        var writer = new TdsWriter();
        foreach (var token in Prelude) writer.WriteBytes(token);
        foreach (var message in Messages) ResponseWriter.WriteInfo(writer, 0, 1, message, serverName);

        if (Error is { } error)
        {
            ResponseWriter.WriteError(writer, error.Number, GatewayErrors.Severity, error.Message, serverName);
            if (rpc) ResponseWriter.WriteReturnStatus(writer, -6);
            ResponseWriter.WriteDone(writer, rpc ? TdsConstants.Token.DoneProc : TdsConstants.Token.Done,
                                     TdsConstants.DoneStatus.Error, rpc ? ProcCurCmd : (ushort)0, 0);
            return writer.ToArray();
        }

        for (int i = 0; i < ResultSets.Count; i++)
        {
            var resultSet = ResultSets[i];
            bool more = rpc || i < ResultSets.Count - 1;
            ResponseWriter.WriteResultSet(writer, resultSet, columnEncryption);
            ResponseWriter.WriteDone(writer, rpc ? TdsConstants.Token.DoneInProc : TdsConstants.Token.Done,
                                     (ushort)(TdsConstants.DoneStatus.Count | (more ? TdsConstants.DoneStatus.More : 0)),
                                     SelectCurCmd, (ulong)resultSet.Rows.Count);
        }

        if (rpc)
        {
            ResponseWriter.WriteReturnStatus(writer, 0);
            ResponseWriter.WriteDone(writer, TdsConstants.Token.DoneProc, TdsConstants.DoneStatus.Final, ProcCurCmd, 0);
        }
        else if (ResultSets.Count == 0)
        {
            ResponseWriter.WriteDone(writer, TdsConstants.Token.Done, TdsConstants.DoneStatus.Final, 0, 0);
        }

        return writer.ToArray();
    }
}
