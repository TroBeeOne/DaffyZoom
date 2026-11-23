using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DaffyZoom.Tds;

/// <summary>
/// Supplies the certificate the TLS listener presents, generating a self-signed one on first run.
///
/// Everything here is the BCL — no OpenSSL, no makecert, no certificate authority. A fresh machine
/// provisions itself the first time the proxy starts, which is the point: nothing to install and
/// nothing to remember.
/// </summary>
internal static class ServerCertificate
{
    private const string DefaultFileName = "daffyzoom.pfx";

    /// <summary>
    /// A fixed password on a file sitting next to the key it protects buys nothing; it is here
    /// because the PFX format expects one and because SChannel needs a loadable key.
    /// </summary>
    private const string DefaultPassword = "daffyzoom";

    /// <summary>
    /// Beside the user store rather than in whatever folder the tool was started from: a tool that
    /// can be run from anywhere should not scatter a private key behind it, and the certificate is
    /// worth keeping across runs so a client only has to be told to trust it once.
    /// </summary>
    private static string DefaultDirectory => Path.Combine(AppPaths.Home, "certs");

    /// <summary>Where the private half lives, resolved the same way whether or not it exists yet.</summary>
    public static string ResolvePath(string? path) =>
        Path.GetFullPath(path ?? Path.Combine(DefaultDirectory, DefaultFileName));

    /// <summary>The public half a client is pointed at, which always sits beside the private one.</summary>
    public static string PublicPath(string? path) => Path.ChangeExtension(ResolvePath(path), ".cer");

    /// <summary>
    /// The generated certificate as it stands, without generating or replacing it: what
    /// 'daffyzoom cert' shows and untrust looks for. Null when there is none yet.
    /// </summary>
    public static X509Certificate2? Existing()
    {
        string path = ResolvePath(null);
        return File.Exists(path)
            ? X509CertificateLoader.LoadPkcs12FromFile(path, DefaultPassword, X509KeyStorageFlags.EphemeralKeySet)
            : null;
    }

    /// <summary>The names a client may use to reach DaffyZoom and still match the certificate.</summary>
    public static IReadOnlyList<string> Names(X509Certificate2 certificate)
    {
        if (certificate.Extensions["2.5.29.17"] is not { } extension)
            return [certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)];

        var names = extension as X509SubjectAlternativeNameExtension ?? new X509SubjectAlternativeNameExtension(extension.RawData);
        return [.. names.EnumerateDnsNames(), .. names.EnumerateIPAddresses().Select(ip => ip.ToString())];
    }

    public static X509Certificate2 Load(string? path, string? password, bool regenerate)
    {
        path = ResolvePath(path);
        password ??= DefaultPassword;

        if (File.Exists(path) && !regenerate)
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);

            // A certificate that quietly expires is a mystery failure a year later; replace it.
            if (existing.NotAfter > DateTime.Now.AddDays(1))
            {
                Console.WriteLine($"[tls]    certificate {path} (expires {existing.NotAfter:yyyy-MM-dd})");
                return existing;
            }

            Console.WriteLine($"[tls]    certificate expired on {existing.NotAfter:yyyy-MM-dd}; generating a new one");
        }

        return Generate(path, password);
    }

    /// <summary>
    /// Writes a new self-signed certificate, plus the public half beside it for a client to trust.
    ///
    /// The SAN always covers localhost and 127.0.0.1 as well as this machine's name, so copying the
    /// folder to another machine still validates for local connections even though the host name
    /// baked in is now someone else's.
    /// </summary>
    private static X509Certificate2 Generate(string path, string password)
    {
        string host = Dns.GetHostName();
        using var generated = CreateSelfSigned(host);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx, password));

        string publicPath = PublicPath(path);
        File.WriteAllBytes(publicPath, generated.Export(X509ContentType.Cert));

        Console.WriteLine($"[tls]    generated a self-signed certificate for {host}");
        Console.WriteLine($"[tls]    private: {path}");
        Console.WriteLine($"[tls]    public : {publicPath}   <- point a client at this to trust it");

        // CreateSelfSigned leaves the key ephemeral, which SChannel cannot use for a server
        // handshake. Round-tripping through PKCS#12 gives it one it can.
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// The certificate itself, which is also what makes it safe to trust (see
    /// <see cref="CertificateTrust"/>): not a certificate authority, no certificate signing, and
    /// for server authentication only.
    /// </summary>
    internal static X509Certificate2 CreateSelfSigned(string host)
    {
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddDnsName(host);
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: true));        // server authentication

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }
}
