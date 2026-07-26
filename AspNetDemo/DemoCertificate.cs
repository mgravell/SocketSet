using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SocketSets.AspNet;

/// <summary>
/// The ONE server certificate every TLS leg of the demo uses — Kestrel's own HTTPS (SslStream), the
/// SChannel provider, and the OpenSSL provider alike.
///
/// This exists specifically so the comparison is honest. Certificate choice moves TLS numbers a lot: the
/// key algorithm and size set the cost of the handshake's signature, and a leg quietly running RSA-2048
/// against another running RSA-4096 (or ECDSA P-256) is measuring the certificate, not the transport. One
/// key, generated once per process from the constants below, is handed to whichever leg runs — so the only
/// thing that differs between runs is the thing under test.
///
/// The three consumers want it in different shapes, which is the only reason this class has three
/// properties rather than one: Kestrel and SChannel take an <see cref="X509Certificate2"/>, OpenSSL takes
/// PEM text. They are all the same key.
/// </summary>
internal sealed class DemoCertificate : IDisposable
{
    /// <summary>Shared across every leg — change it here or not at all.</summary>
    public const int KeyBits = 2048;

    private static readonly HashAlgorithmName SignatureHash = HashAlgorithmName.SHA256;

    private bool _disposed;

    private DemoCertificate(X509Certificate2 certificate, string certPem, string keyPem)
    {
        Certificate = certificate;
        CertPem = certPem;
        KeyPem = keyPem;
    }

    /// <summary>For Kestrel's UseHttps and for the SChannel provider. Carries a usable private key.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>For the OpenSSL provider (PEM in, per its constructor).</summary>
    public string CertPem { get; }

    public string KeyPem { get; }

    public static DemoCertificate Create(string host = "localhost")
    {
        using var rsa = RSA.Create(KeyBits);
        var req = new CertificateRequest($"CN={host}", rsa, SignatureHash, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        // Without the serverAuth EKU, SChannel will not select this as a server credential.
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        string certPem = ephemeral.ExportCertificatePem();
        string keyPem = rsa.ExportPkcs8PrivateKeyPem();

        // CreateSelfSigned leaves the private key in memory only, which SChannel cannot see (and which
        // SslStream on Windows is also unhappy with). Round-tripping through a PFX with a persisted key
        // set gives it a key container both can open; Dispose removes the container again.
        string password = Guid.NewGuid().ToString("N");
        var usable = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, password), password, X509KeyStorageFlags.Exportable);

        return new DemoCertificate(usable, certPem, keyPem);
    }

    /// <summary>Describes what every leg is actually presenting, so the fairness claim is visible rather
    /// than assumed.</summary>
    public string Describe() => $"RSA-{KeyBits}/{SignatureHash.Name} self-signed {Certificate.Subject}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (OperatingSystem.IsWindows()) DeletePersistedKey(Certificate);
        Certificate.Dispose();
    }

    /// <summary>Remove the on-disk CNG key container the PFX round-trip had to create — disposing an
    /// <see cref="X509Certificate2"/> does not, so without this every run leaks a key into the user's
    /// crypto store.</summary>
    [SupportedOSPlatform("windows")]
    private static void DeletePersistedKey(X509Certificate2 cert)
    {
        try
        {
            using var key = cert.GetRSAPrivateKey();
            if (key is RSACng cng) cng.Key.Delete();
        }
        catch { /* best effort — a leaked demo key is not worth throwing over */ }
    }
}
