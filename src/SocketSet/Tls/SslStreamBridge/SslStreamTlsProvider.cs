#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SocketSets.Tls.SslStreamBridge;

/// <summary>
/// <see cref="TlsProvider"/> backed by <see cref="SslStream"/> through the push↔pull bridge in
/// <see cref="SslStreamTlsFilter"/>. A MEASUREMENT REFERENCE, not a production provider: it exists so the
/// TLS engine A/B (bench/run-tls-engine-ab.sh, SmokeTest <c>--tls-sslstream</c>) can price what hosting
/// the ecosystem's standard TLS shape inside this transport costs — the staging copies, the thread-pool
/// hops, and the loop thread blocking until the stream machinery goes quiescent. See the filter's remarks
/// for exactly what is charged where. Prior art for the expected ordering: Kestrel's own union A/B
/// (dotnet/aspnetcore#69565) had the buffer-session engine ~8% ABOVE its SslStream path.
///
/// Validation parity: the same ValidateRemote policy as the other three engines (chain + hostname,
/// pin-exactly-this-cert test mode), here enforced INSIDE the handshake as the
/// <see cref="RemoteCertificateValidationCallback"/> — with this engine a rejection faults the handshake
/// task, so fail-closed comes built in. The announce/verify split (SNI vs checked name) matches too.
///
/// Knowingly not supported: kTLS (no seam), and nothing is done to tame SslStream's own buffer pooling or
/// session caching — the point is to measure the stock shape, not to tune it.
/// </summary>
public sealed class SslStreamTlsProvider : TlsProvider, IDisposable
{
    public override string Name => "sslstream";

    private readonly X509Certificate2? _serverCert;
    private readonly SslStreamCertificateContext? _serverCertContext;
    private readonly X509Certificate2? _trust;
    private readonly bool _verifyServer;
    private readonly bool _ownsCerts;
    private readonly SslProtocols _protocols;
    private readonly X509RevocationMode _revocationMode;
    private bool _disposed;

    /// <param name="serverCertificate">Certificate (WITH private key) to present to inbound connections, or
    /// null for a client-only provider. On Windows SslStream IS SChannel, so the key must be one SChannel
    /// can open; see <see cref="CreateSelfSignedLoopback"/>.</param>
    /// <param name="trustCertificate">If set, the ONLY root the client trusts; if null, system stores.</param>
    /// <param name="verifyServer">Whether the client verifies the server certificate + hostname. False is
    /// the man-in-the-middle footgun and exists only for bring-up.</param>
    /// <param name="minProtocol">Lowest TLS version to negotiate; defaults to TLS 1.3 like the others.</param>
    /// <param name="revocationMode">Revocation policy for the chain build; NoCheck matches the others.</param>
    public SslStreamTlsProvider(
        X509Certificate2? serverCertificate = null,
        X509Certificate2? trustCertificate = null,
        bool verifyServer = true,
        TlsProtocol minProtocol = TlsProtocol.Tls13,
        X509RevocationMode revocationMode = X509RevocationMode.NoCheck)
        : this(serverCertificate, trustCertificate, verifyServer, minProtocol, ownsCerts: false, revocationMode)
    {
    }

    private SslStreamTlsProvider(
        X509Certificate2? serverCertificate, X509Certificate2? trustCertificate, bool verifyServer,
        TlsProtocol minProtocol, bool ownsCerts, X509RevocationMode revocationMode = X509RevocationMode.NoCheck)
    {
        _serverCert = serverCertificate;
        _trust = trustCertificate;
        _verifyServer = verifyServer;
        _ownsCerts = ownsCerts;
        _revocationMode = revocationMode;
        _protocols = minProtocol == TlsProtocol.Tls13 ? SslProtocols.Tls13 : SslProtocols.Tls12 | SslProtocols.Tls13;
        if (serverCertificate is not null)
        {
            _serverCertContext = SslStreamCertificateContext.Create(serverCertificate, additionalCertificates: null);
        }
    }

