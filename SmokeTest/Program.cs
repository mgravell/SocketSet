using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SmokeTest;
using SocketSets;

bool server = false;
int clientCount = 0;
int seconds = 0; // 0 == run until Ctrl+C
string? uds = null; // UDS name (e.g. "@fastnet-smoke" for the abstract namespace)
int size = 512; // message size
int window = 1; // client send window: 1 = ping/pong, N = bounded pipeline, int.MaxValue = unbounded
string? cpus = null; // CPU affinity spec, e.g. "0-5" or "0,2,4" or "0-3,8"
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
        case "--pipeline":
            window = int.MaxValue; // unbounded (deadlock-prone on a symmetric echo — for comparison)
            break;
        case "--window" when i + 1 < args.Length && int.TryParse(args[i + 1], out var w):
            window = Math.Max(1, w);
            break;
    }
}

if (!server && clientCount == 0)
{
    Console.WriteLine("usage: SmokeTest -s [-c N] [-t seconds] [-u name] [-z bytes] [-n shards] [-p true|false]");
    Console.WriteLine("  -s / --server     run the echo server");
    Console.WriteLine("  -c / --client N   open N client connections that ping-pong");
    Console.WriteLine("  -t / --seconds S  stop after S seconds (default: run until Ctrl+C)");
    Console.WriteLine("  -u / --uds name   use a Unix domain socket (e.g. @foo for abstract) instead of TCP");
    Console.WriteLine("  -z / --size N     ping-pong message size in bytes (default 512)");
    Console.WriteLine("  -n / --shards N   number of shards / worker threads (default 4)");
    Console.WriteLine("  -p / --pin B      pin worker threads to CPUs, true|false (default true)");
    Console.WriteLine("  -e / --entries N  io_uring SQ entries per shard (default 4096; lower if RLIMIT_MEMLOCK is tight)");
    Console.WriteLine("  -m / --managed    force the portable managed-socket fallback (default auto-detects)");
    Console.WriteLine("  --cpus SPEC       pin this process to CPUs (e.g. 0-5 or 0,2,4) — covers shards, thread pool and GC");
    Console.WriteLine("  --window N        client keeps up to N messages in flight (1=ping/pong default, N=bounded pipeline)");
    Console.WriteLine("  --pipeline        unbounded in-flight (throughput, but can wedge a symmetric echo; use --window instead)");
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
#pragma warning disable CA1416 - checked
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

EndPoint endpoint;
#if NET
endpoint = uds is null ? new IPEndPoint(IPAddress.Loopback, 10000) : new UnixDomainSocketEndPoint(uds);
#else
if (uds is not null) Console.WriteLine("note: UDS not supported on this target framework; falling back to TCP");
endpoint = new IPEndPoint(IPAddress.Loopback, 10000);
#endif
using var set = new EchoServer(options) { GreetingSize = size, Window = window };
string mode = window == 1 ? "ping/pong" : window == int.MaxValue ? "pipeline(unbounded)" : $"pipeline(window={window})";
Console.WriteLine(
    $"backend={options.Factory.GetType().Name} transport={(uds is null ? "tcp" : "uds")} " +
    $"mode={mode} size={size} shards={options.Shards} pin={options.PinWorkerThreads}");

if (server)
{
    set.Listen(endpoint, EchoServer.ServerToken);
    Console.WriteLine($"listening on {endpoint}");
}

for (int i = 0; i < clientCount; i++)
{
    set.Connect(endpoint);
}
if (clientCount > 0) Console.WriteLine($"opened {clientCount} client connection(s)");

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
    long bytes = set.RoundTripBytes;
    double dt = (now - lastElapsed).TotalSeconds;
    double mbps = (bytes - lastBytes) / dt / (1024 * 1024);
    Console.WriteLine(
        $"[{now.TotalSeconds,5:0}s] conns={set.Connected} round-trips={bytes,15:n0} bytes " +
        $"({mbps,8:n1} MiB/s)  echoed={set.Echoed,15:n0}");
    lastBytes = bytes;
    lastElapsed = now;

    if (seconds > 0 && ++tick >= seconds) break;
}

Console.WriteLine($"done: {set.RoundTripBytes:n0} round-trip bytes over {sw.Elapsed.TotalSeconds:0.0}s across {set.Connected} connection(s)");
Console.WriteLine($"recv: {set.RecvOps:n0} completions, {set.RecvBytes:n0} bytes, avg {set.AvgRecvSize:n0} bytes/recv");
