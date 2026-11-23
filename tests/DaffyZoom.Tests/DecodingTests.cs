using System.Globalization;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>Decoding real request payloads, captured from Microsoft.Data.SqlClient through DaffyZoom with --raw.</summary>
public class RequestDecodingTests
{
    internal static byte[] Fixture(string name) =>
        Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".b64")));

    [Fact]
    public void SpExecuteSqlWithEveryCommonType()
    {
        var call = Assert.Single(RequestParser.ParseRpc(Fixture("rpc-parameters")));

        Assert.Equal("sp_executesql", call.ProcName);
        Assert.Equal("SELECT @i + 1, @s + N'!', @d * 2, @dt, @g, @b, @bin, @n", call.Sql);
        Assert.Null(call.Remainder);

        var p = call.Parameters.ToDictionary(x => x.Name);
        Assert.Equal(41, p["@i"].Value);
        Assert.Equal("int", p["@i"].DeclaredType);
        Assert.Equal("héllo", p["@s"].Value);
        Assert.Equal("nvarchar(50)", p["@s"].DeclaredType);
        Assert.Equal(10.25m, p["@d"].Value);
        Assert.Equal("decimal(18,2)", p["@d"].DeclaredType);
        Assert.Equal(new DateTime(2026, 9, 23, 1, 2, 3), p["@dt"].Value);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), p["@g"].Value);
        Assert.Equal(true, p["@b"].Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, p["@bin"].Value);
        Assert.Null(p["@n"].Value);
        Assert.All(call.Parameters, x => Assert.False(x.IsOutput));
    }

    /// <summary>
    /// datetime is days since 1900-01-01, then 1/300-second units since midnight. 0.3.0 multiplied
    /// the units as a uint, which wrapped for any time after 00:02:23.163.
    /// </summary>
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(1, "00:00:00.003")]
    [InlineData(2, "00:00:00.007")]
    [InlineData(42_949, "00:02:23.163")]        // the last time 0.3.0 got right
    [InlineData(42_950, "00:02:23.167")]        // the first it got wrong
    [InlineData(12_960_000, "12:00:00")]
    [InlineData(25_919_999, "23:59:59.997")]    // the last of the day
    public void DatetimeIsDecodedWhateverTheTimeOfDay(int threeHundredths, string time)
    {
        var expected = new DateTime(2026, 10, 10) + TimeSpan.Parse(time, CultureInfo.InvariantCulture);

        var cursor = new TdsCursor(Datetime(0x08, 46_303, threeHundredths));            // DATETIMN, length 8
        Assert.Equal(expected, TypeInfoReader.ReadValue(ref cursor, new TypeInfo(0x6F, 8)));

        cursor = new TdsCursor(Datetime(null, 46_303, threeHundredths));                // fixed-length DATETIME
        Assert.Equal(expected, TypeInfoReader.ReadValue(ref cursor, new TypeInfo(0x3D, 8)));
    }

    private static byte[] Datetime(byte? length, int days, int threeHundredths)
    {
        var w = new TdsWriter();
        if (length is { } n) w.WriteByte(n);
        w.WriteInt32(days);
        w.WriteInt32(threeHundredths);
        return w.ToArray();
    }

    [Fact]
    public void TableValuedParameterComesBackAsRows()
    {
        var call = Assert.Single(RequestParser.ParseRpc(Fixture("rpc-tvp")));
        Assert.Equal("dbo.GetCustomersByIds", call.ProcName);

        var table = Assert.IsType<TableValue>(Assert.Single(call.Parameters).Value);
        Assert.Equal("dbo.IdList", table.Type.TypeName);
        Assert.Equal([1, 3, 4], table.Rows.Select(r => (int)r[0]!));
    }

    [Fact]
    public void StoredProcedureWithOutputParameter()
    {
        var call = Assert.Single(RequestParser.ParseRpc(Fixture("rpc-proc-output")));
        Assert.Equal("dbo.GetCustomer", call.ProcName);
        Assert.Equal(2, call.Parameters[0].Value);
        Assert.True(call.Parameters[1].IsOutput);
        Assert.Equal("nvarchar(100)", call.Parameters[1].DeclaredType);
    }

    [Fact]
    public void PreparedStatementsCarryStatementThenHandle()
    {
        var prepare = Assert.Single(RequestParser.ParseRpc(Fixture("rpc-prepexec")));
        Assert.Equal("sp_prepexec", prepare.ProcName);
        Assert.Equal("SELECT Name FROM dbo.Customers WHERE Id = @id", prepare.Sql);
        Assert.Equal("@id int", prepare.ParamDeclarations);
        Assert.Equal(1, Assert.Single(prepare.Parameters).Value);

        var execute = Assert.Single(RequestParser.ParseRpc(Fixture("rpc-execute")));
        Assert.Equal("sp_execute", execute.ProcName);
        Assert.NotNull(execute.Handle);
        Assert.Null(execute.Sql);
        Assert.Equal("@id", Assert.Single(execute.Parameters).Name);
    }

    [Fact]
    public void TransactionManagerRequests()
    {
        var begin = TransactionRequestParser.Parse(Fixture("txn-begin"));
        Assert.Equal("begin", begin.Operation);
        Assert.Equal("read committed", begin.IsolationLevel);

        byte[] save = Fixture("txn-save");
        Assert.Equal("save", TransactionRequestParser.Parse(save).Operation);
        Assert.Equal("half", TransactionRequestParser.Parse(save).Name);
        Assert.NotEqual(0UL, AllHeaders.TransactionDescriptor(save));
    }

    [Fact]
    public void TruncatedRequestsDecodeWhatTheyCanAndKeepTheRest()
    {
        byte[] whole = Fixture("rpc-parameters");
        var calls = RequestParser.ParseRpc(whole.AsSpan(0, whole.Length - 20));
        var call = Assert.Single(calls);
        Assert.NotNull(call.Remainder);
        Assert.NotEmpty(call.Parameters);
    }

    [Fact]
    public void PromptKeepsQuotedArgumentsTogether()
    {
        Assert.Equal(
            [":user", "add", "alice", "--target", "Server=tcp:sql01,1433;User Id=svc_reports;Initial Catalog=Sales"],
            Repl.ReplLoop.Tokenize("  :user add  alice --target \"Server=tcp:sql01,1433;User Id=svc_reports;Initial Catalog=Sales\" "));
        Assert.Equal([":sample", "10%", "session"], Repl.ReplLoop.Tokenize(":sample 10%\tsession"));
        Assert.Equal([":x", ""], Repl.ReplLoop.Tokenize(":x \"\""));
    }
}

