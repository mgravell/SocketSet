// Embedded Garnet on the SocketSet transport — the GarnetDemo plays the role AspNetDemo plays for
// Kestrel: the smallest host that lets the rigs point at a real server, with a banner they can gate on.
//
// usage: GarnetDemo [--port N] [--backend iocp|rio|io-uring|epoll|managed] [--shards N]
//                   [--stock] [--tls] [--ktls] [--tls-dir D] [--listen-uds P]
//   --stock hosts Garnet's OWN GarnetServerTcp instead (the SAEA layer) on the same options, so a
//   stock-vs-socketset A/B is one flag on one binary — the application-held-constant discipline again.
//
// WINDOWS (2026-08-07). This was Linux-only by accident rather than by design: the LIBRARY
// (SocketSet.Garnet) is platform-neutral and always was — it takes whatever SocketSetOptions you hand
// it — but this demo hard-coded the io_uring backend, an OpenSSL provider and an absolute /home path, so
// on Windows it built fine and died at construction with a PlatformNotSupportedException from three
// frames deep. Three things changed and nothing else:
//   - --backend now accepts iocp and rio, and DEFAULTS per OS (iocp on Windows, io-uring on Linux), so
//     the bare command works on both. A backend that cannot exist on this OS is refused BY NAME here
//     rather than throwing from inside the factory.
//   - TLS picks the engine this OS actually has: SChannel on Windows, OpenSSL (optionally kTLS)
//     elsewhere. Same split as bench/GateBackends.cs, for the same reason.
//   - The certificate is DISCOVERED, and if nothing is found it is GENERATED in-process rather than
//     failing. Which one happened is in the banner, because a generated certificate and a file one are
//     not interchangeable for a TLS A/B (see DemoCertificate: key algorithm and size move TLS numbers,
//     so both legs must present the same key or the measurement is of the certificate).
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Garnet;
using Garnet.server;
using Garnet.server.TLS;
using SocketSets;
using SocketSets.AspNet;   // DemoCertificate — linked, not referenced; see GarnetDemo.csproj
using SocketSets.Garnet;

int port = 6390, shards = 8;
string? backendArg = null;                 // null == "this OS's default", resolved below
bool stock = false, tls = false, ktls = false;
string? tlsDir = null;                     // null == discover, then fall back to a generated cert
string? listenUds = null;                  // /path or @abstract; stock and socketset legs both support either
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
        case "--shards" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): shards = s; i++; break;
        case "--backend" when i + 1 < args.Length: backendArg = args[++i]; break;
        case "--stock": stock = true; break;
        case "--tls": tls = true; break;
        // The visitor toggle: kTLS needs kernel + OpenSSL 3.0+/3.2+ (TX/RX) support, and its BIG win
        // (NIC inline offload) needs hardware this loopback box does not have -- so if you have the
        // right lab, this is the one flag to flip. Capability is DISCOVERED, not assumed: watch for the
        // provider's [ktls] banner lines reporting what actually engaged (SS_KTLS_NO_RX=1 forces
        // TX-only for A/Bs). Implies --tls; refused with --stock (SslStream has no kTLS path).
        case "--ktls": ktls = tls = true; break;
        case "--tls-dir" when i + 1 < args.Length: tlsDir = args[++i]; break;
        case "--listen-uds" when i + 1 < args.Length: listenUds = args[++i]; break;
        default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return 1;
    }
}

bool isWindows = OperatingSystem.IsWindows();
string backend = backendArg ?? (isWindows ? "iocp" : "io-uring");

// Refuse an impossible combination BY NAME, here, rather than letting the factory throw from three
// frames down. "--backend rio on Linux" should read as a usage error, not as a platform stack trace.
static int Refuse(string why) { Console.Error.WriteLine(why); return 1; }

if ((backend is "iocp" or "rio") && !isWindows) return Refuse($"--backend {backend} needs Windows.");
if ((backend is "io-uring" or "epoll") && isWindows) return Refuse($"--backend {backend} needs Linux.");
if (backend is not ("iocp" or "rio" or "io-uring" or "epoll" or "managed"))
    return Refuse($"unknown backend '{backend}' (iocp|rio|io-uring|epoll|managed)");
