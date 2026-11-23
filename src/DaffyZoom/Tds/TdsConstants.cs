namespace DaffyZoom.Tds;

/// <summary>
/// Wire constants for MS-TDS. Values cross-checked against the MS-TDS spec and the MIT-licensed
/// Microsoft.Data.SqlClient source (TdsEnums.cs), which is the client we implement against.
/// </summary>
internal static class TdsConstants
{
    // Packet header: Type(1) Status(1) Length(2, BIG-endian) SPID(2, BE) PacketId(1) Window(1).
    // The length field is the only big-endian value in an otherwise little-endian protocol.
    public const int HeaderLength = 8;
    public const int DefaultPacketSize = 4096;

    // TDS versions as LOGINACK carries them. 8.0 is numerically the smallest, so compare with care.
    public const uint TdsVersion73 = 0x730A0003;
    public const uint TdsVersion80 = 0x08000000;

    /// <summary>A TDS version as LOGIN7 or LOGINACK carries it, by name; 7.0 and 7.1 have two spellings each.</summary>
    public static string VersionName(uint version) => version switch
    {
        0x74000004 => "7.4",
        0x730B0003 => "7.3B",
        0x730A0003 => "7.3A",
        0x72090002 => "7.2",
        0x71000001 or 0x71000000 or 0x01000071 or 0x07010000 => "7.1",
        0x70000000 or 0x07000000 => "7.0",
        0x08000000 => "8.0",
        _ => $"0x{version:X8}",
    };

    /// <summary>
    /// TDS before 7.2: 7.1 (SQL Server 2000, and the SQLSRV32 ODBC driver and SQLOLEDB provider that
    /// still ship with Windows) and 7.0. Those clients send a LOGIN7 with an 86-byte fixed part and no
    /// FeatureExt, requests without ALL_HEADERS, and read 4-byte DONE row counts, 2-byte UserTypes and
    /// 2-byte line numbers. Every spelling <see cref="VersionName"/> knows for 7.0 and 7.1 counts.
    /// 0 - not known yet - counts as 7.2 or later, so nothing changes for anything that never asked.
    /// </summary>
    public static bool IsPre72(uint version) =>
        version is 0x70000000 or 0x07000000 or 0x71000000 or 0x71000001 or 0x01000071 or 0x07010000;

    /// <summary>The bytes of a DONE, DONEPROC or DONEINPROC after its token byte: the row count is 4 bytes before TDS 7.2, 8 after.</summary>
    public static int DoneLength(uint version) => IsPre72(version) ? 8 : 12;

    public static class PacketType
    {
        public const byte SqlBatch = 0x01;
        public const byte PreTds7Login = 0x02;
        public const byte Rpc = 0x03;
        public const byte TabularResult = 0x04;
        public const byte Attention = 0x06;
        public const byte BulkLoad = 0x07;
        public const byte FedAuthToken = 0x08;
        public const byte TransactionManager = 0x0E;
        public const byte Login7 = 0x10;
        public const byte Sspi = 0x11;
        public const byte PreLogin = 0x12;
    }

    /// <summary>Packet header Status byte is a bitmask, not an enum.</summary>
    public static class PacketStatus
    {
        public const byte Normal = 0x00;
        public const byte EndOfMessage = 0x01;
        public const byte Ignore = 0x02;
        public const byte ResetConnection = 0x08;
        public const byte ResetConnectionSkipTran = 0x10;
    }

    public static class PreLoginOption
    {
        public const byte Version = 0x00;
        public const byte Encryption = 0x01;
        public const byte InstOpt = 0x02;
        public const byte ThreadId = 0x03;
        public const byte Mars = 0x04;
        public const byte TraceId = 0x05;
        public const byte FedAuthRequired = 0x06;
        public const byte NonceOpt = 0x07;
        public const byte Terminator = 0xFF;
    }

    public static class Encryption
    {
        public const byte Off = 0x00;          // encrypt login packet only
        public const byte On = 0x01;           // encrypt everything
        public const byte NotSupported = 0x02;
        public const byte Required = 0x03;
    }

