#if NET
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static SocketSets.Tls.OpenSsl.NativeOpenSsl;

namespace SocketSets.Tls.OpenSsl;

/// <summary>
/// OpenSSL-backed <see cref="TlsProvider"/>. Owns a client <c>SSL_CTX</c> (trust + verify config) and,
/// when a server certificate is supplied, a server <c>SSL_CTX</c> (cert + key). Each connection gets its
/// own <c>SSL*</c> plus a memory-BIO pair (see <see cref="OpenSslTlsFilter"/>).
/// </summary>
public sealed unsafe class OpenSslTlsProvider : TlsProvider, IDisposable
{
    private readonly nint _clientCtx;
    private readonly nint _serverCtx; // 0 when no server certificate was configured (client-only)
    private readonly bool _verifyServer;
    private readonly bool _kernelOffload;
    private bool _disposed;

    // Server-side ALPN. The selection callback is per-SSL_CTX, so the list has to live as long as the
    // context and is handed to the callback as its `arg`: [int length][RFC 7301 list bytes].
    private readonly object _alpnLock = new();
    private nint _serverAlpn;
    private IReadOnlyList<string>? _serverAlpnSource;

    /// <summary>True if built with kernel-offload (kTLS): the CTXs carry SSL_OP_ENABLE_KTLS, so a
    /// connection driven via the fd-bound path (see <see cref="CreateKernelSsl"/>) will hand keys to the
    /// kernel at handshake completion. The io_uring backend uses this to pick its kTLS path.</summary>
    public override bool SupportsKernelOffload => _kernelOffload;

    /// <param name="serverCertPem">PEM server certificate (+ chain), or null for a client-only provider.</param>
    /// <param name="serverKeyPem">PEM private key matching <paramref name="serverCertPem"/>.</param>
    /// <param name="trustCertPem">If set, the ONLY certificate the client trusts (e.g. a self-signed test
    /// cert); if null, the client uses the system CA store.</param>
    /// <param name="verifyServer">Whether the client verifies the server certificate + hostname. Leaving
    /// this true is the safe default; false is the man-in-the-middle footgun and exists only for bring-up.</param>
    /// <param name="kernelOffload">Enable kTLS (SSL_OP_ENABLE_KTLS) on the contexts, so connections driven
    /// via the fd-bound path hand their keys to the kernel at handshake completion; see
    /// <see cref="SupportsKernelOffload"/>.</param>
    public OpenSslTlsProvider(string? serverCertPem = null, string? serverKeyPem = null,
        string? trustCertPem = null, bool verifyServer = true, bool kernelOffload = false)
    {
        _verifyServer = verifyServer;
        _kernelOffload = kernelOffload;

        _clientCtx = SSL_CTX_new(TLS_method());
        if (_clientCtx == 0) throw Err("SSL_CTX_new(client)");
        // Reject client-initiated renegotiation (TLS <= 1.2 DoS hardening; no effect on TLS 1.3 KeyUpdate).
        // A TLS 1.2 client is reachable here (no min-version pin), and the server otherwise advertises
        // "Secure Renegotiation IS supported", so the vector is real. Set on both roles for symmetry.
        SSL_CTX_set_options(_clientCtx, SSL_OP_NO_RENEGOTIATION);
        if (kernelOffload) { SSL_CTX_set_options(_clientCtx, SSL_OP_ENABLE_KTLS); ApplyKtlsRxOverride(_clientCtx); }
        if (trustCertPem is not null)
        {
            // Trust exactly this certificate (self-signed test setup): add it to the client's store.
            nint x = LoadX509(trustCertPem);
            if (X509_STORE_add_cert(SSL_CTX_get_cert_store(_clientCtx), x) != 1) { X509_free(x); throw Err("X509_STORE_add_cert"); }
            X509_free(x); // the store took a reference
        }
        else
        {
            SSL_CTX_set_default_verify_paths(_clientCtx); // system CA bundle
        }
        SSL_CTX_set_verify(_clientCtx, verifyServer ? SSL_VERIFY_PEER : SSL_VERIFY_NONE, IntPtr.Zero);

        if (serverCertPem is not null)
        {
            if (serverKeyPem is null) throw new ArgumentNullException(nameof(serverKeyPem), "A server certificate needs its private key.");
            _serverCtx = SSL_CTX_new(TLS_method());
            if (_serverCtx == 0) throw Err("SSL_CTX_new(server)");
            SSL_CTX_set_options(_serverCtx, SSL_OP_NO_RENEGOTIATION); // reject client renegotiation (see client ctx)
            if (kernelOffload) { SSL_CTX_set_options(_serverCtx, SSL_OP_ENABLE_KTLS); ApplyKtlsRxOverride(_serverCtx); }
            nint cert = LoadX509(serverCertPem);
            try
            {
                if (SSL_CTX_use_certificate(_serverCtx, cert) != 1) throw Err("SSL_CTX_use_certificate");
            }
            finally { X509_free(cert); }
            nint key = LoadKey(serverKeyPem);
            try
            {
                if (SSL_CTX_use_PrivateKey(_serverCtx, key) != 1) throw Err("SSL_CTX_use_PrivateKey");
            }
            finally { EVP_PKEY_free(key); }
            if (SSL_CTX_check_private_key(_serverCtx) != 1) throw Err("SSL_CTX_check_private_key");
        }
    }