if (ktls && isWindows) return Refuse("--ktls is the OpenSSL/Linux path; Windows terminates TLS on SChannel.");
if (ktls && stock) return Refuse("--ktls is the SocketSet/OpenSSL path; --stock uses SslStream, which has no kTLS. Drop one.");

// The abstract form differs per stack: .NET's UnixDomainSocketEndPoint wants a leading NUL byte for the
// abstract namespace, while SocketSet maps a leading '@' itself. One user-facing spelling (@name), two
// internal spellings — the kernel-side name is identical, which is what the benchmark dials.
if (listenUds is not null)
{
    // Windows has AF_UNIX but only the PATHNAME form; the abstract namespace is a Linux invention and
    // there is nothing to map it onto. And RIO cannot do AF_UNIX at all (it is TCP/UDP only), which is
    // why the library routes AF_UNIX to IOCP — say so instead of failing at bind.
    if (listenUds.StartsWith('@') && isWindows)
        return Refuse("@abstract unix sockets are Linux-only; use a pathname, or drop --listen-uds.");
    if (backend == "rio" && !stock)
        return Refuse("--backend rio cannot serve AF_UNIX (RIO is TCP/UDP only); use --backend iocp.");
}

EndPoint endpoint;
if (listenUds is null)
{
    endpoint = new IPEndPoint(IPAddress.Loopback, port);
}
else if (listenUds.StartsWith('@'))
{
    endpoint = stock
        ? new System.Net.Sockets.UnixDomainSocketEndPoint("\0" + listenUds[1..])
        : new System.Net.Sockets.UnixDomainSocketEndPoint(listenUds);
}
else
{
    if (File.Exists(listenUds)) File.Delete(listenUds); // stale pathname socket refuses the bind
    endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(listenUds);
}

// ---- the certificate, and WHERE it came from -------------------------------------------------------
// Both legs must present the SAME key or a TLS A/B measures the certificate rather than the stack. The
// directory form keeps that guarantee across the two processes (stock reads cert.pfx, ours reads the pem
// pair — same key material, prepared once); the generated form keeps it within one process.
DemoCertificate? generated = null;
string? tempPfx = null;
string certSource;

if (tls)
{
    tlsDir ??= FindTlsDir();
    if (tlsDir is null)
    {
        // No prepared material: generate one key and hand every leg the same one. This is the path a
        // fresh Windows box takes, and it is NOT silently equivalent to the directory path — hence the
        // banner token.
        generated = DemoCertificate.Create();
        certSource = $"generated ({generated.Describe()})";
    }
    else
    {
        certSource = $"dir:{tlsDir}";
    }
}
else
{
    certSource = "none";
}

var garnetOpts = new GarnetServerOptions { EndPoints = [endpoint] };
if (tls && stock)
{
    // Garnet's own TLS wants a PFX on DISK. When the cert was generated in-process there is no file, so
    // write one for this run and delete it on the way out.
    string pfxPath, pfxPassword;
    if (generated is not null)
    {
        pfxPassword = Guid.NewGuid().ToString("N");
        tempPfx = pfxPath = Path.Combine(Path.GetTempPath(), $"garnet-demo-{Environment.ProcessId}.pfx");
        File.WriteAllBytes(pfxPath, generated.Certificate.Export(X509ContentType.Pfx, pfxPassword));
    }
    else
    {
        pfxPath = Path.Combine(tlsDir!, "cert.pfx");
        pfxPassword = "";
    }

    garnetOpts.TlsOptions = new GarnetTlsOptions(
        certFileName: pfxPath, certPassword: pfxPassword,
        clientCertificateRequired: false, certificateRevocationCheckMode: X509RevocationMode.NoCheck,
        issuerCertificatePath: null, certSubjectName: null, certificateRefreshFrequency: 0,
        enableCluster: false, clientTargetHost: null);
}