    public static class Token
    {
        public const byte AltMetadata = 0x88;
        public const byte AltRow = 0xD3;
        public const byte ColInfo = 0xA5;
        public const byte ColMetadata = 0x81;
        public const byte Done = 0xFD;
        public const byte DoneProc = 0xFE;
        public const byte DoneInProc = 0xFF;
        public const byte EnvChange = 0xE3;
        public const byte Error = 0xAA;
        public const byte FeatureExtAck = 0xAE;
        public const byte Info = 0xAB;
        public const byte LoginAck = 0xAD;
        public const byte NbcRow = 0xD2;
        public const byte Order = 0xA9;
        public const byte ReturnStatus = 0x79;
        public const byte ReturnValue = 0xAC;
        public const byte Row = 0xD1;
        public const byte SspiToken = 0xED;
        public const byte TabName = 0xA4;
        public const byte SessionState = 0xE4;
        public const byte FedAuthInfo = 0xEE;
        public const byte DataClassification = 0xA3;
        public const byte Offset = 0x78;
    }

    /// <summary>DONE / DONEPROC / DONEINPROC status bitmask (USHORT).</summary>
    public static class DoneStatus
    {
        public const ushort Final = 0x0000;
        public const ushort More = 0x0001;
        public const ushort Error = 0x0002;
        public const ushort InTransaction = 0x0004;
        public const ushort Count = 0x0010;   // DoneRowCount field is meaningful
        public const ushort Attention = 0x0020;
        public const ushort ServerError = 0x0100;
    }

    public static class EnvChangeType
    {
        public const byte Database = 0x01;
        public const byte Language = 0x02;
        public const byte CharacterSet = 0x03;
        public const byte PacketSize = 0x04;
        public const byte SqlCollation = 0x07;
        public const byte BeginTransaction = 0x08;
        public const byte CommitTransaction = 0x09;
        public const byte RollbackTransaction = 0x0A;
        public const byte ResetConnectionAck = 0x12;
        public const byte RoutingInformation = 0x14;
    }

    /// <summary>
    /// LOGIN7 FeatureExt ids, as named in Microsoft.Data.SqlClient's TdsEnums. DaffyZoom passes the
    /// block through untouched; the names exist for logging, and FEDAUTH is the one it refuses.
    /// </summary>
    public static class FeatureId
    {
        public const byte SessionRecovery = 0x01;
        public const byte FedAuth = 0x02;
        public const byte ColumnEncryption = 0x04;
        public const byte GlobalTransactions = 0x05;
        public const byte AzureSqlSupport = 0x08;
        public const byte DataClassification = 0x09;
        public const byte Utf8Support = 0x0A;
        public const byte SqlDnsCaching = 0x0B;
        public const byte JsonSupport = 0x0D;
        public const byte VectorSupport = 0x0E;

        /// <summary>Sent by the Microsoft JDBC Driver 13.4, which names it TDS_FEATURE_EXT_USERAGENT.</summary>
        public const byte UserAgent = 0x10;
        public const byte Terminator = 0xFF;

        public static string Name(byte id) => id switch
        {
            SessionRecovery => "SESSIONRECOVERY",
            FedAuth => "FEDAUTH",
            ColumnEncryption => "COLUMNENCRYPTION",
            GlobalTransactions => "GLOBALTRANSACTIONS",
            AzureSqlSupport => "AZURESQLSUPPORT",
            DataClassification => "DATACLASSIFICATION",
            Utf8Support => "UTF8SUPPORT",
            SqlDnsCaching => "SQLDNSCACHING",
            JsonSupport => "JSONSUPPORT",
            VectorSupport => "VECTORSUPPORT",
            UserAgent => "USERAGENT",
            _ => $"0x{id:X2}",
        };
    }

    /// <summary>
    /// ALL_HEADERS block prefixing every TDS 7.2+ SQL_BATCH and RPC request. The single most
    /// commonly missed piece of the request format.
    /// </summary>
    public static class HeaderType
    {
        public const ushort QueryNotifications = 0x0001;
        public const ushort TransactionDescriptor = 0x0002;
        public const ushort TraceActivity = 0x0003;
    }

    /// <summary>
    /// RPC NameLenProcID == 0xFFFF means a USHORT well-known ProcID follows instead of a name.
    /// SqlClient uses this form, never the literal name.
    /// </summary>
    public const ushort RpcProcIdSwitch = 0xFFFF;

