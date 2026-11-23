using System.Buffers.Binary;
using System.Text;

namespace DaffyZoom.Tds;

/// <summary>
/// A LOGIN7 message, every field of it, readable and writable.
///
/// DaffyDB only ever read LOGIN7. DaffyZoom also has to write one, because the login that reaches the
/// target is the client's own login with four fields swapped: user name, password, server name and
/// (sometimes) database. Everything else the client said about itself - TDS version, packet size,
/// option flags, host, application, language, and the FeatureExt block that negotiates session
/// recovery, UTF-8, Always Encrypted and the rest - goes through as sent, so that when the target's
/// reply is relayed back verbatim both ends agree on what was negotiated.
///
/// Layout: a fixed 94-byte header holding scalar fields and an offset/length table, then the
/// variable-length data the table points into. Before TDS 7.2 the fixed part is 86 bytes: it ends
/// after the AttachDbFile entry, with no change-password entry and no cbSSPILong, and the
/// "extension" slot is unused. The SQLSRV32 ODBC driver and SQLOLEDB that ship with Windows still
/// send logins like that; read as a 7.2 login, bytes 86-93 are its first string, which DaffyZoom
/// 0.1.3 could take for a change-password entry and refuse. Offsets are from the start of the
/// message; string lengths are in characters (UTF-16LE), SSPI's in bytes. When the fExtension bit
/// is set, the "extension" slot points at a 4-byte offset, which in turn points at the FeatureExt
/// block.
/// </summary>
internal sealed class Login7
{
    public const int FixedLength = 94;

    /// <summary>The fixed part before TDS 7.2: no change-password entry, no cbSSPILong.</summary>
    public const int FixedLengthPre72 = 86;

    // OptionFlags2
    private const byte IntegratedSecurityFlag = 0x80;

    // OptionFlags3
    private const byte ChangePasswordFlag = 0x01;
    private const byte ExtensionFlag = 0x10;

    // TypeFlags
    private const byte ReadOnlyIntentFlag = 0x20;

    public uint TdsVersion { get; set; }
    public uint PacketSize { get; set; }
    public uint ClientProgVer { get; set; }
    public uint ClientPid { get; set; }
    public uint ConnectionId { get; set; }
    public byte OptionFlags1 { get; set; }
    public byte OptionFlags2 { get; set; }
    public byte TypeFlags { get; set; }
    public byte OptionFlags3 { get; set; }
    public int ClientTimeZone { get; set; }
    public uint ClientLcid { get; set; }

    public string HostName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string AppName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ClientInterfaceName { get; set; } = "";
    public string Language { get; set; } = "";
    public string Database { get; set; } = "";
    public byte[] ClientId { get; set; } = new byte[6];
    public byte[] Sspi { get; set; } = [];
    public string AttachDbFile { get; set; } = "";
    public string ChangePassword { get; set; } = "";

    /// <summary>
    /// The FeatureExt block from its first feature through the 0xFF terminator, kept as raw bytes
    /// because DaffyZoom forwards it rather than interprets it. Null when the client sent none.
    /// </summary>
    public byte[]? FeatureExt { get; set; }

    public bool UsesIntegratedSecurity => (OptionFlags2 & IntegratedSecurityFlag) != 0 || Sspi.Length > 0;

    /// <summary>OptionFlags3 is a reserved byte before TDS 7.2, and such a login has no change-password entry.</summary>
    public bool RequestsPasswordChange =>
        (!IsPre72 && (OptionFlags3 & ChangePasswordFlag) != 0) || ChangePassword.Length > 0;

    /// <summary>TDS 7.1 or 7.0: the 86-byte fixed part, and no FeatureExt.</summary>
    public bool IsPre72 => TdsConstants.IsPre72(TdsVersion);

    /// <summary>The fixed part's length for this login's TDS version.</summary>
    public int HeaderLength => IsPre72 ? FixedLengthPre72 : FixedLength;

    public bool ReadOnlyIntent => (TypeFlags & ReadOnlyIntentFlag) != 0;

    /// <summary>Feature ids in the FeatureExt block, in the order sent.</summary>
    public IReadOnlyList<byte> FeatureIds
    {
        get
        {
            var ids = new List<byte>();
            if (FeatureExt is null) return ids;

            int position = 0;
            while (position < FeatureExt.Length)
            {
                byte id = FeatureExt[position++];
                if (id == TdsConstants.FeatureId.Terminator || position + 4 > FeatureExt.Length) break;
                ids.Add(id);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(FeatureExt.AsSpan(position));
                position += 4 + (int)length;
            }

            return ids;
        }
    }

    /// <summary>
    /// Adds a feature to the FeatureExt block, ahead of its terminator, creating the block when the
    /// client sent none.
    /// </summary>
    public void AddFeature(byte id, ReadOnlySpan<byte> data)
    {
        var block = new TdsWriter();
        if (FeatureExt is { Length: > 0 } existing)
        {
            int position = 0;
            while (position < existing.Length && existing[position] != TdsConstants.FeatureId.Terminator && position + 5 <= existing.Length)
                position += 5 + (int)BinaryPrimitives.ReadUInt32LittleEndian(existing.AsSpan(position + 1));
            block.WriteBytes(existing.AsSpan(0, Math.Min(position, existing.Length)));
        }

        block.WriteByte(id);
        block.WriteUInt32((uint)data.Length);
        block.WriteBytes(data);
        block.WriteByte(TdsConstants.FeatureId.Terminator);
        FeatureExt = block.ToArray();
    }

