using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SmokeTest;
using SocketSets;
using SocketSets.Tls;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine("### UNHANDLED ###\n" + e.ExceptionObject);

bool server = false;
int clientCount = 0;
int seconds = 0; // 0 == run until Ctrl+C
string? uds = null; // UDS name (e.g. "@socketset-smoke" for the abstract namespace)
int size = 512; // message size
int window = 1; // client send window: 1 = ping/pong, N = bounded pipeline, int.MaxValue = unbounded
bool poke = false; // server echoes out-of-band via Connection.Send from a background thread
bool adopt = false; // bind+listen a socket ourselves, then hand its handle to ListenHandle (socket-activation style)
int verify = 0;    // >0: run the out-of-band Send content-verification harness with this payload size
long verifyEcho = 0; // >0: run the bidirectional echo content-verification harness, round-tripping this many bytes
int closeAfter = 0; // >0: each client sends this many messages then closes (graceful-drain test)
int loops = 1;      // drain test: repeat this many connect→drain cycles (0 = forever, until Ctrl+C)
int churn = 0;      // >0: overlapping-churn soak for this many seconds (reconnect while teardowns fly)
string? cpus = null; // CPU affinity spec, e.g. "0-5" or "0,2,4" or "0-3,8"
string host = "127.0.0.1"; // client connect target (server always binds Any)
int port = 10000;
bool resp = false;          // >0: run the dumb RESP PING client against --host/--port
string? tlsTrust = null;    // path to a PEM cert the TLS client should trust (external-server test)
string tlsHost = "localhost"; // TargetHost for SNI + hostname verification
bool ktls = false;          // use kernel-TLS (kTLS) offload (io_uring only)
var options = new SocketSetOptions();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cpus" when i + 1 < args.Length:
            cpus = args[i + 1];
            break;
        case ("-c" or "--client") when i + 1 < args.Length && int.TryParse(args[i + 1], out var tmp):
            clientCount = tmp;
            break;
        case "-s":
        case "--server":
            server = true;
            break;
        case ("-t" or "--seconds") when i + 1 < args.Length && int.TryParse(args[i + 1], out var secs):
            seconds = secs;
            break;
        case ("-u" or "--uds") when i + 1 < args.Length:
            uds = args[i + 1];
            break;
        case ("-z" or "--size") when i + 1 < args.Length && int.TryParse(args[i + 1], out var sz):
            size = sz;
            break;
        case ("-n" or "--shards") when i + 1 < args.Length && int.TryParse(args[i + 1], out var sh):
            options.Shards = sh;
            break;
        case ("-p" or "--pin") when i + 1 < args.Length && bool.TryParse(args[i + 1], out var pin):
            options.PinWorkerThreads = pin;
            break;
        case ("-e" or "--entries") when i + 1 < args.Length && int.TryParse(args[i + 1], out var ent):
            options.EntriesPerShard = ent;
            break;
        case "-m":
        case "--managed":
            options.Factory = SocketSetFactory.Managed;
            break;
#if NET
        case "--iocp":
            options.Factory = SocketSetFactory.WindowsIocp;
            break;
        case "--tls":
            // Wrap connections in the no-crypto identity TLS filter — a wiring/plumbing test (a real
            // 1-RTT handshake gate + decrypt-in/encrypt-out, but the "crypto" is a passthrough copy).
            options.Tls = new IdentityTlsProvider();
            break;

#if NET
        case "--tls-ssl":
            // Real OpenSSL TLS: a throwaway self-signed cert for "localhost", server configured with it,
            // client trusts exactly it with full verification (SNI + hostname). Loopback self-test only.
            options.Tls = SocketSets.Tls.OpenSsl.OpenSslTlsProvider.CreateSelfSignedLoopback("localhost");
            options.TlsClient.TargetHost = "localhost";
            break;

        case "--ktls":
            // Same as --tls-ssl but with kernel-TLS offload (io_uring only; needs `sudo modprobe tls`).
            options.Tls = SocketSets.Tls.OpenSsl.OpenSslTlsProvider.CreateSelfSignedLoopback("localhost", kernelOffload: true);
            options.TlsClient.TargetHost = "localhost";
            ktls = true;
            break;

        case "--ktls-trust" when i + 1 < args.Length: // kTLS client trusting this cert (external server)
            tlsTrust = args[++i];
            ktls = true;
            break;
#endif

        case "--rio":
            options.Factory = SocketSetFactory.WindowsRio;
            break;
