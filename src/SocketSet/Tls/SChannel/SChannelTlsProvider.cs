#if NET
using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static SocketSets.Tls.SChannel.NativeSspi;

namespace SocketSets.Tls.SChannel;

/// <summary>
/// SChannel-backed <see cref="TlsProvider"/> — Windows TLS with no <c>SslStream</c> anywhere in the
/// picture. Owns the SSPI credential handles (one outbound/client, plus one inbound/server when a
/// certificate is supplied) and hands each connection a <see cref="SChannelTlsFilter"/>.
///
/// Credential handles are deliberately per-PROVIDER, not per-connection: AcquireCredentialsHandle is
/// expensive, and the handle is also what SChannel hangs its session cache off, so sharing it is what
/// makes resumption work across connections.
///
/// Certificate validation is done here, in managed code, rather than by SChannel: the credential carries
/// SCH_CRED_MANUAL_CRED_VALIDATION and the client context ISC_REQ_MANUAL_CRED_VALIDATION, and
/// <see cref="ValidateRemote"/> runs the chain build plus the hostname check. That is what lets this
/// provider offer the same "trust exactly this certificate" test mode as
/// <see cref="OpenSsl.OpenSslTlsProvider"/>, and keeps the security-critical decision somewhere readable.
///
/// <see cref="TlsProvider.SupportsKernelOffload"/> stays false, permanently: Windows has no kTLS.
/// </summary>
public sealed unsafe class SChannelTlsProvider : TlsProvider, IDisposable
{
    private readonly SecHandle* _clientCred;
    private readonly SecHandle* _serverCred; // null for a client-only provider
    private readonly X509Certificate2? _serverCert; // must outlive the credential handle
    private readonly X509Certificate2? _trust;
    private readonly bool _verifyServer;
    private readonly bool _ownsCerts; // set by CreateSelfSignedLoopback: dispose + delete the key on exit
    private bool _disposed;

    // NOTE: the floor is deliberately NOT retained as a field. The renegotiation refusal in
    // SChannelTlsFilter needs the protocol that was actually NEGOTIATED, not the floor that was
    // configured — at a 1.2 floor a connection may still land on 1.3 — so it queries
    // SECPKG_ATTR_CONNECTION_INFO per connection instead. A cached floor here would be the wrong
    // answer in exactly the case that matters.

    /// <param name="serverCertificate">Certificate (WITH private key) to present to inbound connections, or
    /// null for a client-only provider. The key must be one SChannel can open — an ephemeral in-memory key
    /// is invisible to it; see <see cref="CreateSelfSignedLoopback"/> for the round-trip that fixes that.</param>
    /// <param name="trustCertificate">If set, the ONLY root the client trusts (e.g. a self-signed test
    /// certificate); if null, the client uses the system trust stores.</param>
    /// <param name="verifyServer">Whether the client verifies the server certificate + hostname. True is the
    /// safe default; false is the man-in-the-middle footgun and exists only for bring-up.</param>
    /// <param name="minProtocol">Lowest TLS version to negotiate. Defaults to
    /// <see cref="TlsProtocol.Tls13"/>, matching <see cref="OpenSsl.OpenSslTlsProvider"/> — see
    /// <see cref="NativeSspi.SP_PROT_DISABLE_BELOW_TLS1_3"/> for the Windows-version floor that implies.</param>
    public SChannelTlsProvider(
        X509Certificate2? serverCertificate = null,
        X509Certificate2? trustCertificate = null,
        bool verifyServer = true,
        TlsProtocol minProtocol = TlsProtocol.Tls13)
        : this(serverCertificate, trustCertificate, verifyServer, minProtocol, ownsCerts: false)
    {
    }

    private SChannelTlsProvider(
        X509Certificate2? serverCertificate, X509Certificate2? trustCertificate, bool verifyServer,
        TlsProtocol minProtocol, bool ownsCerts)
    {
        _serverCert = serverCertificate;
        _trust = trustCertificate;
        _verifyServer = verifyServer;
        _ownsCerts = ownsCerts;

        _clientCred = Acquire(null, inbound: false, minProtocol);
        if (serverCertificate is not null)
        {
            try
            {
                _serverCred = Acquire(serverCertificate, inbound: true, minProtocol);
            }
            catch
            {
                Release(_clientCred);
                throw;
            }
        }
    }

    internal SecHandle* ClientCredential => _clientCred;

    internal SecHandle* ServerCredential => _serverCred;

    public override TlsFilter CreateClientFilter(TlsClientOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new SChannelTlsFilter(this, client: true, options.TargetHost, BuildAlpnBuffer(options.AlpnProtocols));
    }

