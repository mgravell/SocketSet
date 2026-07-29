#if NET // OpenSSL interop uses LibraryImport (source-gen, .NET 7+); excluded from the netfx fallback build.
using System.Runtime.InteropServices;

namespace SocketSets.Tls.OpenSsl;

/// <summary>
/// Minimal P/Invoke surface for the OpenSSL (libssl/libcrypto) memory-BIO TLS path. Only the functions the
/// push filter needs. OpenSSL 3.x self-initializes on first use, so there is no explicit library-init call.
///
/// Library resolution: the imports name "ssl"/"crypto"; a resolver maps those to the versioned sonames
/// (libssl.so.3 first — OpenSSL 3, current everywhere modern .NET runs — then unversioned / 1.1 fallbacks).
/// Runs only on platforms that actually have OpenSSL (Linux); the file compiles everywhere but is never
/// invoked on Windows (which uses SSPI/SChannel — see <see cref="SChannel.SChannelTlsProvider"/>).
/// </summary>
internal static unsafe partial class NativeOpenSsl
{
    private const string Ssl = "ssl";
    private const string Crypto = "crypto";

    // --- SSL_get_error codes ---
    public const int SSL_ERROR_NONE = 0;
    public const int SSL_ERROR_SSL = 1;
    public const int SSL_ERROR_WANT_READ = 2;
    public const int SSL_ERROR_WANT_WRITE = 3;
    public const int SSL_ERROR_SYSCALL = 5;
    public const int SSL_ERROR_ZERO_RETURN = 6;

    // --- verify modes / ctrl cmds ---
    public const int SSL_VERIFY_NONE = 0;
    public const int SSL_VERIFY_PEER = 1;
    public const int SSL_CTRL_SET_TLSEXT_HOSTNAME = 55;
    public const int TLSEXT_NAMETYPE_host_name = 0;
    public const int BIO_CTRL_PENDING = 10;
    public const long X509_V_OK = 0;

    // --- ALPN select-callback return codes ---
    public const int SSL_TLSEXT_ERR_OK = 0;
    public const int SSL_TLSEXT_ERR_ALERT_FATAL = 2; // RFC 7301 no_application_protocol
    public const int SSL_TLSEXT_ERR_NOACK = 3;       // decline quietly: no ALPN in the ServerHello

    // --- kTLS ---
    public const ulong SSL_OP_ENABLE_KTLS = 1UL << 3; // SSL_OP_BIT(3): let OpenSSL push keys to the kernel
    public const int BIO_CTRL_GET_KTLS_SEND = 73;     // BIO_get_ktls_send: is kernel TX-offload active?
    public const int BIO_CTRL_GET_KTLS_RECV = 76;     // BIO_get_ktls_recv: is kernel RX-offload active?

    // --- SSL_CTX_ctrl commands (the macros SSL_CTX_set_mode / clear_mode / set_max_proto_version) ---
    // Added 2026-07-29 to test WHY kTLS RX never engages here (TlsRxSw is 0 cumulative, and the standalone
    // probe reports RX=False even over a plain socket BIO). Two candidate causes need distinguishing:
    // a mode bit suppressing RX, and OpenSSL < 3.2 declining RX offload for TLS 1.3.
    public const int SSL_CTRL_SET_MODE = 33;
    public const int SSL_CTRL_CLEAR_MODE = 78;
    public const int SSL_CTRL_SET_MAX_PROTO_VERSION = 124;
    public const long SSL_MODE_NO_KTLS_TX = 0x00000200L;
    public const long SSL_MODE_NO_KTLS_RX = 0x00000400L;
    public const long TLS1_2_VERSION = 0x0303;
    public const long TLS1_3_VERSION = 0x0304;