    public override TlsFilter CreateClientFilter(TlsClientOptions options)
    {
        var (ssl, rbio, wbio) = NewSsl(_clientCtx);
        SSL_set_connect_state(ssl);
        if (options.TargetHost is { Length: > 0 } host)
        {
            // SNI: tell the server which name we're asking for.
            nint hp = Marshal.StringToCoTaskMemUTF8(host);
            try { SSL_ctrl(ssl, SSL_CTRL_SET_TLSEXT_HOSTNAME, TLSEXT_NAMETYPE_host_name, hp); }
            finally { Marshal.FreeCoTaskMem(hp); } // OpenSSL dups the name
            // Hostname verification: the cert must actually be valid for this name (the classic check).
            if (_verifyServer && SSL_set1_host(ssl, host) != 1)
                throw Err("SSL_set1_host");
        }
        ApplyClientAlpn(ssl, options.AlpnProtocols);
        return new OpenSslTlsFilter(ssl, rbio, wbio);
    }

    public override TlsFilter CreateServerFilter(TlsServerOptions options)
    {
        if (_serverCtx == 0) throw new InvalidOperationException("This provider has no server certificate; it is client-only.");
        ConfigureServerAlpn(options.AlpnProtocols);
        var (ssl, rbio, wbio) = NewSsl(_serverCtx);
        SSL_set_accept_state(ssl);
        return new OpenSslTlsFilter(ssl, rbio, wbio);
    }

    /// <summary>Offer an ALPN list on a client SSL. OpenSSL copies the bytes, so nothing needs to outlive
    /// the call. Note the inverted convention: SSL_set_alpn_protos returns 0 for SUCCESS.</summary>
    private static void ApplyClientAlpn(nint ssl, IReadOnlyList<string>? protocols)
    {
        byte[]? list = AlpnProtocolList.Encode(protocols);
        if (list is null) return;
        fixed (byte* p = list)
        {
            if (SSL_set_alpn_protos(ssl, p, (uint)list.Length) != 0) throw Err("SSL_set_alpn_protos");
        }
    }

    /// <summary>
    /// Install the server-side ALPN selection callback on the shared SSL_CTX, with this provider's protocol
    /// list as its argument. OpenSSL has no per-SSL equivalent of <c>SSL_CTX_set_alpn_select_cb</c>, so a
    /// provider serves exactly one server list — presenting a different one is an error rather than a
    /// silent last-writer-wins that would reconfigure connections already in flight.
    /// </summary>
    private void ConfigureServerAlpn(IReadOnlyList<string>? protocols)
    {
        if (protocols is null || protocols.Count == 0) return;

        lock (_alpnLock)
        {
            if (_serverAlpn != 0)
            {
                if (!ReferenceEquals(_serverAlpnSource, protocols))
                    throw new InvalidOperationException(
                        "OpenSSL's ALPN selection callback is per-SSL_CTX, so one OpenSslTlsProvider supports one " +
                        "server-side protocol list; use a separate provider for a different list.");
                return;
            }

            byte[] list = AlpnProtocolList.Encode(protocols)!;
            nint arg = Marshal.AllocHGlobal(sizeof(int) + list.Length);
            *(int*)arg = list.Length;
            list.CopyTo(new Span<byte>((byte*)arg + sizeof(int), list.Length));

            SSL_CTX_set_alpn_select_cb(
                _serverCtx,
                (nint)(delegate* unmanaged[Cdecl]<nint, byte**, byte*, byte*, uint, nint, int>)&SelectAlpn,
                arg);

            _serverAlpn = arg;
            _serverAlpnSource = protocols;
        }
    }

