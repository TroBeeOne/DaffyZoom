using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>
/// A TDS server good for one login: answers PRELOGIN (and TLS, given a certificate), keeps the
/// LOGIN7 it is sent, and answers it with a canned reply. Given a FEDAUTHINFO as well, it plays
/// Azure SQL's Entra ID exchange: asks for a token, keeps the FEDAUTH_TOKEN it gets, then replies.
/// </summary>
internal sealed class FakeTdsServer : IAsyncDisposable
{
    public sealed record Received(
        Login7 Login, string? TlsHostName, IReadOnlyList<(byte Option, byte[] Data)> PreLogin, byte[]? FedAuthToken);

    private readonly TcpListener _listener;

    public FakeTdsServer(byte[] reply, X509Certificate2? certificate = null, IPAddress? address = null, byte[]? fedAuthInfo = null)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Served = ServeAsync(reply, certificate, fedAuthInfo);
    }

    public int Port { get; }
    public Task<Received> Served { get; }

    /// <summary>The client's PRELOGIN options, kept as soon as they arrive - also when the exchange then fails.</summary>
    public IReadOnlyList<(byte Option, byte[] Data)>? PreLoginSeen { get; private set; }

    private async Task<Received> ServeAsync(byte[] reply, X509Certificate2? certificate, byte[]? fedAuthInfo)
    {
        using var socket = await _listener.AcceptTcpClientAsync();
        Stream stream = socket.GetStream();
        var reader = new TdsMessageReader(stream);

        var preLogin = await reader.ReadAsync(CancellationToken.None) ?? throw new IOException("no PRELOGIN");
        var requested = PreLogin.Parse(preLogin.Message.Payload);
        PreLoginSeen = requested;
        byte encryption = certificate is null ? TdsConstants.Encryption.NotSupported : TdsConstants.Encryption.On;

        // As Azure SQL: FEDAUTHREQUIRED is answered 1 when the server is going to ask for a token.
        var answer = PreLogin.Parse(PreLogin.BuildServerResponse(requested, encryption, mars: false))
            .Select(o => o.Option == TdsConstants.PreLoginOption.FedAuthRequired && fedAuthInfo is not null ? (o.Option, new byte[] { 1 }) : o)
            .ToList();
        await new PacketWriter(stream).WriteMessageAsync(TdsConstants.PacketType.TabularResult, PreLogin.Build(answer), CancellationToken.None);

        string? tlsHostName = null;
        if (certificate is not null)
        {
            var shim = new PreLoginTlsStream(stream);
            var tls = new SslStream(shim, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12,
            });
            shim.StopFraming();
            tlsHostName = tls.TargetHostName;
            stream = tls;
            reader.Stream = tls;
        }

        var login = await reader.ReadAsync(CancellationToken.None) ?? throw new IOException("no LOGIN7");

        byte[]? fedAuthToken = null;
        if (fedAuthInfo is not null)
        {
            await new PacketWriter(stream).WriteMessageAsync(TdsConstants.PacketType.TabularResult, fedAuthInfo, CancellationToken.None);
            var token = await reader.ReadAsync(CancellationToken.None) ?? throw new IOException("no FEDAUTH_TOKEN");
            if (token.Message.Type != TdsConstants.PacketType.FedAuthToken) throw new IOException($"expected FEDAUTH_TOKEN, got {token.Message.Type}");
            fedAuthToken = token.Message.Payload.ToArray();
        }

        await new PacketWriter(stream).WriteMessageAsync(TdsConstants.PacketType.TabularResult, reply, CancellationToken.None);
        return new Received(Login7.Parse(login.Message.Payload), tlsHostName, requested, fedAuthToken);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try { await Served; } catch { }
    }

    public static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // SChannel cannot serve with an ephemeral key; a PKCS#12 round trip gives it one it can.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
    }
}

