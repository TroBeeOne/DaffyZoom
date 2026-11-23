using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>
/// Which cached answer a request may have: a 128-bit hash of its context and its body. Equal keys
/// mean the same bytes were sent in the same context, so SQL Server would have computed the same
/// answer from the same data.
/// </summary>
internal readonly record struct CacheKey(UInt128 Value)
{
    public override string ToString() => Value.ToString("x32");

    /// <summary>The first 12 hex digits: enough to tell entries apart on the watch list.</summary>
    public string Short => ToString()[..12];

    /// <summary>
    /// Hashes the context and the request's <see cref="Body"/>. Packet headers and ALL_HEADERS are
    /// left out: they hold the SPID, packet numbers, the transaction descriptor and trace activity
    /// ids, which differ from one sending to the next while the question stays the same.
    /// </summary>
    public static CacheKey Compute(in ContextSnapshot context, byte requestType, ReadOnlySpan<byte> payload)
    {
        var hash = new XxHash128();
        Span<byte> number = stackalloc byte[8];

        hash.Append([requestType]);
        Text(hash, context.Target.ToLowerInvariant());    // a host name, which DNS compares without case
        Text(hash, context.TargetLogin);                  // exactly as given: on a case-sensitive server,
        Text(hash, context.Database ?? "");               // 'Sales' and 'sales' are two databases
        BinaryPrimitives.WriteUInt64LittleEndian(number, context.LoginHash);
        hash.Append(number);
        BinaryPrimitives.WriteUInt64LittleEndian(number, context.SettingsHash);
        hash.Append(number);

        hash.Append(Body(requestType, payload));
        return new CacheKey(hash.GetCurrentHashAsUInt128());
    }

    /// <summary>
    /// What of a request is hashed: its body after ALL_HEADERS, byte for byte - except, for a SQL
    /// batch, whitespace that cannot change its answer. Spaces, tabs and line breaks after its last
    /// character, and spaces and tabs before its first line's text, are left out: they mean nothing
    /// to T-SQL and move no line number. Line breaks before the text are kept, because they move
    /// every line number SQL Server reports, and a cached answer replays its messages (a PRINT, a
    /// warning) with theirs. An RPC is hashed whole: its parameter values end where it does.
    /// </summary>
    public static ReadOnlySpan<byte> Body(byte requestType, ReadOnlySpan<byte> payload)
    {
        var body = payload[AllHeaders.BodyOffset(payload)..];
        if (requestType != TdsConstants.PacketType.SqlBatch || (body.Length & 1) != 0) return body;

        // UTF-16LE: an ASCII character is its byte, then zero.
        int start = 0, end = body.Length;
        while (start < end && body[start + 1] == 0 && body[start] is (byte)' ' or (byte)'\t') start += 2;
        while (end > start && body[end - 1] == 0 && body[end - 2] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') end -= 2;
        return body[start..end];
    }

    /// <summary>Terminated, so "ab" + "c" and "a" + "bc" cannot collide.</summary>
    private static void Text(XxHash128 hash, string value)
    {
        hash.Append(Encoding.UTF8.GetBytes(value));
        hash.Append([0]);
    }
}