    public override TlsFilter CreateClientFilter(TlsClientOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Same contract as the other providers: TargetHost required, AnyHost ("*") is the loud opt-out,
        // announce (SNI) and verify are independent.
        string? host = options.TargetHost;
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException(
                "TargetHost is required. Pass the name being dialled so the certificate can be verified "
                + $"against it, or \"{TlsClientAuthenticateContext.AnyHost}\" to state explicitly that no "
                + "name check is wanted.", nameof(options));

        bool anyHost = host == TlsClientAuthenticateContext.AnyHost;
        string? sniName = TlsClientAuthenticateContext.ResolveSni(host, options.ServerNameIndication);
        string? verifyName = anyHost ? null : host;

        // The rejection reason crosses from the validation callback (inside the handshake) to the filter's
        // FaultReason — without this, every certificate rejection reads "Authentication failed, see inner
        // exception", which is exactly the silent-failure property FaultReason exists to prevent.
        var rejectReason = new string?[1];
        var clientOptions = new SslClientAuthenticationOptions
        {
            TargetHost = sniName ?? string.Empty,
            EnabledSslProtocols = _protocols,
            ApplicationProtocols = ToAlpn(options.AlpnProtocols),
            // OUR policy, same as the other engines, but run inside the handshake: SslStream's own name
            // check is superseded (we check verifyName, which can differ from the announced SNI), and the
            // chain is OUR build (CustomRootTrust when pinned). Returning false faults the handshake task.
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                bool ok = ValidateRemote(certificate as X509Certificate2
                    ?? (certificate is null ? null : new X509Certificate2(certificate)), verifyName, out string reason);
                if (!ok) rejectReason[0] = reason;
                return ok;
            },
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck, // revocation is in OUR chain build
        };
        return new SslStreamTlsFilter(this, client: true, clientOptions, serverOptions: null, rejectReason);
    }

    public override TlsFilter CreateServerFilter(TlsServerOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_serverCertContext is null)
            throw new InvalidOperationException("This provider has no server certificate; it is client-only.");
        var serverOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = _serverCertContext,
            EnabledSslProtocols = _protocols,
            ApplicationProtocols = ToAlpn(options.AlpnProtocols),
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
        return new SslStreamTlsFilter(this, client: false, clientOptions: null, serverOptions, rejectReason: null);
    }

    private static List<SslApplicationProtocol>? ToAlpn(IReadOnlyList<string>? protocols)
    {
        if (protocols is null || protocols.Count == 0) return null;
        var list = new List<SslApplicationProtocol>(protocols.Count);
        foreach (var p in protocols) list.Add(new SslApplicationProtocol(p));
        return list;
    }

    /// <summary>
    /// SECURITY-CRITICAL. Verbatim the SChannel/tls-session policy (one policy, four engines): chain to a
    /// trusted root (exactly the pinned certificate when one is set), and actually be issued for the name
    /// asked for. A null <paramref name="host"/> is the resolved "*" opt-out, not an unset field.
    /// </summary>
    internal bool ValidateRemote(X509Certificate2? remote, string? host, out string reason)
    {
        reason = "";
        if (!_verifyServer) return true; // loud, opt-in bring-up escape hatch

        if (remote is null) { reason = "server presented no certificate"; return false; }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = _revocationMode;
        if (_trust is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_trust);
        }

        if (!chain.Build(remote))
        {
            string detail = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
            reason = $"certificate chain rejected: {(detail.Length == 0 ? "unknown reason" : detail)}";
            return false;
        }

        if (host is not null && !remote.MatchesHostname(host))
        {
            reason = $"certificate is not valid for host '{host}'";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsCerts)
        {
            if (OperatingSystem.IsWindows()) DeletePersistedKey(_serverCert);
            _serverCert?.Dispose();
            _trust?.Dispose();
        }
    }

    /// <summary>
    /// Convenience for tests: throwaway self-signed certificate for <paramref name="host"/>, server
    /// configured with it, client trusting exactly it with full verification. Mirrors the other providers'
    /// helpers; the Windows PFX round-trip exists because SslStream's engine there IS SChannel, with the
    /// same blindness to an ephemeral in-memory key.
    /// </summary>
    public static SslStreamTlsProvider CreateSelfSignedLoopback(string host = "localhost",
        TlsProtocol minProtocol = TlsProtocol.Tls13)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        string password = Guid.NewGuid().ToString("N");
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx, password);
        var serverCert = X509CertificateLoader.LoadPkcs12(
            pfx, password,
            OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet
                : X509KeyStorageFlags.Exportable);
        var trust = X509CertificateLoader.LoadCertificate(ephemeral.Export(X509ContentType.Cert));

        try
        {
            return new SslStreamTlsProvider(serverCert, trust, verifyServer: true, minProtocol, ownsCerts: true);
        }
        catch
        {
            if (OperatingSystem.IsWindows()) DeletePersistedKey(serverCert);
            serverCert.Dispose();
            trust.Dispose();
            throw;
        }
    }

    private static void DeletePersistedKey(X509Certificate2? cert)
    {
        if (cert is null || !OperatingSystem.IsWindows()) return;
        try
        {
            using var key = cert.GetRSAPrivateKey();
            if (key is RSACng cng) cng.Key.Delete();
        }
        catch { /* best effort: a leaked test key is not worth throwing from Dispose over */ }
    }
}
#endif