/// <summary>Login responses, token by token, as a server would send them.</summary>
internal static class LoginReplies
{
    /// <summary>Accepts: database ENVCHANGE, LOGINACK, then any FEATUREEXTACK entries, then DONE.</summary>
    public static byte[] Accepted(string database, params (byte Feature, byte[] Data)[] acknowledged)
    {
        var writer = new TdsWriter();
        writer.WriteByte(TdsConstants.Token.EnvChange);
        int length = writer.ReserveUInt16();
        writer.WriteByte(TdsConstants.EnvChangeType.Database);
        writer.WriteBVarChar(database);
        writer.WriteBVarChar("master");
        writer.PatchLengthFrom(length);

        WriteLoginAck(writer);

        if (acknowledged.Length > 0)
        {
            writer.WriteByte(TdsConstants.Token.FeatureExtAck);
            foreach (var (feature, data) in acknowledged)
            {
                writer.WriteByte(feature);
                writer.WriteUInt32((uint)data.Length);
                writer.WriteBytes(data);
            }
            writer.WriteByte(TdsConstants.FeatureId.Terminator);
        }

        WriteDone(writer);
        return writer.ToArray();
    }

    /// <summary>What Azure SQL's gateway answers under the Redirect policy: LOGINACK, then where to go, then DONE.</summary>
    public static byte[] Redirect(string server, int port, byte protocol = 0)
    {
        var writer = new TdsWriter();
        WriteLoginAck(writer);

        writer.WriteByte(TdsConstants.Token.EnvChange);
        int length = writer.ReserveUInt16();
        writer.WriteByte(TdsConstants.EnvChangeType.RoutingInformation);
        int value = writer.ReserveUInt16();
        writer.WriteByte(protocol);
        writer.WriteUInt16((ushort)port);
        writer.WriteUsVarChar(server);
        writer.PatchLengthFrom(value);
        writer.WriteUInt16(0);                            // old value: none
        writer.PatchLengthFrom(length);

        WriteDone(writer);
        return writer.ToArray();
    }

    /// <summary>FEDAUTHINFO: count, then (id, length, offset) entries, then UTF-16LE data - offsets from the count.</summary>
    public static byte[] FedAuthInfo(string stsUrl, string spn)
    {
        byte[] sts = System.Text.Encoding.Unicode.GetBytes(stsUrl);
        byte[] name = System.Text.Encoding.Unicode.GetBytes(spn);
        int dataStart = 4 + 2 * 9;

        var writer = new TdsWriter();
        writer.WriteByte(TdsConstants.Token.FedAuthInfo);
        writer.WriteUInt32((uint)(dataStart + sts.Length + name.Length));
        writer.WriteUInt32(2);
        writer.WriteByte(0x01);
        writer.WriteUInt32((uint)sts.Length);
        writer.WriteUInt32((uint)dataStart);
        writer.WriteByte(0x02);
        writer.WriteUInt32((uint)name.Length);
        writer.WriteUInt32((uint)(dataStart + sts.Length));
        writer.WriteBytes(sts);
        writer.WriteBytes(name);
        return writer.ToArray();
    }

    private static void WriteLoginAck(TdsWriter writer)
    {
        writer.WriteByte(TdsConstants.Token.LoginAck);
        int length = writer.ReserveUInt16();
        writer.WriteByte(1);                              // interface: SQL
        writer.WriteBytes([0x74, 0x00, 0x00, 0x04]);      // TDS 7.4, big-endian
        writer.WriteBVarChar("Microsoft SQL Server");
        writer.WriteBytes([12, 0, 0x01, 0x00]);           // 12.0.256, as Azure SQL reports itself
        writer.PatchLengthFrom(length);
    }

    private static void WriteDone(TdsWriter writer)
    {
        writer.WriteByte(TdsConstants.Token.Done);
        writer.WriteUInt16(TdsConstants.DoneStatus.Final);
        writer.WriteUInt16(0);
        writer.WriteUInt64(0);
    }
}
