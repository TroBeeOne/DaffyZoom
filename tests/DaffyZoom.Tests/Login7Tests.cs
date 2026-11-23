using System.Net;
using System.Net.Sockets;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;
using Microsoft.Data.SqlClient;

namespace DaffyZoom.Tests;

public class Login7Tests
{
    /// <summary>
    /// The rewrite only works if a login that is parsed and not changed comes out exactly as it went
    /// in - every flag, offset and feature byte - because the target sees what DaffyZoom builds.
    /// </summary>
    [Fact]
    public async Task RealSqlClientLoginRebuildsByteForByte()
    {
        byte[] captured = await CaptureSqlClientLogin7Async("Initial Catalog=Sales;Application Name=Probe;Workstation ID=WS42");

        var login = Login7.Parse(captured);
        Assert.Equal("alice", login.UserName);
        Assert.Equal("s3cret pässword", login.Password);
        Assert.Equal("Sales", login.Database);
        Assert.Equal("Probe", login.AppName);
        Assert.Equal("WS42", login.HostName);
        Assert.Equal("7.4", login.TdsVersionName);
        Assert.NotNull(login.FeatureExt);
        // ConnectRetryCount=0 (set below, so the fake server's hang-up is not retried) also switches
        // off SESSIONRECOVERY, so assert on features SqlClient always asks for.
        Assert.Contains(TdsConstants.FeatureId.Utf8Support, login.FeatureIds);
        Assert.Contains(TdsConstants.FeatureId.ColumnEncryption, login.FeatureIds);
        Assert.False(login.UsesIntegratedSecurity);

        Assert.Equal(Convert.ToHexString(captured), Convert.ToHexString(login.Build()));
    }

    [Fact]
    public async Task RewriteForTargetSwapsOnlyTheIdentity()
    {
        byte[] captured = await CaptureSqlClientLogin7Async("Initial Catalog=Sales;Application Name=Probe");
        var client = Login7.Parse(captured);

        var target = TargetEndpoint.Parse("Server=tcp:sql01.corp.local,1444;User Id=svc_app;Password=Svc!Longer-Password#2026;Initial Catalog=Other");
        var rewritten = Login7.Parse(UpstreamConnector.ForTarget(client, target, client.Database).Build());

        Assert.Equal("svc_app", rewritten.UserName);
        Assert.Equal("Svc!Longer-Password#2026", rewritten.Password);
        Assert.Equal("sql01.corp.local", rewritten.ServerName);
        Assert.Equal("Sales", rewritten.Database);                   // the client's choice wins
        Assert.Equal(client.AppName, rewritten.AppName);
        Assert.Equal(client.HostName, rewritten.HostName);
        Assert.Equal(client.PacketSize, rewritten.PacketSize);
        Assert.Equal(client.TdsVersion, rewritten.TdsVersion);
        Assert.Equal(client.OptionFlags1, rewritten.OptionFlags1);
        Assert.Equal(client.OptionFlags2, rewritten.OptionFlags2);
        Assert.Equal(client.TypeFlags, rewritten.TypeFlags);
        Assert.Equal(Convert.ToHexString(client.FeatureExt!), Convert.ToHexString(rewritten.FeatureExt!));

        // The original is untouched: the rewrite works on a copy.
        Assert.Equal("alice", client.UserName);
    }

    [Fact]
    public async Task MappingDatabaseAppliesWhenTheClientNamesNone()
    {
        byte[] captured = await CaptureSqlClientLogin7Async("");
        var client = Login7.Parse(captured);
        Assert.Equal("", client.Database);

        var target = TargetEndpoint.Parse("Server=h;User Id=u;Password=p;Database=Default");
        Assert.Equal("Default", UpstreamConnector.ForTarget(client, target, client.Database).Database);
    }

