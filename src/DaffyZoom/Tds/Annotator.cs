using System.Text;

namespace DaffyZoom.Tds;

/// <summary>
/// Turns reassembled TDS messages into readable text. Stateful per connection: COLMETADATA defines
/// how the following ROW tokens decode.
///
/// Deliberately best-effort: anything it cannot decode falls back to a hex dump rather than
/// throwing, because its whole job is to make unfamiliar bytes legible.
/// </summary>
internal sealed class Annotator
{
    private readonly List<ColumnShape> _columns = [];
    private bool _serverAckedColumnEncryption;
    private readonly bool _pre72;

    /// <summary>
    /// For an annotator that starts after login, and so never saw the FEATUREEXTACK that decides
    /// whether COLMETADATA carries a CekTable, or the LOGINACK that says which TDS version the
    /// tokens follow (before 7.2: 4-byte DONE row counts, 2-byte UserTypes).
    /// </summary>
    public Annotator(bool columnEncryptionAcknowledged = false, uint tdsVersion = 0)
    {
        _serverAckedColumnEncryption = columnEncryptionAcknowledged;
        _pre72 = TdsConstants.IsPre72(tdsVersion);
    }

    public string Describe(in TdsMessage message)
    {
        var sb = new StringBuilder();
        sb.Append("  type=0x").Append(message.Type.ToString("X2"))
          .Append(' ').Append(TdsConstants.PacketTypeName(message.Type))
          .Append("  packets=").Append(message.PacketCount)
          .Append("  payload=").Append(message.Payload.Length).Append(" bytes\n");

        try
        {
            switch (message.Type)
            {
                case TdsConstants.PacketType.PreLogin: DescribePreLogin(sb, message.Payload); break;
                case TdsConstants.PacketType.Login7: DescribeLogin7(sb, message.Payload); break;
                case TdsConstants.PacketType.SqlBatch: DescribeSqlBatch(sb, message.Payload); break;
                case TdsConstants.PacketType.Rpc: DescribeRpc(sb, message.Payload); break;
                case TdsConstants.PacketType.TabularResult: DescribeServerMessage(sb, message.Payload); break;
                case TdsConstants.PacketType.Attention: sb.Append("    (no payload)\n"); break;
                default: HexDump.Write(sb, message.Payload); break;
            }
        }
        catch (Exception ex)
        {
            sb.Append("    !! decode failed: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            HexDump.Write(sb, message.Payload);
        }

        return sb.ToString();
    }

    /// <summary>
    /// A server sends its PRELOGIN reply in a TABULAR_RESULT packet (0x04), not another 0x12, so
    /// the first 0x04 of a connection is an option table rather than a token stream.
    /// </summary>
    private void DescribeServerMessage(StringBuilder sb, byte[] payload)
    {
        if (payload.Length > 0 && payload[0] == TdsConstants.PreLoginOption.Version && LooksLikePreLogin(payload))
        {
            sb.Append("    (PRELOGIN response)\n");
            DescribePreLogin(sb, payload);
            return;
        }

        DescribeTokenStream(sb, payload);
    }

    private static bool LooksLikePreLogin(byte[] payload)
    {
        // Option table entries are 5 bytes each and terminate with 0xFF before any option data.
        for (int i = 0; i + 1 < payload.Length; i += 5)
        {
            if (payload[i] == TdsConstants.PreLoginOption.Terminator) return i > 0;
            if (payload[i] > TdsConstants.PreLoginOption.NonceOpt) return false;
        }
        return false;
    }

    private static void DescribePreLogin(StringBuilder sb, byte[] payload)
    {
        var cursor = new TdsCursor(payload);
        var entries = new List<(byte Option, ushort Offset, ushort Length)>();

        while (true)
        {
            byte option = cursor.ReadByte();
            if (option == TdsConstants.PreLoginOption.Terminator) break;
            ushort offset = cursor.ReadUInt16BigEndian();  // PRELOGIN offsets are big-endian
            ushort length = cursor.ReadUInt16BigEndian();
            entries.Add((option, offset, length));
        }

        foreach (var (option, offset, length) in entries)
        {
            var data = payload.AsSpan(offset, length);
            sb.Append("    ").Append(TdsConstants.PreLoginOptionName(option).PadRight(16))
              .Append(" off=").Append(offset).Append(" len=").Append(length).Append("  ");

            switch (option)
            {
                case TdsConstants.PreLoginOption.Version when length >= 6:
                    sb.Append($"{data[0]}.{data[1]}.{(data[2] << 8) | data[3]} subbuild={(data[4] << 8) | data[5]}");
                    break;
                case TdsConstants.PreLoginOption.Encryption when length >= 1:
                    sb.Append("0x").Append(data[0].ToString("X2")).Append(' ')
                      .Append(TdsConstants.EncryptionName(data[0]));
                    break;
                case TdsConstants.PreLoginOption.Mars when length >= 1:
                    sb.Append(data[0] == 0 ? "off" : "ON");
                    break;
                default:
                    sb.Append(HexDump.Inline(data));
                    break;
            }
            sb.Append('\n');
        }
    }

    private static void DescribeLogin7(StringBuilder sb, byte[] payload)
    {
        var login = Login7.Parse(payload);
        var cursor = new TdsCursor(payload);
        cursor.Skip(4);

        sb.Append("    TDSVersion=").Append(HexDump.Inline(cursor.ReadBytes(4)))
          .Append("  PacketSize=").Append(login.PacketSize).Append('\n');
        sb.Append("    ").Append(login).Append('\n');

        if (login.UsesIntegratedSecurity) sb.Append("    SSPI blob present (integrated auth)\n");
    }

    private static void DescribeSqlBatch(StringBuilder sb, byte[] payload)
    {
        int offset = AllHeaders.Describe(sb, payload);
        string sql = Encoding.Unicode.GetString(payload.AsSpan(offset));
        sb.Append("    SQL: ").Append(sql.Replace("\n", "\n         ")).Append('\n');
    }

    private static void DescribeRpc(StringBuilder sb, byte[] payload)
    {
        AllHeaders.Describe(sb, payload);

        foreach (var call in RequestParser.ParseRpc(payload))
        {
            sb.Append("    RPC: ").Append(call.ProcName);
            if (call.OptionFlags != 0) sb.Append("  options=0x").Append(call.OptionFlags.ToString("X4"));
            sb.Append('\n');
            if (call.Sql is not null) sb.Append("    SQL: ").Append(call.Sql.Replace("\n", "\n         ")).Append('\n');
            foreach (var parameter in call.Parameters) sb.Append("    param ").Append(parameter).Append('\n');
        }
    }

    private void DescribeTokenStream(StringBuilder sb, byte[] payload)
    {
        var cursor = new TdsCursor(payload);

        while (cursor.Remaining > 0)
        {
            int tokenStart = cursor.Position;
            byte token = cursor.ReadByte();
            sb.Append("    [").Append(tokenStart.ToString("X4")).Append("] 0x")
              .Append(token.ToString("X2")).Append(' ')
              .Append(TdsConstants.TokenName(token).PadRight(14));

            switch (token)
            {
                case TdsConstants.Token.LoginAck:
                {
                    ushort length = cursor.ReadUInt16();
                    int end = cursor.Position + length;
                    byte iface = cursor.ReadByte();
                    var version = cursor.ReadBytes(4);
                    string progName = cursor.ReadBVarChar();
                    byte major = cursor.ReadByte(), minor = cursor.ReadByte();
                    byte buildHi = cursor.ReadByte(), buildLo = cursor.ReadByte();
                    sb.Append("interface=").Append(iface)
                      .Append(" TDSVersion=[").Append(HexDump.Inline(version)).Append(']')
                      .Append(" prog='").Append(progName.TrimEnd('\0')).Append('\'')
                      .Append(" ver=").Append(major).Append('.').Append(minor)
                      .Append('.').Append((buildHi << 8) | buildLo);
                    cursor.Seek(end);
                    break;
                }

                case TdsConstants.Token.EnvChange:
                {
                    ushort length = cursor.ReadUInt16();
                    int end = cursor.Position + length;
                    byte type = cursor.ReadByte();
                    sb.Append("type=").Append(type).Append(' ');
                    if (type is TdsConstants.EnvChangeType.Database
                        or TdsConstants.EnvChangeType.Language
                        or TdsConstants.EnvChangeType.CharacterSet
                        or TdsConstants.EnvChangeType.PacketSize)
                    {
                        string newValue = cursor.ReadBVarChar();
                        string oldValue = cursor.Position < end ? cursor.ReadBVarChar() : "";
                        sb.Append('"').Append(newValue).Append("\" (was \"").Append(oldValue).Append("\")");
                    }
                    else
                    {
                        sb.Append(HexDump.Inline(cursor.Slice(cursor.Position, end - cursor.Position)));
                    }
                    cursor.Seek(end);
                    break;
                }

                case TdsConstants.Token.Info:
                case TdsConstants.Token.Error:
                {
                    ushort length = cursor.ReadUInt16();
                    int end = cursor.Position + length;
                    int number = cursor.ReadInt32();
                    byte state = cursor.ReadByte();
                    byte severity = cursor.ReadByte();
                    string text = cursor.ReadUsVarChar();
                    sb.Append("num=").Append(number).Append(" state=").Append(state)
                      .Append(" class=").Append(severity).Append(" \"").Append(text).Append('"');
                    cursor.Seek(end);
                    break;
                }

                case TdsConstants.Token.Done:
                case TdsConstants.Token.DoneProc:
                case TdsConstants.Token.DoneInProc:
                {
                    ushort status = cursor.ReadUInt16();
                    ushort curCmd = cursor.ReadUInt16();
                    ulong rowCount = _pre72 ? cursor.ReadUInt32() : cursor.ReadUInt64();   // 8 bytes on TDS 7.2+, 4 before
                    sb.Append("status=0x").Append(status.ToString("X4"))
                      .Append(" [").Append(DescribeDoneStatus(status)).Append(']')
                      .Append(" curcmd=0x").Append(curCmd.ToString("X4")).Append(" rows=").Append(rowCount);
                    break;
                }

                case TdsConstants.Token.ReturnStatus:
                    sb.Append("value=").Append(cursor.ReadInt32());
                    break;

                case TdsConstants.Token.SspiToken:
                {
                    ushort length = cursor.ReadUInt16();
                    sb.Append(length).Append(" bytes ").Append(HexDump.Inline(cursor.ReadBytes(length), 16));
                    break;
                }

                case TdsConstants.Token.FeatureExtAck:
                {
                    sb.Append("features:");
                    while (true)
                    {
                        byte featureId = cursor.ReadByte();
                        if (featureId == 0xFF) break;
                        uint dataLength = cursor.ReadUInt32();
                        if (featureId == 0x04) _serverAckedColumnEncryption = true;
                        sb.Append(" 0x").Append(featureId.ToString("X2"))
                          .Append('(').Append(dataLength).Append("B)");
                        cursor.Skip((int)dataLength);
                    }

                    break;
                }

                case TdsConstants.Token.ColMetadata:
                {
                    ushort count = cursor.ReadUInt16();
                    sb.Append("count=").Append(count);

                    // Present only when the server acknowledged the column-encryption feature.
                    if (_serverAckedColumnEncryption)
                    {
                        ushort cekCount = cursor.ReadUInt16();
                        sb.Append(" cekTable=").Append(cekCount);
                        if (cekCount != 0)
                        {
                            sb.Append(" (entries not decoded)\n");
                            HexDump.Write(sb, cursor.Rest);
                            return;
                        }
                    }

                    _columns.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        uint userType = _pre72 ? cursor.ReadUInt16() : cursor.ReadUInt32();
                        ushort flags = cursor.ReadUInt16();
                        var column = ColumnShape.Read(ref cursor) with { };
                        column.Name = cursor.ReadBVarChar();
                        _columns.Add(column);

                        sb.Append("\n                       col[").Append(i).Append("] ")
                          .Append(column.Name.PadRight(14))
                          .Append(column.Describe())
                          .Append("  flags=0x").Append(flags.ToString("X4"))
                          .Append(" userType=").Append(userType);
                    }
                    break;
                }

                case TdsConstants.Token.Row:
                {
                    if (_columns.Count == 0)
                    {
                        sb.Append("-- no COLMETADATA seen, cannot decode\n");
                        HexDump.Write(sb, cursor.Rest);
                        return;
                    }

                    for (int i = 0; i < _columns.Count; i++)
                    {
                        sb.Append(i == 0 ? "" : ", ").Append(_columns[i].Name).Append('=')
                          .Append(_columns[i].ReadValue(ref cursor));
                    }
                    break;
                }

                default:
                    sb.Append("-- not decoded, remainder follows\n");
                    HexDump.Write(sb, cursor.Rest);
                    return;
            }

            sb.Append('\n');
        }
    }

