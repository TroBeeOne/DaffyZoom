using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DaffyZoom.Tds;

/// <summary>A TYPE_INFO, decoded: enough to read a value of that type and to describe it.</summary>
internal sealed record TypeInfo(byte Type, int MaxLength, byte Precision = 0, byte Scale = 0, byte[]? Collation = null)
{
    public bool IsPlp => MaxLength == 0xFFFF && Type is 0xA5 or 0xA7 or 0xE7 || Type is 0xF1 or 0xF0;

    public string? UdtName { get; init; }
    public TableTypeInfo? Table { get; init; }

    /// <summary>The SQL Server type name, sized the way a declaration would be: nvarchar(50), decimal(18,2).</summary>
    public string SqlName => Type switch
    {
        0x30 => "tinyint",
        0x34 => "smallint",
        0x38 => "int",
        0x7F => "bigint",
        0x26 => MaxLength switch { 1 => "tinyint", 2 => "smallint", 8 => "bigint", _ => "int" },
        0x32 or 0x68 => "bit",
        0x3B => "real",
        0x3E => "float",
        0x6D => MaxLength == 4 ? "real" : "float",
        0x3C => "money",
        0x7A => "smallmoney",
        0x6E => MaxLength == 4 ? "smallmoney" : "money",
        0x3A => "smalldatetime",
        0x3D => "datetime",
        0x6F => MaxLength == 4 ? "smalldatetime" : "datetime",
        0x28 => "date",
        0x29 => $"time({Scale})",
        0x2A => $"datetime2({Scale})",
        0x2B => $"datetimeoffset({Scale})",
        0x6A or 0x37 => $"decimal({Precision},{Scale})",
        0x6C or 0x3F => $"numeric({Precision},{Scale})",
        0x24 => "uniqueidentifier",
        0xE7 => IsPlp ? "nvarchar(max)" : $"nvarchar({MaxLength / 2})",
        0xEF => $"nchar({MaxLength / 2})",
        0xA7 => IsPlp ? "varchar(max)" : $"varchar({MaxLength})",
        0xAF => $"char({MaxLength})",
        0xA5 => IsPlp ? "varbinary(max)" : $"varbinary({MaxLength})",
        0xAD => $"binary({MaxLength})",
        0x23 => "text",
        0x63 => "ntext",
        0x22 => "image",
        0x62 => "sql_variant",
        0xF1 => "xml",
        0xF0 => UdtName ?? "udt",
        0xF3 => Table?.TypeName ?? "table",
        _ => $"type0x{Type:X2}",
    };
}

/// <summary>A table-valued parameter: its declared type, its columns, and (as the value) its rows.</summary>
internal sealed record TableTypeInfo(string TypeName, IReadOnlyList<(string Name, TypeInfo Type)> Columns);

/// <summary>The rows of a table-valued parameter.</summary>
internal sealed record TableValue(TableTypeInfo Type, IReadOnlyList<object?[]> Rows);

/// <summary>A value DaffyZoom does not decode, kept exactly as sent so nothing is lost.</summary>
internal sealed record RawValue(byte[] Bytes, string Reason);

/// <summary>An Always Encrypted value: ciphertext, which DaffyZoom cannot and should not read.</summary>
internal sealed record EncryptedValue(byte[] Ciphertext);

/// <summary>
/// Reads TYPE_INFO and the value that follows it, as they appear in RPC parameters (and, for
/// table-valued parameters, in each row).
///
/// Covers every type a client sends: the fixed types, the nullable "N" types, decimal of any
/// precision, the date and time family, character data in its declared collation, MAX types
/// (sent in PLP chunks), xml, sql_variant, CLR UDTs and table-valued parameters. A type it does
/// not know throws <see cref="TdsUnsupportedException"/>, because without knowing a type's shape
/// there is no way to find where the next value starts - the caller keeps the rest raw.
/// </summary>
internal static class TypeInfoReader
{
    private const ulong PlpNull = 0xFFFFFFFFFFFFFFFF;
    private const uint Utf8CollationFlag = 1u << 26;

    public static (TypeInfo Type, object? Value) Read(ref TdsCursor cursor)
    {
        var type = ReadTypeInfo(ref cursor);
        return (type, ReadValue(ref cursor, type));
    }

