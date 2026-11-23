using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using DaffyZoom.Proxy;

namespace DaffyZoom.Cache;

/// <summary>
/// Everything a request's answer depends on besides the request's own bytes, frozen at the moment
/// the request was sent. Two requests can share a cached answer only if these are equal.
/// </summary>
/// <param name="Target">The mapping's configured server, host:port - not an Azure redirect node.</param>
/// <param name="TargetLogin">Who SQL Server sees: the mapped login, or the Entra application's client id.</param>
/// <param name="LoginHash">The login's defaults and what was negotiated: option flags, language, TDS version, features.</param>
/// <param name="Database">The session's current database; null when DaffyZoom cannot be sure of it.</param>
/// <param name="SettingsHash">The login defaults, folded with every SET the session has sent since login or reset.</param>
/// <param name="QuotedIdentifier">Whether "x" is an identifier or a string, which T-SQL needs to be parsed at all.</param>
/// <param name="Taint">Why the session cannot be served from the cache until it is reset, or null.</param>
/// <param name="DirtyReads">The session may read uncommitted data (READ UNCOMMITTED, or a SET that could not be read plainly): tracked mode will not cache it.</param>
internal readonly record struct ContextSnapshot(
    string Target, string TargetLogin, ulong LoginHash, string? Database, ulong SettingsHash, bool QuotedIdentifier, string? Taint,
    bool DirtyReads = false);

/// <summary>
/// One session's <see cref="ContextSnapshot"/> as its traffic changes it. Every change passes through
/// DaffyZoom - a USE, a SET, a pooled connection's reset - so it is followed here rather than asked of
/// the server. Where DaffyZoom cannot follow (a USE whose answer is too large to read, a batch too
/// large to scan), it says so - an unknown database, a taint - and the session is relayed only.
///
/// Changed only by the session's own observer, under its lock.
/// </summary>
internal sealed class SessionContext
{
    private readonly ulong _initialSettings;
    private readonly bool _initialQuotedIdentifier;

    public SessionContext(string target, string targetLogin, ulong loginHash, string? database, bool quotedIdentifier)
    {
        Target = target;
        TargetLogin = targetLogin;
        LoginHash = loginHash;
        InitialDatabase = Database = database;
        _initialSettings = SettingsHash = loginHash;
        _initialQuotedIdentifier = QuotedIdentifier = quotedIdentifier;
    }

    public string Target { get; }
    public string TargetLogin { get; }
    public ulong LoginHash { get; }

    /// <summary>The database the login put the session in, which a reset puts it back in.</summary>
    public string? InitialDatabase { get; }

    public string? Database { get; set; }
    public ulong SettingsHash { get; private set; }
    public bool QuotedIdentifier { get; set; }
    public string? Taint { get; private set; }

    public static SessionContext For(ProxySession session) =>
        new(session.Target ?? "", session.TargetLogin ?? "", LoginHashOf(session), session.Database, session.LoginDefaults.QuotedIdentifier);

    public ContextSnapshot Snapshot() => new(Target, TargetLogin, LoginHash, Database, SettingsHash, QuotedIdentifier, Taint, DirtyReads);

    /// <summary>READ UNCOMMITTED is set, or a SET could not be read plainly and might have set it.</summary>
    public bool DirtyReads => _unclear != 0 || _assignments.Any(a => a.Option == "TRANSACTION" && a.Value.Contains("READ UNCOMMITTED", StringComparison.Ordinal));

    /// <summary>The context a request carrying a reset will run in - asked by the gate before the request is seen, so nothing is changed.</summary>
    public ContextSnapshot SnapshotAfterReset() =>
        new(Target, TargetLogin, LoginHash, InitialDatabase, _initialSettings, _initialQuotedIdentifier, null);

    /// <summary>A pooled connection's reset: the server puts the session back as the login left it, and so does this.</summary>
    public void Reset()
    {
        Database = InitialDatabase;
        _assignments.Clear();
        _unclear = 0;
        SettingsHash = _initialSettings;
        QuotedIdentifier = _initialQuotedIdentifier;
        Taint = null;
    }

    /// <summary>The first reason sticks: once tainted, a session stays so until it is reset.</summary>
    public void TaintWith(string reason) => Taint ??= reason;

    /// <summary>The SET options set since login or reset, in the order last set.</summary>
    private readonly List<SettingAssignment> _assignments = [];

    /// <summary>Batches whose SETs could not be read plainly, folded in whole.</summary>
    private ulong _unclear;

    /// <summary>
    /// Records what a batch set. The session's settings are the options it has set, each with its
    /// last value, in the order they were last set: setting one again moves it to the end. So the same
    /// batch sent again leaves the same state - a report re-run on one connection keeps its key - while
    /// the same options set in a different order still count as different, which they can be (SET
    /// ANSI_DEFAULTS overlaps SET ANSI_NULLS). A batch whose SETs could not be read plainly is folded
    /// in whole, which can only make keys differ, never make two different sessions look alike.
    /// </summary>
    public void ApplySettings(SessionEffects effects, ReadOnlySpan<char> batch)
    {
        foreach (var assignment in effects.Assignments)
        {
            _assignments.RemoveAll(a => a.Option == assignment.Option);
            _assignments.Add(assignment);
        }

        if (effects.Unclear)
        {
            var fold = new XxHash64();
            Span<byte> previous = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(previous, _unclear);
            fold.Append(previous);
            fold.Append(MemoryMarshal.AsBytes(batch));
            _unclear = fold.GetCurrentHashAsUInt64();
        }

        if (_assignments.Count == 0 && _unclear == 0)
        {
            SettingsHash = _initialSettings;
            return;
        }

        var hash = new XxHash64();
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(number, _initialSettings);
        hash.Append(number);
        BinaryPrimitives.WriteUInt64LittleEndian(number, _unclear);
        hash.Append(number);
        foreach (var (option, value) in _assignments) hash.Append(Encoding.UTF8.GetBytes($"{option}={value}\n"));
        SettingsHash = hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// The login half of the context: what the client asked for and the target agreed to. Features
    /// change the wire format of results (UTF-8 collations, JSON and vector types, Always Encrypted
    /// key tables), the option flags and language change their content.
    /// </summary>
    public static ulong LoginHashOf(ProxySession session)
    {
        var d = session.LoginDefaults;
        var text = new StringBuilder()
            .Append(d.OptionFlags1).Append('|').Append(d.OptionFlags2).Append('|').Append(d.TypeFlags).Append('|')
            .Append(d.OptionFlags3).Append('|').Append(d.ClientLcid).Append('|').Append(d.Language.ToLowerInvariant()).Append('|')
            .Append(session.TdsVersion).Append('|')
            .Append(string.Join(",", session.AcknowledgedFeatures.Order()));
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text.ToString()));
    }
}