    private static string DescribeDoneStatus(ushort status)
    {
        if (status == 0) return "FINAL";
        var parts = new List<string>();
        if ((status & TdsConstants.DoneStatus.More) != 0) parts.Add("MORE");
        if ((status & TdsConstants.DoneStatus.Error) != 0) parts.Add("ERROR");
        if ((status & TdsConstants.DoneStatus.InTransaction) != 0) parts.Add("INXACT");
        if ((status & TdsConstants.DoneStatus.Count) != 0) parts.Add("COUNT");
        if ((status & TdsConstants.DoneStatus.Attention) != 0) parts.Add("ATTN");
        if ((status & TdsConstants.DoneStatus.ServerError) != 0) parts.Add("SRVERROR");
        return parts.Count == 0 ? "?" : string.Join("|", parts);
    }
}

/// <summary>
/// A column's TYPE_INFO as it appears in COLMETADATA, plus the rule for reading one row value of
/// that type. Separate from <see cref="TypeInfoReader"/> because COLMETADATA reads the type once
/// and then many values, while an RPC parameter carries type and value together.
/// </summary>
internal sealed record ColumnShape(byte TypeByte, int MaxLength, byte Scale)
{
    public string Name { get; set; } = "";

    public static ColumnShape Read(ref TdsCursor cursor)
    {
        byte type = cursor.ReadByte();

        switch (type)
        {
            case 0x30 or 0x32 or 0x34 or 0x38 or 0x7F or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x7A:
                return new ColumnShape(type, FixedLength(type), 0);

            case 0x26 or 0x68 or 0x6D or 0x6E or 0x6F or 0x24:
                return new ColumnShape(type, cursor.ReadByte(), 0);

            case 0x28:
                return new ColumnShape(type, 3, 0);

            case 0x29 or 0x2A or 0x2B:
                return new ColumnShape(type, 0, cursor.ReadByte());

            case 0x6A or 0x6C:
            {
                int maxLength = cursor.ReadByte();
                cursor.ReadByte();                       // precision
                return new ColumnShape(type, maxLength, cursor.ReadByte());
            }

            case 0xE7 or 0xEF or 0xA7 or 0xAF:
            {
                int maxLength = cursor.ReadUInt16();
                cursor.ReadBytes(5);                     // collation
                return new ColumnShape(type, maxLength, 0);
            }

            case 0xA5 or 0xAD:
                return new ColumnShape(type, cursor.ReadUInt16(), 0);

            default:
                throw new TdsUnsupportedException($"column type 0x{type:X2}");
        }
    }

