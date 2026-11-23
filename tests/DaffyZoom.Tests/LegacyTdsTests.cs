using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DaffyZoom.Cache;
using DaffyZoom.Proxy;
using DaffyZoom.Security;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>
/// TDS 7.1, as the SQLSRV32 ODBC driver and the SQLOLEDB provider that ship with Windows still speak
/// it. The fixtures are real, from DaffyTee's work on the same drivers: the two LOGIN7s were captured
/// from those drivers (Workstation ID WS42, the ClientID - the sender's MAC address - zeroed), and
/// the three responses are SQL Server 2022's own answers to the ODBC driver: a refused login, a
/// login, and SELECT @@VERSION. Such a session is relayed, never cached; what matters is that it
/// logs in, is refused in a shape it can read, and has its USE followed.
/// </summary>
public class LegacyTdsTests
{
    private const uint Tds71 = 0x71000001;

    private static byte[] Fixture(string name) => RequestDecodingTests.Fixture(name);

    [Fact]
    public void WindowsOdbcLoginIsReadAsTds71()
    {
        var login = Login7.Parse(Fixture("login7-tds71-odbc"));

        Assert.Equal("7.1", login.TdsVersionName);
        Assert.Equal(86, login.HeaderLength);
        Assert.Equal("WS42", login.HostName);
        Assert.Equal("legacy", login.UserName);
        Assert.Equal("legacy-pass", login.Password);
        Assert.Equal("LegacyApp", login.AppName);
        Assert.Equal("Sales", login.Database);
        Assert.Equal("ODBC", login.ClientInterfaceName);
        Assert.Equal(0u, login.PacketSize);
        Assert.Empty(login.FeatureIds);

        // 0.1.3 read bytes 86-93 - the start of the first string - as a change-password entry, and
        // refused the login whenever that entry happened to fall inside the packet.
        Assert.Equal("", login.ChangePassword);
        Assert.False(login.RequestsPasswordChange);
        // The driver sends 0x08 in what is a reserved byte before TDS 7.2.
        Assert.Equal(0x08, login.OptionFlags3);
    }

    [Fact]
    public void WindowsOleDbLoginIsReadAsTds71()
    {
        var login = Login7.Parse(Fixture("login7-tds71-oledb"));

        Assert.Equal("7.1", login.TdsVersionName);
        Assert.Equal("OLEDB", login.ClientInterfaceName);
        Assert.Equal(4096u, login.PacketSize);
        Assert.Equal(0x10, login.TypeFlags);                 // fOLEDB
        Assert.False(login.RequestsPasswordChange);
        Assert.Equal("legacy-pass", login.Password);
    }

    /// <summary>
    /// The 0.1.3 failure, made certain: a TDS 7.1 login long enough that its first string's first two
    /// characters, read as a change-password entry, point inside the packet.
    /// </summary>
    [Fact]
    public void LongTds71LoginIsNotTakenForAPasswordChange()
    {
        var login = Login7.Parse(Fixture("login7-tds71-odbc"));
        login.AppName = new string('L', 200);                // 'W' 'S' of WS42: 83 characters at offset 87, now in range

        var parsed = Login7.Parse(login.Build());

        Assert.Equal("WS42", parsed.HostName);
        Assert.Equal(login.AppName, parsed.AppName);
        Assert.False(parsed.RequestsPasswordChange);
    }

    [Fact]
    public void Tds71LoginIsRebuiltWithItsOwnFixedPart()
    {
        var original = Login7.Parse(Fixture("login7-tds71-odbc"));
        byte[] built = original.Build();

        Assert.Equal(86, BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(36)));   // ibHostName: right after the fixed part
        Assert.Equal(built.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(built));

