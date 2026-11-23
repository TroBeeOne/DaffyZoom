using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DaffyZoom.Proxy;

namespace DaffyZoom.Security;

/// <summary>
/// One user mapping: who may log in to DaffyZoom, and where their sessions go. It is one entity on
/// purpose - Bob's DaffyZoom password and Bob's target connection string are created, changed and
/// removed together.
/// </summary>
internal sealed class UserRecord
{
    public string Login { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// May run DaffyZoom's admin commands in T-SQL (daffyzoom.sessions, daffyzoom.add_user, ...). The
    /// first admin is made on the host - daffyzoom user admin NAME on - so a store from before admins
    /// existed has none, and those commands are refused until someone decides otherwise.
    /// </summary>
    public bool Admin { get; set; }

    /// <summary>PBKDF2 of the DaffyZoom password. The password itself is never stored.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>The target connection string, target password included. Readable only inside the sealed store.</summary>
    public string Target { get; set; } = "";

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastValidatedUtc { get; set; }

    /// <summary>What the target said about itself when last validated, e.g. "SQL Server 16.0.4295".</summary>
    public string? LastValidation { get; set; }

    [JsonIgnore]
    public TargetEndpoint Endpoint => TargetEndpoint.Parse(Target);
}

internal sealed class StoreDocument
{
    public int Version { get; set; } = 1;
    public List<UserRecord> Users { get; set; } = [];

    /// <summary>Other settings that belong with the users.</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each database's cache policy. A database with none is not cached.</summary>
    public List<Cache.DatabasePolicy> Databases { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoreDocument))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

internal enum AuthOutcome { Accepted, UnknownUser, Disabled, BadPassword, EmptyPassword }

internal readonly record struct AuthResult(AuthOutcome Outcome, UserRecord? User)
{
    public bool Accepted => Outcome == AuthOutcome.Accepted;
}

/// <summary>
/// The user mappings, encrypted at rest as a single sealed blob.
///
/// Sealing the whole file, rather than just the passwords in it, means nothing about the mappings -
/// not even who has one - can be read off disk. Inside it, DaffyZoom passwords are additionally
/// hashed, because nothing ever needs them back.
///
/// Several processes can use one store: `daffyzoom user add` in one terminal while the proxy runs in
/// another. Writes take an exclusive lock file and re-read the store before changing it, and a
/// running proxy notices a newer file on the next login, so a user added from the command line can
/// log in straight away.
/// </summary>
internal sealed class UserStore
{
    private const string FileName = "users.dat";
    private const string LockName = "users.lock";
    private const string Purpose = "DaffyZoom.users.v1";
    private static readonly byte[] Magic = "DZS1"u8.ToArray();

    /// <summary>
    /// How long a successful password check is remembered. PBKDF2 is deliberately slow, and a client
    /// pool opening a hundred connections at once would otherwise spend seconds proving the same
    /// password a hundred times. Failures are never cached, so guessing stays slow.
    /// </summary>
    private static readonly TimeSpan VerifiedLifetime = TimeSpan.FromMinutes(10);

    private readonly string _directory;
    private readonly SecretProtector _protector;
    private readonly object _gate = new();
    private readonly byte[] _cacheKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, (byte[] Mac, string Hash, DateTime Expires)> _verified =
        new(StringComparer.OrdinalIgnoreCase);

    private StoreDocument _document = new();
    private DateTime _loadedWriteTimeUtc;
    private long _lastFreshnessCheck;
    private long _version;

    private UserStore(string directory, SecretProtector protector)
    {
        _directory = directory;
        _protector = protector;
    }

    public string FilePath => Path.Combine(_directory, FileName);

    public string KeyDescription => _protector.KeyDescription;

    public static UserStore Open(string directory, KeyScope scope = KeyScope.CurrentUser)
    {
        var store = new UserStore(directory, SecretProtector.LoadOrCreate(directory, scope));
        store.Reload();
        return store;
    }

