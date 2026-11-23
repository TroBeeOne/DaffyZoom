using DaffyZoom.Cache;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>sp_prepexec: classified by its statement, its own prepare built from it, its handle found and replaced.</summary>
public class PreparedTests
{
    private const string Statement = "SELECT a FROM dbo.t WHERE b > @P1";
    private const string Declarations = "@P1 int";

    private static Classification Of(byte[] packet) =>
        Analyzer.Classify(packet[0], packet.AsSpan(TdsConstants.HeaderLength), quotedIdentifier: true);

    /// <summary>An sp_prepexec answer as SQL Server 2022 sends one: the rows, RETURNSTATUS, the handle, DONEPROC.</summary>
    internal static byte[] PrepExecAnswer(int handle, params int[] values)
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("a", SqlColumnType.Int)], values.Select(v => new object?[] { v }).ToList()));
        byte[] encoded = reply.Encode(rpc: true);
        // Reply ends RETURNSTATUS (5 bytes), DONEPROC (13): the handle goes between them, where SQL Server puts it.
        return [.. encoded[..^13], .. Requests.HandleValue(handle), .. encoded[^13..]];
    }

    [Fact]
    public void AnSpPrepexecIsJudgedByItsStatementAndMarked()
    {
        var cacheable = Of(Requests.PrepExec(Statement, Declarations, 300));
        Assert.True(cacheable.Cacheable);
        Assert.True(cacheable.Prepares);

        Assert.Equal(Refusal.NonDeterministic, Of(Requests.PrepExec("SELECT a FROM dbo.t WHERE d < GETDATE() AND b > @P1", Declarations, 1)).Refusal);
        Assert.False(Of(Requests.Rpc("sp_executesql", ("@stmt", Statement), ("@params", Declarations), ("@P1", "1"))).Prepares);
    }

    /// <summary>Whether a SET inside a prepared statement outlives the call is not something a hit could repeat.</summary>
    [Fact]
    public void ASetInsideAPreparedStatementIsRefused()
    {
        var refused = Of(Requests.PrepExec("SET ARITHABORT ON; SELECT a FROM dbo.t WHERE b > @P1", Declarations, 1));
        Assert.Equal(Refusal.SessionState, refused.Refusal);
        Assert.Contains("prepared statement", refused.Detail);
    }

    [Fact]
    public void TheParserKnowsWhereEachArgumentIs()
    {
        byte[] payload = Requests.PrepExecPayload(Statement, Declarations, 300);
        var call = Assert.Single(RequestParser.ParseRpc(payload));
        Assert.Equal(4, call.ArgumentSpans.Count);
        Assert.Equal(payload.Length, call.ArgumentSpans[^1].Start + call.ArgumentSpans[^1].Length);
        for (int i = 1; i < call.ArgumentSpans.Count; i++)
            Assert.Equal(call.ArgumentSpans[i - 1].Start + call.ArgumentSpans[i - 1].Length, call.ArgumentSpans[i].Start);
    }

    /// <summary>DaffyZoom's own sp_prepare is the client's handle, declarations and statement, byte for byte, and nothing else.</summary>
    [Fact]
    public void ThePrepareIsTheRequestsOwnArgumentsWithoutTheValues()
    {
        byte[] payload = Requests.PrepExecPayload(Statement, Declarations, 300, optionFlags: 0x02);
        byte[] prepare = PreparedStatements.PrepareFor(payload)!;

        var call = Assert.Single(RequestParser.ParseRpc(prepare));
        Assert.Equal(TdsConstants.ProcId.Prepare, call.ProcId);
        Assert.Equal(Statement, call.Sql);
        Assert.Equal(Declarations, call.ParamDeclarations);
        Assert.Empty(call.Parameters);                          // no values: sp_prepare's fourth argument, options, is left out
        Assert.Equal(0, call.OptionFlags);                      // the client's fNoMetaData was about its own call
        Assert.Equal(payload[..22], prepare[..22]);             // ALL_HEADERS as the client sent them

        var original = Assert.Single(RequestParser.ParseRpc(payload));
        int argumentsLength = original.ArgumentSpans.Take(3).Sum(s => s.Length);
        Assert.Equal(payload.AsSpan(original.ArgumentSpans[0].Start, argumentsLength).ToArray(), prepare[(22 + 6)..]);
    }

    [Fact]
    public void OnlyAnSpPrepexecHasAPrepare()
    {
        Assert.Null(PreparedStatements.PrepareFor(Requests.Rpc("sp_executesql", ("@stmt", Statement), ("@params", Declarations), ("@P1", "1")).AsSpan(TdsConstants.HeaderLength)));
        Assert.Null(PreparedStatements.PrepareFor(Requests.BatchPayload("SELECT 1")));
    }

    [Fact]
    public void TheHandleIsFoundWhereItsFourBytesAre()
    {
        byte[] answer = PrepExecAnswer(7, 1, 2, 3);
        var detail = ResponseReader.Read(answer, columnEncryption: false);
        Assert.Equal(7, detail.OutputHandle);
        Assert.Equal(7, BitConverter.ToInt32(answer, detail.OutputHandleOffset!.Value));
        Assert.Null(ReplayCheck.Check(detail, reset: false).Refused);
    }

    [Fact]
    public void TheServersAnswerToThePrepareGivesTheHandleAndWhatTheClientMustHear()
    {
        byte[] resetAck = [TdsConstants.Token.EnvChange, 3, 0, TdsConstants.EnvChangeType.ResetConnectionAck, 0, 0];
        var prepared = PreparedStatements.Read([.. resetAck, .. Requests.HandleValue(42), .. Requests.ProcEnd()], columnEncryption: false);
        Assert.Equal(42, prepared.Handle);
        Assert.Equal(resetAck, Assert.Single(prepared.ForClient));

        var plain = PreparedStatements.Read([.. Requests.HandleValue(43), .. Requests.ProcEnd()], columnEncryption: false);
        Assert.Equal(43, plain.Handle);
        Assert.Empty(plain.ForClient);
    }

    [Fact]
    public void AnErrorToThePrepareOrNoHandleMeansNoHit()
    {
        byte[] failed = Reply.Failed(new Commands.GatewayException(208, "Invalid object name 'dbo.t'.")).Encode(rpc: true);
        var prepared = PreparedStatements.Read(failed, columnEncryption: false);
        Assert.Null(prepared.Handle);
        Assert.Contains("208", prepared.Problem);

        var empty = PreparedStatements.Read(Requests.ProcEnd(), columnEncryption: false);
        Assert.Null(empty.Handle);
        Assert.Contains("no handle", empty.Problem);
    }

    /// <summary>The handle is written over wherever it falls, across a chunk boundary too; other chunks are passed on as they are.</summary>
    [Fact]
    public void ThePatchWritesTheHandleAcrossChunks()
    {
        byte[] answer = PrepExecAnswer(7, 1, 2, 3);
        int offset = ResponseReader.Read(answer, false).OutputHandleOffset!.Value;
        var patch = new HandlePatch(offset, 0x11223344);
        byte[] expected = [.. answer];
        BitConverter.GetBytes(0x11223344).CopyTo(expected, offset);

        foreach (int split in new[] { offset - 5, offset, offset + 1, offset + 3, offset + 4 })
        {
            var first = new ReadOnlyMemory<byte>(answer, 0, split);
            var second = new ReadOnlyMemory<byte>(answer, split, answer.Length - split);
            byte[] patched = [.. patch.Apply(first, 0).ToArray(), .. patch.Apply(second, split).ToArray()];
            Assert.Equal(expected, patched);
        }

        var untouched = new ReadOnlyMemory<byte>(answer, 0, offset);
        Assert.True(patch.Apply(untouched, 0).Equals(untouched));
    }

    [Fact]
    public void TheStoredOffsetLeavesOutWhatIsNotStored()
    {
        // Stored from 10 on, without a 6-byte SESSIONSTATE at 20 and one at 50 (after the handle).
        Assert.Equal(30, PreparedStatements.StoredOffset(46, 10, [(20, 6), (50, 6)]));
        Assert.Equal(36, PreparedStatements.StoredOffset(46, 10, [(4, 6), (50, 6)]));
    }
}