    public override TlsFilter CreateServerFilter(TlsServerOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_serverCred == null)
            throw new InvalidOperationException("This provider has no server certificate; it is client-only.");
        return new SChannelTlsFilter(this, client: false, targetHost: null, BuildAlpnBuffer(options.AlpnProtocols));
    }

    /// <summary>
    /// Wrap an ALPN id list in the SECBUFFER_APPLICATION_PROTOCOLS payload SChannel expects. Unlike
    /// OpenSSL — where the server's choice is made by a per-SSL_CTX callback we have to write — SChannel
    /// takes the SAME list shape from both sides and does the selection itself, so client and server differ
    /// only in what they put in the list.
    ///
    /// Layout (little-endian, packed): SEC_APPLICATION_PROTOCOLS { ULONG ProtocolListsSize } followed by
    /// SEC_APPLICATION_PROTOCOL_LIST { ULONG ProtoNegoExt; USHORT ProtocolListSize; BYTE List[] }.
    /// ProtocolListsSize covers the inner struct — its own field is not counted.
    /// </summary>
    private static byte[]? BuildAlpnBuffer(IReadOnlyList<string>? protocols)
    {
        byte[]? list = AlpnProtocolList.Encode(protocols);
        if (list is null) return null;

        var buffer = new byte[AlpnHeaderSize + list.Length];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, sizeof(int) + sizeof(short) + list.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], SecApplicationProtocolNegotiationExt_ALPN);
        BinaryPrimitives.WriteInt16LittleEndian(span[8..], (short)list.Length);
        list.CopyTo(span[AlpnHeaderSize..]);
        return buffer;
    }

    /// <summary>
    /// SECURITY-CRITICAL. Decide whether the server certificate is acceptable: chain to a trusted root, and
    /// actually be issued for the name we asked for. With a raw TLS engine both halves are our job, and
    /// skipping the hostname check is the classic silent man-in-the-middle hole — a certificate valid for
    /// ANY host would otherwise pass.
    /// </summary>
    internal bool ValidateRemote(X509Certificate2? remote, string? host, out string reason)
    {
        reason = "";
        if (!_verifyServer) return true; // loud, opt-in bring-up escape hatch

        if (remote is null) { reason = "server presented no certificate"; return false; }

        using var chain = new X509Chain();
        // Matches SslStream's default (CheckCertificateRevocation: false).
        // TODO: expose revocation policy; Online is the right answer for a real off-box client.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
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

        if (host is { Length: > 0 } && !remote.MatchesHostname(host))
        {
            reason = $"certificate is not valid for host '{host}'";
            return false;
        }

        return true;
    }

    /// <summary>Build an SSPI credential handle over the SChannel package. Lives in native memory so the
    /// pointer handed to every filter is stable for the provider's lifetime.</summary>
    private static SecHandle* Acquire(X509Certificate2? cert, bool inbound, TlsProtocol minProtocol)
    {
        var tlsParams = new TlsParameters
        {
            grbitDisabledProtocols = minProtocol == TlsProtocol.Tls13
                ? SP_PROT_DISABLE_BELOW_TLS1_3
                : SP_PROT_DISABLE_BELOW_TLS1_2,
        };
        nint certHandle = cert?.Handle ?? 0;

        var creds = new SchCredentials
        {
            dwVersion = SCH_CREDENTIALS_VERSION,
            dwCredFormat = SCH_CRED_FORMAT_CERT_CONTEXT,
            cTlsParameters = 1,
            pTlsParameters = &tlsParams,
            dwFlags = inbound
                ? SCH_USE_STRONG_CRYPTO
                // Outbound: we do our own chain + hostname check, and never volunteer a client certificate.
                : SCH_USE_STRONG_CRYPTO | SCH_CRED_MANUAL_CRED_VALIDATION | SCH_CRED_NO_DEFAULT_CREDS,
        };
        if (certHandle != 0)
        {
            creds.cCreds = 1;
            creds.paCred = &certHandle;
        }

        var handle = (SecHandle*)NativeMemory.AllocZeroed((nuint)sizeof(SecHandle));
        int status;
        fixed (char* package = UnispName)
        {
            status = AcquireCredentialsHandle(
                null, package, inbound ? SECPKG_CRED_INBOUND : SECPKG_CRED_OUTBOUND,
                null, &creds, null, null, handle, null);
        }
        GC.KeepAlive(cert); // the cert context must stay alive across the call

        if (status != SEC_E_OK)
        {
            NativeMemory.Free(handle);
            throw new InvalidOperationException(
                $"SChannel AcquireCredentialsHandle({(inbound ? "inbound" : "outbound")}) failed: {Describe(status)}");
        }
        return handle;
    }

    private static void Release(SecHandle* handle)
    {
        if (handle == null) return;
        FreeCredentialsHandle(handle);
        NativeMemory.Free(handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release(_clientCred);
        Release(_serverCred);

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
    /// exercised rather than bypassed. NOT for production. Mirrors
    /// <see cref="OpenSsl.OpenSslTlsProvider.CreateSelfSignedLoopback"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SChannelTlsProvider CreateSelfSignedLoopback(string host = "localhost",
        TlsProtocol minProtocol = TlsProtocol.Tls13)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        // SChannel will not select a server credential whose certificate lacks the serverAuth EKU.
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // CreateSelfSigned leaves the private key in memory only, and SChannel cannot see such a key —
        // AcquireCredentialsHandle fails with SEC_E_UNKNOWN_CREDENTIALS. Round-tripping through a PFX with
        // a PERSISTED key set (note: NOT EphemeralKeySet, which reproduces the same problem) gives the
        // certificate a CNG key container SChannel can open. Dispose deletes the container again.
        string password = Guid.NewGuid().ToString("N");
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx, password);
        var serverCert = X509CertificateLoader.LoadPkcs12(
            pfx, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        var trust = X509CertificateLoader.LoadCertificate(ephemeral.Export(X509ContentType.Cert));

        try
        {
            return new SChannelTlsProvider(serverCert, trust, verifyServer: true, minProtocol, ownsCerts: true);
        }
        catch
        {
            DeletePersistedKey(serverCert);
            serverCert.Dispose();
            trust.Dispose();
            throw;
        }
    }

    /// <summary>Remove the on-disk CNG key container that <see cref="CreateSelfSignedLoopback"/> had to
    /// create — disposing an <see cref="X509Certificate2"/> does not, so without this every run would leave
    /// a key behind in the user's crypto store.</summary>
    [SupportedOSPlatform("windows")]
    private static void DeletePersistedKey(X509Certificate2? cert)
    {
        if (cert is null) return;
        try
        {
            using var key = cert.GetRSAPrivateKey();
            if (key is RSACng cng) cng.Key.Delete();
        }
        catch { /* best effort: a leaked test key is not worth throwing from Dispose over */ }
    }
}
#endif
