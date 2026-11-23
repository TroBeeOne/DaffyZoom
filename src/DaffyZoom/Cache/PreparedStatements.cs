using System.Buffers.Binary;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>
/// Serving sp_prepexec. Its answer carries one thing that belongs
/// to the session it ran on: the handle SQL Server issued for the statement it prepared, which the
/// client keeps and later runs (sp_execute) or frees (sp_unprepare) on its own session. A hit replays
/// the cached answer, but the handle in it must be one this session's server really issued: so the
/// hit first prepares the same statement on the session - DaffyZoom's own sp_prepare, made of the
/// client's own handle, declarations and statement arguments, byte for byte - and the replay carries
/// the handle that comes back. Everything else in the answer is as cached.
/// </summary>
internal static class PreparedStatements
{
    /// <summary>
    /// The sp_prepare for an sp_prepexec request: its ALL_HEADERS, then a call to sp_prepare (by id)
    /// with the request's first three arguments as they came - @handle OUTPUT, @params, @stmt - and
    /// no values. Null when the request is not one sp_prepexec that could be read whole.
    /// </summary>
    public static byte[]? PrepareFor(ReadOnlySpan<byte> payload)
    {
        IReadOnlyList<RpcCall> calls;
        try
        {
            calls = RequestParser.ParseRpc(payload);
        }
        catch (Exception ex) when (ex is TdsUnsupportedException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
        if (calls is not [{ ProcId: TdsConstants.ProcId.PrepExec, Remainder: null, Sql: not null } call] || call.ArgumentSpans.Count < 3) return null;

        int headers = AllHeaders.BodyOffset(payload);
        int arguments = call.ArgumentSpans.Take(3).Sum(s => s.Length);
        var prepare = new byte[headers + 6 + arguments];
        payload[..headers].CopyTo(prepare);
        BinaryPrimitives.WriteUInt16LittleEndian(prepare.AsSpan(headers), TdsConstants.RpcProcIdSwitch);
        BinaryPrimitives.WriteUInt16LittleEndian(prepare.AsSpan(headers + 2), TdsConstants.ProcId.Prepare);
        // Option flags (headers + 4): none. fNoMetaData, if the client set it, was about its own call's results.

        int at = headers + 6;
        foreach (var (start, length) in call.ArgumentSpans.Take(3))
        {
            payload.Slice(start, length).CopyTo(prepare.AsSpan(at));
            at += length;
        }
        return prepare;
    }

    /// <summary>What SQL Server said to DaffyZoom's own sp_prepare.</summary>
    /// <param name="Handle">The handle it issued, or null if it did not prepare the statement.</param>
    /// <param name="ForClient">The ENVCHANGE and SESSIONSTATE tokens the client must hear first: a reset's acknowledgement, the state it went back to.</param>
    /// <param name="Problem">Why there is no handle - SQL Server refused the statement, or answered unexpectedly. Either way the entry is not tried again.</param>
    public sealed record Prepared(int? Handle, List<byte[]> ForClient, string? Problem);

    public static Prepared Read(ReadOnlySpan<byte> answer, bool columnEncryption)
    {
        var detail = ResponseReader.Read(answer, columnEncryption);
        if (detail.Errors.Count > 0)
            return new(null, [], $"SQL Server would not prepare its statement (error {detail.Errors[0].Number}: {detail.Errors[0].Text})");
        if (detail.Problem is not null) return new(null, [], $"the answer to its prepare could not be read ({detail.Problem})");
        if (detail.EnvChangeAfterStart) return new(null, [], "its prepare changed the session");
        if (detail.OutputHandle is not { } handle) return new(null, [], "its prepare returned no handle");

        // The leading run - a reset's ENVCHANGEs, with any SESSIONSTATE among them - and SESSIONSTATE
        // tokens after it, in order: what the client would have heard from its own request.
        var forClient = new List<byte[]>();
        if (detail.LeadingEnvChangeEnd > 0) forClient.Add(answer[..detail.LeadingEnvChangeEnd].ToArray());
        foreach (var (start, length) in detail.SessionStates)
            if (start >= detail.LeadingEnvChangeEnd) forClient.Add(answer.Slice(start, length).ToArray());
        return new(handle, forClient, null);
    }

    /// <summary>
    /// Where an offset into a filled answer lands in what is stored of it: the stored copy starts at
    /// <paramref name="start"/> and leaves the SESSIONSTATE tokens out (see <see cref="Filler.WithoutSessionStates"/>).
    /// </summary>
    public static long StoredOffset(int offset, int start, List<(int Start, int Length)> sessionStates) =>
        offset - start - sessionStates.Where(s => s.Start >= start && s.Start < offset).Sum(s => (long)s.Length);
}

/// <summary>
/// One int written over a replay as it streams: the handle this session's server issued, where the
/// cached answer has the filling session's. Chunks that do not hold it pass through untouched.
/// </summary>
internal readonly record struct HandlePatch(long Offset, int Handle)
{
    public ReadOnlyMemory<byte> Apply(ReadOnlyMemory<byte> chunk, long chunkStart)
    {
        long end = chunkStart + chunk.Length;
        if (Offset + 4 <= chunkStart || Offset >= end) return chunk;

        Span<byte> handle = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(handle, Handle);
        byte[] patched = chunk.ToArray();
        for (int i = 0; i < 4; i++)
        {
            long at = Offset + i - chunkStart;
            if (at >= 0 && at < patched.Length) patched[at] = handle[i];
        }
        return patched;
    }
}