    [Fact]
    public void BuiltLoginRoundTripsEveryField()
    {
        var login = new Login7
        {
            TdsVersion = 0x74000004, PacketSize = 8000, ClientProgVer = 7, ClientPid = 1234, ConnectionId = 9,
            OptionFlags1 = 0xE0, OptionFlags2 = 0x03, TypeFlags = 0x20, OptionFlags3 = 0x08,
            ClientTimeZone = -300, ClientLcid = 0x0409,
            HostName = "host", UserName = "user", Password = "Pässw0rd!", AppName = "app", ServerName = "server",
            ClientInterfaceName = "iface", Language = "us_english", Database = "db",
            ClientId = [1, 2, 3, 4, 5, 6],
            FeatureExt = [0x01, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x01, 0x00, 0x00, 0x00, 0x01, 0xFF],
        };

        var parsed = Login7.Parse(login.Build());

        Assert.Equal(login.PacketSize, parsed.PacketSize);
        Assert.Equal(login.ClientTimeZone, parsed.ClientTimeZone);
        Assert.Equal("Pässw0rd!", parsed.Password);
        Assert.Equal("iface", parsed.ClientInterfaceName);
        Assert.Equal("us_english", parsed.Language);
        Assert.Equal(login.ClientId, parsed.ClientId);
        Assert.True(parsed.ReadOnlyIntent);
        Assert.Equal([TdsConstants.FeatureId.SessionRecovery, TdsConstants.FeatureId.Utf8Support], parsed.FeatureIds);
        Assert.Equal(Convert.ToHexString(login.Build()), Convert.ToHexString(parsed.Build()));
    }

    [Fact]
    public void RemovingTheFeatureBlockClearsTheExtensionFlag()
    {
        var login = new Login7 { UserName = "u", Password = "p", OptionFlags3 = 0x18, FeatureExt = null };
        var parsed = Login7.Parse(login.Build());
        Assert.Null(parsed.FeatureExt);
        Assert.Equal(0x08, parsed.OptionFlags3);
    }

    /// <summary>
    /// The versions drivers ask for and SQL Server answers with, as the log names them: the legacy
    /// Windows drivers ask for 7.0 or 7.1 (each with two spellings), and the cache needs 7.3 or later.
    /// </summary>
    [Theory]
    [InlineData(0x74000004u, "7.4")]
    [InlineData(0x730B0003u, "7.3B")]
    [InlineData(0x730A0003u, "7.3A")]
    [InlineData(0x72090002u, "7.2")]
    [InlineData(0x71000001u, "7.1")]
    [InlineData(0x71000000u, "7.1")]
    [InlineData(0x01000071u, "7.1")]
    [InlineData(0x70000000u, "7.0")]
    [InlineData(0x07000000u, "7.0")]
    [InlineData(0x08000000u, "8.0")]
    [InlineData(0x12345678u, "0x12345678")]
    public void TdsVersionsHaveTheirNames(uint version, string name)
    {
        Assert.Equal(name, TdsConstants.VersionName(version));
        Assert.Equal(name, new Login7 { TdsVersion = version }.TdsVersionName);
    }

    [Fact]
    public void ScrambleIsTheDocumentedNibbleSwapAndXor()
    {
        // 'a' is 0x61 0x00 in UTF-16LE: swap nibbles -> 0x16 0x00, XOR 0xA5 -> 0xB3 0xA5.
        Assert.Equal(new byte[] { 0xB3, 0xA5 }, Login7.Scramble("a"));
    }

    /// <summary>
    /// Plays a server for exactly as long as it takes a real Microsoft.Data.SqlClient to send its
    /// LOGIN7: answers PRELOGIN with "no encryption" (the client asked for Optional), keeps the
    /// LOGIN7, and hangs up.
    /// </summary>
    private static async Task<byte[]> CaptureSqlClientLogin7Async(string extra)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var connectionString = $"Server=tcp:127.0.0.1,{port};User Id=alice;Password=s3cret pässword;Encrypt=Optional;" +
                               $"Pooling=False;Connect Timeout=5;ConnectRetryCount=0;{extra}";

        var clientTask = Task.Run(async () =>
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
            }
            catch (SqlException) { }   // the fake server hangs up after LOGIN7, as intended
        });

        try
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var reader = new TdsMessageReader(stream);

            var preLogin = await reader.ReadAsync(CancellationToken.None);
            Assert.NotNull(preLogin);
            var requested = PreLogin.Parse(preLogin.Message.Payload);

            var writer = new PacketWriter(stream);
            await writer.WriteMessageAsync(TdsConstants.PacketType.TabularResult,
                PreLogin.BuildServerResponse(requested, TdsConstants.Encryption.NotSupported, mars: false), CancellationToken.None);

            var login = await reader.ReadAsync(CancellationToken.None);
            Assert.NotNull(login);
            Assert.Equal(TdsConstants.PacketType.Login7, login.Message.Type);
            return login.Message.Payload;
        }
        finally
        {
            listener.Stop();
            await clientTask;
        }
    }
}
