namespace DaffyZoom.Tds;

/// <summary>
/// The only responses DaffyZoom ever writes itself: refusals, sent in place of a LOGINACK before any
/// relay exists. Everything a client receives after a successful login comes from the target.
///
/// Carried over from DaffyDB's ResponseWriter, which had them verified byte-for-byte against a
/// real server.
/// </summary>
internal static class ServerMessages
{
    public const string ServerName = "DaffyZoom";

    /// <summary>SQL Server's own number for a failed login, which clients recognise and report well.</summary>
    public const int LoginFailed = 18456;

    /// <summary>Azure SQL: "Database '...' on server '...' is not currently available" - resuming, or failing over.</summary>
    public const int DatabaseNotAvailable = 40613;

    /// <summary>
    /// An ERROR token followed by DONE(ERROR). Severity 11 or higher is what makes SqlClient raise
    /// a SqlException rather than an InfoMessage - the difference between a readable failure and a hang.
    /// </summary>
    /// <param name="tdsVersion">
    /// The client's TDS version, from its LOGIN7. Before 7.2 the line number is 2 bytes and DONE's row
    /// count 4: the SQLSRV32 ODBC driver and SQLOLEDB misread the 7.2 shape and report "SQL Server
    /// version 6.5 and all previous versions are no longer supported" instead of the refusal.
    /// </param>
    public static byte[] Error(int number, byte severity, string message, byte state = 1, uint tdsVersion = 0)
    {
        bool pre72 = TdsConstants.IsPre72(tdsVersion);
        var writer = new TdsWriter();
        WriteMessageToken(writer, TdsConstants.Token.Error, number, state, severity, message, pre72);
        WriteDone(writer, TdsConstants.DoneStatus.Error, pre72);
        return writer.ToArray();
    }

    private static void WriteMessageToken(TdsWriter writer, byte token, int number, byte state, byte severity, string message, bool pre72)
    {
        writer.WriteByte(token);
        int length = writer.ReserveUInt16();

        writer.WriteInt32(number);
        writer.WriteByte(state);
        writer.WriteByte(severity);
        writer.WriteUsVarChar(message);
        writer.WriteBVarChar(ServerName);
        writer.WriteBVarChar("");   // procedure name
        if (pre72) writer.WriteUInt16(1);   // line number: 2 bytes before TDS 7.2
        else writer.WriteInt32(1);

        writer.PatchLengthFrom(length);
    }

    /// <summary>DONE's row count is 8 bytes on TDS 7.2+, 4 before; the wrong size hangs or confuses the client.</summary>
    private static void WriteDone(TdsWriter writer, ushort status, bool pre72)
    {
        writer.WriteByte(TdsConstants.Token.Done);
        writer.WriteUInt16(status);
        writer.WriteUInt16(0);
        if (pre72) writer.WriteUInt32(0);
        else writer.WriteUInt64(0);
    }
}
