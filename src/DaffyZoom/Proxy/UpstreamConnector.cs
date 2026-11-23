using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DaffyZoom.Tds;

namespace DaffyZoom.Proxy;

/// <summary>Why an upstream login did not produce a usable connection.</summary>
internal enum UpstreamFailure
{
    /// <summary>TCP connect failed or timed out.</summary>
    Unreachable,

    /// <summary>The two sides could not agree on encryption, or the TLS handshake failed.</summary>
    Security,

    /// <summary>The target answered LOGIN7 with an error: bad password, no such database, disabled login.</summary>
    LoginRejected,

    /// <summary>
    /// The target redirected the login somewhere DaffyZoom will not follow: a second time, after the
    /// one redirect it does follow.
    /// </summary>
    Routed,

    /// <summary>The target said something that is not a valid TDS login exchange.</summary>
    Protocol,

    /// <summary>
    /// No Entra ID access token for the target: Entra refused the application or its secret, could
    /// not be reached, or the target asked for one from somewhere that is not Entra ID.
    /// </summary>
    Token,
}

internal sealed class UpstreamException(UpstreamFailure failure, string message, LoginResponse? response = null, Exception? inner = null)
    : Exception(message, inner)
{
    public UpstreamFailure Failure { get; } = failure;
    public LoginResponse? Response { get; } = response;
}

/// <summary>
/// A logged-in connection to the target, ready for the relay: the transport (TLS or not) plus what
/// the target said when it accepted the login.
/// </summary>
internal sealed class UpstreamConnection(TcpClient client, Stream stream, SslStream? tls,
                                         LoginResponse login, byte[] rawLoginResponse, ushort spid,
                                         bool mars, byte[] leftover, RoutingTarget? routedTo, TimeSpan elapsed)
    : IAsyncDisposable
{
    public TcpClient Client { get; } = client;
    public Stream Stream { get; } = stream;
    public LoginResponse Login { get; } = login;

    /// <summary>The target's login response exactly as it arrived, for relaying to the client unchanged.</summary>
    public byte[] RawLoginResponse { get; } = rawLoginResponse;

    public ushort Spid { get; } = spid;
    public bool Mars { get; } = mars;

    /// <summary>Bytes read past the end of the login response. Empty unless something is badly off.</summary>
    public byte[] Leftover { get; } = leftover;

    /// <summary>Where the target redirected the login, when it did: the server this connection is really to.</summary>
    public RoutingTarget? RoutedTo { get; } = routedTo;

    public TimeSpan Elapsed { get; } = elapsed;

    public string Security => tls is null
        ? "unencrypted"
        : $"{tls.SslProtocol}, {tls.NegotiatedCipherSuite}";

    public async ValueTask DisposeAsync()
    {
        try { if (tls is not null) await tls.DisposeAsync(); } catch { }
        Client.Dispose();
    }
}

/// <summary>
/// DaffyZoom as a TDS client: connect to the target, negotiate PRELOGIN and TLS, send a LOGIN7, and
/// read the answer. The relay uses it to open each session's upstream half; `user add` and
/// `user test` use exactly the same code to validate a mapping, so a mapping that validates is one
/// the relay can use.
///
/// The TLS handshake for TDS 7.4 travels inside PRELOGIN packets, so SslStream is handed the same
/// <see cref="PreLoginTlsStream"/> shim the front end uses as a server - it frames and unframes the
/// same way in either direction. TLS 1.2 is pinned for 7.4 for the same reason as on the front end.
///
/// A target may answer the login with "go there instead" - a routing ENVCHANGE, which is how Azure
/// SQL's Redirect connection policy and an availability group's read-only routing work. A client
/// that saw it would reconnect straight to that server and leave DaffyZoom, and its cache, behind,
/// so DaffyZoom follows the redirect itself: it logs in again where it was sent, and the client only
/// ever sees the second server's answer. One redirect per login, as in SqlClient.
///
/// With an Entra ID application as the target login, the exchange is SqlClient's for
/// Authentication=ActiveDirectoryServicePrincipal: PRELOGIN says FEDAUTHREQUIRED, LOGIN7 carries a
/// FEDAUTH feature instead of a user name and password, the target answers with FEDAUTHINFO (where
/// to get a token, and for what), and DaffyZoom sends the token in a FEDAUTH_TOKEN message before
/// the real login response arrives. That exchange is DaffyZoom's own business, so the FEDAUTH
/// acknowledgement is taken out of the response the client is given.
/// </summary>
internal static class UpstreamConnector
{
    private static readonly SslApplicationProtocol Tds8 = new("tds/8.0");

