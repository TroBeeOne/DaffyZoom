using System.Collections.Concurrent;
using System.Net.Sockets;

namespace DaffyZoom.Proxy;

internal enum SessionState { Handshake, Relaying, Closed }

/// <summary>
/// What the client's LOGIN7 said about the session's defaults - the option flags that set ANSI
/// behaviour (ODBC_ON turns on ANSI_NULLS, QUOTED_IDENTIFIER and the rest), the type flags, the
/// locale and language - and nothing about who it is. These change what a query returns, so they
/// are part of every cache key on the session.
/// </summary>
internal sealed record LoginDefaults(byte OptionFlags1, byte OptionFlags2, byte TypeFlags, byte OptionFlags3, uint ClientLcid, string Language)
{
    /// <summary>OptionFlags2's fODBC: the client asked for the ODBC defaults, QUOTED_IDENTIFIER ON among them.</summary>
    private const byte OdbcFlag = 0x02;

    public static readonly LoginDefaults None = new(0, 0, 0, 0, 0, "");

    public bool QuotedIdentifier => (OptionFlags2 & OdbcFlag) != 0;

    public static LoginDefaults From(Tds.Login7 login) =>
        new(login.OptionFlags1, login.OptionFlags2, login.TypeFlags, login.OptionFlags3, login.ClientLcid, login.Language);
}

/// <summary>
/// One client connection and, once logged in, its upstream twin. Written by the session's own
/// tasks and read by the console, so counters go through Interlocked and the rest is set once.
/// </summary>
internal sealed class ProxySession(int id, string clientAddress)
{
    private TcpClient? _client;
    private TcpClient? _upstream;
    private long _bytesFromClient, _bytesFromServer, _requests, _lastActivityTicks = DateTime.UtcNow.Ticks;

    public int Id { get; } = id;
    public Guid ConnectionId { get; } = Guid.CreateVersion7();
    public string ClientAddress { get; } = clientAddress;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public SessionState State { get; set; } = SessionState.Handshake;
    public string? Login { get; set; }
    public string? HostName { get; set; }
    public string? AppName { get; set; }
    public string? Database { get; set; }
    public string? Target { get; set; }

    /// <summary>host:port the target redirected the login to (Azure SQL, read-only routing), if it did.</summary>
    public string? RoutedTo { get; set; }
    public string? TargetLogin { get; set; }
    public string? ServerVersion { get; set; }
    public ushort Spid { get; set; }
    public bool Mars { get; set; }

    /// <summary>The TDS version client and target agreed on at login, as LOGINACK gave it.</summary>
    public uint TdsVersion { get; set; }

    /// <summary>
    /// What the target agreed to in FEATUREEXTACK. Decoding responses depends on it: with column
    /// encryption acknowledged, every COLMETADATA carries a CekTable.
    /// </summary>
    public IReadOnlyList<byte> AcknowledgedFeatures { get; set; } = [];

    public bool ColumnEncryption => AcknowledgedFeatures.Contains(Tds.TdsConstants.FeatureId.ColumnEncryption);

    /// <summary>The session defaults the client's LOGIN7 asked for (never its credentials).</summary>
    public LoginDefaults LoginDefaults { get; set; } = LoginDefaults.None;
    public string FrontSecurity { get; set; } = "unencrypted";
    public string UpstreamSecurity { get; set; } = "";
    public string? CloseReason { get; set; }

    public long BytesFromClient => Interlocked.Read(ref _bytesFromClient);
    public long BytesFromServer => Interlocked.Read(ref _bytesFromServer);
    public long Requests => Interlocked.Read(ref _requests);
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    public void AddClientBytes(int count)
    {
        Interlocked.Add(ref _bytesFromClient, count);
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
    }

    public void AddServerBytes(int count)
    {
        Interlocked.Add(ref _bytesFromServer, count);
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
    }

    public void CountRequest() => Interlocked.Increment(ref _requests);

    public void Attach(TcpClient client) => _client = client;
    public void AttachUpstream(TcpClient upstream) => _upstream = upstream;

    /// <summary>
    /// Closes both sockets. Every pending read on either side then fails, which is how both relay
    /// pumps, and the handshake if it is still going, find out the session is over.
    /// </summary>
    public void Abort(string reason)
    {
        CloseReason ??= reason;
        try { _client?.Client.Close(0); } catch { }
        try { _upstream?.Client.Close(0); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _upstream?.Dispose(); } catch { }
    }

    /// <summary>"svc_app@sql01:1433", with "(redirected)" when the target sent the login elsewhere.</summary>
    public string DescribeTarget() =>
        State != SessionState.Relaying ? State.ToString().ToLowerInvariant()
        : RoutedTo is null ? $"{TargetLogin}@{Target}" : $"{TargetLogin}@{Target} (redirected)";

    public override string ToString() =>
        $"session {Id} login='{Login}' -> {Target} app='{AppName}' host='{HostName}' from={ClientAddress}";
}

/// <summary>The sessions that are open now, plus lifetime totals for :status.</summary>
internal sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<int, ProxySession> _sessions = new();
    private int _nextId;
    private long _accepted, _loginsSucceeded, _loginsFailed, _upstreamFailures;

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public long Accepted => Interlocked.Read(ref _accepted);
    public long LoginsSucceeded => Interlocked.Read(ref _loginsSucceeded);
    public long LoginsFailed => Interlocked.Read(ref _loginsFailed);
    public long UpstreamFailures => Interlocked.Read(ref _upstreamFailures);
    public int Active => _sessions.Count;

    public ProxySession Open(string clientAddress)
    {
        var session = new ProxySession(Interlocked.Increment(ref _nextId), clientAddress);
        _sessions[session.Id] = session;
        Interlocked.Increment(ref _accepted);
        return session;
    }

    public void Close(ProxySession session)
    {
        session.State = SessionState.Closed;
        _sessions.TryRemove(session.Id, out _);
    }

    public void LoginSucceeded() => Interlocked.Increment(ref _loginsSucceeded);
    public void LoginFailed() => Interlocked.Increment(ref _loginsFailed);
    public void UpstreamFailed() => Interlocked.Increment(ref _upstreamFailures);

    public ProxySession? Find(int id) => _sessions.GetValueOrDefault(id);

    /// <summary>
    /// Disconnects a session - :kill at the prompt, daffyzoom.kill_session in T-SQL. Both of its
    /// connections are closed; the target sees its client go away and rolls back what was open.
    /// <paramref name="reason"/> says who did it, for the log and the session's end. Null when there
    /// is no such session.
    /// </summary>
    public ProxySession? Kill(int id, string reason)
    {
        if (Find(id) is not { } session) return null;
        session.Abort(reason);
        Log.Info("session.killed", $"session {id} ('{session.Login}') disconnected: {reason}", id);
        return session;
    }

    public IReadOnlyList<ProxySession> Snapshot() => _sessions.Values.OrderBy(s => s.Id).ToList();

    public void AbortAll(string reason)
    {
        foreach (var session in _sessions.Values) session.Abort(reason);
    }
}