    public IReadOnlyList<UserRecord> Users
    {
        get
        {
            ReloadIfChanged(force: true);
            lock (_gate) return _document.Users.OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public UserRecord? Find(string login)
    {
        ReloadIfChanged(force: true);
        lock (_gate) return _document.Users.FirstOrDefault(u => SameLogin(u.Login, login));
    }

    public string? GetSetting(string key)
    {
        ReloadIfChanged(force: true);
        lock (_gate) return _document.Settings.GetValueOrDefault(key);
    }

    public void SetSetting(string key, string? value) => Mutate(document =>
    {
        if (value is null) document.Settings.Remove(key);
        else document.Settings[key] = value;
    });

    /// <summary>Goes up whenever the store is reloaded or changed, so a cached copy of its policies knows to refresh.</summary>
    public long Version
    {
        get
        {
            ReloadIfChanged(force: false);
            return Interlocked.Read(ref _version);
        }
    }

    public IReadOnlyList<Cache.DatabasePolicy> Policies
    {
        get
        {
            ReloadIfChanged(force: false);
            lock (_gate) return _document.Databases.ToList();
        }
    }

    /// <summary>
    /// Adds or replaces a database's policy, matched by server and database without regard to case.
    /// Its change time is now - which ends what was cached under it - unless <paramref name="keepChangeTime"/>,
    /// for a save that changes no setting.
    /// </summary>
    public void SavePolicy(Cache.DatabasePolicy policy, bool keepChangeTime = false)
    {
        if (!keepChangeTime) policy.UpdatedUtc = DateTime.UtcNow;
        Mutate(document =>
        {
            document.Databases.RemoveAll(p => SamePolicy(p, policy.Server, policy.Database));
            document.Databases.Add(policy);
        });
    }

    public bool RemovePolicy(string server, string database)
    {
        bool removed = false;
        Mutate(document => removed = document.Databases.RemoveAll(p => SamePolicy(p, server, database)) > 0);
        return removed;
    }

    private static bool SamePolicy(Cache.DatabasePolicy policy, string server, string database) =>
        string.Equals(policy.Server, server, StringComparison.OrdinalIgnoreCase) && Cache.DatabasePolicy.SameDatabase(policy.Database, database);

    /// <summary>Adds or replaces a user, matched by login name without regard to case.</summary>
    public void Save(UserRecord record)
    {
        ValidateLogin(record.Login);
        Mutate(document =>
        {
            document.Users.RemoveAll(u => SameLogin(u.Login, record.Login));
            document.Users.Add(record);
        });
        _verified.TryRemove(record.Login, out _);
    }

    public bool Remove(string login)
    {
        bool removed = false;
        Mutate(document => removed = document.Users.RemoveAll(u => SameLogin(u.Login, login)) > 0);
        _verified.TryRemove(login, out _);
        return removed;
    }

    /// <summary>
    /// Checks a DaffyZoom login. The same answer is given to the client for every failure; the
    /// outcome is for DaffyZoom's own log.
    /// </summary>
    public AuthResult Authenticate(string login, string password)
    {
        ReloadIfChanged(force: false);

        UserRecord? user;
        lock (_gate) user = _document.Users.FirstOrDefault(u => SameLogin(u.Login, login));

        if (user is null)
        {
            // Spend the same time as a real check, so response time does not reveal which names exist.
            PasswordHasher.Verify(password, DecoyHash.Value);
            return new AuthResult(AuthOutcome.UnknownUser, null);
        }

        if (string.IsNullOrEmpty(password)) return new AuthResult(AuthOutcome.EmptyPassword, user);

        bool ok = IsRememberedAsVerified(user, password) || VerifyAndRemember(user, password);
        if (!ok) return new AuthResult(AuthOutcome.BadPassword, user);

        return user.Enabled ? new AuthResult(AuthOutcome.Accepted, user) : new AuthResult(AuthOutcome.Disabled, user);
    }

    public static void ValidateLogin(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) throw new ArgumentException("a login name is required");
        if (login != login.Trim()) throw new ArgumentException("a login name cannot start or end with a space");
        if (login.Length > 128) throw new ArgumentException("a login name can be at most 128 characters");
    }

    private static readonly Lazy<string> DecoyHash = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString()));

    private static bool SameLogin(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool IsRememberedAsVerified(UserRecord user, string password) =>
        _verified.TryGetValue(user.Login, out var entry)
        && entry.Expires > DateTime.UtcNow
        && entry.Hash == user.PasswordHash
        && CryptographicOperations.FixedTimeEquals(entry.Mac, Mac(user.Login, password));

    private bool VerifyAndRemember(UserRecord user, string password)
    {
        if (!PasswordHasher.Verify(password, user.PasswordHash)) return false;
        _verified[user.Login] = (Mac(user.Login, password), user.PasswordHash, DateTime.UtcNow + VerifiedLifetime);
        return true;
    }

    /// <summary>A keyed digest, so the cache never holds anything a memory dump could turn back into a password.</summary>
    private byte[] Mac(string login, string password) =>
        HMACSHA256.HashData(_cacheKey, Encoding.UTF8.GetBytes(login.ToLowerInvariant() + "\0" + password));

    private void ReloadIfChanged(bool force)
    {
        // A login storm should not stat the file thousands of times a second; once a second is plenty.
        long now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastFreshnessCheck) < 1000) return;
        Interlocked.Exchange(ref _lastFreshnessCheck, now);

        var writeTime = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        if (writeTime != _loadedWriteTimeUtc) Reload();
    }

    private void Reload()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                _document = new StoreDocument();
                Interlocked.Increment(ref _version);
                _loadedWriteTimeUtc = DateTime.MinValue;
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(FilePath);
            _document = Read();
            Interlocked.Increment(ref _version);
            _loadedWriteTimeUtc = writeTime;
        }
    }

    private StoreDocument Read()
    {
        byte[] file = ReadShared(FilePath);
        if (!file.AsSpan().StartsWith(Magic))
            throw new CryptographicException($"{FilePath} is not a DaffyZoom user store");

        byte[] json = _protector.Unseal(file.AsSpan(Magic.Length), Purpose);
        try
        {
            return JsonSerializer.Deserialize(json, StoreJsonContext.Default.StoreDocument) ?? new StoreDocument();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <summary>Read-modify-write under an exclusive lock, so two processes never overwrite each other.</summary>
    private void Mutate(Action<StoreDocument> change)
    {
        using var fileLock = AcquireLock();
        lock (_gate)
        {
            var document = File.Exists(FilePath) ? Read() : new StoreDocument();
            change(document);

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, StoreJsonContext.Default.StoreDocument);
            byte[] sealedBytes = [.. Magic, .. _protector.Seal(json, Purpose)];
            CryptographicOperations.ZeroMemory(json);

            // Write beside, then rename over: a crash mid-write leaves the old store, never half a new one.
            string temporary = FilePath + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(sealedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);

            _document = document;
            Interlocked.Increment(ref _version);
            _loadedWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
        }
    }

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, LockName);
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Reads while another process may be renaming a new version into place.</summary>
    private static byte[] ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }
}
