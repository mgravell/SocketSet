using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SmokeTest;
using SocketSets;

bool server = false;
int clientCount = 0;
int seconds = 0; // 0 == run until Ctrl+C
string? uds = null; // UDS name (e.g. "@fastnet-smoke" for the abstract namespace)
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
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
    }
}

if (!server && clientCount == 0)
{
    Console.WriteLine("usage: SmokeTest -s [-c N] [-t seconds] [-u name]");
    Console.WriteLine("  -s / --server     run the echo server");
    Console.WriteLine("  -c / --client N   open N client connections that ping-pong");
    Console.WriteLine("  -t / --seconds S  stop after S seconds (default: run until Ctrl+C)");
    Console.WriteLine("  -u / --uds name   use a Unix domain socket (e.g. @foo for abstract) instead of TCP");
    return;
}

EndPoint endpoint = uds is null
    ? new IPEndPoint(IPAddress.Loopback, 10000)
    : new UnixDomainSocketEndPoint(uds);
using var set = new EchoServer(new SocketSetOptions());

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