    public static TypeInfo ReadTypeInfo(ref TdsCursor cursor)
    {
        byte type = cursor.ReadByte();
        switch (type)
        {
            // Fixed length: nothing in TYPE_INFO, and no length before the value.
            case 0x1F: return new TypeInfo(type, 0);
            case 0x30 or 0x32: return new TypeInfo(type, 1);
            case 0x34: return new TypeInfo(type, 2);
            case 0x38 or 0x3A or 0x3B or 0x7A: return new TypeInfo(type, 4);
            case 0x3C or 0x3D or 0x3E or 0x7F: return new TypeInfo(type, 8);

            // One-byte max length.
            case 0x24 or 0x26 or 0x68 or 0x6D or 0x6E or 0x6F:
            case 0x2F or 0x27 or 0x2D or 0x25:
                return new TypeInfo(type, cursor.ReadByte());

            case 0x6A or 0x6C or 0x37 or 0x3F:
            {
                int maxLength = cursor.ReadByte();
                byte precision = cursor.ReadByte();
                byte scale = cursor.ReadByte();
                return new TypeInfo(type, maxLength, precision, scale);
            }

            case 0x28: return new TypeInfo(type, 3);
            case 0x29 or 0x2A or 0x2B: return new TypeInfo(type, 0, Scale: cursor.ReadByte());

            // Two-byte max length; the character types carry a collation.
            case 0xA7 or 0xAF or 0xE7 or 0xEF:
            {
                int maxLength = cursor.ReadUInt16();
                return new TypeInfo(type, maxLength, Collation: cursor.ReadBytes(5).ToArray());
            }
            case 0xA5 or 0xAD:
                return new TypeInfo(type, cursor.ReadUInt16());

            // Four-byte max length.
            case 0x23 or 0x63:
            {
                int maxLength = (int)cursor.ReadUInt32();
                return new TypeInfo(type, maxLength, Collation: cursor.ReadBytes(5).ToArray());
            }
            case 0x22 or 0x62:
                return new TypeInfo(type, (int)cursor.ReadUInt32());

            case 0xF1:
            {
                if (cursor.ReadByte() != 0)
                {
                    cursor.ReadBVarChar();     // database
                    cursor.ReadBVarChar();     // owning schema
                    cursor.ReadUsVarChar();    // schema collection
                }
                return new TypeInfo(type, 0xFFFF);
            }

            case 0xF0:
            {
                // In an RPC parameter a UDT is named, not sized: database, schema, type.
                string database = cursor.ReadBVarChar();
                string schema = cursor.ReadBVarChar();
                string name = cursor.ReadBVarChar();
                return new TypeInfo(type, 0xFFFF) { UdtName = Qualify(database, schema, name) };
            }

            case 0xF3:
                return new TypeInfo(type, 0) { Table = ReadTableType(ref cursor) };

            default:
                throw new TdsUnsupportedException($"type 0x{type:X2}");
        }
    }

    public static object? ReadValue(ref TdsCursor cursor, TypeInfo type)
    {
        switch (type.Type)
        {
            case 0x1F: return null;
            case 0x30: return cursor.ReadByte();
            case 0x32: return cursor.ReadByte() != 0;
            case 0x34: return (short)cursor.ReadUInt16();
            case 0x38: return cursor.ReadInt32();
            case 0x7F: return (long)cursor.ReadUInt64();
            case 0x3B: return BinaryPrimitives.ReadSingleLittleEndian(cursor.ReadBytes(4));
            case 0x3E: return BinaryPrimitives.ReadDoubleLittleEndian(cursor.ReadBytes(8));
            case 0x3A: return SmallDateTime(cursor.ReadBytes(4));
            case 0x3D: return DateTime(cursor.ReadBytes(8));
            case 0x7A: return Money4(cursor.ReadBytes(4));
            case 0x3C: return Money(cursor.ReadBytes(8));

            case 0x24 or 0x26 or 0x68 or 0x6D or 0x6E or 0x6F or 0x28 or 0x29 or 0x2A or 0x2B
                 or 0x6A or 0x6C or 0x37 or 0x3F or 0x2F or 0x27 or 0x2D or 0x25:
            {
                int length = cursor.ReadByte();
                if (length == 0) return null;
                return ByteLengthValue(type, cursor.ReadBytes(length));
            }

            case 0xA7 or 0xAF or 0xE7 or 0xEF or 0xA5 or 0xAD when !type.IsPlp:
            {
                ushort length = cursor.ReadUInt16();
                if (length == 0xFFFF) return null;   // 0xFFFF is NULL; 0 is the empty string
                return Character(type, cursor.ReadBytes(length));
            }

            case 0xA7 or 0xE7 or 0xA5 or 0xF1 or 0xF0:
            {
                byte[]? data = ReadPlp(ref cursor);
                if (data is null) return null;
                return type.Type switch
                {
                    0xF1 => Encoding.Unicode.GetString(data),
                    0xF0 => new RawValue(data, $"CLR type {type.UdtName}"),
                    _ => Character(type, data),
                };
            }

            case 0x23 or 0x63 or 0x22:
            {
                uint length = cursor.ReadUInt32();
                if (length == 0xFFFFFFFF) return null;
                var data = cursor.ReadBytes((int)length);
                return Character(type with { Type = type.Type switch { 0x23 => 0xA7, 0x63 => 0xE7, _ => 0xA5 } }, data);
            }

            case 0x62:
                return Variant(ref cursor);

            case 0xF3:
                return TableRows(ref cursor, type.Table!);

            default:
                throw new TdsUnsupportedException($"type 0x{type.Type:X2}");
        }
    }