    public string TdsVersionName => TdsConstants.VersionName(TdsVersion);

    public Login7 Clone()
    {
        var copy = (Login7)MemberwiseClone();
        copy.ClientId = (byte[])ClientId.Clone();
        copy.Sspi = (byte[])Sspi.Clone();
        copy.FeatureExt = (byte[]?)FeatureExt?.Clone();
        return copy;
    }

    public static Login7 Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedLengthPre72)
            throw new InvalidDataException($"LOGIN7 is {payload.Length} bytes, shorter than any fixed part");

        uint tdsVersion = U32(payload, 4);
        bool pre72 = TdsConstants.IsPre72(tdsVersion);
        int fixedLength = pre72 ? FixedLengthPre72 : FixedLength;
        if (payload.Length < fixedLength)
            throw new InvalidDataException($"LOGIN7 is {payload.Length} bytes, shorter than its {fixedLength}-byte fixed part");

        var login = new Login7
        {
            TdsVersion = tdsVersion,
            PacketSize = U32(payload, 8),
            ClientProgVer = U32(payload, 12),
            ClientPid = U32(payload, 16),
            ConnectionId = U32(payload, 20),
            OptionFlags1 = payload[24],
            OptionFlags2 = payload[25],
            TypeFlags = payload[26],
            OptionFlags3 = payload[27],
            ClientTimeZone = BinaryPrimitives.ReadInt32LittleEndian(payload[28..]),
            ClientLcid = U32(payload, 32),

            HostName = ReadText(payload, 36),
            UserName = ReadText(payload, 40),
            Password = ReadScrambled(payload, 44),
            AppName = ReadText(payload, 48),
            ServerName = ReadText(payload, 52),
            ClientInterfaceName = ReadText(payload, 60),
            Language = ReadText(payload, 64),
            Database = ReadText(payload, 68),
            ClientId = payload.Slice(72, 6).ToArray(),
            AttachDbFile = ReadText(payload, 82),
            ChangePassword = pre72 ? "" : ReadScrambled(payload, 86),
        };

        // SSPI's length is in bytes, and 0xFFFF means "too long for 16 bits, see cbSSPILong" (7.2+).
        int sspiOffset = U16(payload, 78);
        int sspiLength = U16(payload, 80);
        if (sspiLength == 0xFFFF && !pre72) sspiLength = (int)U32(payload, 90);
        if (sspiLength > 0 && sspiOffset + sspiLength <= payload.Length)
            login.Sspi = payload.Slice(sspiOffset, sspiLength).ToArray();

        // fExtension is a TDS 7.4 bit; before 7.2 the whole byte is reserved (the Windows drivers
        // send 0x08 there).
        if (!pre72 && (login.OptionFlags3 & ExtensionFlag) != 0)
        {
            int extensionOffset = U16(payload, 56);
            int extensionLength = U16(payload, 58);
            if (extensionLength >= 4 && extensionOffset + 4 <= payload.Length)
            {
                int featureOffset = (int)U32(payload, extensionOffset);
                if (featureOffset >= FixedLength && featureOffset < payload.Length)
                    login.FeatureExt = ReadFeatureBlock(payload[featureOffset..]);
            }
        }

        return login;
    }

    /// <summary>
    /// Writes the message in the same order Microsoft.Data.SqlClient does, so a login that was
    /// parsed and not changed comes out byte for byte as it went in. A login before TDS 7.2 gets
    /// its own 86-byte fixed part, which is what the target expects for that version.
    /// </summary>
    public byte[] Build()
    {
        var data = new TdsWriter();
        var table = new TdsWriter();
        bool pre72 = IsPre72;
        int fixedLength = HeaderLength;

        int NextOffset() => fixedLength + data.Length;

        void Text(string value)
        {
            table.WriteUInt16((ushort)NextOffset());
            table.WriteUInt16((ushort)value.Length);
            data.WriteBytes(Encoding.Unicode.GetBytes(value));
        }

        void Scrambled(string value)
        {
            table.WriteUInt16((ushort)NextOffset());
            table.WriteUInt16((ushort)value.Length);
            data.WriteBytes(Scramble(value));
        }

        bool hasFeatures = FeatureExt is { Length: > 0 };
        if (hasFeatures && pre72)
            throw new InvalidOperationException($"a TDS {TdsVersionName} login cannot carry a FeatureExt block");

        Text(HostName);
        Text(UserName);
        Scrambled(Password);
        Text(AppName);
        Text(ServerName);

        // The extension slot holds a 4-byte offset to the FeatureExt block, which goes last; its
        // value is patched once everything in between has been laid out.
        int featureOffsetPosition = -1;
        table.WriteUInt16((ushort)NextOffset());
        if (hasFeatures)
        {
            table.WriteUInt16(4);
            featureOffsetPosition = data.Length;
            data.WriteUInt32(0);
        }
        else
        {
            table.WriteUInt16(0);
        }

        Text(ClientInterfaceName);
        Text(Language);
        Text(Database);
        table.WriteBytes(ClientId.Length == 6 ? ClientId : new byte[6]);

        table.WriteUInt16((ushort)NextOffset());
        table.WriteUInt16((ushort)Math.Min(Sspi.Length, 0xFFFF));
        data.WriteBytes(Sspi);

        Text(AttachDbFile);
        if (!pre72)
        {
            Scrambled(ChangePassword);
            table.WriteUInt32(Sspi.Length >= 0xFFFF ? (uint)Sspi.Length : 0);   // cbSSPILong
        }

        if (hasFeatures)
        {
            data.PatchUInt32(featureOffsetPosition, (uint)NextOffset());
            data.WriteBytes(FeatureExt!);
        }

        // Before TDS 7.2 the byte is reserved: kept as the client sent it.
        byte optionFlags3 = pre72 ? OptionFlags3
            : hasFeatures ? (byte)(OptionFlags3 | ExtensionFlag)
            : (byte)(OptionFlags3 & ~ExtensionFlag);

        var message = new TdsWriter();
        message.WriteUInt32((uint)(fixedLength + data.Length));
        message.WriteUInt32(TdsVersion);
        message.WriteUInt32(PacketSize);
        message.WriteUInt32(ClientProgVer);
        message.WriteUInt32(ClientPid);
        message.WriteUInt32(ConnectionId);
        message.WriteByte(OptionFlags1);
        message.WriteByte(OptionFlags2);
        message.WriteByte(TypeFlags);
        message.WriteByte(optionFlags3);
        message.WriteInt32(ClientTimeZone);
        message.WriteUInt32(ClientLcid);
        message.WriteBytes(table.AsMemory().Span);
        message.WriteBytes(data.AsMemory().Span);

        return message.ToArray();
    }

    /// <summary>
    /// The password is scrambled rather than encrypted: each byte of the UTF-16LE text has its
    /// nibbles swapped and is then XORed with 0xA5. On an unencrypted connection that makes it
    /// readable by anyone on the wire, which is why DaffyZoom encrypts by default.
    /// </summary>
    public static byte[] Scramble(string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(((bytes[i] << 4) | (bytes[i] >> 4)) ^ 0xA5);
        return bytes;
    }

    private static string Unscramble(ReadOnlySpan<byte> scrambled)
    {
        var bytes = new byte[scrambled.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte x = (byte)(scrambled[i] ^ 0xA5);
            bytes[i] = (byte)((x >> 4) | (x << 4));
        }
        return Encoding.Unicode.GetString(bytes);
    }

    /// <summary>
    /// Safe to log: never the password. The option flags are where clients ask for most of their
    /// session settings (ODBC_ON, for one, has the server apply the ANSI defaults), so they are
    /// here too, in wire order: OptionFlags1, OptionFlags2, TypeFlags, OptionFlags3. With the
    /// interface name, program version, PID, LCID and time zone, they are how clients differ
    /// before any SQL is sent.
    /// </summary>
    public override string ToString() =>
        $"user='{UserName}' host='{HostName}' app='{AppName}' server='{ServerName}' db='{Database}' " +
        $"tds={TdsVersionName} packetSize={PacketSize} " +
        $"features=[{string.Join(",", FeatureIds.Select(TdsConstants.FeatureId.Name))}] " +
        $"interface='{ClientInterfaceName}' progVer=0x{ClientProgVer:X8} pid={ClientPid} lcid=0x{ClientLcid:X4} " +
        $"tz={ClientTimeZone} flags={OptionFlags1:X2}-{OptionFlags2:X2}-{TypeFlags:X2}-{OptionFlags3:X2}" +
        (ReadOnlyIntent ? " readOnlyIntent" : "");

    private static string ReadText(ReadOnlySpan<byte> payload, int entry)
    {
        int offset = U16(payload, entry);
        int chars = U16(payload, entry + 2);
        if (chars == 0 || offset + chars * 2 > payload.Length) return "";
        return Encoding.Unicode.GetString(payload.Slice(offset, chars * 2));
    }

    private static string ReadScrambled(ReadOnlySpan<byte> payload, int entry)
    {
        int offset = U16(payload, entry);
        int chars = U16(payload, entry + 2);
        if (chars == 0 || offset + chars * 2 > payload.Length) return "";
        return Unscramble(payload.Slice(offset, chars * 2));
    }

    /// <summary>Walks feature entries to find where the block ends; a malformed one keeps the rest.</summary>
    private static byte[] ReadFeatureBlock(ReadOnlySpan<byte> block)
    {
        int position = 0;
        while (position < block.Length)
        {
            byte id = block[position++];
            if (id == TdsConstants.FeatureId.Terminator) return block[..position].ToArray();
            if (position + 4 > block.Length) break;

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(block[position..]);
            position += 4;
            if (length > (uint)(block.Length - position)) break;
            position += (int)length;
        }

        return block.ToArray();
    }

    private static ushort U16(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);

    private static uint U32(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
}