#endif
        case "--pipeline":
            window = int.MaxValue; // unbounded (deadlock-prone on a symmetric echo — for comparison)
            break;
        case "--poke":
            poke = true;
            break;
        case "--adopt":
            adopt = true;
            break;
        case "--verify" when i + 1 < args.Length && int.TryParse(args[i + 1], out var vp):
            verify = Math.Max(1, vp);
            break;
        case "--verify-echo" when i + 1 < args.Length && long.TryParse(args[i + 1], out var ve):
            verifyEcho = Math.Max(1, ve);
            break;
        case "--close-after" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ca):
            closeAfter = Math.Max(1, ca);
            break;
        case "--loops" when i + 1 < args.Length && int.TryParse(args[i + 1], out var lp):
            loops = Math.Max(0, lp); // 0 = forever
            break;
        case "--churn" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ch):
            churn = Math.Max(1, ch);
            break;
        case "--sockets" when i + 1 < args.Length && int.TryParse(args[i + 1], out var sk):
            options.SocketsPerShard = Math.Max(1, sk);
            break;
        case "--window" when i + 1 < args.Length && int.TryParse(args[i + 1], out var w):
            window = Math.Max(1, w);
            break;
        case "--host" when i + 1 < args.Length:
            host = args[i + 1];
            break;
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pt):
            port = pt;
            break;

        // --- scenario aliases: set a bundle of workload-shape defaults for a common test mode. They
        // only touch the shape knobs (window/size/close/churn), never scale (-c/-n) or backend, and any
        // explicit flag placed AFTER the alias overrides it (left-to-right parse). ---
        case "--latency":   // no-pipeline ping/pong, small messages — per-op latency
            window = 1; size = 64;
            break;
        case "--bandwidth": // deep pipe, fat messages — saturation throughput
            window = 64; size = 4096;
            break;
        case "--accept":    // one round-trip per socket then drop + reconnect — accept/connect/teardown churn
            closeAfter = 1; churn = 10;
            break;
        case "--reset-close": // abortive close (RST, no TIME_WAIT) — makes TCP churn measure the backend, not the port recycler
            options.ResetOnClose = true;
            break;

        case "--resp": // dumb RESP client: connect, PING, expect +PONG (against Garnet/Redis)
            resp = true;
            break;
        case "--tls-trust" when i + 1 < args.Length: // PEM cert the TLS client trusts (external server)
            tlsTrust = args[++i];
            break;
        case "--tls-host" when i + 1 < args.Length: // SNI + hostname-verify name (default localhost)
            tlsHost = args[++i];
            break;
    }
}

if (resp)
{
    RunRespPing(options, host, port, tlsTrust, tlsHost, ktls);
    return;
}

if (args.Contains("--http"))
{
    // Bare SocketSet HTTP responder (no Kestrel/pipes) — RST-truncation isolation harness.
    using var http = new HttpBench(options);
    http.Listen(new IPEndPoint(IPAddress.Any, port));
    Console.WriteLine($"http-bench: backend={options.Factory.GetType().Name} listening on {port} (Ctrl+C to stop)");
    var httpStop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; httpStop.Set(); };
    httpStop.Wait();
    return;
}

#if NET
if (args.Contains("--ktls-spike"))
{
    var (ok, report) = SocketSets.Tls.OpenSsl.KtlsProbe.Run();
    Console.WriteLine(report);
    Console.WriteLine($"ktls-spike => {(ok ? "PASS" : "FAIL")}");
    return;
}
#endif

if (verify > 0)
{
    RunVerify(options, verify, port);
    return;
}

if (verifyEcho > 0)
{
    RunEchoVerify(options, verifyEcho, size, window, port);
    return;
}

static void RunRespPing(SocketSetOptions opts, string host, int port, string? tlsTrust, string tlsHost, bool ktls)
{
#if NET
    if (tlsTrust is not null)
    {
        // Client-only TLS: trust exactly the given cert (the server's), verify chain + hostname.
        opts.Tls = new SocketSets.Tls.OpenSsl.OpenSslTlsProvider(
            trustCertPem: File.ReadAllText(tlsTrust), verifyServer: true, kernelOffload: ktls);
        opts.TlsClient.TargetHost = tlsHost;
    }
#endif
    using var set = new RespPing(opts);
    var ep = new IPEndPoint(IPAddress.Parse(host), port);
    Console.WriteLine($"resp-ping: {host}:{port} tls={(tlsTrust is not null ? $"on (trust={Path.GetFileName(tlsTrust)}, host={tlsHost})" : "off")}");
    set.Connect(ep);

    bool completed = set.Wait(TimeSpan.FromSeconds(10));
    string reply = set.Reply.Replace("\r", "\\r").Replace("\n", "\\n");
    Console.WriteLine($"resp-ping: reply=\"{reply}\" => {(completed && set.Ok ? "PASS" : "FAIL")}");
}