    /// <summary>
    /// The value that follows an Always Encrypted parameter, whose status carried fEncrypted: the
    /// ciphertext arrives as varbinary, then ParamCipherInfo describes the plaintext type and key.
    /// </summary>
    public static EncryptedValue? ReadEncrypted(ref TdsCursor cursor, TypeInfo type)
    {
        object? value = ReadValue(ref cursor, type);

        ReadTypeInfo(ref cursor);                  // the plaintext type
        byte algorithm = cursor.ReadByte();
        if (algorithm == 0) cursor.ReadBVarChar();  // custom algorithm name
        cursor.ReadByte();                          // encryption type
        cursor.Skip(4 + 4 + 4 + 8);                 // database id, CEK id, CEK version, CEK metadata version
        cursor.ReadByte();                          // normalization version

        return value is byte[] ciphertext ? new EncryptedValue(ciphertext) : null;
    }

    private static object? ByteLengthValue(TypeInfo type, ReadOnlySpan<byte> data) => type.Type switch
    {
        0x26 => data.Length switch
        {
            1 => (object)data[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(data),
            4 => BinaryPrimitives.ReadInt32LittleEndian(data),
            _ => BinaryPrimitives.ReadInt64LittleEndian(data),
        },
        0x68 => data[0] != 0,
        0x6D => data.Length == 4 ? BinaryPrimitives.ReadSingleLittleEndian(data) : BinaryPrimitives.ReadDoubleLittleEndian(data),
        0x6E => data.Length == 4 ? Money4(data) : Money(data),
        0x6F => data.Length == 4 ? SmallDateTime(data) : DateTime(data),
        0x24 => new Guid(data),
        0x28 => DateOnly.FromDayNumber(Days(data)),
        0x29 => TimeSpan.FromTicks(TimeTicks(data, type.Scale)),
        0x2A => new DateTime(DateOnly.FromDayNumber(Days(data[^3..])), TimeOnly.MinValue)
                    .AddTicks(TimeTicks(data[..^3], type.Scale)),
        0x2B => DateTimeOffsetValue(data, type.Scale),
        0x6A or 0x6C or 0x37 or 0x3F => Decimal(data, type.Scale),
        0x2F or 0x27 => Encoding.Latin1.GetString(data),
        _ => data.ToArray(),
    };

    private static object Character(TypeInfo type, ReadOnlySpan<byte> data) => type.Type switch
    {
        0xE7 or 0xEF => Encoding.Unicode.GetString(data),
        0xA7 or 0xAF => EncodingFor(type.Collation).GetString(data),
        _ => data.ToArray(),
    };

    /// <summary>
    /// The code page a varchar was written in. A UTF-8 collation says so in a flag; otherwise the
    /// collation's LCID gives the Windows code page, and SQL-style collations (a non-zero sort id)
    /// are all but universally 1252.
    /// </summary>
    public static Encoding EncodingFor(byte[]? collation)
    {
        if (collation is not { Length: 5 }) return CodePage(1252);

        uint info = BinaryPrimitives.ReadUInt32LittleEndian(collation);
        if ((info & Utf8CollationFlag) != 0) return Encoding.UTF8;
        if (collation[4] != 0) return CodePage(1252);

        int lcid = (int)(info & 0xFFFFF);
        try
        {
            return CodePage(CultureInfo.GetCultureInfo(lcid).TextInfo.ANSICodePage);
        }
        catch (CultureNotFoundException)
        {
            return CodePage(1252);
        }
    }

