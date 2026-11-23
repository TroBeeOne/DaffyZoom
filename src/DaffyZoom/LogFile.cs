using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DaffyZoom;

/// <summary>
/// --log-file: the operational log as JSON lines, one object per line -
/// {"ts":"...Z","level":"warn","event":"session.login_refused","session":3,"msg":"..."} - so log tooling can
/// ingest it without parsing rules and alerts can key on the stable event code. The console keeps
/// its human-readable form; this is the same entries, for machines.
/// </summary>
internal static class LogFile
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static RollingFileWriter Attach(string template)
    {
        var file = new RollingFileWriter(template);
        Log.AttachFile(entries => file.WriteBatch(entries.Select(Format)));
        return file;
    }

    public static string Format(LogEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteString("ts", entry.TimestampUtc);
            w.WriteString("level", entry.Level.ToString().ToLowerInvariant());
            w.WriteString("event", entry.Code);
            if (entry.Session > 0) w.WriteNumber("session", entry.Session);
            w.WriteString("msg", entry.Message);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }
}
