using System.Security.Cryptography;
using System.Text;

namespace DaffyZoom.Security;

/// <summary>
/// DaffyZoom login passwords are hashed, not encrypted. They only ever need checking, never
/// recovering - unlike the target password in the same mapping, which has to be sent to SQL Server
/// and so is encrypted instead. Keeping the two apart means a stolen store and key still do not
/// reveal what people type to log in to DaffyZoom.
///
/// PBKDF2-SHA256 at 210,000 iterations (the OWASP figure), 16-byte salt, 32-byte hash, encoded as
/// pbkdf2-sha256$iterations$salt$hash so the cost can be raised later without breaking old hashes.
/// </summary>
internal static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(password, salt, iterations);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        string[] parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out int iterations) || iterations < 1)
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Derive(password, salt, iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
