using System.Buffers.Binary;
using System.Text;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Cache;

/// <summary>
/// Connections of DaffyZoom's own to the target, logged in as a mapping's target login and kept
/// open, for tracked mode's small queries: resolving a request's tables, and the check on every hit,
/// which has a deadline of its own and cannot wait for a login each time.
///
/// At most <c>maxPerTarget</c> (--check-connections) are in use at once for each target: a query
/// that finds them all busy waits for one, within its deadline, rather than opening another login -
/// a hundred hits at once must not be a hundred logins. Identical checks waiting for a connection
/// share one query (<see cref="QuerySharedAsync"/>).
///
/// A connection is used by one query at a time. One that fails, times out or is cancelled part-way
/// is closed, never reused: what state it is in can no longer be known. A reused idle connection the
/// server has closed meanwhile gets one retry on a fresh one.
/// </summary>
internal sealed class BackgroundConnections(int maxPerTarget = 8) : IAsyncDisposable
{
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(5);

    private sealed class Lease(UpstreamConnection connection, int packetSize)
    {
        public UpstreamConnection Connection { get; } = connection;
        public int PacketSize { get; } = packetSize;
        public long LastUsed { get; set; } = Environment.TickCount64;
    }

    /// <summary>One target's connections: those idle, and a slot for each one in use or being opened.</summary>
    private sealed class Pool(int max)
    {
        public SemaphoreSlim Slots { get; } = new(max, max);
        public Stack<Lease> Idle { get; } = new();
    }

    /// <summary>A shared query not yet sent: identical ones asked for meanwhile wait for its answer.</summary>
    private sealed class Shared
    {
        public TaskCompletionSource<byte[]> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly Dictionary<TargetEndpoint, Pool> _pools = [];
    private readonly Dictionary<(TargetEndpoint Target, string Sql), Shared> _unsent = [];
    private bool _disposed;

    /// <summary>Opens and logs in a connection. Tests stand in for SQL Server here.</summary>
    internal Func<TargetEndpoint, CancellationToken, Task<UpstreamConnection>> Connect { get; init; } =
        (target, cancellationToken) => UpstreamConnector.ConnectAsync(target, UpstreamConnector.CreateValidationLogin(target), mars: false, cancellationToken);

    public int MaxPerTarget => maxPerTarget;

    public int IdleCount { get { lock (_gate) return _pools.Values.Sum(p => p.Idle.Count); } }

    /// <summary>
    /// Runs one batch as <paramref name="target"/>'s login, in its database, and returns the answer's
    /// token stream - on a connection of its own, waited for while all are in use.
    /// </summary>
    public async Task<byte[]> QueryAsync(TargetEndpoint target, string sql, CancellationToken cancellationToken)
    {
        var pool = PoolOf(target);
        await pool.Slots.WaitAsync(cancellationToken);
        try
        {
            return await RunOnAnyAsync(target, pool, sql, cancellationToken);
        }
        finally
        {
            pool.Slots.Release();
        }
    }

    /// <summary>
    /// The same, for a query whose answer may be shared - the check a hit makes: one asked for while an
    /// identical one is still waiting for a connection waits for that one's answer instead. Only a
    /// query not yet sent is joined, so the answer is never older than the request asking. The query
    /// runs within <paramref name="limit"/> whoever gives up on it: a caller whose own deadline passes
    /// (<paramref name="cancellationToken"/>) stops waiting, and the query and its connection go on.
    /// </summary>
    public Task<byte[]> QuerySharedAsync(TargetEndpoint target, string sql, TimeSpan limit, CancellationToken cancellationToken)
    {
        Shared? shared;
        bool mine = false;
        lock (_gate)
        {
            if (!_unsent.TryGetValue((target, sql), out shared))
            {
                _unsent[(target, sql)] = shared = new Shared();
                mine = true;
            }
        }
        if (mine) _ = SendSharedAsync(target, sql, shared, limit);
        return shared.Answer.Task.WaitAsync(cancellationToken);
    }

    private async Task SendSharedAsync(TargetEndpoint target, string sql, Shared shared, TimeSpan limit)
    {
        var pool = PoolOf(target);
        bool slot = false;
        try
        {
            using var timeout = new CancellationTokenSource(limit);
            try
            {
                await pool.Slots.WaitAsync(timeout.Token);
                slot = true;
            }
            finally
            {
                // From here on it is being sent: whoever asks next sends one of their own.
                lock (_gate)
                    if (_unsent.TryGetValue((target, sql), out var unsent) && unsent == shared) _unsent.Remove((target, sql));
            }
            shared.Answer.TrySetResult(await RunOnAnyAsync(target, pool, sql, timeout.Token));
        }
        catch (OperationCanceledException)
        {
            shared.Answer.TrySetCanceled();
        }
        catch (Exception ex)
        {
            shared.Answer.TrySetException(ex);
            _ = shared.Answer.Task.Exception;
        }
        finally
        {
            if (slot) pool.Slots.Release();
        }
    }

