using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DaffyZoom.Cli;
using DaffyZoom.Proxy;
using DaffyZoom.Repl;
using DaffyZoom.Security;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>
/// Nothing here writes to a certificate store: on Windows every write to the trusted roots raises a
/// dialog that would wait for a click. Reads are harmless, and the writes are proven by hand.
/// </summary>
public class CertificateTrustTests
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    [Fact]
    public void TheGeneratedCertificateMayBeTrusted()
    {
        using var certificate = ServerCertificate.CreateSelfSigned("DEV01");

        Assert.Null(CertificateTrust.Refusal(certificate));
        Assert.Equal(["localhost", "DEV01", "127.0.0.1", "::1"], ServerCertificate.Names(certificate));
    }

    [Fact]
    public void ACertificateAuthorityIsNeverTrusted()
    {
        using var authority = Make("CN=Some Root", certificateAuthority: true,
                                   usage: X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign);

        Assert.Contains("certificate authority", CertificateTrust.Refusal(authority));
    }

    [Fact]
    public void NorOneThatDoesNotSayWhetherItIsOne()
    {
        using var unmarked = Make("CN=DEV01", certificateAuthority: null);

        Assert.Contains("does not say", CertificateTrust.Refusal(unmarked));
    }

    [Fact]
    public void NorOneWhoseKeyMaySignCertificates()
    {
        using var signer = Make("CN=DEV01", certificateAuthority: false,
                                usage: X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign);

        Assert.Contains("sign certificates", CertificateTrust.Refusal(signer));
    }

    [Fact]
    public void NorOneForSomethingOtherThanAServer()
    {
        using var client = Make("CN=DEV01", certificateAuthority: false, purposes: [ClientAuthentication]);

        Assert.Contains("not for server authentication", CertificateTrust.Refusal(client));
    }

    [Fact]
    public void NorOneIssuedBySomeoneElse()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Test Issuing Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=sql01", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        using var leaf = leafRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), [1, 2, 3, 4]);

        Assert.Contains("issued by CN=Test Issuing Root", CertificateTrust.Refusal(leaf));
    }

    [Fact]
    public void OnlyThePublicHalfGoesInTheStore()
    {
        using var certificate = ServerCertificate.CreateSelfSigned("DEV01");
        Assert.True(certificate.HasPrivateKey);

        using var copy = CertificateTrust.PublicCopy(certificate);

        Assert.False(copy.HasPrivateKey);
        Assert.Equal(certificate.Thumbprint, copy.Thumbprint);
        if (OperatingSystem.IsWindows()) Assert.Equal(CertificateTrust.FriendlyName, copy.FriendlyName);
    }

    [Fact]
    public void ANoIsRememberedUntilTheCertificateChanges()
    {
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "daffyzoom.pfx");
        using var certificate = ServerCertificate.CreateSelfSigned("DEV01");
        using var replacement = ServerCertificate.CreateSelfSigned("DEV01");

        Assert.False(CertificateTrust.Declined(certificate, path));
        CertificateTrust.RememberDeclined(certificate, path);

        Assert.True(CertificateTrust.Declined(certificate, path));
        Assert.False(CertificateTrust.Declined(replacement, path));
        Assert.True(File.Exists(Path.Combine(directory.Path, "daffyzoom.trust-declined")));
    }

    [Fact]
    public void TheQuestionIsOnlyForACertificateThatMayBeTrustedAndWasNotDeclined()
    {
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "daffyzoom.pfx");
        using var fresh = ServerCertificate.CreateSelfSigned("DEV01");
        using var authority = Make("CN=Some Root", certificateAuthority: true);

        Assert.Equal(OperatingSystem.IsWindows(), CertificateTrust.ShouldOffer(fresh, path));
        Assert.False(CertificateTrust.ShouldOffer(authority, path));

        CertificateTrust.RememberDeclined(fresh, path);
        Assert.False(CertificateTrust.ShouldOffer(fresh, path));
    }

    [Fact]
    public void ACertFileCertificateIsShownButNeverAdded()
    {
        using var supplied = ServerCertificate.CreateSelfSigned("DEV01");
        var output = new StringWriter();
        var commands = new CertCommands(output, supplied, suppliedPath: "company.pfx");

        Assert.Equal(1, commands.Run(["trust"]));
        Assert.Contains("came from --cert-file", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, commands.Run([]));
        Assert.Contains("(--cert-file)", output.ToString());
        Assert.Contains("localhost, DEV01, 127.0.0.1, ::1", output.ToString());
    }

    [Fact]
    public void TheServedCertificateIsDescribed()
    {
        using var served = ServerCertificate.CreateSelfSigned("DEV01");
        var output = new StringWriter();

        Assert.Equal(0, new CertCommands(output, served).Run(["show"]));

        string text = output.ToString();
        Assert.Contains("(generated by DaffyZoom)", text);
        Assert.Contains($"thumbprint   {served.Thumbprint}", text);
        Assert.Contains(OperatingSystem.IsWindows() ? "trusted      no - " : "trusted      not checked", text);
    }

    [Fact]
    public void AnUnknownCertCommandShowsTheUsage()
    {
        var output = new StringWriter();

        Assert.Equal(1, new CertCommands(output).Run(["renew"]));
        Assert.Contains("daffyzoom cert untrust", output.ToString());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ThePromptAsksOnlySomeoneTyping(bool interactive, int expected)
    {
        using var directory = new TempDirectory();
        var context = new ProxyContext { Users = UserStore.Open(directory.Path), Sessions = new SessionRegistry() };
        var output = new StringWriter();
        int asked = 0;
        string? answer = null;

        var loop = new ReplLoop(context, new LogBuffer(), output, new StringReader("n\n:quit\n"), new CancellationTokenSource(),
                                interactive, question: (input, _) => { asked++; answer = input.ReadLine(); });
        await loop.RunAsync();

        Assert.Equal(expected, asked);
        if (interactive) Assert.Equal("n", answer);     // the answer is read before the first command
        Assert.Contains("stopping", output.ToString());
    }

    private static X509Certificate2 Make(string subject, bool? certificateAuthority,
                                         X509KeyUsageFlags? usage = null, string[]? purposes = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (certificateAuthority is { } authority)
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, critical: true));
        if (usage is { } flags)
            request.CertificateExtensions.Add(new X509KeyUsageExtension(flags, critical: true));

        var oids = new OidCollection();
        foreach (string purpose in purposes ?? [ServerAuthentication]) oids.Add(new Oid(purpose));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
