using System.Security.Cryptography;
using DaffyZoom.Proxy;
using DaffyZoom.Security;

namespace DaffyZoom.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "daffyzoom-test-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void VerifiesOnlyTheRightPassword()
    {
        string hash = PasswordHasher.Hash("correct horse", iterations: 1000);
        Assert.StartsWith("pbkdf2-sha256$1000$", hash);
        Assert.True(PasswordHasher.Verify("correct horse", hash));
        Assert.False(PasswordHasher.Verify("correct horse ", hash));
        Assert.False(PasswordHasher.Verify("", hash));
        Assert.False(PasswordHasher.Verify("correct horse", "garbage"));
    }

    [Fact]
    public void SaltsEveryHash()
    {
        Assert.NotEqual(PasswordHasher.Hash("same", 1000), PasswordHasher.Hash("same", 1000));
    }
}

public class SecretProtectorTests
{
    [Fact]
    public void SealedDataOpensOnlyWithTheSameKeyAndPurpose()
    {
        using var directory = new TempDirectory();
        var protector = SecretProtector.LoadOrCreate(directory.Path);

        byte[] sealedBytes = protector.Seal("the target password"u8, "purpose-a");
        Assert.Equal("the target password"u8.ToArray(), protector.Unseal(sealedBytes, "purpose-a"));

        // Reloading the key from disk opens it too.
        Assert.Equal("the target password"u8.ToArray(), SecretProtector.LoadOrCreate(directory.Path).Unseal(sealedBytes, "purpose-a"));

        Assert.ThrowsAny<CryptographicException>(() => protector.Unseal(sealedBytes, "purpose-b"));

        sealedBytes[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unseal(sealedBytes, "purpose-a"));
    }

    [Fact]
    public void ADifferentKeyCannotOpenIt()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        byte[] sealedBytes = SecretProtector.LoadOrCreate(first.Path).Seal("x"u8, "p");
        Assert.ThrowsAny<CryptographicException>(() => SecretProtector.LoadOrCreate(second.Path).Unseal(sealedBytes, "p"));
    }
}

public class UserStoreTests
{
    private static UserRecord Bob(string password = "bobs-password") => new()
    {
        Login = "Bob",
        PasswordHash = PasswordHasher.Hash(password, 1000),
        Target = "Server=tcp:sql01,1433;User Id=svc_app;Password=target-secret;Encrypt=Mandatory",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public void AuthenticatesCaseInsensitivelyByLoginAndExactlyByPassword()
    {
        using var directory = new TempDirectory();
        var store = UserStore.Open(directory.Path);
        store.Save(Bob());

        Assert.Equal(AuthOutcome.Accepted, store.Authenticate("bob", "bobs-password").Outcome);
        Assert.Equal(AuthOutcome.Accepted, store.Authenticate("BOB", "bobs-password").Outcome);   // cached path
        Assert.Equal(AuthOutcome.BadPassword, store.Authenticate("bob", "BOBS-PASSWORD").Outcome);
        Assert.Equal(AuthOutcome.EmptyPassword, store.Authenticate("bob", "").Outcome);
        Assert.Equal(AuthOutcome.UnknownUser, store.Authenticate("alice", "bobs-password").Outcome);
    }

    [Fact]
    public void DisabledUsersAreRefusedAndPasswordChangesTakeEffect()
    {
        using var directory = new TempDirectory();
        var store = UserStore.Open(directory.Path);
        var bob = Bob();
        store.Save(bob);
        Assert.True(store.Authenticate("bob", "bobs-password").Accepted);

        bob.Enabled = false;
        store.Save(bob);
        Assert.Equal(AuthOutcome.Disabled, store.Authenticate("bob", "bobs-password").Outcome);

        bob.Enabled = true;
        bob.PasswordHash = PasswordHasher.Hash("new-password", 1000);
        store.Save(bob);
        Assert.Equal(AuthOutcome.BadPassword, store.Authenticate("bob", "bobs-password").Outcome);   // the cache does not outlive a change
        Assert.True(store.Authenticate("bob", "new-password").Accepted);
    }

    [Fact]
    public void NothingIsReadableOnDisk()
    {
        using var directory = new TempDirectory();
        var store = UserStore.Open(directory.Path);
        store.Save(Bob());

        string onDisk = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath));
        Assert.DoesNotContain("svc_app", onDisk);
        Assert.DoesNotContain("target-secret", onDisk);
        Assert.DoesNotContain("Bob", onDisk);
    }

    [Fact]
    public void ASecondProcessSeesChangesAndNeitherOverwritesTheOther()
    {
        using var directory = new TempDirectory();
        var serverView = UserStore.Open(directory.Path);
        var cliView = UserStore.Open(directory.Path);

        cliView.Save(Bob());
        Thread.Sleep(1100);   // the running proxy re-checks the file at most once a second
        Assert.True(serverView.Authenticate("bob", "bobs-password").Accepted);

        var alice = Bob();
        alice.Login = "alice";
        serverView.Save(alice);

        Assert.Equal(["alice", "Bob"], cliView.Users.Select(u => u.Login));
    }

    [Fact]
    public void RemovingAUserEndsTheirLogins()
    {
        using var directory = new TempDirectory();
        var store = UserStore.Open(directory.Path);
        store.Save(Bob());
        Assert.True(store.Authenticate("bob", "bobs-password").Accepted);

        Assert.True(store.Remove("BOB"));
        Assert.Equal(AuthOutcome.UnknownUser, store.Authenticate("bob", "bobs-password").Outcome);
        Assert.False(store.Remove("bob"));
    }
}

public class TargetEndpointTests
{
    [Theory]
    [InlineData("Server=tcp:sql01,1444;User Id=u;Password=p", "sql01", 1444)]
    [InlineData("Data Source=sql01;UID=u;PWD=p", "sql01", 1433)]
    [InlineData("server=.;user id=u;password=p", "localhost", 1433)]
    [InlineData("Address=10.0.0.5, 14333 ;User=u;Password=p", "10.0.0.5", 14333)]
    public void ParsesServerForms(string connectionString, string host, int port)
    {
        var endpoint = TargetEndpoint.Parse(connectionString);
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal("u", endpoint.UserName);
        Assert.Equal(TargetEncrypt.Mandatory, endpoint.Encrypt);
    }

    [Theory]
    [InlineData("Server=h;Integrated Security=True")]
    [InlineData("Server=h;User Id=u;Password=p;Authentication=ActiveDirectoryPassword")]
    [InlineData("Server=h\\SQLEXPRESS;User Id=u;Password=p")]
    [InlineData("Server=h;User Id=u;Password=p;Frobnicate=yes")]
    [InlineData("User Id=u;Password=p")]
    [InlineData("Server=h;User Id=u;Password=p;Encrypt=sometimes")]
    public void RefusesWhatARelayCannotHonour(string connectionString)
    {
        Assert.Throws<FormatException>(() => TargetEndpoint.Parse(connectionString));
    }

    [Fact]
    public void RoundTripsThroughItsStoredForm()
    {
        var original = TargetEndpoint.Parse(
            "Server=tcp:sql01,1444;User Id=svc;Password='p;w=d';Initial Catalog=Sales;Encrypt=Strict;" +
            "HostNameInCertificate=sql01.corp;Connect Timeout=30;TrustServerCertificate=true");
        var copy = TargetEndpoint.Parse(original.ToConnectionString());
        Assert.Equal(original, copy);
        Assert.Equal("p;w=d", copy.Password);
        Assert.DoesNotContain("p;w=d", original.ToString());
    }
}
