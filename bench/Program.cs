using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// Pipelined echo load client.
// Each connection keeps `depth` requests in flight (write a batch, read the
// batch back, repeat) which is exactly how a Redis multiplexer pipelines —
// so this measures the loop's steady-state throughput, not TCP handshakes.
//
// usage: Bench <host> <port> <connections> <depth> <payload> <seconds>

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 8080;
int connections = args.Length > 2 ? int.Parse(args[2]) : 64;
int depth = args.Length > 3 ? int.Parse(args[3]) : 16;
int payload = args.Length > 4 ? int.Parse(args[4]) : 64;
double seconds = args.Length > 5 ? double.Parse(args[5]) : 5.0;

Console.WriteLine($"conns={connections} depth={depth} payload={payload}B duration={seconds}s -> {host}:{port}");

var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
long totalOps = 0;
var latencies = new List<double>[connections];

var tasks = new Task[connections];
for (int i = 0; i < connections; i++)
{
    int id = i;
    tasks[i] = Task.Run(async () =>
    {
        var lat = latencies[id] = new List<double>(capacity: 1 << 16);
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sock.NoDelay = true;
        await sock.ConnectAsync(IPAddress.Parse(host), port);

        // One buffer holding `depth` back-to-back payloads; read the same count back.
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
    });
}

var wall = Stopwatch.StartNew();
await Task.WhenAll(tasks);
wall.Stop();

var all = latencies.Where(l => l != null).SelectMany(l => l!).OrderBy(x => x).ToArray();
double elapsed = wall.Elapsed.TotalSeconds;
double opsPerSec = totalOps / elapsed;
double mbPerSec = totalOps * (double)payload / (1024 * 1024) / elapsed;

double Pct(double p) => all.Length == 0 ? 0 : all[Math.Min(all.Length - 1, (int)(p * all.Length))];

Console.WriteLine($"ops={totalOps:N0}  {opsPerSec:N0} ops/s  {mbPerSec:N1} MiB/s (echo, so ~2x on wire)");
Console.WriteLine($"latency/req us: p50={Pct(0.50):F1} p99={Pct(0.99):F1} p999={Pct(0.999):F1} max={(all.Length > 0 ? all[^1] : 0):F1}");
