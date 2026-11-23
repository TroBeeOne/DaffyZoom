using System.Diagnostics;
using System.Text;
using DaffyZoom.Tds;

namespace DaffyZoom.Proxy;

/// <summary>
/// An annotated, decoded record of both directions of a session: --transcript DIR writes one file
/// per session, --trace prints the same to the log.
///
/// This is DaffyDB's capture relay brought back (it was removed from DaffyDB at c1f6b56). It
/// is more useful here than it was there: DaffyZoom terminates TLS on both sides, so the transcript
/// shows plaintext TDS that Wireshark, looking at the wire, would only see as encrypted records.
///
/// It is a debugging aid and is priced like one - it copies and decodes every message in line, which
/// slows the session it watches, and it writes SQL and parameter values to disk in the clear.
/// Passwords never appear: they travel only in LOGIN7, which happens before any transcript starts.
/// </summary>
internal sealed class Transcript : IRelayObserver
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly Direction _client;
    private readonly Direction _server;

    private Transcript(TextWriter writer, bool ownsWriter, ProxySession session)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
        _client = new Direction("CLIENT --> TARGET", columnEncryption: false, session.TdsVersion);
        _server = new Direction("TARGET --> CLIENT", session.ColumnEncryption, session.TdsVersion);
    }

    public static Transcript ToFile(string directory, ProxySession session)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{session.Id:D4}.txt");
        var writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        writer.WriteLine($"### {session}");
        writer.WriteLine($"### target {session.TargetLogin}@{session.Target}" +
                         (session.RoutedTo is null ? "" : $" (redirected to {session.RoutedTo})") +
                         $" spid={session.Spid} version={session.ServerVersion}");
        writer.WriteLine($"### client side: {session.FrontSecurity}; target side: {session.UpstreamSecurity}");
        Log.Info("transcript.open", $"transcript: {path}", session.Id);
        return new Transcript(writer, ownsWriter: true, session);
    }

    public static Transcript ToConsole(ProxySession session) => new(Console.Out, ownsWriter: false, session);

    public void OnClientBytes(ReadOnlySpan<byte> data, long timestamp) => Feed(_client, data, timestamp);

    public void OnServerBytes(ReadOnlySpan<byte> data, long timestamp) => Feed(_server, data, timestamp);

    public void OnClosed(string reason)
    {
        lock (_gate)
        {
            _writer.WriteLine($"\n### closed: {reason}");
            if (_ownsWriter) _writer.Dispose();
        }
    }

    private void Feed(Direction direction, ReadOnlySpan<byte> data, long timestamp)
    {
        lock (_gate)
        {
            if (direction.Framer.Desynced)
            {
                // Most often MARS: after login both sides speak SMP, which wraps TDS in its own frames.
                Banner(direction, $"{data.Length} opaque bytes (not TDS packets - MARS/SMP?)", timestamp);
                var hex = new StringBuilder();
                HexDump.Write(hex, data, maxBytes: 128);
                _writer.Write(hex.ToString());
                return;
            }

            foreach (var packet in direction.Framer.Push(data))
            {
                if (direction.Assembler.Push(packet) is not { } message) continue;

                Banner(direction, TdsConstants.PacketTypeName(message.Type), timestamp);
                _writer.Write(direction.Annotator.Describe(message));
            }
        }
    }

    private void Banner(Direction direction, string label, long timestamp)
    {
        double ms = Stopwatch.GetElapsedTime(_start, timestamp).TotalMilliseconds;
        _writer.WriteLine($"\n=== {direction.Label}  {label}  +{ms:F1}ms");
    }

    /// <summary>Decoding is stateful per direction: COLMETADATA says how the ROWs after it read.</summary>
    private sealed class Direction(string label, bool columnEncryption, uint tdsVersion)
    {
        public string Label { get; } = label;
        public PacketFramer Framer { get; } = new();
        public MessageAssembler Assembler { get; } = new();
        public Annotator Annotator { get; } = new(columnEncryption, tdsVersion);
    }
}