    private static Encoding CodePage(int codePage)
    {
        try { return Encoding.GetEncoding(codePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
    }

    /// <summary>PLP: an 8-byte total length (all ones for NULL), then chunks each with a 4-byte length, ending at 0.</summary>
    private static byte[]? ReadPlp(ref TdsCursor cursor)
    {
        ulong total = cursor.ReadUInt64();
        if (total == PlpNull) return null;

        var data = new MemoryStream(total < 0xFFFFFFFF ? (int)Math.Min(total, 64 * 1024 * 1024) : 0);
        while (true)
        {
            uint chunk = cursor.ReadUInt32();
            if (chunk == 0) break;
            data.Write(cursor.ReadBytes((int)chunk));
        }
        return data.ToArray();
    }

    private static object? Variant(ref TdsCursor cursor)
    {
        int length = (int)cursor.ReadUInt32();
        if (length == 0) return null;

        var body = cursor.ReadBytes(length);
        byte baseType = body[0];
        byte propertyBytes = body[1];
        var properties = body.Slice(2, propertyBytes);
        var data = body[(2 + propertyBytes)..];

        try
        {
            return baseType switch
            {
                0x30 or 0x32 or 0x34 or 0x38 or 0x7F or 0x3B or 0x3E or 0x3A or 0x3D or 0x7A or 0x3C =>
                    ReadFixed(baseType, data),
                0x24 => new Guid(data),
                0x28 => DateOnly.FromDayNumber(Days(data)),
                0x29 or 0x2A or 0x2B => ByteLengthValue(new TypeInfo(baseType, 0, Scale: properties[0]), data),
                0x6A or 0x6C => Decimal(data, properties[1]),
                0xE7 or 0xEF => Encoding.Unicode.GetString(data),
                0xA7 or 0xAF => EncodingFor(properties[..5].ToArray()).GetString(data),
                0xA5 or 0xAD => data.ToArray(),
                _ => new RawValue(body.ToArray(), $"sql_variant base type 0x{baseType:X2}"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return new RawValue(body.ToArray(), "sql_variant");
        }
    }

    private static object ReadFixed(byte type, ReadOnlySpan<byte> data)
    {
        var cursor = new TdsCursor(data);
        return ReadValue(ref cursor, new TypeInfo(type, data.Length))!;
    }

    /// <summary>
    /// TVP TYPE_INFO: the type's three-part name, then column metadata, then optional ordering tokens
    /// ending at 0x00. A column count of 0xFFFF means a NULL table.
    /// </summary>
    private static TableTypeInfo ReadTableType(ref TdsCursor cursor)
    {
        string database = cursor.ReadBVarChar();
        string schema = cursor.ReadBVarChar();
        string name = cursor.ReadBVarChar();
        string typeName = Qualify(database, schema, name);

        var columns = new List<(string, TypeInfo)>();
        ushort count = cursor.ReadUInt16();
        if (count != 0xFFFF)
        {
            for (int i = 0; i < count; i++)
            {
                cursor.ReadUInt32();                         // UserType
                ushort flags = cursor.ReadUInt16();
                var columnType = ReadTypeInfo(ref cursor);
                string columnName = cursor.ReadBVarChar();   // sent empty by SqlClient
                if ((flags & 0x0200) != 0) columnType = columnType with { MaxLength = -1 };   // fDefault: no values sent
                columns.Add((columnName.Length > 0 ? columnName : $"column{i + 1}", columnType));
            }
        }

        // TVP_ORDER_UNIQUE (0x10) and TVP_COLUMN_ORDERING (0x11), then the 0x00 terminator.
        while (true)
        {
            byte token = cursor.ReadByte();
            if (token == 0x00) break;
            if (token == 0x10) { ushort n = cursor.ReadUInt16(); cursor.Skip(n * 3); }
            else if (token == 0x11) { ushort n = cursor.ReadUInt16(); cursor.Skip(n * 2); }
            else throw new TdsUnsupportedException($"TVP metadata token 0x{token:X2}");
        }

        return new TableTypeInfo(typeName, columns);
    }

    /// <summary>TVP rows: TVP_ROW (0x01) then one value per column, until TVP_END (0x00).</summary>
    private static TableValue TableRows(ref TdsCursor cursor, TableTypeInfo table)
    {
        var rows = new List<object?[]>();
        while (true)
        {
            byte token = cursor.ReadByte();
            if (token == 0x00) break;
            if (token != 0x01) throw new TdsUnsupportedException($"TVP row token 0x{token:X2}");

            var row = new object?[table.Columns.Count];
            for (int i = 0; i < row.Length; i++)
                row[i] = table.Columns[i].Type.MaxLength == -1 ? null : ReadValue(ref cursor, table.Columns[i].Type);
            rows.Add(row);
        }
        return new TableValue(table, rows);
    }

    private static int Days(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);

    private static long TimeTicks(ReadOnlySpan<byte> data, byte scale)
    {
        long units = 0;
        for (int i = data.Length - 1; i >= 0; i--) units = (units << 8) | data[i];
        for (int i = scale; i < 7; i++) units *= 10;   // stored in 10^-scale seconds; ticks are 10^-7
        return units;
    }

    /// <summary>DATETIMEOFFSET stores UTC plus the offset in minutes; the local time is UTC + offset.</summary>
    private static DateTimeOffset DateTimeOffsetValue(ReadOnlySpan<byte> data, byte scale)
    {
        var utc = new DateTime(DateOnly.FromDayNumber(Days(data[^5..^2])), TimeOnly.MinValue)
            .AddTicks(TimeTicks(data[..^5], scale));
        var offset = TimeSpan.FromMinutes(BinaryPrimitives.ReadInt16LittleEndian(data[^2..]));
        return new DateTimeOffset(utc.Ticks + offset.Ticks, offset);
    }

    /// <summary>
    /// Sign byte, then a little-endian magnitude of up to 16 bytes. Anything a .NET decimal can hold
    /// comes back as one; wider (precision above 28) comes back as exact text.
    /// </summary>
    private static object Decimal(ReadOnlySpan<byte> data, byte scale)
    {
        bool negative = data[0] == 0;
        var magnitude = data[1..];

        if (magnitude.Length <= 12 && scale <= 28)
        {
            Span<int> parts = stackalloc int[3];
            for (int i = 0; i < magnitude.Length; i++) parts[i / 4] |= magnitude[i] << (8 * (i % 4));
            return new decimal(parts[0], parts[1], parts[2], negative, scale);
        }

        var value = new BigInteger(magnitude, isUnsigned: true, isBigEndian: false);
        string digits = value.ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        string text = scale == 0 ? digits : $"{digits[..^scale]}.{digits[^scale..]}";

        // Try the decimal once more: a 16-byte magnitude often holds a small number.
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal fits) && fits.Scale == scale)
            return negative ? -fits : fits;
        return negative ? "-" + text : text;
    }

    /// <summary>
    /// Days since 1900-01-01, then 1/300-second units since midnight - rounded to the millisecond as
    /// SqlClient and SQL Server show them (.000, .003, .007), so a value written back as a literal
    /// converts to datetime again: datetime takes no more than three decimal places.
    /// </summary>
    private static DateTime DateTime(ReadOnlySpan<byte> data)
    {
        int days = BinaryPrimitives.ReadInt32LittleEndian(data);
        long threeHundredths = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        long milliseconds = (threeHundredths * 20 + 3) / 6;   // threeHundredths / 0.3, rounded half up
        return new DateTime(1900, 1, 1).AddDays(days).AddTicks(milliseconds * TimeSpan.TicksPerMillisecond);
    }

    private static DateTime SmallDateTime(ReadOnlySpan<byte> data) =>
        new DateTime(1900, 1, 1).AddDays(BinaryPrimitives.ReadUInt16LittleEndian(data))
                                .AddMinutes(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]));

    private static decimal Money(ReadOnlySpan<byte> data)
    {
        int high = BinaryPrimitives.ReadInt32LittleEndian(data);
        uint low = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        return ((long)high << 32 | low) / 10000m;
    }

    private static decimal Money4(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadInt32LittleEndian(data) / 10000m;

    private static string Qualify(string database, string schema, string name) =>
        string.Join('.', new[] { database, schema, name }.Where(p => p.Length > 0));
}

/// <summary>Something the client sent that DaffyZoom does not know how to decode.</summary>
internal sealed class TdsUnsupportedException(string message) : Exception(message);
