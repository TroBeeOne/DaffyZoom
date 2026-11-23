using System.Text;

namespace DaffyZoom.Repl;

/// <summary>
/// Where the running commentary goes while the REPL owns the terminal.
///
/// In plain listener mode every session and every translated statement prints as it happens, which
/// is the best thing about watching this run. At a prompt it is the worst: log lines from another
/// client arrive mid-keystroke and shove the cursor around. So the REPL takes Console.Out away and
/// points it here instead, and <c>:log</c> hands it back on demand.
///
/// Nothing is lost, only deferred. Startup output — the banner, the mount list, the certificate —
/// is printed before the swap, because that is what tells you the server is usable.
/// </summary>
internal sealed class LogBuffer : TextWriter
{
    /// <summary>Enough to cover a session or two of chatter; older lines fall off the back.</summary>
    private const int Capacity = 1000;

    private readonly Queue<string> _lines = new();
    private readonly StringBuilder _partial = new();
    private readonly object _gate = new();

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>
    /// Set while <c>:log follow</c> is running, so new lines are mirrored to the real terminal as
    /// they arrive. Invoked outside the lock: a follower writing to the console must not be able
    /// to block a TDS session that is only trying to log.
    /// </summary>
    public Action<string>? Follower { get; set; }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        List<string>? completed = null;

        lock (_gate)
        {
            foreach (char c in value)
            {
                if (c == '\n')
                {
                    string line = _partial.ToString().TrimEnd('\r');
                    _partial.Clear();
                    Append(line);
                    (completed ??= []).Add(line);
                }
                else
                {
                    _partial.Append(c);
                }
            }
        }

        if (completed is null) return;
        var follower = Follower;
        if (follower is null) return;
        foreach (string line in completed) follower(line);
    }

    public override void WriteLine(string? value) => Write((value ?? string.Empty) + "\n");

    public override void WriteLine() => Write("\n");

    /// <summary>The most recent <paramref name="count"/> lines, oldest first.</summary>
    public IReadOnlyList<string> Tail(int count)
    {
        lock (_gate)
        {
            if (count >= _lines.Count) return _lines.ToArray();
            return _lines.Skip(_lines.Count - count).ToArray();
        }
    }

    public int Count
    {
        get { lock (_gate) return _lines.Count; }
    }

    private void Append(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > Capacity) _lines.Dequeue();
    }
}
