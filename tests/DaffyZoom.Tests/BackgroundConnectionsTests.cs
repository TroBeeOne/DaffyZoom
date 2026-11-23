using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DaffyZoom.Cache;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>
/// Tracked mode's own connections, to a stand-in for SQL Server that answers each batch with its own
/// text once let: never more open at once than allowed, identical checks waiting for one share it,
/// and a hit that gives up leaves its check and the connection under it alone.
/// </summary>
public sealed class BackgroundConnectionsTests : IAsyncDisposable
{
    private static readonly TargetEndpoint Target = TargetEndpoint.Parse("Server=127.0.0.1;User Id=svc_reports;Password=x;Database=Sales");

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<string> _batches = new();
    private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TcpClient> _sockets = [];
    private readonly Task _serving;
    private int _opened;

    public BackgroundConnectionsTests()
    {
        _listener.Start();
        _serving = ServeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _answer.TrySetResult();
        _listener.Stop();
        lock (_sockets) foreach (var socket in _sockets) socket.Dispose();
        try { await _serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                var socket = await _listener.AcceptTcpClientAsync();
                lock (_sockets) _sockets.Add(socket);
                _ = AnswerAsync(socket.GetStream());
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    /// <summary>Each batch's text, back as the answer, once <see cref="_answer"/> says so.</summary>
    private async Task AnswerAsync(Stream stream)
    {
        try
        {
            var reader = new TdsMessageReader(stream);
            while (await reader.ReadAsync(CancellationToken.None) is { } received)
            {
                byte[] payload = received.Message.Payload;
                string sql = Encoding.Unicode.GetString(payload, 22, payload.Length - 22);
                _batches.Enqueue(sql);
                await _answer.Task;
                await stream.WriteAsync(PacketWriter.Frame(TdsConstants.PacketType.TabularResult, Encoding.Unicode.GetBytes(sql), 4096, 1));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
    }

    private BackgroundConnections Connections(int max) => new(max)
    {
        Connect = async (target, cancellationToken) =>
        {
            Interlocked.Increment(ref _opened);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, cancellationToken);
            return new UpstreamConnection(client, client.GetStream(), null, new LoginResponse(), [], 1, false, [], null, TimeSpan.Zero);
        },
    };

    private static string Text(byte[] answer) => Encoding.Unicode.GetString(answer);

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task NoMoreConnectionsAreOpenAtOnceThanAllowed()
    {
        await using var connections = Connections(2);
        var queries = Enumerable.Range(1, 6).Select(i => connections.QueryAsync(Target, $"SELECT {i}", CancellationToken.None)).ToList();

        await WaitForAsync(() => _batches.Count == 2, "two queries reached the server");
        await Task.Delay(200);
        Assert.Equal(2, _batches.Count);                 // the other four wait for a connection
        Assert.Equal(2, _opened);

        _answer.SetResult();
        var answers = await Task.WhenAll(queries).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"SELECT {i}"), answers.Select(Text));
        Assert.Equal(2, _opened);                        // and run on the same two, one after another
        Assert.Equal(2, connections.IdleCount);
    }

    [Fact]
    public async Task IdenticalChecksWaitingForAConnectionShareOne()
    {
        await using var connections = Connections(1);
        var busy = connections.QueryAsync(Target, "SELECT 'busy'", CancellationToken.None);
        await WaitForAsync(() => _batches.Count == 1, "the connection is busy");

        var checks = Enumerable.Range(0, 20)
            .Select(_ => connections.QuerySharedAsync(Target, "SELECT 'check'", TimeSpan.FromSeconds(10), CancellationToken.None)).ToList();
        _answer.SetResult();

        Assert.All(await Task.WhenAll(checks).WaitAsync(TimeSpan.FromSeconds(10)), answer => Assert.Equal("SELECT 'check'", Text(answer)));
        await busy;
        Assert.Equal(new[] { "SELECT 'busy'", "SELECT 'check'" }, _batches.ToArray());
        Assert.Equal(1, _opened);
    }

    /// <summary>A check already sent may be answering for a moment before this request came in: it is not joined.</summary>
    [Fact]
    public async Task ACheckAlreadySentIsNotJoined()
    {
        await using var connections = Connections(2);
        var first = connections.QuerySharedAsync(Target, "SELECT 'check'", TimeSpan.FromSeconds(10), CancellationToken.None);
        await WaitForAsync(() => _batches.Count == 1, "the first check was sent");

        var second = connections.QuerySharedAsync(Target, "SELECT 'check'", TimeSpan.FromSeconds(10), CancellationToken.None);
        await WaitForAsync(() => _batches.Count == 2, "the second check was sent too");
        _answer.SetResult();

        Assert.Equal("SELECT 'check'", Text(await first));
        Assert.Equal("SELECT 'check'", Text(await second));
    }

    /// <summary>
    /// A hit's deadline ends its wait, not the check: cancelling the query part-way would cost the
    /// connection under it, and the next check another login.
    /// </summary>
    [Fact]
    public async Task AHitThatGivesUpLeavesTheCheckAndItsConnection()
    {
        await using var connections = Connections(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var check = connections.QuerySharedAsync(Target, "SELECT 'check'", TimeSpan.FromSeconds(10), deadline.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        _answer.SetResult();
        await WaitForAsync(() => connections.IdleCount == 1, "the check finished and its connection was kept");

        Assert.Equal("SELECT 'next'", Text(await connections.QueryAsync(Target, "SELECT 'next'", CancellationToken.None)));
        Assert.Equal(1, _opened);
    }
}
