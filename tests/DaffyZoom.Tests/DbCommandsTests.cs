using DaffyZoom.Cache;
using DaffyZoom.Cli;
using DaffyZoom.Proxy;
using DaffyZoom.Security;

namespace DaffyZoom.Tests;

/// <summary>`daffyzoom db` and `:db`: one policy per database in any spelling, and a word when a policy would cache nothing.</summary>
public sealed class DbCommandsTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly UserStore _store;

    public DbCommandsTests()
    {
        _store = UserStore.Open(_directory.Path);
        UserAdmin.Create(_store, "reports", "reports-password", TargetEndpoint.Parse("Server=sql01;User Id=svc_reports;Password=p;Database=Sales"), "test");
    }

    public void Dispose() => _directory.Dispose();

    private string Run(params string[] args)
    {
        var output = new StringWriter();
        DbCommands.Run(_store, args, output);
        return output.ToString();
    }

    [Fact]
    public void OneDatabaseInAnotherCaseIsTheSamePolicy()
    {
        Run("set", "sql01/Sales", "--mode", "ttl");
        Run("set", "SQL01/SALES", "--max-age", "1h");          // not new, so no --mode needed
        var policy = Assert.Single(_store.Policies);
        Assert.Equal(CacheMode.Ttl, policy.Mode);
        Assert.Equal(60, policy.MaxAgeMinutes);

        Assert.Equal(0, DbCommands.Run(_store, ["remove", "sql01/sales"], TextWriter.Null));
        Assert.Empty(_store.Policies);
    }

    /// <summary>The mistake made in testing 0.1.0: SERVER given as the DaffyZoom login, which no target is on.</summary>
    [Fact]
    public void ASetForAServerNoTargetIsOnSaysSo()
    {
        string said = Run("set", "reports/Sales", "--mode", "ttl");
        Assert.Contains("no DaffyZoom login's target is on reports:1433", said);
        Assert.Contains("'reports' is a DaffyZoom login", said);
        Assert.Contains("The targets are on sql01:1433", said);

        Assert.DoesNotContain("note:", Run("set", "sql01/Sales", "--mode", "ttl"));
        Assert.DoesNotContain("note:", Run("set", "elsewhere/Sales", "--mode", "off"));    // off caches nothing anyway
    }

    /// <summary>The stored policy is the one a running DaffyZoom serves by: a set that fails part-way leaves it as it was.</summary>
    [Fact]
    public void ASetThatFailsLeavesThePolicyAsItWas()
    {
        Run("set", "sql01/Sales", "--mode", "ttl", "--max-age", "4h");
        Assert.Contains("--mode is ttl, tracked or off", Run("set", "sql01/Sales", "--max-age", "1h", "--mode", "sometimes"));
        Assert.Equal(240, Assert.Single(_store.Policies).MaxAgeMinutes);
    }

    [Fact]
    public void ANewDatabaseNeedsAMode() =>
        Assert.Contains("a new database needs --mode", Run("set", "sql01/Sales", "--max-age", "1h"));

    /// <summary>
    /// The same settings again - a setup script run twice, or the database spelled another way, as in
    /// testing 0.1.1 - keep the change time that cached answers are checked against. A real change does not.
    /// </summary>
    [Fact]
    public void TheSameSettingsAgainKeepWhatIsCached()
    {
        Run("set", "sql01/adventureworks", "--mode", "ttl", "--max-age", "1h");
        var before = Assert.Single(_store.Policies).UpdatedUtc;
        Thread.Sleep(20);

        Assert.Contains("(unchanged: what is cached for it is kept)", Run("set", "sql01/AdventureWorks", "--mode", "ttl", "--max-age", "1h"));
        var policy = Assert.Single(_store.Policies);
        Assert.Equal("AdventureWorks", policy.Database);            // the new spelling is kept
        Assert.Equal(before, policy.UpdatedUtc);

        Assert.DoesNotContain("unchanged", Run("set", "sql01/AdventureWorks", "--max-age", "2h"));
        Assert.True(Assert.Single(_store.Policies).UpdatedUtc > before);
    }

    /// <summary>At the prompt, a running cache is flushed by a change, and not by the same settings again.</summary>
    [Fact]
    public async Task AtThePromptTheSameSettingsAgainFlushNothing()
    {
        Run("set", "sql01/Sales", "--mode", "ttl");
        await using var cache = new ZoomCache(new CacheOptions(), new PolicySet(() => _store.Policies, () => 1), Path.Combine(_directory.Path, "cache"));

        DbCommands.Run(_store, ["set", "sql01/SALES", "--mode", "ttl"], TextWriter.Null, cache);
        Assert.Equal(0, cache.Index.Epoch("sql01:1433", "Sales"));
        DbCommands.Run(_store, ["set", "sql01/Sales", "--max-age", "1h"], TextWriter.Null, cache);
        Assert.Equal(1, cache.Index.Epoch("sql01:1433", "Sales"));
    }

    /// <summary>Every property of a policy but its name and change time is a setting, and SameSettings must compare it.</summary>
    [Fact]
    public void EverySettingCountsAsAChange()
    {
        var policy = new DatabasePolicy { Server = "sql01:1433", Database = "Sales", Mode = CacheMode.Ttl };
        string[] notSettings = [nameof(DatabasePolicy.Server), nameof(DatabasePolicy.Database), nameof(DatabasePolicy.UpdatedUtc)];
        foreach (var property in typeof(DatabasePolicy).GetProperties().Where(p => p.CanWrite && !notSettings.Contains(p.Name)))
        {
            var changed = policy.Copy();
            object value = property.PropertyType switch
            {
                var t when t == typeof(int) => (int)property.GetValue(policy)! + 1,
                var t when t == typeof(string) => "changed",
                var t when t == typeof(CacheMode) => CacheMode.Tracked,
                var t when t == typeof(List<string>) => new List<string> { "02:00" },
                var t => throw new InvalidOperationException($"{property.Name} is a {t.Name}, which this test does not know how to change"),
            };
            property.SetValue(changed, value);
            Assert.False(policy.SameSettings(changed), $"{property.Name} is not compared by SameSettings");
        }

        var respelled = policy.Copy();
        respelled.Database = "SALES";
        respelled.UpdatedUtc = DateTime.UtcNow;
        Assert.True(policy.SameSettings(respelled));
    }
}