    /// <summary>On an idle connection, or a new one. The caller holds one of the pool's slots.</summary>
    private async Task<byte[]> RunOnAnyAsync(TargetEndpoint target, Pool pool, string sql, CancellationToken cancellationToken)
    {
        var lease = Take(pool);
        bool reused = lease is not null;
        lease ??= await OpenAsync(target, cancellationToken);
        try
        {
            return await RunAsync(pool, lease, sql, cancellationToken);
        }
        catch (Exception ex) when (reused && ex is IOException or EndOfStreamException && !cancellationToken.IsCancellationRequested)
        {
            // An idle connection the server had closed: once more, on a new one.
            return await RunAsync(pool, await OpenAsync(target, cancellationToken), sql, cancellationToken);
        }
    }

    private async Task<byte[]> RunAsync(Pool pool, Lease lease, string sql, CancellationToken cancellationToken)
    {
        bool healthy = false;
        try
        {
            byte[] packets = Frame(sql, lease.PacketSize);
            for (int at = 0; at < packets.Length; at += lease.PacketSize)
                await lease.Connection.Stream.WriteAsync(packets.AsMemory(at, Math.Min(lease.PacketSize, packets.Length - at)), cancellationToken);
            var reply = await new TdsMessageReader(lease.Connection.Stream).ReadAsync(cancellationToken)
                        ?? throw new EndOfStreamException("the server closed the connection");
            healthy = true;
            return reply.Message.Payload;
        }
        finally
        {
            if (healthy) Return(pool, lease);
            else await CloseAsync(lease);
        }
    }

    private async Task<Lease> OpenAsync(TargetEndpoint target, CancellationToken cancellationToken)
    {
        var connection = await Connect(target, cancellationToken);
        return new Lease(connection, connection.Login.PacketSize ?? TdsConstants.DefaultPacketSize);
    }

    private Pool PoolOf(TargetEndpoint target)
    {
        lock (_gate)
        {
            if (!_pools.TryGetValue(target, out var pool)) _pools[target] = pool = new Pool(maxPerTarget);
            return pool;
        }
    }

    private Lease? Take(Pool pool)
    {
        List<Lease> expired = [];
        Lease? found = null;
        lock (_gate)
        {
            while (pool.Idle.TryPop(out var lease))
            {
                if (Environment.TickCount64 - lease.LastUsed > IdleFor.TotalMilliseconds) expired.Add(lease);
                else { found = lease; break; }
            }
        }
        foreach (var lease in expired) _ = CloseAsync(lease);
        return found;
    }

    private void Return(Pool pool, Lease lease)
    {
        lease.LastUsed = Environment.TickCount64;
        lock (_gate)
        {
            if (!_disposed && pool.Idle.Count < maxPerTarget)
            {
                pool.Idle.Push(lease);
                return;
            }
        }
        _ = CloseAsync(lease);
    }

    /// <summary>Closes connections idle too long. Called by the sweeper.</summary>
    public void Prune()
    {
        List<Lease> expired = [];
        lock (_gate)
        {
            foreach (var pool in _pools.Values)
            {
                var kept = pool.Idle.Where(l => Environment.TickCount64 - l.LastUsed <= IdleFor.TotalMilliseconds).Reverse().ToList();
                expired.AddRange(pool.Idle.Except(kept));
                pool.Idle.Clear();
                foreach (var lease in kept) pool.Idle.Push(lease);
            }
        }
        foreach (var lease in expired) _ = CloseAsync(lease);
    }

    private static async Task CloseAsync(Lease lease)
    {
        try { await lease.Connection.DisposeAsync(); } catch { }
    }

    /// <summary>A SQL_BATCH in as many packets as it needs: ALL_HEADERS (no transaction, one outstanding request), then the text.</summary>
    internal static byte[] Frame(string sql, int packetSize)
    {
        byte[] text = Encoding.Unicode.GetBytes(sql);
        var payload = new byte[22 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 22);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 18);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), TdsConstants.HeaderType.TransactionDescriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(18), 1);
        text.CopyTo(payload, 22);
        return PacketWriter.Frame(TdsConstants.PacketType.SqlBatch, payload, packetSize, 0);
    }

    public async ValueTask DisposeAsync()
    {
        List<Lease> all;
        lock (_gate)
        {
            _disposed = true;
            all = _pools.Values.SelectMany(p => p.Idle).ToList();
            foreach (var pool in _pools.Values) pool.Idle.Clear();
        }
        foreach (var lease in all) await CloseAsync(lease);
    }
}
