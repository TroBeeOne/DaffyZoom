using System.Threading.Channels;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>
/// Works out, off the relay, what each slow request is: its key, whether it may be cached and why
/// not, and what admission it gets on the watch list. One worker, fed by a bounded queue that the
/// relay only ever tries to write to: when the queue is full the request is dropped and counted,
/// and the session never notices.
/// </summary>
internal sealed class Analyzer(CacheOptions options, WatchList watchList, CacheCounters counters, ByteBudget budget) : IAsyncDisposable
{
    private readonly Channel<SlowRequest> _queue = Channel.CreateBounded<SlowRequest>(
        new BoundedChannelOptions(options.AnalyzerQueue) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });

    private Task? _worker;
    private int _failuresLogged;

    /// <summary>Told about every analysis: the request, its key and classification, and the watch list's verdict. For :watch and tests.</summary>
    public event Action<SlowRequest, CacheKey, Classification, string>? Analyzed;

    /// <summary>For tests: until this is done, the worker waits before each analysis, as a busy analyzer would.</summary>
    internal Task? Held { get; set; }

    public void Start() => _worker = Task.Run(WorkAsync);

    /// <summary>
    /// Called on the relay. Never waits: a full queue drops the request. Until it is recorded, the
    /// watch list knows it is coming, so the next run of it may fill early (<see cref="WatchList.FillWanted"/>).
    /// </summary>
    public bool TrySubmit(SlowRequest request)
    {
        if (request.Key is { } key) watchList.Pending(key);
        if (_queue.Writer.TryWrite(request)) return true;
        if (request.Key is { } dropped) watchList.NoLongerPending(dropped);
        counters.CountQueueFull();
        request.Release(budget);
        return false;
    }

    private async Task WorkAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (Held is { } held) await held;
                Analyze(request, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // A request the analyzer cannot make sense of costs its own analysis, nothing else.
                if (Interlocked.Increment(ref _failuresLogged) <= 10)
                    Log.Error("cache.analyze_failed", $"could not analyze a request from session {request.Session}: {ex}", request.Session);
            }
            finally
            {
                // After it is recorded, never before: an early fill asking meanwhile sees it one way or the other.
                if (request.Key is { } key) watchList.NoLongerPending(key);
                request.Release(budget);
            }
        }
    }

    internal void Analyze(SlowRequest request, DateTime nowUtc)
    {
        var payload = request.Span;
        var key = request.Key ?? CacheKey.Compute(request.Context, request.Type, payload);
        var classification = watchList.Known(key) ?? Classify(request.Type, payload, request.Context.QuotedIdentifier);

        Refusal? run =
            (request.DoneStatus & (TdsConstants.DoneStatus.Error | TdsConstants.DoneStatus.ServerError)) != 0 ? Refusal.ResponseHadError
            : request.ResponseBytes > options.MaxEntryBytes ? Refusal.TooLarge
            : request.DurationMs < options.MinBenefit * options.EstimatedReplayMs(request.ResponseBytes) ? Refusal.NotWorthIt
            : null;

        string verdict = watchList.Record(key, classification, run, request, nowUtc);
        counters.CountAnalyzed();
        Analyzed?.Invoke(request, key, classification, verdict);
    }

    /// <summary>What a request is, from its bytes: a batch's text, or the one call of an RPC.</summary>
    public static Classification Classify(byte type, ReadOnlySpan<byte> payload, bool quotedIdentifier)
    {
        if (type == TdsConstants.PacketType.SqlBatch)
            return Classifier.Batch(RequestParser.ParseBatchText(payload), quotedIdentifier);

        var calls = RequestParser.ParseRpc(payload);
        if (calls.Count != 1)
            return Classification.Refused(Refusal.MultipleCalls, $"{calls.Count} calls", string.Join("; ", calls.Select(c => "EXEC " + c.ProcName.ToLowerInvariant())));

        var call = calls[0];
        string procedure = "EXEC " + call.ProcName.ToLowerInvariant();
        if (call.Remainder is not null) return Classification.Refused(Refusal.ParseFailed, call.RemainderReason, procedure);

        // fNoMetaData is not refused here: System.Data.SqlClient sets it on every parameterised
        // query, and SQL Server sends the metadata anyway (seen against SQL Server 2022). It is in
        // the key, since it is in the request's bytes. What cannot be replayed is an answer that
        // really left its metadata out - COLMETADATA 0xFFFF - and that is refused when the answer is
        // checked before it is kept.
        if (call.Parameters.Any(p => p.IsEncrypted)) return Classification.Refused(Refusal.Encrypted, "Always Encrypted parameters", procedure);

        switch (call.ProcId)
        {
            case TdsConstants.ProcId.ExecuteSql:
                return call.Sql is { } sql
                    ? Classifier.Statement(sql, quotedIdentifier)
                    : Classification.Refused(Refusal.ParseFailed, "sp_executesql without a statement", procedure);

            // Prepared and executed in one call: cached as its statement is, the handle in its answer
            // issued afresh on every hit. A SET inside it is refused: whether it outlives the call, as
            // it would not inside sp_executesql, is not something a hit could repeat.
            case TdsConstants.ProcId.PrepExec:
            {
                if (call.Sql is not { } statement) return Classification.Refused(Refusal.ParseFailed, "sp_prepexec without a statement", procedure);
                var classification = Classifier.Statement(statement, quotedIdentifier);
                if (classification.Cacheable && classification.LeadingSets > 0)
                    return Classification.Refused(Refusal.SessionState, "a SET inside a prepared statement", classification.Shape);
                return classification with { Prepares = true };
            }

            // By handle, or preparing only: what a handle stands for is the session's, not the request's.
            case TdsConstants.ProcId.Prepare or TdsConstants.ProcId.PrepExecRpc
                 or TdsConstants.ProcId.Execute or TdsConstants.ProcId.Unprepare:
                return Classification.Refused(Refusal.Prepared, call.ProcName, procedure);

            case >= TdsConstants.ProcId.Cursor and <= TdsConstants.ProcId.CursorClose:
                return Classification.Refused(Refusal.Cursor, call.ProcName, procedure);

            default:
                return Classification.Refused(Refusal.Procedure, call.ProcName, procedure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        while (_queue.Reader.TryRead(out var left))
        {
            if (left.Key is { } key) watchList.NoLongerPending(key);
            left.Release(budget);
        }
    }
}
