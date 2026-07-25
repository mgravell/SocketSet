#if NET
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static SocketSets.Tls.OpenSsl.NativeOpenSsl;

namespace SocketSets.Tls.OpenSsl;

/// <summary>
/// Standalone kTLS feasibility probe — NOT part of the backend. It answers, on this box with this
/// OpenSSL: "if we let OpenSSL own a real socket fd with SSL_OP_ENABLE_KTLS, does it push the keys into
/// the kernel (BIO_get_ktls_send/recv), and can we then send/recv PLAINTEXT on that fd (kernel does the
/// crypto) — the model io_uring needs?" Uses a blocking loopback pair; the real integration will drive
/// the handshake via io_uring POLL instead. Requires the `tls` kernel module (sudo modprobe tls).
/// </summary>
public static unsafe class KtlsProbe
{
    public static (bool Ok, string Report) Run(string host = "localhost")
    {
        var sb = new StringBuilder();
        void Log(string m) { sb.Append(m).Append('\n'); }

        // Self-signed cert for the server; client trusts exactly it.
        string certPem, keyPem;
        using (var rsa = RSA.Create(2048))
        {
            var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            certPem = cert.ExportCertificatePem();
            keyPem = rsa.ExportPkcs8PrivateKeyPem();
        }

        // Loopback TCP pair (blocking .NET sockets; OpenSSL will drive I/O on their fds).
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = System.Threading.Tasks.Task.Run(() => client.Connect(new IPEndPoint(IPAddress.Loopback, port)));
        using var server = listener.Accept();
        connectTask.Wait(TimeSpan.FromSeconds(5));

        int cfd = (int)client.Handle, sfd = (int)server.Handle;

        // Contexts, both with kTLS enabled.
        nint sctx = SSL_CTX_new(TLS_method());
        nint cctx = SSL_CTX_new(TLS_method());
        if (sctx == 0 || cctx == 0) return (false, "SSL_CTX_new failed: " + DrainErrors());
        SSL_CTX_set_options(sctx, SSL_OP_ENABLE_KTLS);
        SSL_CTX_set_options(cctx, SSL_OP_ENABLE_KTLS);

        // Server cert + key.
        nint x = LoadX509(certPem), k = LoadKey(keyPem);
        if (SSL_CTX_use_certificate(sctx, x) != 1 || SSL_CTX_use_PrivateKey(sctx, k) != 1)
            return (false, "server cert/key load failed: " + DrainErrors());
        X509_free(x); EVP_PKEY_free(k);

        // Client trusts exactly the server cert, full verification.
        nint tx = LoadX509(certPem);
        X509_STORE_add_cert(SSL_CTX_get_cert_store(cctx), tx);
        X509_free(tx);
        SSL_CTX_set_verify(cctx, SSL_VERIFY_PEER, IntPtr.Zero);

        nint sssl = SSL_new(sctx), cssl = SSL_new(cctx);
        SSL_set_fd(sssl, sfd);
        SSL_set_fd(cssl, cfd);
        SSL_set_accept_state(sssl);
        SSL_set_connect_state(cssl);
        SSL_set1_host(cssl, host);

        // Drive both handshakes concurrently (blocking sockets → each call blocks until it needs the peer).
        int sret = 0, cret = 0;
        var st = new System.Threading.Thread(() => sret = SSL_do_handshake(sssl));
        var ct = new System.Threading.Thread(() => cret = SSL_do_handshake(cssl));
        st.Start(); ct.Start();
        bool joined = st.Join(TimeSpan.FromSeconds(5)) & ct.Join(TimeSpan.FromSeconds(5));
        if (!joined) return (false, "handshake did not complete within 5s (hung)");
        if (sret != 1 || cret != 1) return (false, $"handshake failed (server={sret} client={cret}): {DrainErrors()}");
        Log("handshake: OK (TLS via socket fd, SSL_OP_ENABLE_KTLS)");

        // Did OpenSSL push keys into the kernel?
        bool cTx = BIO_get_ktls_send(SSL_get_wbio(cssl)), cRx = BIO_get_ktls_recv(SSL_get_rbio(cssl));
        bool sTx = BIO_get_ktls_send(SSL_get_wbio(sssl)), sRx = BIO_get_ktls_recv(SSL_get_rbio(sssl));
        Log($"kTLS active: client TX={cTx} RX={cRx} | server TX={sTx} RX={sRx}");

        bool ok = cTx && sRx; // the direction we exercise below (client→server)

        // The payoff test: send/recv PLAINTEXT with PLAIN socket ops (no SSL_*), i.e. exactly what
        // io_uring would do. If the kernel is doing the crypto, "PING via kTLS" round-trips verbatim at
        // the app layer while being an encrypted TLS record on the wire. Client→server only: the client
        // sends no control records, so server-side plain recv() sees clean application data (a TLS 1.3
        // server would also emit a NewSessionTicket the other way — that's the RX-control-record case the
        // real integration handles with recvmsg).
        if (ok)
        {
            var msg = "PING via kTLS"u8.ToArray();
            client.Send(msg);                       // plain send → kernel kTLS-encrypts
            var buf = new byte[64];
            server.ReceiveTimeout = 3000;
            int n = server.Receive(buf);            // plain recv → kernel kTLS-decrypts
            string got = Encoding.ASCII.GetString(buf, 0, n);
            bool match = got == "PING via kTLS";
            Log($"plaintext round-trip over kTLS socket (plain send→plain recv): got=\"{got}\" => {(match ? "OK" : "MISMATCH")}");
            ok = match;

            // RX model the io_uring integration will use: server plain-send (kTLS TX), client SSL_read.
            // SSL_read on a kTLS-RX socket transparently consumes the TLS 1.3 NewSessionTicket control
            // record the server emitted after the handshake, then returns the application data — so we
            // don't have to parse recvmsg cmsg record-types ourselves. This is the crux of RX kTLS.
            server.Send("PONG via kTLS"u8.ToArray());
            var rbuf = new byte[64];
            int rn;
            fixed (byte* rp = rbuf) rn = SSL_read(cssl, rp, rbuf.Length);
            string rgot = rn > 0 ? Encoding.ASCII.GetString(rbuf, 0, rn) : $"(SSL_read={rn}, err={SSL_get_error(cssl, rn)})";
            bool rmatch = rgot == "PONG via kTLS";
            Log($"RX via SSL_read (eats session-ticket, returns app data): got=\"{rgot}\" => {(rmatch ? "OK" : "MISMATCH")}");
            ok &= rmatch;

            // close_notify over kTLS (what the io_uring CloseClient path does on a graceful close): the
            // server SSL_shutdown emits the alert (kTLS-encrypted); the client's SSL_read then returns 0
            // with SSL_ERROR_ZERO_RETURN — a clean TLS shutdown, distinguishable from a bare-FIN truncation.
            SSL_shutdown(sssl);
            int cn;
            fixed (byte* rp = rbuf) cn = SSL_read(cssl, rp, rbuf.Length);
            int cnerr = SSL_get_error(cssl, cn);
            bool clean = cn <= 0 && cnerr == SSL_ERROR_ZERO_RETURN;
            Log($"close_notify over kTLS: SSL_read={cn} err={cnerr} => {(clean ? "OK (clean shutdown)" : "NOT clean")}");
            ok &= clean;
        }
        else
        {
            Log("plaintext round-trip: SKIPPED (client-TX or server-RX kTLS not active)");
        }

        SSL_free(sssl); SSL_free(cssl);
        SSL_CTX_free(sctx); SSL_CTX_free(cctx);
        return (ok, sb.ToString().TrimEnd('\n'));
    }

    private static nint LoadX509(string pem)
    {
        var bytes = Encoding.ASCII.GetBytes(pem);
        fixed (byte* p = bytes) { nint bio = BIO_new_mem_buf(p, bytes.Length); try { return PEM_read_bio_X509(bio, 0, 0, 0); } finally { BIO_free(bio); } }
    }

    private static nint LoadKey(string pem)
    {
        var bytes = Encoding.ASCII.GetBytes(pem);
        fixed (byte* p = bytes) { nint bio = BIO_new_mem_buf(p, bytes.Length); try { return PEM_read_bio_PrivateKey(bio, 0, 0, 0); } finally { BIO_free(bio); } }
    }
}
#endif