    static NativeOpenSsl()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeOpenSsl).Assembly, static (name, asm, path) =>
        {
            string[] candidates = name switch
            {
                Ssl => ["libssl.so.3", "libssl.so", "libssl.so.1.1"],
                Crypto => ["libcrypto.so.3", "libcrypto.so", "libcrypto.so.1.1"],
                _ => [],
            };
            foreach (var c in candidates)
                if (NativeLibrary.TryLoad(c, out var h)) return h;
            return IntPtr.Zero; // fall back to default resolution
        });
    }

    // ===================== libssl: contexts, connections =====================

    [LibraryImport(Ssl)] public static partial IntPtr TLS_method();
    [LibraryImport(Ssl)] public static partial IntPtr SSL_CTX_new(IntPtr method);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_free(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_set_default_verify_paths(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_set_verify(IntPtr ctx, int mode, IntPtr callback);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_use_certificate(IntPtr ctx, IntPtr x509);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_use_PrivateKey(IntPtr ctx, IntPtr pkey);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_check_private_key(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_CTX_get_cert_store(IntPtr ctx);

    [LibraryImport(Ssl)] public static partial IntPtr SSL_new(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial void SSL_free(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_connect_state(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_accept_state(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_bio(IntPtr ssl, IntPtr rbio, IntPtr wbio);
    [LibraryImport(Ssl)] public static partial int SSL_do_handshake(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial int SSL_read(IntPtr ssl, byte* buf, int num);
    [LibraryImport(Ssl)] public static partial int SSL_write(IntPtr ssl, byte* buf, int num);
    [LibraryImport(Ssl)] public static partial int SSL_get_error(IntPtr ssl, int ret);
    [LibraryImport(Ssl)] public static partial int SSL_shutdown(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial long SSL_ctrl(IntPtr ssl, int cmd, long larg, IntPtr parg);
    [LibraryImport(Ssl)] public static partial long SSL_get_verify_result(IntPtr ssl);
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)] public static partial int SSL_set1_host(IntPtr ssl, string hostname);

    // ALPN. Note the asymmetry: a CLIENT just hands over its preference list, but a SERVER has to supply a
    // selection CALLBACK — and that callback is per-SSL_CTX, with no per-SSL equivalent, which is why one
    // OpenSslTlsProvider can serve exactly one server-side protocol list.
    [LibraryImport(Ssl)] public static partial int SSL_set_alpn_protos(IntPtr ssl, byte* protos, uint protosLen);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_set_alpn_select_cb(IntPtr ctx, IntPtr cb, IntPtr arg);
    [LibraryImport(Ssl)] public static partial void SSL_get0_alpn_selected(IntPtr ssl, byte** data, uint* len);

    // kTLS path: bind the SSL directly to a socket fd (socket BIO), toggle options, inspect the BIOs.
    [LibraryImport(Ssl)] public static partial int SSL_set_fd(IntPtr ssl, int fd);
    [LibraryImport(Ssl)] public static partial ulong SSL_CTX_set_options(IntPtr ctx, ulong options);
    [LibraryImport(Ssl)] public static partial long SSL_CTX_ctrl(IntPtr ctx, int cmd, long larg, IntPtr parg);

    /// <summary>Which libcrypto actually got loaded. The resolver tries several sonames, and kTLS RX
    /// behaviour differs by version (3.0.x declines it for TLS 1.3), so "which OpenSSL is this?" is a
    /// load-bearing question rather than trivia. Encoded as 0xMNN00PP0 in 3.x.</summary>
    [LibraryImport(Crypto)] public static partial ulong OpenSSL_version_num();

    /// <summary>OpenSSL_version(OPENSSL_CFLAGS) — the configure/compiler flags of the loaded build. Needed
    /// because kTLS is a COMPILE-TIME option (`enable-ktls`): a build without it reports TX=False RX=False
    /// and is indistinguishable from "this version declines kTLS" unless you look. Measured 2026-07-29,
    /// when a 3.5.7 build reported both false and turned out to be built without kTLS at all.</summary>
    [LibraryImport(Crypto)] public static partial IntPtr OpenSSL_version(int type);

    public static string OpenSslCFlags()
    {
        try
        {
            var p = OpenSSL_version(1 /* OPENSSL_CFLAGS */);
            return p == IntPtr.Zero ? "(null)" : (Marshal.PtrToStringUTF8(p) ?? "(null)");
        }
        catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
    }

    /// <summary>Was the loaded OpenSSL built with kTLS support at all?</summary>
    public static bool OpenSslHasKtls() => !OpenSslCFlags().Contains("OPENSSL_NO_KTLS", StringComparison.Ordinal);

    public static string OpenSslVersionString()
    {
        try
        {
            ulong v = OpenSSL_version_num();
            return $"{(v >> 28) & 0xF}.{(v >> 20) & 0xFF}.{(v >> 4) & 0xFF}";
        }
        catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
    }
    [LibraryImport(Ssl)] public static partial ulong SSL_set_options(IntPtr ssl, ulong options);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_rbio(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_wbio(IntPtr ssl);

    // ===================== libcrypto: memory BIOs, PEM, errors =====================

    [LibraryImport(Crypto)] public static partial IntPtr BIO_new(IntPtr method);
    [LibraryImport(Crypto)] public static partial IntPtr BIO_s_mem();
    [LibraryImport(Crypto)] public static partial int BIO_write(IntPtr bio, byte* data, int len);
    [LibraryImport(Crypto)] public static partial int BIO_read(IntPtr bio, byte* data, int len);
    [LibraryImport(Crypto)] public static partial long BIO_ctrl(IntPtr bio, int cmd, long larg, IntPtr parg);
    [LibraryImport(Crypto)] public static partial IntPtr BIO_new_mem_buf(byte* buf, int len);
    [LibraryImport(Crypto)] public static partial int BIO_free(IntPtr bio);

    [LibraryImport(Crypto)] public static partial IntPtr PEM_read_bio_X509(IntPtr bio, IntPtr x, IntPtr cb, IntPtr u);
    [LibraryImport(Crypto)] public static partial IntPtr PEM_read_bio_PrivateKey(IntPtr bio, IntPtr x, IntPtr cb, IntPtr u);
    [LibraryImport(Crypto)] public static partial void X509_free(IntPtr x509);
    [LibraryImport(Crypto)] public static partial void EVP_PKEY_free(IntPtr pkey);
    [LibraryImport(Crypto)] public static partial int X509_STORE_add_cert(IntPtr store, IntPtr x509);

    [LibraryImport(Crypto)] public static partial ulong ERR_get_error();
    [LibraryImport(Crypto)] public static partial void ERR_error_string_n(ulong e, byte* buf, nuint len);

    // ---- helpers ----

    /// <summary>Bytes queued in a memory BIO awaiting read (the <c>BIO_pending</c> macro).</summary>
    public static long BIO_pending(IntPtr bio) => BIO_ctrl(bio, BIO_CTRL_PENDING, 0, IntPtr.Zero);

    /// <summary>Is kernel TLS send-offload active on this BIO? (<c>BIO_get_ktls_send</c> macro.)</summary>
    public static bool BIO_get_ktls_send(IntPtr bio) => BIO_ctrl(bio, BIO_CTRL_GET_KTLS_SEND, 0, IntPtr.Zero) > 0;

    /// <summary>Is kernel TLS recv-offload active on this BIO? (<c>BIO_get_ktls_recv</c> macro.)</summary>
    public static bool BIO_get_ktls_recv(IntPtr bio) => BIO_ctrl(bio, BIO_CTRL_GET_KTLS_RECV, 0, IntPtr.Zero) > 0;

    /// <summary>Drain and format the OpenSSL error queue (for diagnostics on a fault).</summary>
    public static string DrainErrors()
    {
        var sb = new System.Text.StringBuilder();
        ulong e;
        Span<byte> buf = stackalloc byte[256];
        while ((e = ERR_get_error()) != 0)
        {
            fixed (byte* p = buf) ERR_error_string_n(e, p, (nuint)buf.Length);
            int len = buf.IndexOf((byte)0); if (len < 0) len = buf.Length;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(System.Text.Encoding.ASCII.GetString(buf[..len]));
        }
        return sb.Length == 0 ? "(no OpenSSL error detail)" : sb.ToString();
    }
}
#endif
