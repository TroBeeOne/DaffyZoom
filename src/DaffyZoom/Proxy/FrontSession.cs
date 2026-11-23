using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using DaffyZoom.Security;
using DaffyZoom.Tds;

namespace DaffyZoom.Proxy;

/// <summary>
/// One client connection, from its first byte to its last.
///
/// The handshake is where DaffyZoom is a server in its own right: it answers PRELOGIN, negotiates
/// TLS, reads LOGIN7 and decides who the caller is. Then it becomes a client, logs in to the target
/// as that caller's mapped login, hands the target's login response back to the client unchanged,
/// and gets out of the way: from there on the session is a byte relay.
///
/// Every refusal before that point is answered the way SQL Server answers a failed login - error
/// 18456, "Login failed for user '...'" - naming the login the client used and nothing else. Why it
/// failed, which target was involved, and which service account was tried go to DaffyZoom's log, not
/// to whoever is knocking. The one exception is the target turning down the client's own request,
/// such as a database it cannot open, which is passed on under the target's error number.
/// </summary>
internal sealed class FrontSession(TcpClient client, ProxySession session, ProxyContext context)
{
    /// <summary>First byte of a TLS handshake record, and not a valid TDS packet type: the Strict tell.</summary>
    private const byte TlsHandshake = 0x16;

    /// <summary>How long a client gets to finish PRELOGIN, TLS and LOGIN7. Stops idle half-open connections piling up.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private static readonly SslApplicationProtocol Tds8 = new("tds/8.0");

    private Stream _stream = null!;
    private PacketWriter _writer = null!;
    private string _clientLogin = "";

    /// <summary>The TDS version of the client's LOGIN7, for refusals written in a shape it can read.</summary>
    private uint _clientTdsVersion;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        session.Attach(client);
        Log.Debug("session.accepted", $"connection from {session.ClientAddress}", session.Id);

        SslStream? frontTls = null;
        UpstreamConnection? upstream = null;
        bool relaying = false;
        try
        {
            client.NoDelay = true;
            SocketTuning.EnableKeepAlive(client.Client);

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(HandshakeTimeout);

            _stream = client.GetStream();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            frontTls = await MaybeStrictAsync(handshake.Token);
            if (frontTls is not null) _stream = frontTls;

            var reader = new TdsMessageReader(_stream);
            _writer = new PacketWriter(_stream);

            var (proceed, mars, upgraded) = await PreLoginAsync(reader, handshake.Token);
            if (!proceed) return;
            if (upgraded is not null) frontTls = upgraded;

            var tlsMs = clock.ElapsedMilliseconds;
            var login = await ReadLoginAsync(reader, encrypted: frontTls is not null, handshake.Token);
            if (login is null) return;

            var authStart = clock.ElapsedMilliseconds;
            var user = await AuthenticateAsync(login, handshake.Token);
            var authMs = clock.ElapsedMilliseconds - authStart;
            if (user is null) return;

            upstream = await ConnectUpstreamAsync(login, user, mars, cancellationToken);
            var targetMs = upstream is null ? 0 : (long)upstream.Elapsed.TotalMilliseconds;
            if (upstream is null) return;

            session.AttachUpstream(upstream.Client);

            // The target's own answer goes back as it came: its LOGINACK, version, packet size,
            // collation and feature acknowledgements are what the client and target will both now
            // assume, so neither side can be told anything different. It still travels at the
            // default packet size; the new size, if the target granted one, applies from the next
            // message on.
            await Relay.WriteInPacketSizedPiecesAsync(_stream, upstream.RawLoginResponse, TdsConstants.DefaultPacketSize, cancellationToken);
            int packetSize = upstream.Login.PacketSize ?? TdsConstants.DefaultPacketSize;

            // Anything either handshake reader already pulled off the wire belongs to the relay.
            byte[] early = reader.Drain();
            if (upstream.Leftover.Length > 0) await Relay.WriteInPacketSizedPiecesAsync(_stream, upstream.Leftover, packetSize, cancellationToken);

            relaying = true;
            session.State = SessionState.Relaying;
            context.Sessions.LoginSucceeded();
            Log.Info("session.open",
                $"{_clientLogin} -> {session.TargetLogin}@{session.Target}" +
                (session.RoutedTo is null ? "" : $", redirected to {session.RoutedTo}") +
                $" (spid {session.Spid}, {session.ServerVersion}) " +
                $"app='{session.AppName}' host='{session.HostName}' db='{session.Database}' from {session.ClientAddress}" +
                $", tds {TdsConstants.VersionName(session.TdsVersion)}, {session.FrontSecurity}" +
                (session.Mars ? ", MARS (relayed only)" : CacheableVersion(session) ? "" : ", relayed only: TDS older than 7.3") +
                $" [handshake {clock.ElapsedMilliseconds} ms: client tls {tlsMs}, auth {authMs}, target login {targetMs}]", session.Id);
            context.SessionStarted?.Invoke(session);

            // The cache's observer is also the relay's gate for answering from the cache. MARS
            // sessions have none: their client packets travel in SMP frames that cannot be answered for.
            var observers = BuildObservers();
            var gate = session.Mars ? null : observers.OfType<Cache.ICacheGate>().FirstOrDefault();
            await Relay.RunAsync(session, _stream, upstream.Stream, packetSize, observers, CommandsFor(session), early, gate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug("session.handshake_timeout", "client did not complete the handshake in time; closed", session.Id);
        }
        catch (AuthenticationException ex)
        {
            Log.Debug("session.tls_failed", $"TLS handshake with the client failed: {Innermost(ex)}", session.Id);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            Log.Debug("session.dropped", $"connection dropped during {(relaying ? "relay" : "handshake")}: {Innermost(ex)}", session.Id);
        }
        catch (Exception ex)
        {
            Log.Error("session.error", $"unexpected failure: {ex}", session.Id);
        }
        finally
        {
            session.Abort(session.CloseReason ?? "closed");
            if (upstream is not null) await upstream.DisposeAsync();
            if (frontTls is not null) { try { await frontTls.DisposeAsync(); } catch { } }
            context.Sessions.Close(session);

            if (relaying)
            {
                Log.Info("session.close",
                    $"closed: {session.CloseReason}; {session.Requests} request(s), " +
                    $"{Size(session.BytesFromClient)} in, {Size(session.BytesFromServer)} out, " +
                    $"{DateTime.UtcNow - session.StartedUtc:hh\\:mm\\:ss}", session.Id);
                context.SessionEnded?.Invoke(session);
            }
        }
    }

