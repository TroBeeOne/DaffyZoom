using DaffyZoom.Cache;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>
/// How large the cache may be: the smaller of its cap and what its disk can spare, the least useful
/// entries making way - and only ever for something worth more.
/// </summary>
public sealed class CacheSpaceTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly CacheIndex _index = new();

    /// <summary>A disk of <see cref="_capacity"/> bytes holding the cache's files and <see cref="_other"/> bytes of something else.</summary>
    private long _capacity = 10_000, _other;

    private CacheSpace Space(long cap, long? minFree = 100) =>
        new(new CacheOptions { CacheMaxBytes = cap, CacheMinFreeBytes = minFree }, _index, _directory.Path,
            _ => (_capacity - _other - _index.FileBytes, _capacity));

    private CacheEntry Add(long fileBytes, double originalMs)
    {
        var entry = new CacheEntry
        {
            Key = new CacheKey((UInt128)Random.Shared.NextInt64()), Path = Path.Combine(_directory.Path, "nope.dzc"), Length = fileBytes,
            FileBytes = fileBytes, Server = "sql01:1433", Database = "Sales", Epoch = 0, CreatedUtc = DateTime.UtcNow,
            OriginalMs = originalMs, FirstPacketHash = 1, ShapeHash = 1, Shape = "SELECT ?",
        };
        _index.Add(entry);
        return entry;
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheAllowanceIsTheSmallerOfTheCapAndWhatTheDiskCanSpare()
    {
        Assert.Equal(1_000, Space(cap: 1_000).Allowance());
        _other = 9_500;                                         // 500 free, 100 kept free
        Assert.Equal(400, Space(cap: 1_000).Allowance());
        _other = 10_000;
        Assert.Equal(0, Space(cap: 1_000).Allowance());
    }

    [Fact]
    public void WhatIsKeptFreeIsTheLargerOf1GbAnd5PercentUnlessSet()
    {
        var byDefault = new CacheSpace(new CacheOptions(), _index, _directory.Path, _ => null);
        Assert.Equal(1L << 30, byDefault.MinFree(10L << 30));
        Assert.Equal(5L << 30, byDefault.MinFree(100L << 30));
        Assert.Equal(100, Space(cap: 1_000).MinFree(100L << 30));
    }

    [Fact]
    public void ANewAnswerDisplacesOnlyEntriesWorthLessThanItself()
    {
        var cheap = Add(400, originalMs: 100);                 // 0.25 ms saved per byte
        var dear = Add(400, originalMs: 10_000);               // 25
        var space = Space(cap: 1_000);

        Assert.True(space.MakeRoom(400, benefit: 1));
        Assert.Null(_index.Find(cheap.Key));
        Assert.NotNull(_index.Find(dear.Key));
    }

    [Fact]
    public void WhenOnlyEntriesWorthMoreStandInTheWayNothingIsEvicted()
    {
        var cheap = Add(400, originalMs: 100);
        var dear = Add(400, originalMs: 10_000);

        Assert.False(Space(cap: 1_000).MakeRoom(400, benefit: 0.1));
        Assert.NotNull(_index.Find(cheap.Key));
        Assert.NotNull(_index.Find(dear.Key));
        Assert.False(Space(cap: 1_000).MakeRoom(5_000, benefit: 1_000));   // larger than the cache may ever be
        Assert.Equal(800, _index.FileBytes);
    }

    /// <summary>Something else fills the disk: at the next sweep the cache shrinks, the least useful entries first.</summary>
    [Fact]
    public void TheCacheShrinksWhenItsDiskFillsUp()
    {
        var entries = Enumerable.Range(1, 5).Select(i => Add(1_000, originalMs: i * 1_000)).ToList();
        _other = 4_000;                                         // 1,000 free: room to spare
        Assert.Equal(0, Space(cap: 10_000).Trim());

        _other = 4_800;                                         // 200 free: the cache may be 5,100
        Assert.Equal(2, Space(cap: 10_000).Trim());
        Assert.Equal(3_000, _index.FileBytes);
        Assert.Null(_index.Find(entries[0].Key));
        Assert.Null(_index.Find(entries[1].Key));
        Assert.NotNull(_index.Find(entries[4].Key));
    }

    [Fact]
    public void AnUnmeasurableDiskLeavesTheCapInForce()
    {
        var space = new CacheSpace(new CacheOptions { CacheMaxBytes = 1_000 }, _index, _directory.Path, _ => null);
        Assert.Equal(1_000, space.Allowance());
        Assert.False(space.ShortOf(10_000));
    }

    /// <summary>A real disk can be measured: the one this test runs on.</summary>
    [Fact]
    public void TheDiskUnderTheCacheDirectoryIsMeasured()
    {
        var disk = CacheSpace.MeasureDisk(_directory.Path);
        Assert.NotNull(disk);
        Assert.InRange(disk.Value.Available, 1, disk.Value.Total);
    }

    /// <summary>With no room on its disk, an answer is not kept, and nothing is written; with room, it is.</summary>
    [Fact]
    public void TheFillerKeepsAnAnswerOnlyWhenThereIsRoom()
    {
        var options = new CacheOptions { SlowMs = 50, CacheMaxBytes = 1L << 30, CacheMinFreeBytes = 100 };
        var store = CacheStore.Open(Path.Combine(_directory.Path, "cache"));
        _capacity = 10_000;
        _other = 9_950;                                         // 50 free, 100 kept free: no room at all
        var space = new CacheSpace(options, _index, store.Directory, _ => (_capacity - _other - _index.FileBytes, _capacity));
        var filler = new Filler(options, _index, store, new ByteBudget(1 << 20), new CacheCounters(), space);

        Assert.Contains("no room", filler.Keep(Completed()));
        Assert.Equal(0, _index.Count);
        Assert.Empty(Directory.GetFiles(store.Directory));

        _other = 0;
        Assert.Null(filler.Keep(Completed()));
        Assert.Equal(1, _index.Count);
    }

    private static CompletedFill Completed()
    {
        var reply = new Reply();
        reply.ResultSets.Add(new Tds.ResultSet([new ColumnDef("a", SqlColumnType.Int)], [[1], [2]]));
        byte[] answer = reply.Encode(rpc: false);
        var fill = new Fill
        {
            Key = new CacheKey((UInt128)Random.Shared.NextInt64()), Server = "sql01:1433", Database = "Sales", Epoch = 0, FirstPacketHash = 1,
            Classification = new Classification(Refusal.None, null, 0, "SELECT ?", 1), Reset = false, ColumnEncryption = false,
        };
        fill.Append(answer, 1 << 20, new ByteBudget(1 << 20));
        return new CompletedFill(fill, DurationMs: 5_000, DoneStatus: 0, Attention: false);
    }
}
