using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DaffyZoom.Tds;

/// <summary>
/// Puts DaffyZoom's own certificate among the current Windows user's trusted roots, when asked to.
///
/// Most clients can be told to accept a self-signed certificate - TrustServerCertificate=True - but
/// some cannot, and Power BI's SQL Server connector is the one people meet first: it checks the
/// certificate and has nowhere to say otherwise. An application whose connection string should not
/// change beyond its server is in the same position. For those the certificate has to be trusted by
/// Windows, which is what this does, and only ever on a yes. Taken from DaffyDB, which does the same.
///
/// Four things keep that from being more than it sounds:
/// <list type="bullet">
///   <item>Only the public certificate is added, and only to the current user's store - not the
///   machine's, which would need elevation and would speak for every account.</item>
///   <item>Windows asks again itself, with a security warning that no program can skip, so the
///   answer here is never the only one.</item>
///   <item>The certificate is not a certificate authority: its basic constraints say so and its key
///   usage has no certificate signing. Trusting it vouches for DaffyZoom's own listener on the names
///   in it - localhost, 127.0.0.1 and this machine - and for nothing it could sign.</item>
///   <item>Nothing else is ever added: <see cref="Refusal"/> turns away any certificate that could
///   sign others, or that was issued by someone else (whose authority is what should be trusted).</item>
/// </list>
/// Untrust takes it back out.
/// </summary>
internal static class CertificateTrust
{
    /// <summary>
    /// Set on the copy put in the store, so the store's list shows what it is, and so that
    /// untrust can find every DaffyZoom certificate ever added, not just today's. It differs from
    /// DaffyDB's, so each tool only ever removes its own.
    /// </summary>
    internal const string FriendlyName = "DaffyZoom (self-signed, for local connections)";

    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>Where Windows trusts this certificate from, or null where it does not (and everywhere but Windows).</summary>
    public static StoreLocation? TrustedIn(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.Root, location);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException)
            {
                continue;
            }

            if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0)
                return location;
        }

        return null;
    }

    public static bool IsTrusted(X509Certificate2 certificate) => TrustedIn(certificate) is not null;

    /// <summary>
    /// Why this certificate must not go among the trusted roots, or null when it may. Only a
    /// self-signed certificate for a server qualifies, and only one that says it is not a
    /// certificate authority: a root that could sign would vouch for whatever it signed.
    /// </summary>
    public static string? Refusal(X509Certificate2 certificate)
    {
        if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            return $"it was issued by {certificate.Issuer}, not by itself; trust that issuer instead (it may be trusted already)";

        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (constraints is null)
            return "it does not say whether it is a certificate authority, so Windows could treat it as one";
        if (constraints.CertificateAuthority)
            return "it is a certificate authority, and trusting it would vouch for everything it signs";

        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage is not null && (usage.KeyUsages & (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign)) != 0)
            return "its key may sign certificates";

        var purposes = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (purposes is not null && !purposes.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthentication))
            return "it is not for server authentication";

        return null;
    }

    /// <summary>
    /// The copy that goes in the store: the public half alone, labelled. The store would take the
    /// private key too if it were offered, and nothing that only needs to recognise the
    /// certificate should hold that.
    /// </summary>
    internal static X509Certificate2 PublicCopy(X509Certificate2 certificate)
    {
        var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        if (OperatingSystem.IsWindows()) publicOnly.FriendlyName = FriendlyName;
        return publicOnly;
    }

    /// <summary>
    /// Adds the certificate to the current user's trusted roots. Windows shows its own warning
    /// first; saying no there comes back as a failure, and is reported as that and nothing worse.
    /// </summary>
    public static bool Trust(X509Certificate2 certificate, TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("  Trusting the certificate is only done on Windows. Elsewhere, use TrustServerCertificate=True,");
            output.WriteLine("  or Encrypt=Strict with ServerCertificate= pointed at the .cer file.");
            return false;
        }

        if (TrustedIn(certificate) is { } location)
        {
            output.WriteLine($"  The certificate is already trusted ({Describe(location)}).");
            return true;
        }

        if (Refusal(certificate) is { } reason)
        {
            output.WriteLine($"  Not trusted: {reason}.");
            return false;
        }

        using var publicOnly = PublicCopy(certificate);
        try
        {
            output.WriteLine("  Windows will now ask you to confirm; answer Yes to trust the certificate.");
            output.Flush();
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(publicOnly);
        }
        catch (CryptographicException ex)
        {
            output.WriteLine($"  Not trusted: {ex.Message.Trim()}");
            return false;
        }

        // Windows returns quietly when the warning is declined on some versions, so look rather
        // than assume.
        if (!IsTrusted(certificate))
        {
            output.WriteLine("  Not trusted: Windows did not add it.");
            return false;
        }

        output.WriteLine($"  Trusted for this Windows user ({Describe(StoreLocation.CurrentUser)}).");

        int stale = Superseded(certificate);
        if (stale > 0)
            output.WriteLine($"  {stale} older DaffyZoom certificate(s) are still trusted but no longer used; " +
                             "'daffyzoom cert untrust' removes every one, then trust this one again.");
        return true;
    }

    /// <summary>
    /// Removes every DaffyZoom certificate from the current user's trusted roots - this one and any
    /// left behind by a regenerated one. Windows confirms each removal itself.
    /// </summary>
    public static int Untrust(X509Certificate2? current, TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("  Nothing to do: certificates are only ever trusted on Windows.");
            return 0;
        }

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var ours = store.Certificates
            .Where(c => c.FriendlyName == FriendlyName ||
                        (current is not null && c.Thumbprint == current.Thumbprint))
            .ToList();

        foreach (var certificate in ours)
        {
            try
            {
                store.Remove(certificate);
            }
            catch (CryptographicException ex)
            {
                output.WriteLine($"  Kept {certificate.Subject}: {ex.Message.Trim()}");
            }
        }

        // As with adding, a declined removal can come back as success; count what actually went.
        store.Close();
        store.Open(OpenFlags.ReadOnly);
        var remaining = store.Certificates;
        int removed = ours.Count(c => remaining.Find(X509FindType.FindByThumbprint, c.Thumbprint, validOnly: false).Count == 0);
        foreach (var certificate in ours.Concat(remaining.Cast<X509Certificate2>())) certificate.Dispose();

        output.WriteLine(ours.Count == 0 ? "  No DaffyZoom certificate was trusted for this Windows user."
            : removed == 0 ? "  No DaffyZoom certificate was removed from this Windows user's trusted roots."
            : $"  Removed {removed} DaffyZoom certificate(s) from this Windows user's trusted roots.");

        if (current is not null && TrustedIn(current) == StoreLocation.LocalMachine)
            output.WriteLine("  It is still trusted machine-wide (Local Machine); remove it there with certlm.msc.");

        return removed;
    }

    /// <summary>DaffyZoom certificates in the current user's trusted roots other than this one.</summary>
    public static int Superseded(X509Certificate2 current)
    {
        if (!OperatingSystem.IsWindows()) return 0;

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var all = store.Certificates;
        try
        {
            return all.Count(c => c.FriendlyName == FriendlyName && c.Thumbprint != current.Thumbprint);
        }
        finally
        {
            foreach (var certificate in all) certificate.Dispose();
        }
    }

    /// <summary>
    /// Whether to ask, which is once per certificate: not if Windows already trusts it, not if it
    /// is one Windows should not trust, and not if the answer was no - that is remembered beside
    /// the certificate, until the certificate changes.
    /// </summary>
    public static bool ShouldOffer(X509Certificate2 certificate, string certificatePath) =>
        OperatingSystem.IsWindows() && !Declined(certificate, certificatePath) &&
        Refusal(certificate) is null && !IsTrusted(certificate);

    internal static bool Declined(X509Certificate2 certificate, string certificatePath)
    {
        string declined = DeclinedPath(certificatePath);
        try
        {
            return File.Exists(declined) && File.ReadAllText(declined).Trim() == certificate.Thumbprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void RememberDeclined(X509Certificate2 certificate, string certificatePath)
    {
        try
        {
            File.WriteAllText(DeclinedPath(certificatePath), certificate.Thumbprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Asked again next time, which is the only cost of not being able to remember.
        }
    }

    /// <summary>
    /// The question, asked at the prompt before its first command. It is never asked before the
    /// listener is up: a copy started in a window nobody is looking at would otherwise wait for an
    /// answer with its port closed. And it is only asked where there is a prompt at all - not
    /// under --no-repl, and not with input or output redirected.
    /// </summary>
    public static void Offer(X509Certificate2 certificate, string certificatePath, TextReader input, TextWriter output)
    {
        output.WriteLine("  DaffyZoom's certificate is self-signed. Most clients can be told to accept it");
        output.WriteLine("  (TrustServerCertificate=True), but some cannot - Power BI's SQL Server connector among");
        output.WriteLine("  them - and only work once Windows trusts it. Trusting it covers DaffyZoom on localhost,");
        output.WriteLine("  127.0.0.1 and this machine's name, for your Windows account only. It cannot vouch for");
        output.WriteLine("  anything else.");
        output.WriteLine();
        output.Write("  Trust it now? Windows will ask you to confirm. [y/N] ");
        output.Flush();

        string answer = (input.ReadLine() ?? "").Trim();
        output.WriteLine();

        if (answer.StartsWith("y", StringComparison.OrdinalIgnoreCase))
        {
            Trust(certificate, output);
            output.WriteLine();
            return;
        }

        RememberDeclined(certificate, certificatePath);
        output.WriteLine("  Not trusted, and you will not be asked again. ':cert trust' or 'daffyzoom cert trust' does it later.");
        output.WriteLine();
    }

    public static string Describe(StoreLocation location) => location == StoreLocation.CurrentUser
        ? "Current User > Trusted Root Certification Authorities"
        : "Local Machine > Trusted Root Certification Authorities";

    private static string DeclinedPath(string certificatePath) => Path.ChangeExtension(certificatePath, ".trust-declined");
}