    /// <summary>
    /// One server on the way to a logged-in session: the configured target, or where it redirected
    /// the login. <paramref name="RedirectedFrom"/> is the certificate name of the server that sent
    /// DaffyZoom here, which this one's certificate may carry instead of its own (see AuthenticateAsync).
    /// </summary>
    private sealed record Hop(string Host, int Port, string CertificateHost, string? RedirectedFrom = null)
    {
        public override string ToString() => $"{Host}:{Port}";
    }

    public static async Task<UpstreamConnection> ConnectAsync(
        TargetEndpoint target, Login7 login, bool mars, CancellationToken cancellationToken, IAccessTokenSource? tokens = null)
    {
        tokens ??= EntraTokenSource.Shared;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(target.ConnectTimeoutSeconds));

        var hop = new Hop(target.Host, target.Port, target.HostNameInCertificate ?? target.Host);
        RoutingTarget? routedTo = null;

        while (true)
        {
            try
            {
                return await ConnectToHopAsync(target, hop, login, mars, routedTo, tokens, timeout, cancellationToken, stopwatch);
            }
            catch (UpstreamException ex) when (ex.Failure == UpstreamFailure.Routed && ex.Response?.Routing is { } routing)
            {
                if (routedTo is not null)
                    throw new UpstreamException(UpstreamFailure.Routed,
                        $"{target.Host}:{target.Port} redirected the login to {routedTo}, which redirected it again, to {routing}; " +
                        "only one redirect per login is followed", ex.Response);

                if (routing.Protocol != 0 || routing.Port == 0 || routing.Server.Length == 0)
                    throw new UpstreamException(UpstreamFailure.Protocol,
                        $"the target redirected the login to an address that cannot be used ('{routing}', protocol {routing.Protocol})",
                        ex.Response);

                Log.Debug("upstream.redirected", $"{hop} redirected the login to {routing}; following");
                routedTo = routing;
                hop = new Hop(routing.Server, routing.Port, routing.Server, RedirectedFrom: hop.CertificateHost);

                // SqlClient names the server it was sent to, with its port, in the second LOGIN7.
                login = login.Clone();
                login.ServerName = $"{routing.Server},{routing.Port}";
            }
        }
    }

    private static async Task<UpstreamConnection> ConnectToHopAsync(
        TargetEndpoint target, Hop hop, Login7 login, bool mars, RoutingTarget? routedTo, IAccessTokenSource tokens,
        CancellationTokenSource timeout, CancellationToken cancellationToken, Stopwatch stopwatch)
    {
        for (int attempt = 1; ; attempt++)
        {
            var progress = new AttemptProgress();
            try
            {
                return await AttemptAsync(target, hop, login, mars, routedTo, tokens, timeout.Token, cancellationToken, progress, stopwatch);
            }
            catch (UpstreamException ex) when (attempt < MaxAttempts && !progress.LoginSent && !timeout.IsCancellationRequested
                                               && ex.Failure is UpstreamFailure.Unreachable or UpstreamFailure.Protocol)
            {
                Log.Debug("upstream.retry", $"{hop} failed before login on attempt {attempt} ({ex.Message}); retrying");
                try { await Task.Delay(attempt == 1 ? 50 : 250, timeout.Token); }
                catch (OperationCanceledException) { throw ex; }
            }
        }
    }

    /// <summary>
    /// Attempts made at connecting. A failure BEFORE LOGIN7 is sent - the connect itself, a peer that
    /// hangs up during PRELOGIN or the TLS handshake - is retried: nothing has been presented and
    /// nothing on the server has happened, and it is just what a port proxy, load balancer or busy
    /// listener does under a burst of connections. Once LOGIN7 is on the wire there is no retry.
    /// </summary>
    private const int MaxAttempts = 3;

    private sealed class AttemptProgress
    {
        public bool LoginSent;
    }

    private static async Task<UpstreamConnection> AttemptAsync(
        TargetEndpoint target, Hop hop, Login7 login, bool mars, RoutingTarget? routedTo, IAccessTokenSource tokens,
        CancellationToken token, CancellationToken cancellationToken, AttemptProgress progress, Stopwatch stopwatch)
    {
        var client = new TcpClient { NoDelay = true };
        SslStream? tls = null;
        try
        {
            try
            {
                await client.ConnectAsync(hop.Host, hop.Port, token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new UpstreamException(UpstreamFailure.Unreachable,
                    ex is OperationCanceledException
                        ? $"could not connect to {hop} within {target.ConnectTimeoutSeconds}s"
                        : $"could not connect to {hop}: {ex.Message}", inner: ex);
            }

            SocketTuning.EnableKeepAlive(client.Client);
            Stream stream = client.GetStream();

            // Strict: TLS from byte zero, with the protocol named in ALPN, then plain TDS inside it.
            if (target.Encrypt == TargetEncrypt.Strict)
            {
                tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await AuthenticateAsync(tls, target, hop, strict: true, token);
                stream = tls;
            }

            var reader = new TdsMessageReader(stream);
            var writer = new PacketWriter(stream);

            // Under Strict the session is already encrypted, so PRELOGIN says NOT_SUP - which is what
            // SqlClient sends too - to stop the server starting a second handshake inside the first.
            byte encryption = target.Encrypt == TargetEncrypt.Mandatory
                ? TdsConstants.Encryption.On
                : TdsConstants.Encryption.NotSupported;

            await writer.WriteMessageAsync(TdsConstants.PacketType.PreLogin,
                PreLogin.BuildClientRequest(encryption, mars, fedAuth: target.IsEntra), token);

            var preLogin = await reader.ReadAsync(token)
                ?? throw new UpstreamException(UpstreamFailure.Protocol, "the target closed the connection during PRELOGIN");

            var answer = PreLogin.Parse(preLogin.Message.Payload);
            byte serverEncryption = PreLogin.Value(answer, TdsConstants.PreLoginOption.Encryption) ?? TdsConstants.Encryption.NotSupported;
            bool serverMars = PreLogin.Value(answer, TdsConstants.PreLoginOption.Mars) == 1;

            if (target.Encrypt == TargetEncrypt.Mandatory)
            {
                if (serverEncryption is not (TdsConstants.Encryption.On or TdsConstants.Encryption.Required))
                    throw new UpstreamException(UpstreamFailure.Security,
                        $"the target does not support encryption (it answered {TdsConstants.EncryptionName(serverEncryption)}); " +
                        "install a certificate on it, or set Encrypt=Optional in the mapping to connect unencrypted");

                var shim = new PreLoginTlsStream(stream);
                tls = new SslStream(shim, leaveInnerStreamOpen: true);
                await AuthenticateAsync(tls, target, hop, strict: false, token);
                shim.StopFraming();

                stream = tls;
                reader.Stream = tls;
                writer = new PacketWriter(tls);
            }
            else if (target.Encrypt == TargetEncrypt.Optional && serverEncryption == TdsConstants.Encryption.Required)
            {
                throw new UpstreamException(UpstreamFailure.Security,
                    "the target requires encryption; set Encrypt=Mandatory in the mapping");
            }

            if (target.IsEntra) login = WithFedAuth(login, echo: PreLogin.Value(answer, TdsConstants.PreLoginOption.FedAuthRequired) == 1);

            progress.LoginSent = true;
            await writer.WriteMessageAsync(TdsConstants.PacketType.Login7, login.Build(), token);

            var loginReply = await reader.ReadAsync(token)
                ?? throw new UpstreamException(UpstreamFailure.Protocol, "the target closed the connection after LOGIN7");

            var response = LoginResponse.Parse(loginReply.Message.Payload, login.TdsVersion);

            if (response.FedAuth is { } fedAuth)
            {
                if (!target.IsEntra)
                    throw new UpstreamException(UpstreamFailure.Protocol, "the target asked for an Entra ID token, which a SQL login does not use");

                string accessToken = await tokens.GetTokenAsync(target, fedAuth, token);
                byte[]? nonce = answer.FirstOrDefault(o => o.Option == TdsConstants.PreLoginOption.NonceOpt).Data;
                await writer.WriteMessageAsync(TdsConstants.PacketType.FedAuthToken, FedAuthToken(accessToken, nonce), token);

                loginReply = await reader.ReadAsync(token)
                    ?? throw new UpstreamException(UpstreamFailure.Protocol, "the target closed the connection after the Entra ID token");
                response = LoginResponse.Parse(loginReply.Message.Payload, login.TdsVersion);
            }

            // Checked before success: a redirect can come with a LOGINACK, but the session is not here.
            if (response.Routing is not null)
                throw new UpstreamException(UpstreamFailure.Routed, $"{hop} redirected the login to {response.Routing}", response);

            if (!response.Succeeded)
                throw new UpstreamException(UpstreamFailure.LoginRejected,
                    response.Errors.Count > 0
                        ? string.Join("; ", response.Errors)
                        : "the target did not acknowledge the login",
                    response);

            byte[] raw = target.IsEntra
                ? await FrameAsync(LoginResponse.WithoutFeatureAck(loginReply.Message.Payload, TdsConstants.FeatureId.FedAuth), loginReply.Spid)
                : loginReply.RawPackets;

            return new UpstreamConnection(client, stream, tls, response, raw, loginReply.Spid,
                                          mars && serverMars, reader.Drain(), routedTo, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeQuietly(tls, client);
            throw new UpstreamException(UpstreamFailure.Unreachable,
                $"the target did not complete the login within {target.ConnectTimeoutSeconds}s");
        }
        catch (UpstreamException)
        {
            await DisposeQuietly(tls, client);
            throw;
        }
        catch (Exception ex) when (ex is AuthenticationException)
        {
            await DisposeQuietly(tls, client);

            // A peer that simply hung up mid-handshake is a connection problem, not a certificate one.
            throw ex.InnerException is IOException
                ? new UpstreamException(UpstreamFailure.Protocol, $"the target closed the connection during the TLS handshake: {Innermost(ex)}", inner: ex)
                : new UpstreamException(UpstreamFailure.Security, $"TLS with the target failed: {Innermost(ex)}", inner: ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            await DisposeQuietly(tls, client);
            throw new UpstreamException(UpstreamFailure.Protocol, $"the connection to the target failed: {Innermost(ex)}", inner: ex);
        }
        catch
        {
            await DisposeQuietly(tls, client);
            throw;
        }
    }

    /// <summary>
    /// The LOGIN7 DaffyZoom sends when there is no client behind it - validating a mapping. Shaped like
    /// Microsoft.Data.SqlClient's own, so the target treats it no differently.
    /// </summary>
    public static Login7 CreateValidationLogin(TargetEndpoint target) => ForTarget(new Login7
    {
        TdsVersion = 0x74000004,
        PacketSize = TdsConstants.DefaultPacketSize,
        ClientProgVer = 0x06000000,
        ClientPid = (uint)Environment.ProcessId,
        OptionFlags1 = 0xE0,        // USE_DB_ON | INIT_DB_FATAL | SET_LANG_ON
        OptionFlags2 = 0x03,        // INIT_LANG_FATAL | ODBC_ON
        OptionFlags3 = 0x08,        // UNKNOWN_COLLATION_HANDLING
        ClientLcid = 0x0409,
        HostName = Environment.MachineName,
        AppName = "DaffyZoom",
        ClientInterfaceName = "DaffyZoom",
    }, target, clientDatabase: null);

    /// <summary>
    /// The client's LOGIN7 with the identity swapped for the target's. Everything else - version,
    /// packet size, flags, host, application, language, features - is the client's own.
    /// </summary>
    public static Login7 ForTarget(Login7 client, TargetEndpoint target, string? clientDatabase)
    {
        var login = client.Clone();

        // An Entra ID login names no user and carries no password: the token says who it is.
        login.UserName = target.IsEntra ? "" : target.UserName;
        login.Password = target.IsEntra ? "" : target.Password;
        login.ServerName = target.HostNameInCertificate ?? target.Host;
        login.Database = !string.IsNullOrEmpty(clientDatabase) ? clientDatabase : target.Database ?? "";
        login.ChangePassword = "";
        if (!login.IsPre72) login.OptionFlags3 &= unchecked((byte)~0x01);   // fChangePassword; a reserved byte before TDS 7.2
        login.Sspi = [];
        login.OptionFlags2 &= unchecked((byte)~0x80);   // fIntSecurity
        return login;
    }

    /// <summary>
    /// Adds the FEDAUTH feature as SqlClient does for a service principal: the MSAL library (2) in
    /// the upper bits of the options byte, the echo of the target's FEDAUTHREQUIRED in the lowest,
    /// then the workflow - 1, the byte SqlClient uses for a service principal.
    /// </summary>
    private static Login7 WithFedAuth(Login7 login, bool echo)
    {
        var withFedAuth = login.Clone();
        withFedAuth.AddFeature(TdsConstants.FeatureId.FedAuth, [(byte)((0x02 << 1) | (echo ? 1 : 0)), 0x01]);
        return withFedAuth;
    }

    /// <summary>
    /// FEDAUTH_TOKEN: the length of what follows, the token as UTF-16LE with its own length in
    /// front, and the nonce the target sent in PRELOGIN if it sent one.
    /// </summary>
    internal static byte[] FedAuthToken(string accessToken, byte[]? nonce)
    {
        byte[] token = Encoding.Unicode.GetBytes(accessToken);
        bool withNonce = nonce is { Length: 32 };

        var writer = new TdsWriter();
        writer.WriteUInt32((uint)(4 + token.Length + (withNonce ? 32 : 0)));
        writer.WriteUInt32((uint)token.Length);
        writer.WriteBytes(token);
        if (withNonce) writer.WriteBytes(nonce);
        return writer.ToArray();
    }

    /// <summary>A rewritten login response, framed as the target would have: default packet size, its SPID.</summary>
    private static async Task<byte[]> FrameAsync(byte[] payload, ushort spid)
    {
        using var buffer = new MemoryStream();
        await new PacketWriter(buffer) { Spid = spid }.WriteMessageAsync(TdsConstants.PacketType.TabularResult, payload, CancellationToken.None);
        return buffer.ToArray();
    }

    /// <summary>
    /// TLS as a client. The certificate must chain to a trusted issuer and name the server - or, on
    /// a redirected hop, either that server or the one that redirected the login: a trusted
    /// certificate for the server the mapping names is proof enough that this is part of it.
    /// </summary>
    private static async Task AuthenticateAsync(SslStream tls, TargetEndpoint target, Hop hop, bool strict, CancellationToken token)
    {
        SslPolicyErrors rejected = SslPolicyErrors.None;
        string? issuedTo = null;

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = hop.CertificateHost,
            EnabledSslProtocols = strict ? SslProtocols.None : SslProtocols.Tls12,
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (target.TrustServerCertificate || errors == SslPolicyErrors.None) return true;

                var presented = certificate as X509Certificate2;
                if (errors == SslPolicyErrors.RemoteCertificateNameMismatch && hop.RedirectedFrom is { } original
                    && presented?.MatchesHostname(original) == true)
                {
                    Log.Debug("upstream.certificate", $"{hop.Host}'s certificate names {original}, the server that redirected the login; accepted");
                    return true;
                }

                rejected = errors;
                issuedTo = presented?.GetNameInfo(X509NameType.DnsName, forIssuer: false);
                return false;
            },
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
        if (strict) options.ApplicationProtocols = [Tds8];

        try
        {
            await tls.AuthenticateAsClientAsync(options, token);
        }
        catch (AuthenticationException ex) when (!target.TrustServerCertificate)
        {
            string whose = hop.RedirectedFrom is null
                ? "the target's certificate"
                : $"the certificate of {hop.Host}, where the target redirected the login,";
            string why = rejected == SslPolicyErrors.None ? Innermost(ex) : Explain(rejected, hop, issuedTo);
            throw new UpstreamException(UpstreamFailure.Security,
                $"{whose} was not accepted ({why}); trust its issuer on this machine, " +
                "or set TrustServerCertificate=True in the mapping", inner: ex);
        }
        catch (Exception ex) when (strict && ex is IOException or AuthenticationException)
        {
            // SQL Server hangs up mid-handshake, with no TLS alert, when it has no configured
            // certificate: its errorlog says "A valid TLS certificate is not configured to accept
            // strict (TDS 8.0 and above) connections". A self-generated one is not enough.
            throw new UpstreamException(UpstreamFailure.Security,
                "the target closed the connection during the TDS 8.0 (Encrypt=Strict) handshake. SQL Server does this " +
                "when no certificate is configured for strict connections (its self-generated one does not count): " +
                "configure a certificate on the target, or use Encrypt=Mandatory in the mapping", inner: ex);
        }
    }

    private static string Explain(SslPolicyErrors errors, Hop hop, string? issuedTo)
    {
        var reasons = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) reasons.Add("no certificate was presented");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            reasons.Add($"it is issued to '{issuedTo}', not {hop.CertificateHost}" +
                        (hop.RedirectedFrom is null ? "" : $" or {hop.RedirectedFrom}"));
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) reasons.Add("its issuer is not trusted here");
        return string.Join("; ", reasons);
    }

    private static async Task DisposeQuietly(SslStream? tls, TcpClient client)
    {
        try { if (tls is not null) await tls.DisposeAsync(); } catch { }
        client.Dispose();
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }
}