    public static class ProcId
    {
        public const ushort Cursor = 1;
        public const ushort CursorOpen = 2;
        public const ushort CursorPrepare = 3;
        public const ushort CursorExecute = 4;
        public const ushort CursorPrepExec = 5;
        public const ushort CursorUnprepare = 6;
        public const ushort CursorFetch = 7;
        public const ushort CursorOption = 8;
        public const ushort CursorClose = 9;
        public const ushort ExecuteSql = 10;
        public const ushort Prepare = 11;
        public const ushort Execute = 12;
        public const ushort PrepExec = 13;
        public const ushort PrepExecRpc = 14;
        public const ushort Unprepare = 15;

        public static string Name(ushort id) => id switch
        {
            Cursor => "sp_cursor",
            CursorOpen => "sp_cursoropen",
            CursorPrepare => "sp_cursorprepare",
            CursorExecute => "sp_cursorexecute",
            CursorPrepExec => "sp_cursorprepexec",
            CursorUnprepare => "sp_cursorunprepare",
            CursorFetch => "sp_cursorfetch",
            CursorOption => "sp_cursoroption",
            CursorClose => "sp_cursorclose",
            ExecuteSql => "sp_executesql",
            Prepare => "sp_prepare",
            Execute => "sp_execute",
            PrepExec => "sp_prepexec",
            PrepExecRpc => "sp_prepexecrpc",
            Unprepare => "sp_unprepare",
            _ => "proc#" + id,
        };
    }

    public static string PacketTypeName(byte type) => type switch
    {
        PacketType.SqlBatch => "SQL_BATCH",
        PacketType.PreTds7Login => "PRE_TDS7_LOGIN",
        PacketType.Rpc => "RPC",
        PacketType.TabularResult => "TABULAR_RESULT",
        PacketType.Attention => "ATTENTION",
        PacketType.BulkLoad => "BULK_LOAD",
        PacketType.FedAuthToken => "FEDAUTH_TOKEN",
        PacketType.TransactionManager => "TRANSACTION_MGR",
        PacketType.Login7 => "LOGIN7",
        PacketType.Sspi => "SSPI",
        PacketType.PreLogin => "PRELOGIN",
        _ => "UNKNOWN",
    };

    public static string TokenName(byte token) => token switch
    {
        Token.AltMetadata => "ALTMETADATA",
        Token.AltRow => "ALTROW",
        Token.ColInfo => "COLINFO",
        Token.ColMetadata => "COLMETADATA",
        Token.Done => "DONE",
        Token.DoneProc => "DONEPROC",
        Token.DoneInProc => "DONEINPROC",
        Token.EnvChange => "ENVCHANGE",
        Token.Error => "ERROR",
        Token.FeatureExtAck => "FEATUREEXTACK",
        Token.Info => "INFO",
        Token.LoginAck => "LOGINACK",
        Token.NbcRow => "NBCROW",
        Token.Order => "ORDER",
        Token.ReturnStatus => "RETURNSTATUS",
        Token.ReturnValue => "RETURNVALUE",
        Token.Row => "ROW",
        Token.SspiToken => "SSPI",
        Token.TabName => "TABNAME",
        Token.SessionState => "SESSIONSTATE",
        Token.FedAuthInfo => "FEDAUTHINFO",
        Token.DataClassification => "DATACLASSIFICATION",
        Token.Offset => "OFFSET",
        _ => "UNKNOWN",
    };

    public static string EncryptionName(byte value) => value switch
    {
        Encryption.Off => "ENCRYPT_OFF (login packet encrypted)",
        Encryption.On => "ENCRYPT_ON",
        Encryption.NotSupported => "ENCRYPT_NOT_SUP (no TLS)",
        Encryption.Required => "ENCRYPT_REQ",
        _ => "unknown",
    };

    public static string PreLoginOptionName(byte option) => option switch
    {
        PreLoginOption.Version => "VERSION",
        PreLoginOption.Encryption => "ENCRYPTION",
        PreLoginOption.InstOpt => "INSTOPT",
        PreLoginOption.ThreadId => "THREADID",
        PreLoginOption.Mars => "MARS",
        PreLoginOption.TraceId => "TRACEID",
        PreLoginOption.FedAuthRequired => "FEDAUTHREQUIRED",
        PreLoginOption.NonceOpt => "NONCEOPT",
        _ => "OPTION",
    };
}
