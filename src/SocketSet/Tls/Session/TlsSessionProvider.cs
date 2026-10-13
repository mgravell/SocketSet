#if NET11_0_OR_GREATER
#pragma warning disable SYSLIB5007 // TlsContext/TlsBufferSession are [Experimental] in .NET 11 — the whole point of this provider
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SocketSets.Tls.Session;

/// <summary>
/// <see cref="TlsProvider"/> backed by the in-box .NET 11 low-level TLS engine
/// (<see cref="System.Net.Security.TlsContext"/> + <see cref="TlsBufferSession"/>, experimental
/// SYSLIB5007) — real TLS with NO native interop of our own and NO <c>SslStream</c>: the runtime's
/// platform PAL does the crypto, and the byte movement stays in SocketSet's push loop via
/// <see cref="TlsSessionFilter"/>. net11.0 only; on the other TFMs this type does not exist.
///
/// WHY, when OpenSSL and SChannel providers already exist: those are hand-rolled per platform; this one is
/// the portable in-box alternative — most interesting for the MANAGED backend, whose whole point is to run
/// where nothing platform-specific does — and an A/B reference for perf and behaviour against the two
/// hand-rolled engines (SmokeTest's <c>--tls-session</c> vs <c>--tls-ssl</c>/<c>--tls-schannel</c>).
///
/// Validation parity: like <see cref="SChannel.SChannelTlsProvider"/>, certificate trust is decided HERE in
/// managed code (<see cref="ValidateRemote"/> — chain + hostname, with the same pin-exactly-this-cert test
/// mode), not by the engine: the engine is configured to never reject on its own, and the filter enforces
/// our verdict whether or not the engine pauses for external validation (fail-closed; see the filter).
///
/// Knowingly NOT supported in v1, loudly: kTLS (<see cref="TlsProvider.SupportsKernelOffload"/> stays
/// false — the session API has no offload seam), TLS 1.2 renegotiation and post-handshake client auth
/// (the filter faults), and client-side session resumption across connections (the client
/// <see cref="System.Net.Security.TlsContext"/> is per-connection because the target host rides in it;
/// the server context IS shared, which is the half that matters for the server-heavy rigs).
/// </summary>
public sealed class TlsSessionProvider : TlsProvider, IDisposable
{
    public override string Name => "tls-session";

    private readonly X509Certificate2? _serverCert;
    private readonly SslStreamCertificateContext? _serverCertContext;
    private readonly X509Certificate2? _trust;
    private readonly bool _verifyServer;
    private readonly bool _ownsCerts; // set by CreateSelfSignedLoopback: dispose + delete the key on exit
    private readonly SslProtocols _protocols;
    /// <summary>Revocation policy for the chain build in <see cref="ValidateRemote"/>. Defaults to NoCheck,
    /// matching the other providers; see SChannelTlsProvider for why that default is kept despite being the
    /// wrong answer for a client dialling a real off-box service (changing it silently adds a network
    /// dependency to every handshake).</summary>
    private readonly X509RevocationMode _revocationMode;
    private bool _disposed;

    // Server contexts, one per distinct ALPN list (the list rides in the context's options). In practice a
    // provider sees one list for its lifetime; contexts stay alive until the provider is disposed because a
    // live session may still hold one.
    private readonly object _serverLock = new();
    private readonly List<(IReadOnlyList<string>? Alpn, TlsContext Context)> _serverContexts = [];

    /// <param name="serverCertificate">Certificate (WITH private key) to present to inbound connections, or
    /// null for a client-only provider. On Windows the key must be one SChannel can open — the engine's PAL
    /// there is SChannel, so an ephemeral in-memory key fails exactly as it does for
    /// <see cref="SChannel.SChannelTlsProvider"/>; see <see cref="CreateSelfSignedLoopback"/>.</param>
    /// <param name="trustCertificate">If set, the ONLY root the client trusts (e.g. a self-signed test
    /// certificate); if null, the client uses the system trust stores.</param>
    /// <param name="verifyServer">Whether the client verifies the server certificate + hostname. True is the
    /// safe default; false is the man-in-the-middle footgun and exists only for bring-up.</param>
    /// <param name="minProtocol">Lowest TLS version to negotiate. Defaults to
    /// <see cref="TlsProtocol.Tls13"/>, matching the other providers.</param>
    /// <param name="revocationMode">Revocation policy for the chain build; see the field remarks.</param>
    public TlsSessionProvider(
        X509Certificate2? serverCertificate = null,
        X509Certificate2? trustCertificate = null,
        bool verifyServer = true,
        TlsProtocol minProtocol = TlsProtocol.Tls13,
        X509RevocationMode revocationMode = X509RevocationMode.NoCheck)
        : this(serverCertificate, trustCertificate, verifyServer, minProtocol, ownsCerts: false, revocationMode)
    {
    }