public class ResponseReaderTests
{

    [Fact]
    public void FindsErrorsCountsContextChangesAndOutputs()
    {
        var w = new TdsWriter();

        // COLMETADATA: one nullable int column "x", with the (empty) CekTable column encryption adds.
        w.WriteByte(0x81); w.WriteUInt16(1); w.WriteUInt16(0);
        w.WriteUInt32(0); w.WriteUInt16(0x0009); w.WriteByte(0x26); w.WriteByte(4); w.WriteBVarChar("x");
        // Two ROWs, one NBCROW with the column NULL.
        w.WriteByte(0xD1); w.WriteByte(4); w.WriteInt32(10);
        w.WriteByte(0xD1); w.WriteByte(4); w.WriteInt32(20);
        w.WriteByte(0xD2); w.WriteByte(0x01);
        Done(w, 0xFF, 0x0011, 3);
        // ENVCHANGE database, an INFO and an ERROR.
        w.WriteByte(0xE3); int length = w.ReserveUInt16(); w.WriteByte(1); w.WriteBVarChar("Other"); w.WriteBVarChar("db"); w.PatchLengthFrom(length);
        Message(w, 0xAB, 5701, 0, "Changed database context to 'Other'.");
        Message(w, 0xAA, 50000, 16, "boom");
        // RETURNSTATUS, RETURNVALUE (ordinal 0: a prepared handle), DONEPROC.
        w.WriteByte(0x79); w.WriteInt32(0);
        w.WriteByte(0xAC); w.WriteUInt16(0); w.WriteBVarChar("@handle"); w.WriteByte(1); w.WriteUInt32(0); w.WriteUInt16(0);
        w.WriteByte(0x26); w.WriteByte(4); w.WriteByte(4); w.WriteInt32(7);
        Done(w, 0xFE, 0x0002, 0);

        var detail = ResponseReader.Read(w.ToArray(), columnEncryption: true);

        Assert.Null(detail.Problem);
        Assert.Equal([3L], detail.ResultSetRows);
        Assert.Equal([3UL], detail.RowCounts);
        Assert.Equal("Other", detail.Database);
        Assert.Equal(5701, Assert.Single(detail.Messages).Number);
        Assert.Equal("boom", Assert.Single(detail.Errors).Text);
        Assert.Equal(0, detail.ReturnStatus);
        Assert.Equal(7, detail.OutputHandle);
        Assert.Equal(("@handle", (object?)7), Assert.Single(detail.OutputParameters));
    }