        var again = Login7.Parse(built);
        Assert.Equal(original.TdsVersion, again.TdsVersion);
        Assert.Equal(original.HostName, again.HostName);
        Assert.Equal(original.Password, again.Password);
        Assert.Equal(original.ClientInterfaceName, again.ClientInterfaceName);
        Assert.Equal(original.Database, again.Database);
        Assert.Equal(original.OptionFlags3, again.OptionFlags3);
        Assert.Equal(Convert.ToHexString(built), Convert.ToHexString(again.Build()));
    }

    [Fact]
    public void Tds71LoginIsRewrittenForTheTargetAsTds71()
    {
        var client = Login7.Parse(Fixture("login7-tds71-odbc"));
        var target = TargetEndpoint.Parse("Server=tcp:sql01,1433;User Id=svc_app;Password=Svc!App2026;Initial Catalog=Other");

        var rewritten = Login7.Parse(UpstreamConnector.ForTarget(client, target, client.Database).Build());

        Assert.Equal(Tds71, rewritten.TdsVersion);
        Assert.Equal("svc_app", rewritten.UserName);
        Assert.Equal("Svc!App2026", rewritten.Password);
        Assert.Equal("Sales", rewritten.Database);
        Assert.Equal("ODBC", rewritten.ClientInterfaceName);
        Assert.Equal(0x08, rewritten.OptionFlags3);          // a reserved byte, kept as sent
        Assert.Null(rewritten.FeatureExt);
        Assert.False(rewritten.RequestsPasswordChange);
    }

    [Fact]
    public void Tds71LoginCannotCarryFeatures()
    {
        var login = Login7.Parse(Fixture("login7-tds71-odbc"));
        login.AddFeature(TdsConstants.FeatureId.FedAuth, [0x05, 0x01]);
        Assert.Throws<InvalidOperationException>(() => login.Build());
    }

    [Fact]
    public void PasswordChangeIsStillRecognisedFromTds72On()
    {
        var withEntry = new Login7 { TdsVersion = 0x74000004, UserName = "u", Password = "p", ChangePassword = "new" };
        Assert.True(Login7.Parse(withEntry.Build()).RequestsPasswordChange);

        var withFlag = new Login7 { TdsVersion = 0x74000004, UserName = "u", Password = "p", OptionFlags3 = 0x01 };
        Assert.True(Login7.Parse(withFlag.Build()).RequestsPasswordChange);

        // The same bit means nothing in TDS 7.1, where the byte is reserved.
        var old = new Login7 { TdsVersion = Tds71, UserName = "u", Password = "p", OptionFlags3 = 0x01 };
        Assert.False(Login7.Parse(old.Build()).RequestsPasswordChange);
    }

    [Fact]
    public void LoginSummaryShowsInterfaceAndOptionFlags()
    {
        string summary = Login7.Parse(Fixture("login7-tds71-odbc")).ToString();
        Assert.Contains("tds=7.1", summary);
        Assert.Contains("interface='ODBC'", summary);
        Assert.Contains("flags=E0-03-00-08", summary);
        Assert.DoesNotContain("legacy-pass", summary);
    }

    [Fact]
    public void Tds71LoginAckIsRead()
    {
        var response = LoginResponse.Parse(Fixture("tds71-loginack"), Tds71);

        Assert.True(response.Succeeded);
        Assert.Equal(Tds71, response.TdsVersion);
        Assert.Equal("7.1", response.TdsVersionName);
        Assert.Equal("DaffyTeeTest", response.Database);
        Assert.Equal(4096, response.PacketSize);
    }

    [Fact]
    public void Tds71LoginRefusalIsRead()
    {
        var response = LoginResponse.Parse(Fixture("tds71-login-refused"), Tds71);

        Assert.False(response.Succeeded);
        Assert.Equal(ServerMessages.LoginFailed, Assert.Single(response.Errors).Number);
    }

    /// <summary>
    /// DaffyZoom's own refusal, for a TDS 7.1 client, has SQL Server's shape: a 2-byte line number and
    /// an 8-byte DONE. With the 7.2 shape the Windows drivers reported "SQL Server version 6.5 and all
    /// previous versions are no longer supported" instead of the refusal.
    /// </summary>
    [Fact]
    public void RefusalForATds71ClientHasSqlServersShape()
    {
        const string text = "Login failed for user 'legacy'.";
        byte[] ours = ServerMessages.Error(ServerMessages.LoginFailed, 14, text, tdsVersion: Tds71);
        byte[] theirs = Fixture("tds71-login-refused");

        // ERROR: number(4) state(1) class(1) text(US_VARCHAR) server(B_VARCHAR) proc(B_VARCHAR) line(2).
        int body = BinaryPrimitives.ReadUInt16LittleEndian(ours.AsSpan(1));
        int expected = 4 + 1 + 1 + 2 + 2 * text.Length + 1 + 2 * ServerMessages.ServerName.Length + 1 + 2;
        Assert.Equal(expected, body);

        // DONE(ERROR), 8 bytes after its token, exactly as SQL Server ends its own refusal.
        Assert.Equal(Convert.ToHexString(theirs[^9..]), Convert.ToHexString(ours[^9..]));
        Assert.Equal(3 + body + 9, ours.Length);

        var read = LoginResponse.Parse(ours, Tds71);
        Assert.Equal(text, Assert.Single(read.Errors).Text);
    }

    [Fact]
    public void RefusalForCurrentClientsIsUnchanged()
    {
        byte[] ours = ServerMessages.Error(ServerMessages.LoginFailed, 14, "x");
        Assert.Equal(0xFD, ours[^13]);                       // DONE with an 8-byte row count
        Assert.Equal(ServerMessages.LoginFailed, Assert.Single(LoginResponse.Parse(ours).Errors).Number);
    }

    /// <summary>
    /// Through a real listener and front session, the Windows ODBC driver's own LOGIN7: refused before
    /// any target is tried, in the shape a TDS 7.1 client reads. An Entra ID mapping cannot serve it
    /// (FEDAUTH is a TDS 7.4 feature), and a wrong password is a wrong password.
    /// </summary>
    [Theory]
    [InlineData(true, "legacy-pass", "bad_mapping")]
    [InlineData(false, "another-password", "bad_password")]
    public async Task Tds71ClientIsRefusedInItsOwnShape(bool entra, string password, string expectedCode)
    {
        using var directory = new TempDirectory();
        var users = UserStore.Open(directory.Path);
        UserAdmin.Create(users, "legacy", password, TargetEndpoint.Parse(entra
            ? "Server=tcp:127.0.0.1,1;Authentication=ActiveDirectoryServicePrincipal;User Id=5ba10036-0000-4000-8000-000000000000;Password=s;Database=Sales"
            : "Server=tcp:127.0.0.1,1;User Id=svc_app;Password=p;Database=Sales"), "test");
        string? code = null;
        var context = new ProxyContext
        {
            Users = users, Sessions = new SessionRegistry(), AllowPlaintext = true,
            LoginRefused = (_, _, reason) => code = reason,
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new ProxyListener(IPAddress.Loopback, 0, context);
        var listening = new TaskCompletionSource();
        var run = listener.RunAsync(stop.Token, () => listening.SetResult());
        await listening.Task;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Endpoint!.Port, stop.Token);
            var stream = client.GetStream();
            var writer = new PacketWriter(stream);
            var reader = new TdsMessageReader(stream);

            await writer.WriteMessageAsync(TdsConstants.PacketType.PreLogin,
                PreLogin.BuildClientRequest(TdsConstants.Encryption.NotSupported, mars: false), stop.Token);
            Assert.NotNull(await reader.ReadAsync(stop.Token));
            await writer.WriteMessageAsync(TdsConstants.PacketType.Login7, Fixture("login7-tds71-odbc"), stop.Token);
            var reply = await reader.ReadAsync(stop.Token);

            Assert.NotNull(reply);
            byte[] payload = reply.Message.Payload;
            var refusal = LoginResponse.Parse(payload, Tds71);
            var error = Assert.Single(refusal.Errors);
            Assert.Equal(ServerMessages.LoginFailed, error.Number);
            Assert.Equal("Login failed for user 'legacy'.", error.Text);
            Assert.Equal(Convert.ToHexString(Fixture("tds71-login-refused")[^9..]), Convert.ToHexString(payload[^9..]));
            Assert.Equal(expectedCode, code);
        }
        finally
        {
            stop.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void Tds71ResultIsDecoded()
    {
        var detail = ResponseReader.Read(Fixture("tds71-select-version"), columnEncryption: false, tdsVersion: Tds71);

        Assert.Null(detail.Problem);
        Assert.Equal([1L], detail.ResultSetRows);
        Assert.Equal([1UL], detail.RowCounts);
    }

    /// <summary>
    /// A USE answered in TDS 7.1: the database ENVCHANGE, INFO 5701 with its 2-byte line number, and a
    /// DONE with a 4-byte count. Read as 7.2, 0.1.3 stopped at that DONE, so the session's database
    /// became unknown - and a write after it flushed nothing in ttl mode.
    /// </summary>
    [Fact]
    public void Tds71UseIsFollowed()
    {
        var answer = new TdsWriter();
        answer.WriteByte(TdsConstants.Token.EnvChange);
        int envChange = answer.ReserveUInt16();
        answer.WriteByte(TdsConstants.EnvChangeType.Database);
        answer.WriteBVarChar("master");
        answer.WriteBVarChar("Sales");
        answer.PatchLengthFrom(envChange);

        answer.WriteByte(TdsConstants.Token.Info);
        int info = answer.ReserveUInt16();
        answer.WriteInt32(5701);
        answer.WriteByte(2);
        answer.WriteByte(0);
        answer.WriteUsVarChar("Changed database context to 'master'.");
        answer.WriteBVarChar("sql01");
        answer.WriteBVarChar("");
        answer.WriteUInt16(1);                               // line number: 2 bytes before TDS 7.2
        answer.PatchLengthFrom(info);

        answer.WriteByte(TdsConstants.Token.Done);
        answer.WriteUInt16(0);
        answer.WriteUInt16(0x00E2);                          // curcmd
        answer.WriteUInt32(0);                               // row count: 4 bytes before TDS 7.2
        byte[] bytes = answer.ToArray();

        var detail = ResponseReader.Read(bytes, columnEncryption: false, tdsVersion: Tds71);
        Assert.Null(detail.Problem);
        Assert.Equal("master", detail.Database);
        Assert.Equal(5701, Assert.Single(detail.Messages).Number);

        Assert.NotNull(ResponseReader.Read(bytes, columnEncryption: false).Problem);   // as 0.1.3 read it
    }

    /// <summary>
    /// The cache observer keeps a response's last bytes for its final DONE - status, attention. A
    /// TDS 7.1 DONE is 9 bytes, not 13; read as 13, the status was lost, and with it a cancel's.
    /// </summary>
    [Fact]
    public void Tds71FinalDoneIsReadFromTheTail()
    {
        byte[] tail = Fixture("tds71-select-version")[^13..];

        Assert.Equal(TdsConstants.DoneStatus.Count, CacheObserver.FinalDoneStatus(tail, Tds71));
        Assert.Equal(0, CacheObserver.FinalDoneStatus(tail, 0x74000004));

        byte[] current = ServerMessages.Error(ServerMessages.LoginFailed, 14, "x");
        Assert.Equal(TdsConstants.DoneStatus.Error, CacheObserver.FinalDoneStatus(current[^13..], 0x74000004));
        Assert.Equal(0, CacheObserver.FinalDoneStatus(current[^12..], 0x74000004));   // too short to hold one
    }

    [Fact]
    public void Tds71ResultIsAnnotated()
    {
        byte[] payload = Fixture("tds71-select-version");
        var annotator = new Annotator(columnEncryptionAcknowledged: false, tdsVersion: Tds71);

        string text = annotator.Describe(new TdsMessage(TdsConstants.PacketType.TabularResult, payload, 1, 0x01));

        Assert.DoesNotContain("decode failed", text);
        Assert.Contains("userType=0", text);
        Assert.Contains("rows=1", text);
        Assert.Contains("Microsoft SQL Server", text);
    }

    [Fact]
    public void JdbcUserAgentFeatureHasAName()
    {
        Assert.Equal("USERAGENT", TdsConstants.FeatureId.Name(0x10));
    }

    [Fact]
    public void Tds71VersionsAreRecognisedInEveryEncoding()
    {
        Assert.True(TdsConstants.IsPre72(0x71000001));
        Assert.True(TdsConstants.IsPre72(0x07010000));       // LOGINACK's spelling of 7.1
        Assert.True(TdsConstants.IsPre72(0x01000071));
        Assert.True(TdsConstants.IsPre72(0x70000000));
        Assert.False(TdsConstants.IsPre72(0x72090002));
        Assert.False(TdsConstants.IsPre72(0x730B0003));
        Assert.False(TdsConstants.IsPre72(0x74000004));
        Assert.False(TdsConstants.IsPre72(TdsConstants.TdsVersion80));
        Assert.False(TdsConstants.IsPre72(0));               // not known yet: read as current
        Assert.Equal(8, TdsConstants.DoneLength(0x71000001));
        Assert.Equal(12, TdsConstants.DoneLength(0x74000004));
        Assert.Equal("7.1", TdsConstants.VersionName(0x07010000));
    }
}