    /// <summary>
    /// OpenSSL's ALPN selection callback (native calling convention, so it must not throw). Picks by SERVER
    /// preference: the first id WE list that the client also offered. <paramref name="arg"/> is the native
    /// buffer built in <see cref="ConfigureServerAlpn"/>, which outlives every handshake — important,
    /// because the selected id is returned as a POINTER INTO it.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SelectAlpn(nint ssl, byte** selected, byte* selectedLen, byte* client, uint clientLen, nint arg)
    {
        if (arg == 0) return SSL_TLSEXT_ERR_NOACK;
        int serverLen = *(int*)arg;
        byte* server = (byte*)arg + sizeof(int);

        for (int s = 0; s + 1 <= serverLen; s += server[s] + 1)
        {
            int n = server[s];
            if (n == 0 || s + 1 + n > serverLen) break; // malformed; cannot happen via AlpnProtocolList
            for (uint c = 0; c + 1 <= clientLen; c += (uint)client[c] + 1)
            {
                int m = client[c];
                if (m == 0 || c + 1 + m > clientLen) break;
                if (m != n) continue;
                if (new ReadOnlySpan<byte>(server + s + 1, n).SequenceEqual(new ReadOnlySpan<byte>(client + c + 1, m)))
                {
                    *selected = server + s + 1;
                    *selectedLen = (byte)n;
                    return SSL_TLSEXT_ERR_OK;
                }
            }
        }

        // The client offered ALPN and we share nothing: RFC 7301 says fail the handshake rather than serve
        // a protocol neither side agreed to.
        return SSL_TLSEXT_ERR_ALERT_FATAL;
    }

    /// <summary>kTLS path (io_uring only): create an <c>SSL*</c> bound DIRECTLY to the socket fd (a socket
    /// BIO, not memory BIOs) with SSL_OP_ENABLE_KTLS active, so OpenSSL pushes the keys into the kernel at
    /// handshake completion. The caller drives SSL_do_handshake / SSL_read and frees the SSL. Returns the
    /// raw handle (this path deliberately bypasses <see cref="TlsFilter"/> — kTLS is a different I/O model).</summary>
    internal nint CreateKernelSsl(int fd, bool client, string? targetHost, IReadOnlyList<string>? alpn)
    {
        nint ctx = client ? _clientCtx : _serverCtx;
        if (ctx == 0) throw new InvalidOperationException("kTLS: this provider has no " + (client ? "client" : "server") + " context.");
        // This path bypasses CreateServerFilter, so the server's ALPN callback has to be installed here too.
        if (!client) ConfigureServerAlpn(alpn);
        nint ssl = SSL_new(ctx);
        if (ssl == 0) throw Err("SSL_new(ktls)");
        SSL_set_fd(ssl, fd); // socket BIO, BIO_NOCLOSE — the shard still owns/closes the fd
        if (client)
        {
            SSL_set_connect_state(ssl);
            if (targetHost is { Length: > 0 } host)
            {
                nint hp = Marshal.StringToCoTaskMemUTF8(host);
                try { SSL_ctrl(ssl, SSL_CTRL_SET_TLSEXT_HOSTNAME, TLSEXT_NAMETYPE_host_name, hp); }
                finally { Marshal.FreeCoTaskMem(hp); }
                if (_verifyServer) SSL_set1_host(ssl, host);
            }
            ApplyClientAlpn(ssl, alpn);
        }
        else
        {
            SSL_set_accept_state(ssl);
        }
        return ssl;
    }