    private static int FixedLength(byte type) => type switch
    {
        0x30 or 0x32 => 1,
        0x34 => 2,
        0x38 or 0x3A or 0x3B or 0x7A => 4,
        _ => 8,
    };

    public string Describe() => TypeByte switch
    {
        0x26 => $"INTN({MaxLength})",
        0x38 => "INT4",
        0x34 => "INT2",
        0x30 => "INT1",
        0x7F => "INT8",
        0x68 => "BITN",
        0x32 => "BIT",
        0xE7 => $"NVARCHAR({MaxLength} bytes)",
        0xA7 => $"VARCHAR({MaxLength})",
        0x2A => $"DATETIME2({Scale})",
        0x28 => "DATE",
        0x29 => $"TIME({Scale})",
        0x6A or 0x6C => $"DECIMAL(scale {Scale})",
        0x6D => $"FLTN({MaxLength})",
        _ => $"type0x{TypeByte:X2}",
    };

    public string ReadValue(ref TdsCursor cursor)
    {
        switch (TypeByte)
        {
            // Fixed-length types are never null and carry no length prefix.
            case 0x30 or 0x32 or 0x34 or 0x38 or 0x7F or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x7A:
                return Render(cursor.ReadBytes(MaxLength));

            case 0xE7 or 0xEF or 0xA7 or 0xAF or 0xA5 or 0xAD:
            {
                ushort length = cursor.ReadUInt16();
                if (length == 0xFFFF) return "NULL";
                var data = cursor.ReadBytes(length);
                return TypeByte is 0xE7 or 0xEF
                    ? '"' + Encoding.Unicode.GetString(data) + '"'
                    : '"' + Encoding.Latin1.GetString(data) + '"';
            }

            default:
            {
                byte length = cursor.ReadByte();
                return length == 0 ? "NULL" : Render(cursor.ReadBytes(length));
            }
        }
    }

