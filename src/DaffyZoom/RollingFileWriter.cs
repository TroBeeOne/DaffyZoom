using System.Text;

namespace DaffyZoom;

/// <summary>
/// An append-only text file that starts a new file each day when its name contains {date}
/// (e.g. daffyzoom-events-{date}.ndjson becomes daffyzoom-events-20260923.ndjson). Opened for shared
/// reading, so the file can be tailed while DaffyZoom writes it. Used for the event file, the SQL log
/// and the operational log alike.
/// </summary>
internal sealed class RollingFileWriter : IDisposable
{
    private readonly string _template;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string _currentPath = "";

    public RollingFileWriter(string template)
    {
        _template = Path.GetFullPath(template);
        string? directory = Path.GetDirectoryName(_template);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public string CurrentPath
    {
        get { lock (_gate) return _currentPath.Length > 0 ? _currentPath : Resolve(); }
    }

    public string Template => _template;

    public void Write(string text, bool flush = true)
    {
        lock (_gate)
        {
            var writer = Ensure();
            writer.Write(text);
            if (flush) writer.Flush();
        }
    }

    /// <summary>Several texts, then one flush: what the log file's writer does with each batch.</summary>
    public void WriteBatch(IEnumerable<string> texts)
    {
        lock (_gate)
        {
            var writer = Ensure();
            foreach (string text in texts) writer.Write(text);
            writer.Flush();
        }
    }

    public void WriteLines(IEnumerable<ReadOnlyMemory<byte>> lines)
    {
        lock (_gate)
        {
            var writer = Ensure();
            writer.Flush();
            var stream = writer.BaseStream;
            foreach (var line in lines)
            {
                stream.Write(line.Span);
                stream.WriteByte((byte)'\n');
            }
            stream.Flush();
        }
    }

    private StreamWriter Ensure()
    {
        string path = Resolve();
        if (_writer is not null && path == _currentPath) return _writer;

        _writer?.Dispose();
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _currentPath = path;
        return _writer;
    }

    private string Resolve() => _template.Replace("{date}", DateTime.Now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the name holds {date}, so that each day has its own file.</summary>
    public bool Rolls => _template.Contains("{date}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every file this template has made, on any day: the name with eight digits for {date}, in its
    /// directory. None when {date} is in the directory's part of the name, which is not looked into.
    /// </summary>
    public IReadOnlyList<string> DailyFiles()
    {
        string name = Path.GetFileName(_template);
        string? directory = Path.GetDirectoryName(_template);
        int at = name.IndexOf("{date}", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || directory is null || !Directory.Exists(directory)) return [];

        var pattern = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(name[..at]) + @"\d{8}" + System.Text.RegularExpressions.Regex.Escape(name[(at + 6)..]) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(directory).Where(f => pattern.IsMatch(Path.GetFileName(f))).ToList();
    }

    /// <summary>When the file's first line was written, from its "ts"; null if there is no file, or its first line is not a log entry.</summary>
    public DateTime? FirstEntryUtc()
    {
        string path = CurrentPath;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) return null;
            using var entry = System.Text.Json.JsonDocument.Parse(line);
            return entry.RootElement.TryGetProperty("ts", out var ts) && ts.TryGetDateTime(out var time) ? time.ToUniversalTime() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Moves the file to NAME.old, replacing the one before, and starts it afresh with the next line.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            string path = Resolve();
            if (File.Exists(path)) File.Move(path, path + ".old", overwrite: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