    [Fact]
    public void StopsAtWhatItCannotWalkButKeepsWhatItFound()
    {
        var w = new TdsWriter();
        Message(w, 0xAA, 208, 16, "Invalid object name 'nope'.");
        w.WriteByte(0x88);   // ALTMETADATA (COMPUTE BY), long gone from SQL Server
        w.WriteBytes(new byte[10]);

        var detail = ResponseReader.Read(w.ToArray(), columnEncryption: true);
        Assert.Equal(208, Assert.Single(detail.Errors).Number);
        Assert.NotNull(detail.Problem);
    }

    [Fact]
    public void ServerCursorFetchRowsAreNotReadAsTokens()
    {
        // sp_cursorfetch: COLMETADATA 0xFFFF - no new metadata, it came with the cursor's open - then
        // a row whose first value byte, read as a token, would be an ERROR (0xAA).
        var w = new TdsWriter();
        w.WriteByte(0x81); w.WriteUInt16(0xFFFF);
        w.WriteByte(0xD1); w.WriteByte(0xAA); w.WriteBytes(new byte[12]);
        Done(w, 0xFF, 0x0011, 1);

        var detail = ResponseReader.Read(w.ToArray(), columnEncryption: true);
        Assert.Equal("rows of a server-cursor fetch; their metadata came with the cursor's open", detail.Problem);
        Assert.Empty(detail.Errors);
    }

    [Fact]
    public void ServerCursorFetchWithoutRowsIsReadWhole()
    {
        var w = new TdsWriter();
        w.WriteByte(0x81); w.WriteUInt16(0xFFFF);
        Done(w, 0xFD, 0x0000, 0);
        w.WriteByte(0x79); w.WriteInt32(0);
        Done(w, 0xFE, 0x0000, 0);

        var detail = ResponseReader.Read(w.ToArray(), columnEncryption: true);
        Assert.Null(detail.Problem);
        Assert.Equal(0, detail.ReturnStatus);
    }

    private static void Done(TdsWriter w, byte token, ushort status, ulong count)
    {
        w.WriteByte(token); w.WriteUInt16(status); w.WriteUInt16(0xC1); w.WriteUInt64(count);
    }

    private static void Message(TdsWriter w, byte token, int number, byte severity, string text)
    {
        w.WriteByte(token);
        int length = w.ReserveUInt16();
        w.WriteInt32(number); w.WriteByte(1); w.WriteByte(severity); w.WriteUsVarChar(text);
        w.WriteBVarChar("srv"); w.WriteBVarChar(""); w.WriteInt32(1);
        w.PatchLengthFrom(length);
    }
}
