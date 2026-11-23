using System.Buffers;
using System.Collections.Concurrent;

namespace DaffyZoom.Cache;

/// <summary>
/// Bounds the memory the relay may spend copying requests, however many sessions are running.
/// Reserved as bytes are copied, released when the copy is done with; when it runs out, requests
/// are simply relayed without being considered.
/// </summary>
internal sealed class ByteBudget(long maxBytes)
{
    private long _bytes;

    public long MaxBytes { get; } = maxBytes;
    public long Bytes => Interlocked.Read(ref _bytes);

    public bool TryReserve(int count)
    {
        if (Interlocked.Add(ref _bytes, count) <= MaxBytes) return true;
        Interlocked.Add(ref _bytes, -count);
        return false;
    }

    public void Release(int count)
    {
        if (count != 0) Interlocked.Add(ref _bytes, -count);
    }
}

/// <summary>
/// A request that was slower than the threshold, with everything the analyzer needs: its bytes,
/// the context it ran in, and how its answer went. The payload is rented and budgeted;
/// <see cref="Release"/> gives both back and must be called exactly once.
/// </summary>
internal sealed class SlowRequest
{
    public required byte Type { get; init; }
    public required byte[] Payload { get; init; }
    public required int Length { get; init; }
    public required int Reserved { get; init; }
    public required ContextSnapshot Context { get; init; }
    public required int Session { get; init; }
    public required string Login { get; init; }
    public required string App { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required double DurationMs { get; init; }
    public double? FirstByteMs { get; init; }
    public required long ResponseBytes { get; init; }
    public required ushort DoneStatus { get; init; }

    /// <summary>The request's key, as the relay worked it out; null and the analyzer works it out.</summary>
    public CacheKey? Key { get; init; }

    public ReadOnlySpan<byte> Span => Payload.AsSpan(0, Length);

    public void Release(ByteBudget budget)
    {
        ArrayPool<byte>.Shared.Return(Payload);
        budget.Release(Reserved);
    }
}

/// <summary>What the cache has seen, for :status and daffyzoom.health. Written by every session, so every count is Interlocked.</summary>
internal sealed class CacheCounters
{
    private long _requests, _eligible, _slow, _analyzed, _droppedQueueFull, _notCopied, _marsSessions;

    public long Requests => Interlocked.Read(ref _requests);
    public long Eligible => Interlocked.Read(ref _eligible);
    public long Slow => Interlocked.Read(ref _slow);
    public long Analyzed => Interlocked.Read(ref _analyzed);
    public long DroppedQueueFull => Interlocked.Read(ref _droppedQueueFull);
    public long NotCopied => Interlocked.Read(ref _notCopied);
    public long MarsSessions => Interlocked.Read(ref _marsSessions);

    /// <summary>Requests that were relayed without being considered, by why.</summary>
    public ConcurrentDictionary<string, long> Bypassed { get; } = new();

    public void CountRequest() => Interlocked.Increment(ref _requests);
    public void CountEligible() => Interlocked.Increment(ref _eligible);
    public void CountSlow() => Interlocked.Increment(ref _slow);
    public void CountAnalyzed() => Interlocked.Increment(ref _analyzed);
    public void CountQueueFull() => Interlocked.Increment(ref _droppedQueueFull);
    public void CountNotCopied() => Interlocked.Increment(ref _notCopied);
    public void CountMarsSession() => Interlocked.Increment(ref _marsSessions);
    public void CountBypass(string reason) => Bypassed.AddOrUpdate(reason, 1, (_, n) => n + 1);

    private long _hits, _hitBytes, _filled, _fillsStarted, _fallbacks, _invalidated;

    public long Hits => Interlocked.Read(ref _hits);
    public long HitBytes => Interlocked.Read(ref _hitBytes);
    public long FillsStarted => Interlocked.Read(ref _fillsStarted);
    public long Filled => Interlocked.Read(ref _filled);
    public long Fallbacks => Interlocked.Read(ref _fallbacks);
    public long Invalidated => Interlocked.Read(ref _invalidated);

    /// <summary>Fills that were not kept, by why.</summary>
    public ConcurrentDictionary<string, long> FillsAbandoned { get; } = new();

    /// <summary>Hits that fell back to SQL Server before their first byte, by why.</summary>
    public ConcurrentDictionary<string, long> FallbackReasons { get; } = new();

    public void CountHit(long bytes)
    {
        Interlocked.Increment(ref _hits);
        Interlocked.Add(ref _hitBytes, bytes);
    }

    public void CountFillStarted() => Interlocked.Increment(ref _fillsStarted);
    public void CountFilled() => Interlocked.Increment(ref _filled);
    public void CountFillAbandoned(string reason) => FillsAbandoned.AddOrUpdate(reason, 1, (_, n) => n + 1);
    public void CountInvalidated() => Interlocked.Increment(ref _invalidated);

    public void CountFallback(string reason)
    {
        Interlocked.Increment(ref _fallbacks);
        FallbackReasons.AddOrUpdate(reason, 1, (_, n) => n + 1);
    }
}
