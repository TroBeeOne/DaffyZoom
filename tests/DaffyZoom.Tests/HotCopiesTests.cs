using DaffyZoom.Cache;

namespace DaffyZoom.Tests;

/// <summary>
/// The answers being hit, decrypted once and shared: one read for many hits at once, let go with
/// their entry or to make room, and never larger than they may be.
/// </summary>
public sealed class HotCopiesTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly CacheStore _store;

    public HotCopiesTests() => _store = CacheStore.Open(_directory.Path);

    public void Dispose() => _directory.Dispose();

    private static byte[] Data(int key, int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i % 13 + key);
        return data;
    }

    private CacheEntry Write(int key, int length)
    {
        var cacheKey = new CacheKey((ulong)key);
        var (path, bytes) = _store.Write(cacheKey, Data(key, length));
        return new CacheEntry
        {
            Key = cacheKey, Path = path, Length = length, FileBytes = bytes, Server = "sql01:1433", Database = "Sales",
            Epoch = 0, CreatedUtc = DateTime.UtcNow, OriginalMs = 10_000, FirstPacketHash = (ulong)key, ShapeHash = 1, Shape = "SELECT ?",
        };
    }

    private static async Task<byte[]> ReadAllAsync(EntryContent content)
    {
        var all = new List<byte>();
        await foreach (var chunk in content.ChunksAsync()) all.AddRange(chunk.ToArray());
        return [.. all];
    }

    [Fact]
    public async Task ManyHitsAtOnceShareOneRead()
    {
        var entry = Write(1, 300_000);
        var hot = new HotCopies(_store, 64 << 20, 8 << 20);

        var contents = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => hot.OpenAsync(entry, CancellationToken.None))));

        Assert.All(contents, content => Assert.Same(contents[0], content));
        Assert.True(contents[0].InMemory);
        Assert.Equal(Data(1, 300_000), await ReadAllAsync(contents[0]));
        Assert.Equal(1, hot.Count);
        Assert.Equal(300_000, hot.Bytes);
    }

    [Fact]
    public async Task ACopyGoesWithItsEntry()
    {
        var entry = Write(2, 1000);
        var hot = new HotCopies(_store, 64 << 20, 8 << 20);
        var first = await hot.OpenAsync(entry, CancellationToken.None);

        hot.Drop(entry);
        Assert.Equal(0, hot.Count);
        Assert.Equal(0, hot.Bytes);
        Assert.NotSame(first, await hot.OpenAsync(entry, CancellationToken.None));
    }

    /// <summary>Room for four: a fifth answer pushes out the one hit longest ago, not the one hit first.</summary>
    [Fact]
    public async Task TheLeastRecentlyUsedCopyMakesRoom()
    {
        var hot = new HotCopies(_store, 2 << 20, 8 << 20);       // a copy may be 512 KB at most
        var entries = Enumerable.Range(10, 5).Select(key => Write(key, 500_000)).ToList();
        var contents = new List<EntryContent>();
        foreach (var entry in entries.Take(4))
        {
            contents.Add(await hot.OpenAsync(entry, CancellationToken.None));
            await Task.Delay(40);
        }
        Assert.Same(contents[0], await hot.OpenAsync(entries[0], CancellationToken.None));     // the first is hit again
        await Task.Delay(40);

        await hot.OpenAsync(entries[4], CancellationToken.None);
        Assert.Equal(4, hot.Count);
        Assert.True(hot.Bytes <= hot.Capacity);
        Assert.Same(contents[0], await hot.OpenAsync(entries[0], CancellationToken.None));
        Assert.NotSame(contents[1], await hot.OpenAsync(entries[1], CancellationToken.None));  // it was the one let go
    }

    [Fact]
    public async Task AnAnswerTooLargeToShareIsReadForEachHit()
    {
        var entry = Write(3, 300_000);
        var hot = new HotCopies(_store, 1 << 20, 8 << 20);       // a quarter: 256 KB

        var first = await hot.OpenAsync(entry, CancellationToken.None);
        Assert.NotSame(first, await hot.OpenAsync(entry, CancellationToken.None));
        Assert.Equal(Data(3, 300_000), await ReadAllAsync(first));
        Assert.Equal(0, hot.Count);
    }

    [Fact]
    public async Task WithNoMemoryForCopiesEachHitReadsItsOwn()
    {
        var entry = Write(4, 1000);
        var hot = new HotCopies(_store, 0, 8 << 20);
        Assert.NotSame(await hot.OpenAsync(entry, CancellationToken.None), await hot.OpenAsync(entry, CancellationToken.None));
        Assert.Equal(0, hot.Count);
    }

    [Fact]
    public async Task ACopyThatWillNotVerifyIsNotKept()
    {
        var entry = Write(5, 200_000);
        byte[] file = File.ReadAllBytes(entry.Path);
        file[^100] ^= 0x01;
        File.WriteAllBytes(entry.Path, file);
        var hot = new HotCopies(_store, 64 << 20, 8 << 20);

        await Assert.ThrowsAsync<CacheCorruptException>(() => hot.OpenAsync(entry, CancellationToken.None));
        Assert.Equal(0, hot.Count);
        Assert.Equal(0, hot.Bytes);
    }

    /// <summary>A hit's deadline ends its wait, not the read: the next hit finds the copy made.</summary>
    [Fact]
    public async Task AHitThatGivesUpLeavesTheCopyBeingMade()
    {
        var entry = Write(6, 3 * CacheStore.ChunkSize);
        var hot = new HotCopies(_store, 64 << 20, 8 << 20);
        using var gaveUp = new CancellationTokenSource();
        gaveUp.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hot.OpenAsync(entry, gaveUp.Token));
        var content = await hot.OpenAsync(entry, CancellationToken.None);
        Assert.Equal(Data(6, 3 * CacheStore.ChunkSize), await ReadAllAsync(content));
        Assert.Equal(1, hot.Count);
    }
}
