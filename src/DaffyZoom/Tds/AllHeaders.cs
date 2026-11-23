using System.Buffers.Binary;
using System.Text;

namespace DaffyZoom.Tds;

/// <summary>
/// Every TDS 7.2+ SQL_BATCH and RPC request begins with an ALL_HEADERS block:
///
///   TotalLength : DWORD   (includes itself)
///   Header*     : DWORD HeaderLength (includes itself), USHORT HeaderType, then data
///
/// SqlClient always sends one — the Transaction Descriptor (type 0x0002, an 8-byte descriptor plus
/// a 4-byte outstanding-request count), so 22 bytes in total. The SQL text or RPC body starts
/// after it. Reading the payload from offset 0 as UTF-16 is the classic way to get garbage here.
/// </summary>
internal static class AllHeaders
{
    private const int MaxPlausibleLength = 1024;

    /// <summary>Returns the offset at which the actual request body begins.</summary>
    public static int BodyOffset(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return 0;

        uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        // A pre-7.2 client sends no ALL_HEADERS at all. UTF-16 text would start with a low ASCII
        // byte followed by 0x00, which cannot be a plausible TotalLength.
        if (totalLength < 4 || totalLength > MaxPlausibleLength || totalLength > payload.Length) return 0;

        return (int)totalLength;
    }

    /// <summary>
    /// The 8-byte transaction descriptor the request ran under - zero outside a transaction. The
    /// server hands it out when a transaction begins (ENVCHANGE) and the client echoes it on every
    /// request until it ends, so statements that share one belong to the same transaction.
    /// </summary>
    public static ulong? TransactionDescriptor(ReadOnlySpan<byte> payload)
    {
        int bodyOffset = BodyOffset(payload);
        int position = 4;
        while (bodyOffset > 0 && position + 6 <= bodyOffset)
        {
            uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[position..]);
            if (headerLength < 6 || position + headerLength > bodyOffset) break;

            ushort headerType = BinaryPrimitives.ReadUInt16LittleEndian(payload[(position + 4)..]);
            if (headerType == TdsConstants.HeaderType.TransactionDescriptor && headerLength >= 14)
                return BinaryPrimitives.ReadUInt64LittleEndian(payload[(position + 6)..]);

            position += (int)headerLength;
        }
        return null;
    }

    /// <summary>
    /// Whether the block carries a header of this type. A QueryNotifications header (SqlDependency)
    /// asks the server to watch the query's results, which only the server can do.
    /// </summary>
    public static bool Has(ReadOnlySpan<byte> payload, ushort type)
    {
        int bodyOffset = BodyOffset(payload);
        int position = 4;
        while (bodyOffset > 0 && position + 6 <= bodyOffset)
        {
            uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[position..]);
            if (headerLength < 6 || position + headerLength > bodyOffset) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(payload[(position + 4)..]) == type) return true;
            position += (int)headerLength;
        }
        return false;
    }

    /// <summary>Logs the header block and returns the body offset.</summary>
    public static int Describe(StringBuilder sb, ReadOnlySpan<byte> payload)
    {
        int bodyOffset = BodyOffset(payload);
        if (bodyOffset == 0)
        {
            sb.Append("    ALL_HEADERS: absent\n");
            return 0;
        }

        sb.Append("    ALL_HEADERS: ").Append(bodyOffset).Append(" bytes\n");

        int position = 4;
        while (position < bodyOffset)
        {
            uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[position..]);
            if (headerLength < 6 || position + headerLength > bodyOffset) break;

            ushort headerType = BinaryPrimitives.ReadUInt16LittleEndian(payload[(position + 4)..]);
            var data = payload.Slice(position + 6, (int)headerLength - 6);

            sb.Append("      type=0x").Append(headerType.ToString("X4")).Append(' ')
              .Append(HeaderTypeName(headerType)).Append("  ").Append(HexDump.Inline(data)).Append('\n');

            position += (int)headerLength;
        }

        return bodyOffset;
    }

    private static string HeaderTypeName(ushort type) => type switch
    {
        TdsConstants.HeaderType.QueryNotifications => "QUERY_NOTIFICATIONS",
        TdsConstants.HeaderType.TransactionDescriptor => "TRANSACTION_DESCRIPTOR",
        TdsConstants.HeaderType.TraceActivity => "TRACE_ACTIVITY",
        _ => "UNKNOWN",
    };
}