    private TlsSessionProvider(
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
            // Built once: it is the expensive part (chain assembly), it is immutable, and it is what every
            // server context shares.
            _serverCertContext = SslStreamCertificateContext.Create(serverCertificate, additionalCertificates: null);
        }
    }

    public override TlsFilter CreateClientFilter(TlsClientOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Same contract as the SChannel provider: TargetHost is required, AnyHost ("*") is the loud opt-out,
        // and ANNOUNCE (SNI) and VERIFY are independent — the one shared rule keeps the providers from
        // drifting. The verify half is ours entirely (ValidateRemote); the announce half is handed to the
        // engine via TargetHost, with name-checking disabled there (see the accept-all callback below).
        string? host = options.TargetHost;
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException(
                "TargetHost is required. Pass the name being dialled so the certificate can be verified "
                + $"against it, or \"{TlsClientAuthenticateContext.AnyHost}\" to state explicitly that no "
                + "name check is wanted.", nameof(options));

        bool anyHost = host == TlsClientAuthenticateContext.AnyHost;
        string? sniName = TlsClientAuthenticateContext.ResolveSni(host, options.ServerNameIndication);
        string? verifyName = anyHost ? null : host;        // null == do not check the name

        var clientOptions = new SslClientAuthenticationOptions
        {
            // Empty = announce nothing; the engine also suppresses SNI for an IP literal itself, but
            // ResolveSni has already made that decision (plus the "*"/override cases) uniformly.
            TargetHost = sniName ?? string.Empty,
            EnabledSslProtocols = _protocols,
            ApplicationProtocols = ToAlpn(options.AlpnProtocols),
            // Validation is OURS (ValidateRemote via the filter), the same split as the SChannel provider's
            // ISC_REQ_MANUAL_CRED_VALIDATION: the engine must never reject (or accept) on its own, whether
            // it pauses for external validation or runs this callback internally. The filter enforces the
            // real policy fail-closed in both cases.
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck, // revocation is in OUR chain build
        };

        // Per-connection context because TargetHost rides in it. That forfeits cross-connection session
        // resumption on the client — acceptable for v1, noted in the class remarks.
        var context = TlsContext.CreateClient(clientOptions);
        try
        {
            return new TlsSessionFilter(this, client: true, context, ownsContext: true, verifyName);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    public override TlsFilter CreateServerFilter(TlsServerOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_serverCertContext is null)
            throw new InvalidOperationException("This provider has no server certificate; it is client-only.");
        return new TlsSessionFilter(this, client: false, GetServerContext(options.AlpnProtocols), ownsContext: false, verifyName: null);
    }

    private TlsContext GetServerContext(IReadOnlyList<string>? alpn)
    {
        lock (_serverLock)
        {
            foreach (var (existing, context) in _serverContexts)
            {
                if (ReferenceEquals(existing, alpn)
                    || (existing is not null && alpn is not null && existing.SequenceEqual(alpn))
                    || (existing is null && alpn is null))
                {
                    return context;
                }
            }

            var created = TlsContext.CreateServer(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = _serverCertContext,
                EnabledSslProtocols = _protocols,
                ApplicationProtocols = ToAlpn(alpn),
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            _serverContexts.Add((alpn, created));
            return created;
        }
    }

    private static List<SslApplicationProtocol>? ToAlpn(IReadOnlyList<string>? protocols)
    {
        if (protocols is null || protocols.Count == 0) return null;
        var list = new List<SslApplicationProtocol>(protocols.Count);
        foreach (var p in protocols) list.Add(new SslApplicationProtocol(p));
        return list;
    }

    /// <summary>
    /// SECURITY-CRITICAL. Decide whether the server certificate is acceptable: chain to a trusted root, and
    /// actually be issued for the name we asked for. Verbatim the SChannel provider's policy (one policy,
    /// three engines): with a raw TLS engine both halves are our job, and skipping the hostname check is the
    /// classic silent man-in-the-middle hole.
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
            // Trust exactly this certificate and nothing else — not "this, plus every public CA".
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_trust);
        }

        if (!chain.Build(remote))
        {
            string detail = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
            reason = $"certificate chain rejected: {(detail.Length == 0 ? "unknown reason" : detail)}";
            return false;
        }

        // A null host here is the "*" opt-out, resolved in CreateClientFilter, NOT an unset field: an
        // unset TargetHost is refused before a filter is ever built. MatchesHostname handles an IP
        // literal by matching iPAddress SANs, which is why the IP case needs no separate branch.
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

        lock (_serverLock)
        {
            foreach (var (_, context) in _serverContexts) context.Dispose();
            _serverContexts.Clear();
        }

        if (_ownsCerts)
        {
            if (OperatingSystem.IsWindows()) DeletePersistedKey(_serverCert);
            _serverCert?.Dispose();
            _trust?.Dispose();
        }
    }

    /// <summary>
    /// Convenience for tests: generate a throwaway self-signed certificate for <paramref name="host"/>
    /// (valid for the DNS name and the loopback IP), configure the server side with it, and make the client
    /// trust exactly that certificate with full verification — so the real chain + hostname path is
    /// exercised rather than bypassed. NOT for production. Mirrors the OpenSSL/SChannel helpers; the
    /// Windows half IS the SChannel helper's PFX round-trip, because the engine's PAL there is SChannel and
    /// has the same blindness to an ephemeral in-memory key.
    /// </summary>
    public static TlsSessionProvider CreateSelfSignedLoopback(string host = "localhost",
        TlsProtocol minProtocol = TlsProtocol.Tls13)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        // SChannel (the Windows PAL) will not select a server credential whose certificate lacks serverAuth.
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Windows: a memory-only key is invisible to SChannel; round-trip through a PFX with a PERSISTED
        // key (NOT EphemeralKeySet, which reproduces the problem), and Dispose deletes the container.
        // Elsewhere the same round-trip with default storage just gives us a cert that outlives `ephemeral`.
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
            return new TlsSessionProvider(serverCert, trust, verifyServer: true, minProtocol, ownsCerts: true);
        }
        catch
        {
            if (OperatingSystem.IsWindows()) DeletePersistedKey(serverCert);
            serverCert.Dispose();
            trust.Dispose();
            throw;
        }
    }

    /// <summary>Remove the on-disk CNG key container that <see cref="CreateSelfSignedLoopback"/> had to
    /// create on Windows — disposing an <see cref="X509Certificate2"/> does not; same shape as the SChannel
    /// provider's helper.</summary>
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
