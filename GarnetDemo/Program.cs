// Embedded Garnet on the SocketSet transport — the GarnetDemo plays the role AspNetDemo plays for
// Kestrel: the smallest host that lets the rigs point at a real server, with a banner they can gate on.
//
// usage: GarnetDemo [--port N] [--backend io-uring|epoll|managed] [--shards N] [--stock] [--tls] [--ktls]
//   --stock hosts Garnet's OWN GarnetServerTcp instead (the SAEA layer) on the same options, so a
//   stock-vs-socketset A/B is one flag on one binary — the application-held-constant discipline again.
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Garnet;
using Garnet.server;
using Garnet.server.TLS;
using SocketSets;
using SocketSets.Garnet;

int port = 6390, shards = 8;
string backend = "io-uring";
bool stock = false, tls = false, ktls = false;
// Both legs use the SAME key material (bench/.tools/tls-demo): stock consumes the pfx via Garnet's
// SslStream-based path, ours consumes the pem pair in-transport. Identical cert = identical handshake
// work, so a TLS A/B compares the STACKS, not the certificates.
string tlsDir = "/home/marc/code/SocketSet/bench/.tools/tls-demo";
string? listenUds = null; // /path or @abstract; stock and socketset legs both support either
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
        case "--shards" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): shards = s; i++; break;
        case "--backend" when i + 1 < args.Length: backend = args[++i]; break;
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

// The abstract form differs per stack: .NET's UnixDomainSocketEndPoint wants a leading NUL byte for the
// abstract namespace, while SocketSet maps a leading '@' itself. One user-facing spelling (@name), two
// internal spellings — the kernel-side name is identical, which is what the benchmark dials.
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
var garnetOpts = new GarnetServerOptions { EndPoints = [endpoint] };
if (ktls && stock)
{
    Console.Error.WriteLine("--ktls is the SocketSet/OpenSSL path; --stock uses SslStream, which has no kTLS. Drop one.");
    return 1;
}
if (tls && stock)
{
    garnetOpts.TlsOptions = new GarnetTlsOptions(
        certFileName: Path.Combine(tlsDir, "cert.pfx"), certPassword: "",
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
        "io-uring" => SocketSetFactory.IoUring,
        "epoll" => SocketSetFactory.Epoll,
        "managed" => SocketSetFactory.Managed,
        _ => throw new ArgumentException($"unknown backend '{backend}'"),
    };
    var ssOptions = new SocketSetOptions { Factory = factory, Shards = shards };
    if (tls)
    {
        // In-transport TLS: the handler and Garnet's whole session stack see plaintext, and Garnet's own
        // TLS machinery stays idle -- which is what makes the A/B purely their-TLS-vs-ours.
        ssOptions.Tls = new SocketSets.Tls.OpenSsl.OpenSslTlsProvider(
            File.ReadAllText(Path.Combine(tlsDir, "cert.pem")),
            File.ReadAllText(Path.Combine(tlsDir, "key.pem")),
            kernelOffload: ktls);
    }
    servers = [new SocketSetGarnetServer(endpoint, ssOptions)];
}

using var server = new GarnetServer(garnetOpts, loggerFactory: null, servers: servers);
server.Start();

// TRUST THE BANNER: the rigs gate on this line, not on the flags they passed.
Console.WriteLine($"[garnet-demo] transport={(stock ? "garnet-saea" : $"socketset/{backend} shards={shards}")} " +
                  $"tls={(tls ? (stock ? "sslstream" : ktls ? "openssl+ktls" : "openssl") : "off")} " +
                  $"listen={listenUds ?? port.ToString()}");
Console.WriteLine("ready");
Thread.Sleep(Timeout.Infinite);
return 0;
