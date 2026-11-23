using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DaffyZoom.Security;

/// <summary>Who can unwrap the master key on Windows.</summary>
internal enum KeyScope
{
    /// <summary>Only the Windows account that created the store. The default.</summary>
    CurrentUser,

    /// <summary>Any account on this machine - what a Windows service running as another account needs.</summary>
    LocalMachine,
}

/// <summary>
/// Encrypts DaffyZoom's secrets at rest with AES-256-GCM under one random master key.
///
/// The master key lives in store.key beside the store. On Windows it is wrapped with DPAPI, so the
/// file is useless off this machine (or, by default, outside this Windows account). Elsewhere there is
/// no DPAPI; the key is written with owner-only permissions (0600), as SSH does for its keys. GCM
/// authenticates as well as encrypts, so a tampered or truncated store fails to open rather than
/// decrypting to garbage.
///
/// Sealed format: version(1) nonce(12) tag(16) ciphertext. The associated data names what the blob
/// is for, so a sealed blob cannot be passed off as a different kind of secret.
/// </summary>
internal sealed class SecretProtector
{
    private const string KeyFileName = "store.key";
    private const byte SealVersion = 1;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    // Key file headers: magic + format. "DZK1" = DPAPI-wrapped, "DZK0" = raw with 0600 permissions.
    private static readonly byte[] DpapiMagic = "DZK1"u8.ToArray();
    private static readonly byte[] RawMagic = "DZK0"u8.ToArray();
    private static readonly byte[] DpapiEntropy = "DaffyZoom.store.v1"u8.ToArray();

    private readonly byte[] _key;

    private SecretProtector(byte[] key) => _key = key;

    public string KeyDescription { get; private init; } = "";

    public static string KeyPath(string directory) => Path.Combine(directory, KeyFileName);

    public static bool KeyExists(string directory) => File.Exists(KeyPath(directory));

    /// <summary>Loads the master key, creating it (and the directory) the first time.</summary>
    public static SecretProtector LoadOrCreate(string directory, KeyScope scope = KeyScope.CurrentUser)
    {
        string path = KeyPath(directory);
        if (File.Exists(path)) return Load(path);

        Directory.CreateDirectory(directory);
        byte[] key = RandomNumberGenerator.GetBytes(KeyBytes);

        byte[] file;
        string description;
        if (OperatingSystem.IsWindows())
        {
            file = [.. DpapiMagic, (byte)scope, .. Protect(key, scope)];
            description = $"DPAPI ({scope})";
        }
        else
        {
            file = [.. RawMagic, .. key];
            description = "owner-only key file (0600)";
        }

        WriteExclusive(path, file);
        return new SecretProtector(key) { KeyDescription = description };
    }

    private static SecretProtector Load(string path)
    {
        byte[] file = File.ReadAllBytes(path);

        if (file.AsSpan().StartsWith(DpapiMagic))
        {
            if (!OperatingSystem.IsWindows())
                throw new CryptographicException($"{path} is protected with Windows DPAPI and cannot be opened on this platform");

            var scope = (KeyScope)file[DpapiMagic.Length];
            try
            {
                byte[] key = Unprotect(file[(DpapiMagic.Length + 1)..], scope);
                return new SecretProtector(key) { KeyDescription = $"DPAPI ({scope})" };
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException(
                    $"{path} could not be unwrapped by this Windows account ({scope} scope). It belongs to another " +
                    "account or machine; run as that account, or point --config-dir at a store created by this one.", ex);
            }
        }

        if (file.AsSpan().StartsWith(RawMagic) && file.Length == RawMagic.Length + KeyBytes)
        {
            WarnIfReadableByOthers(path);
            return new SecretProtector(file[RawMagic.Length..]) { KeyDescription = "owner-only key file (0600)" };
        }

        throw new CryptographicException($"{path} is not a DaffyZoom key file");
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext, string purpose)
    {
        byte[] sealedBytes = new byte[1 + NonceBytes + TagBytes + plaintext.Length];
        var nonce = sealedBytes.AsSpan(1, NonceBytes);
        var tag = sealedBytes.AsSpan(1 + NonceBytes, TagBytes);
        var ciphertext = sealedBytes.AsSpan(1 + NonceBytes + TagBytes);

        sealedBytes[0] = SealVersion;
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));
        return sealedBytes;
    }

    public byte[] Unseal(ReadOnlySpan<byte> sealedBytes, string purpose)
    {
        if (sealedBytes.Length < 1 + NonceBytes + TagBytes || sealedBytes[0] != SealVersion)
            throw new CryptographicException("not a sealed DaffyZoom blob, or from a newer version");

        var nonce = sealedBytes.Slice(1, NonceBytes);
        var tag = sealedBytes.Slice(1 + NonceBytes, TagBytes);
        var ciphertext = sealedBytes[(1 + NonceBytes + TagBytes)..];

        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] key, KeyScope scope) =>
        ProtectedData.Protect(key, DpapiEntropy,
            scope == KeyScope.LocalMachine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] wrapped, KeyScope scope) =>
        ProtectedData.Unprotect(wrapped, DpapiEntropy,
            scope == KeyScope.LocalMachine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);

    /// <summary>Creates the file with owner-only permissions from the start, never widening then narrowing.</summary>
    private static void WriteExclusive(string path, byte[] content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        stream.Write(content);
    }

    private static void WarnIfReadableByOthers(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        if ((mode & others) != 0)
            Log.Warn("store.key_permissions", $"{path} is readable by other users; run: chmod 600 {path}");
    }
}
