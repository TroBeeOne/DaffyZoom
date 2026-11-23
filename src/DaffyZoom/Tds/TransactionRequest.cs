namespace DaffyZoom.Tds;

/// <summary>A decoded TRANSACTION_MANAGER request.</summary>
internal sealed record TransactionRequest(string Operation, string? Name, string? IsolationLevel, bool BeginsNew);

/// <summary>
/// Transaction manager requests (packet type 0x0E). This is how SqlClient's SqlTransaction talks to
/// the server - BeginTransaction, Commit, Rollback and Save do not send BEGIN TRAN text at all - so
/// without decoding these a transaction would be invisible in the captured stream.
///
/// Layout: ALL_HEADERS, a USHORT request type, then a type-specific payload. Transaction names are
/// length-prefixed in BYTES, not characters as everywhere else - SqlClient writes
/// (byte)(name.Length * 2) - which a SAVE TRANSACTION with a real name shows at once.
/// </summary>
internal static class TransactionRequestParser
{
    public static TransactionRequest Parse(ReadOnlySpan<byte> payload)
    {
        int offset = AllHeaders.BodyOffset(payload);
        var cursor = new TdsCursor(payload[offset..]);
        ushort type = cursor.ReadUInt16();

        switch (type)
        {
            case 0:
                return new TransactionRequest("get_dtc_address", null, null, false);

            case 1:
                return new TransactionRequest("propagate", null, null, false);

            case 5:
            {
                string isolation = Isolation(cursor.ReadByte());
                string name = Name(ref cursor);
                return new TransactionRequest("begin", Empty(name), isolation, true);
            }

            case 6:
                return new TransactionRequest("promote", null, null, false);

            case 7 or 8:
            {
                string name = Name(ref cursor);
                bool beginsNew = cursor.Remaining > 0 && (cursor.ReadByte() & 0x01) != 0;
                string? isolation = null;
                if (beginsNew && cursor.Remaining > 0)
                {
                    isolation = Isolation(cursor.ReadByte());
                    if (cursor.Remaining > 0) Name(ref cursor);
                }
                return new TransactionRequest(type == 7 ? "commit" : "rollback", Empty(name), isolation, beginsNew);
            }

            case 9:
                return new TransactionRequest("save", Empty(Name(ref cursor)), null, false);

            default:
                return new TransactionRequest($"request{type}", null, null, false);
        }
    }

    private static string? Empty(string value) => value.Length == 0 ? null : value;

    private static string Name(ref TdsCursor cursor)
    {
        var bytes = cursor.ReadBVarByte();
        return System.Text.Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);
    }

    private static string Isolation(byte level) => level switch
    {
        0 => "unchanged",
        1 => "read uncommitted",
        2 => "read committed",
        3 => "repeatable read",
        4 => "serializable",
        5 => "snapshot",
        _ => $"level{level}",
    };
}
