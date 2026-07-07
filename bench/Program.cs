using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// Echo load client, two modes:
//
//  default : pipelined ping-pong. Each connection keeps `depth` requests in
//            flight (write a batch, read the batch back, repeat) — how a Redis
//            multiplexer pipelines. Measures request/response steady state and
//            reports per-request latency.
//
//  -stream : full-duplex firehose. Each connection writes as fast as the socket
//            accepts and drains echoes on a separate loop, so many payloads are
//            in flight at once (no lock-step). Measures raw one-way throughput;
//            latency is not meaningful here so it is not reported. This is the
//            workload where a server that keeps recv continuously armed
//            (multishot + buffer ring) should pull ahead of one-recv-at-a-time.
//
// usage: Bench <host> <port> <connections> <depth> <payload> <seconds> [-stream]

bool stream = args.Contains("-stream");
var pos = args.Where(a => !a.StartsWith('-')).ToArray();

string host = pos.Length > 0 ? pos[0] : "127.0.0.1";
int port = pos.Length > 1 ? int.Parse(pos[1]) : 8080;
int connections = pos.Length > 2 ? int.Parse(pos[2]) : 64;
int depth = pos.Length > 3 ? int.Parse(pos[3]) : 16;
int payload = pos.Length > 4 ? int.Parse(pos[4]) : 64;
double seconds = pos.Length > 5 ? double.Parse(pos[5]) : 5.0;

Console.WriteLine($"mode={(stream ? "stream" : "ping-pong")} conns={connections} depth={depth} " +
                  $"payload={payload}B duration={seconds}s -> {host}:{port}");

var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
long totalOps = 0;
var latencies = new List<double>[connections];

async Task<Socket> Connect()
{
    var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    await sock.ConnectAsync(IPAddress.Parse(host), port);
    return sock;
}

// --- ping-pong: write a batch, read it back, repeat -----------------------
async Task PingPong(int id)
{
    var lat = latencies[id] = new List<double>(capacity: 1 << 16);
    using var sock = await Connect();

    var sendBuf = new byte[payload * depth];
    Random.Shared.NextBytes(sendBuf);
    var recvBuf = new byte[payload * depth];
    long localOps = 0;

    while (Stopwatch.GetTimestamp() < deadline)
    {
        long t0 = Stopwatch.GetTimestamp();

        await sock.SendAsync(sendBuf, SocketFlags.None);

        int got = 0;
        while (got < recvBuf.Length)
        {
            int n = await sock.ReceiveAsync(recvBuf.AsMemory(got), SocketFlags.None);
            if (n <= 0) throw new IOException("server closed mid-stream");
            got += n;
        }

        double us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
        lat.Add(us / depth); // per-request latency within the pipelined batch
        localOps += depth;
    }
    Interlocked.Add(ref totalOps, localOps);
}

// --- stream: independent writer + reader, many payloads in flight ---------
async Task Stream(int id)
{
    using var sock = await Connect();

    var sendBuf = new byte[payload * depth];
    Random.Shared.NextBytes(sendBuf);
    long sent = 0;
    bool writing = true;

    var writer = Task.Run(async () =>
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await sock.SendAsync(sendBuf, SocketFlags.None);
            Interlocked.Add(ref sent, sendBuf.Length);
        }
        Volatile.Write(ref writing, false);
    });

    var recvBuf = new byte[payload * depth];
    long recvd = 0;
    // Drain until the writer is done AND every sent byte has been echoed back.
    while (Volatile.Read(ref writing) || recvd < Interlocked.Read(ref sent))
    {
        int n = await sock.ReceiveAsync(recvBuf, SocketFlags.None);
        if (n <= 0) throw new IOException("server closed mid-stream");
        recvd += n;
    }

    await writer;
    Interlocked.Add(ref totalOps, recvd / payload);
}

var tasks = new Task[connections];
for (int i = 0; i < connections; i++)
{
    int id = i;
    tasks[i] = stream ? Stream(id) : PingPong(id);
}

var wall = Stopwatch.StartNew();
await Task.WhenAll(tasks);
wall.Stop();

double elapsed = wall.Elapsed.TotalSeconds;
double opsPerSec = totalOps / elapsed;
double mbPerSec = totalOps * (double)payload / (1024 * 1024) / elapsed;

Console.WriteLine($"ops={totalOps:N0}  {opsPerSec:N0} ops/s  {mbPerSec:N1} MiB/s (echo, so ~2x on wire)");

if (!stream)
{
    var all = latencies.Where(l => l != null).SelectMany(l => l!).OrderBy(x => x).ToArray();
    double Pct(double p) => all.Length == 0 ? 0 : all[Math.Min(all.Length - 1, (int)(p * all.Length))];
    Console.WriteLine($"latency/req us: p50={Pct(0.50):F1} p99={Pct(0.99):F1} p999={Pct(0.999):F1} max={(all.Length > 0 ? all[^1] : 0):F1}");
}