    /// <summary>
    /// TDS 8.0 (Encrypt=Strict) is TLS from the very first byte, so one peeked byte tells it apart
    /// from everything else on the same port. Plaintext and TDS 7.4 clients start with PRELOGIN.
    /// </summary>
    private async Task<SslStream?> MaybeStrictAsync(CancellationToken cancellationToken)
    {
        var probe = new byte[1];
        int peeked = await client.Client.ReceiveAsync(probe, SocketFlags.Peek, cancellationToken);
        if (peeked == 0 || probe[0] != TlsHandshake) return null;

        if (context.Certificate is null)
        {
            Log.Warn("session.strict_without_cert", "client opened with TLS (Encrypt=Strict) but no certificate is loaded", session.Id);
            throw new IOException("Strict client, no certificate");
        }

        var tls = new SslStream(_stream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = context.Certificate,
            ApplicationProtocols = [Tds8],
            ClientCertificateRequired = false,
        }, cancellationToken);

        session.FrontSecurity = $"Strict, {tls.SslProtocol}";
        return tls;
    }

    /// <summary>
    /// Answers PRELOGIN and settles encryption for the client side of the session.
    ///
    /// Mandatory encryption means every session is encrypted end to end, and the policy follows
    /// from what each client asked for:
    ///   ON / REQ      answered ON - the usual Encrypt=Mandatory case.
    ///   OFF           answered REQ, which per MS-TDS makes the client encrypt the whole session.
    ///                 An Encrypt=Optional client is upgraded, never left with login-only encryption.
    ///   NOT_SUP       answered REQ, which such a client cannot meet, so it disconnects.
    /// --allow-plaintext restores DaffyDB's behaviour for OFF and NOT_SUP: no encryption at all.
    /// A Strict client is already inside TLS and is answered NOT_SUP, as SqlClient expects.
    ///
    /// MARS is echoed as the client asked, and requested from the target the same way, because
    /// after login MARS framing passes through the relay end to end.
    /// </summary>
    private async Task<(bool Proceed, bool Mars, SslStream? Upgraded)> PreLoginAsync(
        TdsMessageReader reader, CancellationToken cancellationToken)
    {
        var message = await reader.ReadAsync(cancellationToken);
        if (message is null)
        {
            Log.Debug("session.closed_early", "client closed the connection without sending PRELOGIN (a port check, or not a TDS client)", session.Id);
            return (false, false, null);
        }

        if (message.Message.Type != TdsConstants.PacketType.PreLogin)
        {
            Log.Warn("session.no_prelogin",
                $"client began with {TdsConstants.PacketTypeName(message.Message.Type)} instead of PRELOGIN; closed", session.Id);
            return (false, false, null);
        }

        var requested = PreLogin.Parse(message.Message.Payload);
        byte asked = (byte)((PreLogin.Value(requested, TdsConstants.PreLoginOption.Encryption) ?? TdsConstants.Encryption.NotSupported) & 0x0F);
        bool mars = PreLogin.Value(requested, TdsConstants.PreLoginOption.Mars) == 1;

        bool strict = _stream is SslStream;
        bool haveCertificate = context.Certificate is not null;

        byte answer;
        bool upgrade = false, refuse = false;

        if (strict)
        {
            answer = TdsConstants.Encryption.NotSupported;
        }
        else if (asked is TdsConstants.Encryption.On or TdsConstants.Encryption.Required)
        {
            answer = haveCertificate ? TdsConstants.Encryption.On : TdsConstants.Encryption.NotSupported;
            upgrade = haveCertificate;
            refuse = !haveCertificate;
        }
        else if (context.AllowPlaintext)
        {
            answer = TdsConstants.Encryption.NotSupported;
        }
        else
        {
            answer = TdsConstants.Encryption.Required;
            upgrade = asked == TdsConstants.Encryption.Off;
            refuse = !upgrade;
        }

        await SendAsync(TdsConstants.PacketType.TabularResult, PreLogin.BuildServerResponse(requested, answer, mars), cancellationToken);

        if (refuse)
        {
            Log.Warn("session.encryption_refused",
                haveCertificate
                    ? $"client does not support encryption ({TdsConstants.EncryptionName(asked)}) and encryption is mandatory; closed"
                    : "client asked for encryption but no certificate is loaded; closed", session.Id);
            return (false, mars, null);
        }

        if (!upgrade)
        {
            if (!strict) session.FrontSecurity = "unencrypted (--allow-plaintext)";
            return (true, mars, null);
        }

        // TDS 7.4: the handshake travels inside PRELOGIN packets, then TDS rides inside TLS. TLS 1.2 is
        // pinned because under 1.3 the two ends stop framing at different moments. (DaffyDB, verified.)
        var shim = new PreLoginTlsStream(_stream);
        var tls = new SslStream(shim, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = context.Certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12,
        }, cancellationToken);
        shim.StopFraming();

        _stream = tls;
        reader.Stream = tls;
        _writer = new PacketWriter(tls);

        session.FrontSecurity = $"{(asked == TdsConstants.Encryption.Off ? "Optional->upgraded" : "Mandatory")}, {tls.SslProtocol}";
        return (true, mars, tls);
    }

    /// <summary>
    /// The client's LOGIN7. A client that hangs up instead is logged with a reason: right after TLS,
    /// that is how a client that checks certificates turns down one it does not trust. The SQLSRV32
    /// ODBC driver and SQLOLEDB do it with Encrypt=yes and DaffyZoom's self-signed certificate, and
    /// retry about twenty times, which 0.1.3 logged only as twenty accepted connections.
    /// </summary>
    private async Task<Login7?> ReadLoginAsync(TdsMessageReader reader, bool encrypted, CancellationToken cancellationToken)
    {
        var message = await reader.ReadAsync(cancellationToken);
        if (message is null)
        {
            Log.Debug("session.closed_early", encrypted
                ? "client closed the connection after the TLS handshake, without logging in - a client that checks " +
                  "certificates does this when it does not trust DaffyZoom's (see 'daffyzoom cert')"
                : "client closed the connection after PRELOGIN, without logging in", session.Id);
            return null;
        }

        if (message.Message.Type != TdsConstants.PacketType.Login7)
        {
            Log.Warn("session.no_login7", $"expected LOGIN7, got {TdsConstants.PacketTypeName(message.Message.Type)}; closed", session.Id);
            return null;
        }

        var login = Login7.Parse(message.Message.Payload);
        _clientLogin = login.UserName;
        _clientTdsVersion = login.TdsVersion;
        session.Login = login.UserName;
        session.HostName = login.HostName;
        session.AppName = login.AppName;
        session.Database = login.Database;
        session.LoginDefaults = LoginDefaults.From(login);

        Log.Debug("session.login7", $"LOGIN7 {login} via {session.FrontSecurity}", session.Id);
        return login;
    }

    /// <summary>Who is this? Refuses everything except a SQL login with a password that matches a mapping.</summary>
    private async Task<UserRecord?> AuthenticateAsync(Login7 login, CancellationToken cancellationToken)
    {
        (string Code, string Reason)? refusal =
            login.UsesIntegratedSecurity ? ("integrated_auth", "integrated (Windows) authentication is not supported; use a DaffyZoom login") :
            login.FeatureIds.Contains(TdsConstants.FeatureId.FedAuth) ? ("federated_auth", "Entra ID / federated authentication is not supported; use a DaffyZoom login") :
            login.RequestsPasswordChange ? ("password_change", "password changes cannot be made through DaffyZoom") :
            login.UserName.Length == 0 ? ("no_login", "no login name was given") :
            login.Password.Length == 0 ? ("empty_password", "no password was given") :
            null;

        if (refusal is { } refused)
        {
            await RefuseAsync(refused.Code, refused.Reason, cancellationToken);
            return null;
        }

        var result = context.Users.Authenticate(login.UserName, login.Password);
        if (!result.Accepted)
        {
            var (code, reason) = result.Outcome switch
            {
                AuthOutcome.UnknownUser => ("unknown_user", "no such DaffyZoom login"),
                AuthOutcome.Disabled => ("disabled", "the DaffyZoom login is disabled"),
                AuthOutcome.EmptyPassword => ("empty_password", "no password was given"),
                _ => ("bad_password", "wrong password"),
            };
            await RefuseAsync(code, reason, cancellationToken);
            return null;
        }

        return result.User;
    }

    private async Task<UpstreamConnection?> ConnectUpstreamAsync(
        Login7 login, UserRecord user, bool mars, CancellationToken cancellationToken)
    {
        TargetEndpoint target;
        try
        {
            target = user.Endpoint;
        }
        catch (FormatException ex)
        {
            await RefuseAsync("bad_mapping", $"the mapping's target cannot be read: {ex.Message}", cancellationToken);
            return null;
        }

        session.Target = $"{target.Host}:{target.Port}";
        session.TargetLogin = target.IsEntra ? target.ClientId : target.UserName;

        // Entra ID needs the FEDAUTH feature in the target's LOGIN7, and FeatureExt is TDS 7.4.
        if (target.IsEntra && login.TdsVersion is not (0x74000004 or TdsConstants.TdsVersion80))
        {
            await RefuseAsync("bad_mapping",
                $"the mapping logs in to the target with Entra ID, which needs a TDS 7.4 client; this one speaks TDS {login.TdsVersionName}",
                cancellationToken);
            return null;
        }

        try
        {
            var upstream = await UpstreamConnector.ConnectAsync(
                target, UpstreamConnector.ForTarget(login, target, login.Database), mars, cancellationToken);

            if (mars && !upstream.Mars)
            {
                await upstream.DisposeAsync();
                await RefuseAsync("mars_refused", "the client asked for MARS and the target refused it", cancellationToken);
                return null;
            }

            session.RoutedTo = upstream.RoutedTo?.ToString();
            session.Spid = upstream.Spid;
            session.Mars = upstream.Mars;
            session.TdsVersion = upstream.Login.TdsVersion;
            session.ServerVersion = $"{upstream.Login.ServerProgram} {upstream.Login.ServerVersion}".Trim();
            session.Database = upstream.Login.Database ?? session.Database;
            session.UpstreamSecurity = upstream.Security;
            session.AcknowledgedFeatures = upstream.Login.AcknowledgedFeatures;
            return upstream;
        }
        catch (UpstreamException ex)
        {
            context.Sessions.UpstreamFailed();
            string detail = $"target login as {target.UserName}@{target.Host}:{target.Port} failed ({ex.Failure}): {ex.Message}";

            // A refusal from the target about the client's own request - most often "Cannot open
            // database X requested by the login" - is worth showing, with the service account's name
            // taken out, and under its own number: retry logic (SqlClient's own, EF Core's, Polly's)
            // decides by number, and 40613 "database not currently available" - an Azure SQL
            // database resuming or failing over - is one to retry, where 18456 is not. Anything about
            // DaffyZoom's own plumbing is not the client's business.
            var targetError = ex.Failure == UpstreamFailure.LoginRejected
                && ex.Response?.Errors.FirstOrDefault() is { Number: not ServerMessages.LoginFailed } error ? error : null;
            string? clientMessage = targetError?.Text.Replace(target.UserName, _clientLogin, StringComparison.OrdinalIgnoreCase);

            string code = ex.Failure switch
            {
                UpstreamFailure.Unreachable => "target_unreachable",
                UpstreamFailure.Security => "target_security",
                UpstreamFailure.LoginRejected => "target_rejected",
                UpstreamFailure.Routed => "target_routed",
                UpstreamFailure.Token => "target_token",
                _ => "target_protocol",
            };
            await RefuseAsync(code, detail, cancellationToken, clientMessage, targetError);
            return null;
        }
    }

    /// <summary>
    /// Sends a login failure in place of LOGINACK and logs the real reason. A refusal has to arrive
    /// instead of the acknowledgement, not after it, or the client believes it is connected. It is
    /// 18456 unless the target's own error is passed on.
    /// </summary>
    private async Task RefuseAsync(string code, string reason, CancellationToken cancellationToken,
                                   string? clientMessage = null, ServerMessage? targetError = null)
    {
        context.Sessions.LoginFailed();
        session.CloseReason = "login refused";
        Log.Warn("session.login_refused",
            $"login '{_clientLogin}' from {session.ClientAddress} (app '{session.AppName}') refused: {reason}", session.Id);
        context.LoginRefused?.Invoke(session, _clientLogin, code);

        try
        {
            // Severity 11 or more is what makes a client treat it as an error rather than a message.
            await SendAsync(TdsConstants.PacketType.TabularResult,
                targetError is null
                    ? ServerMessages.Error(ServerMessages.LoginFailed, severity: 14,
                                           clientMessage ?? $"Login failed for user '{_clientLogin}'.", tdsVersion: _clientTdsVersion)
                    : ServerMessages.Error(targetError.Number, Math.Max(targetError.Severity, (byte)11),
                                           clientMessage ?? targetError.Text, targetError.State, _clientTdsVersion),
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The client has already gone; nothing more to tell it.
        }
    }

    private async Task SendAsync(byte type, byte[] payload, CancellationToken cancellationToken)
    {
        _writer.ResetPacketId();
        await _writer.WriteMessageAsync(type, payload, cancellationToken);
    }

    /// <summary>
    /// Whether DaffyZoom answers this session's daffyzoom.* commands. Not under MARS (see
    /// <see cref="Relay"/>), and not for a client older than TDS 7.3, which could not read the
    /// datetime2 columns the answers carry.
    /// </summary>
    private Commands.IGatewayCommands? CommandsFor(ProxySession session) =>
        context.Commands is { } commands && !session.Mars && CacheableVersion(session) ? commands : null;

    /// <summary>TDS 7.3 or later: what the cache and the daffyzoom.* answers need. 8.0 is numerically the smallest.</summary>
    private static bool CacheableVersion(ProxySession session) =>
        session.TdsVersion >= TdsConstants.TdsVersion73 || session.TdsVersion == TdsConstants.TdsVersion80;

    private IReadOnlyList<IRelayObserver> BuildObservers()
    {
        var observers = new List<IRelayObserver> { new RequestCounter(session) };
        if (context.TranscriptDirectory is not null) observers.Add(Transcript.ToFile(context.TranscriptDirectory, session));
        if (context.Trace) observers.Add(Transcript.ToConsole(session));
        if (context.ObserverFactory is not null) observers.AddRange(context.ObserverFactory(session));
        return observers;
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }
}