IGarnetServer[]? servers = null;
if (stock && listenUds is not null)
{
    // The embedding path (servers == null) unconditionally File.Delete's opts.UnixSocketPath for UDS
    // endpoints, which is IMPOSSIBLE for an abstract name (a NUL byte cannot appear in a filesystem
    // path) -- a small upstream gap: embedded UDS assumes pathname. GarnetServerTcp itself is fine with
    // an abstract endpoint (it only touches the path for chmod, guarded on a permission being set), so
    // construct it directly. Pathnames get their stale-file delete above either way.
    servers = [new GarnetServerTcp(endpoint, 0, tls ? garnetOpts.TlsOptions : null,
                                   garnetOpts.NetworkSendThrottleMax, garnetOpts.NetworkConnectionLimit)];
}
else if (!stock)
{
    var factory = backend switch
    {
        "iocp" => SocketSetFactory.WindowsIocp,
        "rio" => SocketSetFactory.WindowsRio,
        "io-uring" => SocketSetFactory.IoUring,
        "epoll" => SocketSetFactory.Epoll,
        "managed" => SocketSetFactory.Managed,
        _ => throw new ArgumentException($"unknown backend '{backend}'"), // unreachable; refused above
    };
    var ssOptions = new SocketSetOptions { Factory = factory, Shards = shards };
    if (tls)
    {
        // In-transport TLS: the handler and Garnet's whole session stack see plaintext, and Garnet's own
        // TLS machinery stays idle -- which is what makes the A/B purely their-TLS-vs-ours. Which ENGINE
        // does it is per-OS, the same split bench/GateBackends.cs makes.
        ssOptions.Tls = isWindows
            ? new SocketSets.Tls.SChannel.SChannelTlsProvider(
                serverCertificate: generated?.Certificate ?? LoadPfx(Path.Combine(tlsDir!, "cert.pfx")))
            : new SocketSets.Tls.OpenSsl.OpenSslTlsProvider(
                generated?.CertPem ?? File.ReadAllText(Path.Combine(tlsDir!, "cert.pem")),
                generated?.KeyPem ?? File.ReadAllText(Path.Combine(tlsDir!, "key.pem")),
                kernelOffload: ktls);
    }
    servers = [new SocketSetGarnetServer(endpoint, ssOptions)];
}

using var server = new GarnetServer(garnetOpts, loggerFactory: null, servers: servers);
server.Start();

// TRUST THE BANNER: the rigs gate on this line, not on the flags they passed. The `transport=... tls=...`
// pair must stay ADJACENT and spelled exactly as-is — bench/run-mux-ab.sh greps for the contiguous
// string "transport=garnet-saea tls=off". New tokens go on the END.
Console.WriteLine($"[garnet-demo] transport={(stock ? "garnet-saea" : $"socketset/{backend} shards={shards}")} " +
                  $"tls={(tls ? (stock ? "sslstream" : ktls ? "openssl+ktls" : isWindows ? "schannel" : "openssl") : "off")} " +
                  $"listen={listenUds ?? port.ToString()} cert={certSource}");
Console.WriteLine("ready");

AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
Console.CancelKeyPress += (_, _) => Cleanup();
Thread.Sleep(Timeout.Infinite);
Cleanup();
return 0;

void Cleanup()
{
    generated?.Dispose();                                    // removes the persisted CNG key on Windows
    if (tempPfx is not null) { try { File.Delete(tempPfx); } catch { } }
}

// Walk up from the binary looking for the prepared material, so the path is not hard-coded to one
// developer's home directory (it was `/home/marc/code/SocketSet/bench/.tools/tls-demo`, which made the
// default unusable anywhere else). The Linux rigs pass no --tls-dir and depend on finding exactly this
// directory, so discovery has to land on the same place they always used.
static string? FindTlsDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (; dir is not null; dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, "bench", ".tools", "tls-demo");
        if (Directory.Exists(candidate)) return candidate;
    }
    return null;
}

static X509Certificate2 LoadPfx(string path)
    => X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), "", X509KeyStorageFlags.Exportable);