    // Build the SSL + its memory-BIO pair; the SSL owns the BIOs after SSL_set_bio.
    private static (nint Ssl, nint Rbio, nint Wbio) NewSsl(nint ctx)
    {
        nint ssl = SSL_new(ctx);
        if (ssl == 0) throw Err("SSL_new");
        nint rbio = BIO_new(BIO_s_mem());
        nint wbio = BIO_new(BIO_s_mem());
        if (rbio == 0 || wbio == 0) { SSL_free(ssl); throw Err("BIO_new"); }
        SSL_set_bio(ssl, rbio, wbio);
        return (ssl, rbio, wbio);
    }

    /// <summary>
    /// Diagnostic control over kTLS RECEIVE offload: <c>SS_KTLS_NO_RX=1</c> sets
    /// <c>SSL_MODE_NO_KTLS_RX</c>, leaving TX offloaded and RX in userspace.
    ///
    /// It exists to make the RX question answerable WITHOUT changing OpenSSL versions. Measured
    /// 2026-07-29, turning RX on (by moving from system 3.0.13 to a self-built 3.5.7, since 3.0.x declines
    /// kTLS RX for TLS 1.3) cost -4.3% at 512B - but that comparison changes the whole library, so it
    /// cannot attribute the loss to RX offload. With this knob the same binary can be run both ways.
    /// </summary>
    private static void ApplyKtlsRxOverride(nint ctx)
    {
        if (Environment.GetEnvironmentVariable("SS_KTLS_NO_RX") != "1") return;
        long before = SSL_CTX_ctrl(ctx, SSL_CTRL_SET_MODE, 0, IntPtr.Zero); // op=0 returns the mode unchanged
        long after = SSL_CTX_ctrl(ctx, SSL_CTRL_SET_MODE, SSL_MODE_NO_KTLS_RX, IntPtr.Zero);
        Console.Error.WriteLine($"[ktls] SS_KTLS_NO_RX: mode 0x{before:x} -> 0x{after:x} " +
                                $"(NO_KTLS_RX=0x{SSL_MODE_NO_KTLS_RX:x} set={(after & SSL_MODE_NO_KTLS_RX) != 0})");
    }

    private static nint LoadX509(string pem)
    {
        var bytes = Encoding.ASCII.GetBytes(pem);
        fixed (byte* p = bytes)
        {
            nint bio = BIO_new_mem_buf(p, bytes.Length);
            try
            {
                nint x = PEM_read_bio_X509(bio, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (x == 0) throw Err("PEM_read_bio_X509");
                return x;
            }
            finally { BIO_free(bio); }
        }
    }

    private static nint LoadKey(string pem)
    {
        var bytes = Encoding.ASCII.GetBytes(pem);
        fixed (byte* p = bytes)
        {
            nint bio = BIO_new_mem_buf(p, bytes.Length);
            try
            {
                nint k = PEM_read_bio_PrivateKey(bio, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (k == 0) throw Err("PEM_read_bio_PrivateKey");
                return k;
            }
            finally { BIO_free(bio); }
        }
    }

    private static InvalidOperationException Err(string what)
        => new($"OpenSSL {what} failed: {DrainErrors()}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_clientCtx != 0) SSL_CTX_free(_clientCtx);
        if (_serverCtx != 0) SSL_CTX_free(_serverCtx); // frees the ctx before its alpn `arg` goes away
        if (_serverAlpn != 0) { Marshal.FreeHGlobal(_serverAlpn); _serverAlpn = 0; }
    }

    /// <summary>
    /// Convenience for tests: generate a throwaway self-signed certificate for <paramref name="host"/>
    /// (valid for the DNS name and the loopback IP), configure the server side with it, and make the
    /// client trust exactly that certificate with full verification (so the real verify + hostname path is
    /// exercised, not bypassed). NOT for production — the key lives only in this process.
    /// </summary>
    public static OpenSslTlsProvider CreateSelfSignedLoopback(string host = "localhost", bool kernelOffload = false)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        string certPem = cert.ExportCertificatePem();
        string keyPem = rsa.ExportPkcs8PrivateKeyPem();
        return new OpenSslTlsProvider(certPem, keyPem, trustCertPem: certPem, verifyServer: true, kernelOffload: kernelOffload);
    }
}
#endif