static void RunVerify(SocketSetOptions opts, int payloadLen, int port)
{
    const int seg = 7000; // multi-segment chunk size for the sequence send (straddles page boundaries)
    using var set = new SendVerify(opts, payloadLen, seg);
    var ep = new IPEndPoint(IPAddress.Loopback, port);
    set.Listen(ep);
    set.Connect(ep);
    Console.WriteLine($"verify: backend={opts.Factory.GetType().Name} payload={payloadLen} seg={seg} expected={set.Expected}");

    var sw = Stopwatch.StartNew();
    while (set.ServerConn is null && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
    if (set.ServerConn is null) { Console.WriteLine("verify: FAIL (no connection accepted)"); return; }

    set.FireSends();

    while (set.Received < set.Expected && sw.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(10);

    bool ok = set.Received == set.Expected && set.Mismatches == 0;
    Console.WriteLine($"verify: received={set.Received}/{set.Expected} mismatches={set.Mismatches} => {(ok ? "PASS" : "FAIL")}");
}

static void RunEchoVerify(SocketSetOptions opts, long totalBytes, int chunk, int window, int port)
{
    using var set = new EchoVerify(opts, totalBytes, chunk, window);
    var ep = new IPEndPoint(IPAddress.Loopback, port);
    set.Listen(ep);
    set.Connect(ep);
    Console.WriteLine($"verify-echo: backend={opts.Factory.GetType().Name} total={totalBytes} chunk={chunk} window={window}");

    var sw = Stopwatch.StartNew();
    while (set.RoundTripped < set.Expected && sw.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(10);

    bool ok = set.RoundTripped == set.Expected && set.ClientMismatches == 0 && set.ServerMismatches == 0;
    Console.WriteLine($"verify-echo: roundtripped={set.RoundTripped}/{set.Expected} " +
        $"clientMismatch={set.ClientMismatches} serverMismatch={set.ServerMismatches} => {(ok ? "PASS" : "FAIL")}");
}

if (!server && clientCount == 0)
{
    Console.WriteLine("usage: SmokeTest -s [-c N] [-t seconds] [-u name] [-z bytes] [-n shards] [-p true|false]");
    Console.WriteLine("  -s / --server     run the echo server");
    Console.WriteLine("  -c / --client N   open N client connections that ping-pong");
    Console.WriteLine("  -t / --seconds S  stop after S seconds (default: run until Ctrl+C)");
    Console.WriteLine("  --host IP         client connect target (default 127.0.0.1; server always binds Any)");
    Console.WriteLine("  --port N          TCP port (default 10000)");
    Console.WriteLine("  -u / --uds name   use a Unix domain socket (e.g. @foo for abstract) instead of TCP");
    Console.WriteLine("  -z / --size N     ping-pong message size in bytes (default 512)");
    Console.WriteLine("  -n / --shards N   number of shards / worker threads (default 4)");
    Console.WriteLine("  -p / --pin B      pin worker threads to CPUs, true|false (default true)");
    Console.WriteLine("  -e / --entries N  io_uring SQ entries per shard (default 4096; lower if RLIMIT_MEMLOCK is tight)");
    Console.WriteLine("  -m / --managed    force the portable managed-socket fallback (default auto-detects)");
    Console.WriteLine("  --cpus SPEC       pin this process to CPUs (e.g. 0-5 or 0,2,4) — covers shards, thread pool and GC");
    Console.WriteLine("  --window N        client keeps up to N messages in flight (1=ping/pong default, N=bounded pipeline)");
    Console.WriteLine("  --pipeline        unbounded in-flight (throughput, but can wedge a symmetric echo; use --window instead)");
    Console.WriteLine("  --poke            server echoes out-of-band via Connection.Send from a background thread");
    Console.WriteLine("  --adopt           bind the listener here and hand its handle to ListenHandle (socket-activation style)");
    Console.WriteLine("  --verify N        run the out-of-band Send correctness harness with an N-byte payload");
    Console.WriteLine("  --verify-echo N   round-trip N bytes of a known pattern through the echo path and");
    Console.WriteLine("                    byte-verify both legs (-z sets chunk size, --window the pipe depth)");
    Console.WriteLine("  --close-after N   each client sends N messages then closes (graceful-drain lifecycle test)");
    Console.WriteLine("  --loops N         repeat the drain cycle N times (0 = forever); a socket-churn soak");
    Console.WriteLine("  --churn S         overlapping-churn soak for S seconds: keep the population topped up so");
    Console.WriteLine("                    reconnects race in-flight teardowns (stresses slot reuse / ABA)");
    Console.WriteLine("  --sockets N       sockets per shard (small = tight table = immediate slot reuse under churn)");
    Console.WriteLine("  --iocp / --rio    force the Windows IOCP / RIO backend (RIO is TCP-only; default auto-detects)");
    Console.WriteLine("  scenario aliases (set workload-shape defaults; any later flag overrides them):");
    Console.WriteLine("  --latency         no-pipeline ping/pong, small messages (window=1, size=64) — per-op latency");
    Console.WriteLine("  --bandwidth       deep pipe, fat messages (window=64, size=4096) — saturation throughput");
    Console.WriteLine("  --accept          one round-trip per socket then drop + reconnect (close-after=1, churn=10s)");
    return;
}

if (cpus is not null) PinToCpus(cpus);

// Constrain the whole process (io_uring shard threads, the managed thread pool, and GC
// alike) to a CPU set. Doing it here — before the SocketSet spins anything up — gives a
// single self-contained way to hand each role a fixed core budget for A/B benchmarking.
static void PinToCpus(string spec)
{
    long mask = 0;
    foreach (var part in spec.Split(','))
    {
        var range = part.Split('-');
        int lo = int.Parse(range[0]);
        int hi = range.Length > 1 ? int.Parse(range[range.Length - 1]) : lo;
        for (int c = lo; c <= hi; c++) mask |= 1L << c;
    }

    try
    {
        using var proc = Process.GetCurrentProcess();
#pragma warning disable CA1416 // checked
        proc.ProcessorAffinity = (IntPtr)mask;
#pragma warning restore CA1416
        Console.WriteLine($"pinned to CPUs {spec} (mask 0x{mask:x})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"warning: could not set CPU affinity ({ex.GetType().Name}: {ex.Message}); launch under taskset instead");
    }
}

if (size > options.BufferPageSize)
{
    // The response rides in a single read/write buffer page; keep the demo honest.
    Console.WriteLine($"note: clamping --size {size} to buffer page size {options.BufferPageSize}");
    size = options.BufferPageSize;
}

// The server always binds Any (so it accepts on the LAN, not just loopback); the client
// dials --host. UDS is local-only, so it uses one endpoint for both.
EndPoint listenEp, connectEp;
#if NET
if (uds is not null)
{
    listenEp = connectEp = new UnixDomainSocketEndPoint(uds);
}
else
{
    listenEp = new IPEndPoint(IPAddress.Any, port);
    connectEp = new IPEndPoint(IPAddress.Parse(host), port);
}
#else
if (uds is not null) Console.WriteLine("note: UDS not supported on this target framework; falling back to TCP");
listenEp = new IPEndPoint(IPAddress.Any, port);
connectEp = new IPEndPoint(IPAddress.Parse(host), port);
#endif
using var set = new EchoServer(options) { GreetingSize = size, Window = window, PokeMode = poke, CloseAfterMessages = closeAfter };
string mode = window == 1 ? "ping/pong" : window == int.MaxValue ? "pipeline(unbounded)" : $"pipeline(window={window})";
Console.WriteLine(
    $"backend={options.Factory.GetType().Name} transport={(uds is null ? "tcp" : "uds")} " +
    $"mode={mode} size={size} shards={options.Shards} pin={options.PinWorkerThreads} poke={poke}");

#if NET
if (server && adopt)
{
    // Socket-activation style: bind+listen a socket here, then hand its raw handle to the set and
    // relinquish ownership (SetHandleAsInvalid, so .NET won't also close the fd the set now owns).
    // NOTE: the address is the caller's to get right — e.g. .NET binds a UDS path literally, whereas
    // io_uring Connect maps a leading '@' to the abstract namespace; use TCP or a filesystem UDS path.
    var lsock = new Socket(listenEp.AddressFamily, SocketType.Stream,
        listenEp is IPEndPoint ? ProtocolType.Tcp : ProtocolType.Unspecified);
    if (listenEp is IPEndPoint) lsock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    lsock.Bind(listenEp);
    lsock.Listen(512);
    nint h = lsock.Handle;
    lsock.SafeHandle.SetHandleAsInvalid(); // hand the fd to the set; don't let this wrapper close it
    set.ListenHandle(h, EchoServer.ServerToken);
    Console.WriteLine($"adopted listener handle {h} on {listenEp}");
}
else
#endif
if (server && adopt)
{
    Console.WriteLine("note: --adopt requires .NET 5+ (SafeSocketHandle); falling back to Listen");
    set.Listen(listenEp, EchoServer.ServerToken);
    Console.WriteLine($"listening on {listenEp}");
}
else if (server)
{
    set.Listen(listenEp, EchoServer.ServerToken);
    Console.WriteLine($"listening on {listenEp}");
}

// TIME_WAIT warning: rapid connect/close over TCP with a graceful (client-active) close piles the
// client's ephemeral ports into TIME_WAIT (~4 min on Windows, no tcp_tw_reuse), so back-to-back churn
// runs share a draining pool and report artificially low rates. UDS has no TIME_WAIT; --reset-close
// (RST) sidesteps it. Warn so the numbers aren't misread.
if ((churn > 0 || closeAfter > 0) && clientCount > 0 && uds is null && !options.ResetOnClose)
{
    Console.WriteLine("warning: TCP socket churn accumulates TIME_WAIT on the client ephemeral pool — " +
        "back-to-back runs will under-report. Use --reset-close (RST), test over UDS (-u), or let " +
        "TIME_WAIT drain between runs (netstat) for meaningful accept rates.");
}

// Graceful-drain lifecycle test / socket-churn soak: each loop opens the clients, each client sends
// N messages then closes, the server tears down its side on EOF, and we wait for everything to return
// to zero live connections before the next loop. Asserts each loop produces exactly 2 closes/client
// (client side + server-accepted side) and the listener stays up throughout.
if (closeAfter > 0 && churn > 0)
{
    // Overlapping-churn soak: keep ~clientCount connections live for `churn` seconds. As clients hit
    // their message quota and close, we immediately reconnect into the freed slots — so reconnect
    // InitClient (an external thread) races the loop thread still reaping the previous tenant's
    // in-flight ops. With a tight --sockets table the same slots recycle instantly, maximizing the
    // slot-reuse/ABA stress. A manifested ABA shows as a fault, a wedge (never drains), or a
    // close-accounting mismatch (closed != 2 × connected).
    using var churnStop = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; churnStop.Set(); };
    Console.WriteLine(
        $"churn: overlapping soak for {churn}s, target ~{clientCount} live, {closeAfter} msg(s)/conn, " +
        $"sockets/shard={options.SocketsPerShard} shards={options.Shards}");

    long connected = 0;
    var csw = Stopwatch.StartNew();
    double churnSecs = 0;
    long nextReport = 1;
    while (csw.Elapsed < TimeSpan.FromSeconds(churn) && !churnStop.IsSet)
    {
        int want = clientCount - (int)set.LiveConnections;
        for (int j = 0; j < want; j++)
        {
            try { set.Connect(connectEp); connected++; }
            catch { break; } // table momentarily full — back off and let teardowns free slots
        }
        if (csw.Elapsed.TotalSeconds >= nextReport)
        {
            nextReport = (long)csw.Elapsed.TotalSeconds + 1;
            Console.WriteLine($"churn: t={csw.Elapsed.TotalSeconds,4:0}s connected={connected:n0} live={set.LiveConnections} closed={set.Closed:n0}");
        }
        Thread.Sleep(1);
    }
    churnSecs = csw.Elapsed.TotalSeconds;

    // Stop connecting and let everything drain. The real invariant is live -> 0: every connection the
    // app saw open, it must see closed. (closed == 2 × connected only holds with a table big enough
    // that server accepts never drop; a tight --sockets deliberately drops accepts, so we don't assert
    // on closed there — but live must still reach 0.) Watch the drain to distinguish a wedge from slow.
    var drain = Stopwatch.StartNew();
    long lastLive = -1;
    while (set.LiveConnections != 0 && drain.Elapsed < TimeSpan.FromSeconds(30))
    {
        long l = set.LiveConnections;
        if (l != lastLive) { Console.WriteLine($"churn: draining t={drain.Elapsed.TotalSeconds,4:0}s live={l} (client={set.LiveClient} server={set.LiveServer})"); lastLive = l; }
        Thread.Sleep(20);
    }
    long liveEnd = set.LiveConnections;
    int openFds = -1;
    try { openFds = System.IO.Directory.GetFileSystemEntries("/proc/self/fd").Length; } catch { }
    bool ok = liveEnd == 0;
    double rate = churnSecs > 0 ? connected / churnSecs : 0;
    Console.WriteLine(
        $"churn: done — connected={connected:n0} in {churnSecs:0.0}s = {rate:n0} conn/s " +
        $"(≈{rate * 2:n0} sockets/s open+close) closed={set.Closed:n0} live={liveEnd} " +
        $"(client={set.LiveClient} server={set.LiveServer}) open-fds={openFds} " +
        $"round-trips={set.RoundTripBytes:n0} => {(ok ? "PASS" : "FAIL (wedged)")}");
    return;
}

if (closeAfter > 0)
{
    using var soakStop = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; soakStop.Set(); };
    Console.WriteLine(
        $"soak: {(loops == 0 ? "∞" : loops.ToString())} loop(s) × {clientCount} clients × {closeAfter} msgs " +
        $"(Ctrl+C to stop); watch live sockets return to 0 each loop");

    long totalSockets = 0;
    bool failed = false;
    for (int loop = 1; (loops == 0 || loop <= loops) && !soakStop.IsSet; loop++)
    {
        long baseClosed = set.Closed;
        for (int i = 0; i < clientCount; i++) set.Connect(connectEp);

        // Wait until this loop's connections have all opened AND closed (2 closes per client). Polling
        // the close-delta (not live count) avoids racing the async connects, which haven't raised the
        // live count yet right after Connect().
        var dsw = Stopwatch.StartNew();
        while (set.Closed - baseClosed < 2L * clientCount && dsw.Elapsed < TimeSpan.FromSeconds(30) && !soakStop.IsSet)
            Thread.Sleep(2);

        long delta = set.Closed - baseClosed;
        long live = set.LiveConnections;
        totalSockets += delta;
        bool ok = live == 0 && delta == 2L * clientCount;
        if (!ok)
        {
            failed = true;
            Console.WriteLine($"soak: loop {loop} FAIL — live={live} closed-this-loop={delta} (expect {2L * clientCount})");
            break;
        }
        if (loop == 1 || loop % 50 == 0 || loops != 0 && loop == loops)
            Console.WriteLine($"soak: loop {loop} ok — total sockets churned={totalSockets:n0} live=0");
    }

    Console.WriteLine(
        $"soak: done — {totalSockets:n0} sockets churned, final live={set.LiveConnections}, " +
        $"listener {(set.LiveConnections == 0 ? "clean" : "LEAK")} => {(failed ? "FAIL" : "PASS")}");
    return;
}

for (int i = 0; i < clientCount; i++)
{
    set.Connect(connectEp);
}
if (clientCount > 0) Console.WriteLine($"opened {clientCount} client connection(s) to {connectEp}");

using var stop = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};

var sw = Stopwatch.StartNew();
long lastBytes = 0;
var lastElapsed = sw.Elapsed;
int tick = 0;
while (!stop.Wait(1000))
{
    var now = sw.Elapsed;
    // Round-trip throughput is driven by the client side; a server-only process isn't measuring
    // anything, so don't spam per-second stats there — just hold the socket open for the duration.
    if (clientCount > 0)
    {
        long bytes = set.RoundTripBytes;
        double dt = (now - lastElapsed).TotalSeconds;
        double mbps = (bytes - lastBytes) / dt / (1024 * 1024);
        Console.WriteLine(
            $"[{now.TotalSeconds,5:0}s] conns={set.Connected} round-trips={bytes,15:n0} bytes " +
            $"({mbps,8:n1} MiB/s)  echoed={set.Echoed,15:n0}");
        lastBytes = bytes;
        lastElapsed = now;
    }

    if (seconds > 0 && ++tick >= seconds) break;
}

if (clientCount > 0)
{
    Console.WriteLine($"done: {set.RoundTripBytes:n0} round-trip bytes over {sw.Elapsed.TotalSeconds:0.0}s across {set.Connected} connection(s)");
    Console.WriteLine($"recv: {set.RecvOps:n0} completions, {set.RecvBytes:n0} bytes, avg {set.AvgRecvSize:n0} bytes/recv");
}