    private string Render(ReadOnlySpan<byte> data) => TypeByte switch
    {
        0x26 or 0x38 or 0x34 or 0x30 or 0x7F => data.Length switch
        {
            1 => data[0].ToString(),
            2 => BitConverter.ToInt16(data).ToString(),
            4 => BitConverter.ToInt32(data).ToString(),
            _ => BitConverter.ToInt64(data).ToString(),
        },
        0x68 or 0x32 => data[0] != 0 ? "true" : "false",
        0x6D or 0x3B or 0x3E => data.Length == 4
            ? BitConverter.ToSingle(data).ToString()
            : BitConverter.ToDouble(data).ToString(),
        0x2A => DescribeDateTime2(data),
        _ => "0x" + HexDump.Inline(data, 16),
    };

    private string DescribeDateTime2(ReadOnlySpan<byte> data)
    {
        var date = data[^3..];
        int days = date[0] | (date[1] << 8) | (date[2] << 16);

        long units = 0;
        var time = data[..^3];
        for (int i = time.Length - 1; i >= 0; i--) units = (units << 8) | time[i];
        for (int i = Scale; i < 7; i++) units *= 10;

        return DateTime.MinValue.AddDays(days).AddTicks(units).ToString("yyyy-MM-dd HH:mm:ss.fffffff");
    }
}
