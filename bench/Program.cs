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
// usage: Bench <host> <port> <connections> <depth> <payload> <seconds> [-stream] [-uds]
//
//  -uds : connect to the server's abstract Unix socket instead of TCP (matches
//         the server's -uds mode). host/port are ignored. UDS has no Nagle, so
//         NoDelay is not set there.

bool stream = args.Contains("-stream");
bool uds = args.Contains("-uds");
var pos = args.Where(a => !a.StartsWith('-')).ToArray();

// Must match Program.AbstractName on the server side.
const string abstractName = "fastnet-echo";

// Tolerant parsing: unparseable/absent positionals fall back to defaults, so
// -uds runs (where host/port are meaningless) can pass anything or nothing.
int Int(int i, int def) => pos.Length > i && int.TryParse(pos[i], out var v) ? v : def;
double Dbl(int i, double def) => pos.Length > i && double.TryParse(pos[i], out var v) ? v : def;

string host = pos.Length > 0 ? pos[0] : "127.0.0.1";
int port = Int(1, 8080);
int connections = Int(2, 64);
int depth = Int(3, 16);
int payload = Int(4, 64);
double seconds = Dbl(5, 5.0);

string target = uds ? $"abstract UDS @{abstractName}" : $"{host}:{port}";
Console.WriteLine($"mode={(stream ? "stream" : "ping-pong")} conns={connections} depth={depth} " +
                  $"payload={payload}B duration={seconds}s -> {target}");

var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
long totalOps = 0;
var latencies = new List<double>[connections];

async Task<Socket> Connect()
{
    if (uds)
    {
        // Leading NUL => Linux abstract namespace; no NoDelay (UDS has no Nagle).
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await s.ConnectAsync(new UnixDomainSocketEndPoint("\0" + abstractName));
        return s;
    }

    var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    await sock.ConnectAsync(IPAddress.Parse(host), port);
    return sock;
}

// SendAsync on a stream socket may flush only part of the buffer under send-buffer
// pressure (it returns the byte count). Loop until the whole payload is on the wire,
// otherwise we'd credit bytes we never actually sent and the echo drain would wait
// forever for the shortfall — an intermittent hang on TCP (UDS rarely partials).
async Task SendAll(Socket s, byte[] buf)
{
    int off = 0;
    while (off < buf.Length)
        off += await s.SendAsync(buf.AsMemory(off), SocketFlags.None);
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

        await SendAll(sock, sendBuf);

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
            await SendAll(sock, sendBuf);
            Interlocked.Add(ref sent, sendBuf.Length);
        }
        Volatile.Write(ref writing, false);
    });

    var recvBuf = new byte[payload * depth];
    long recvd = 0;
    // Drain until the writer is done AND every sent byte has been echoed back.
    //
    // Only ever block in ReceiveAsync when bytes are actually outstanding
    // (sent > recvd) — those echoes are guaranteed to arrive. Gating the receive
    // on `writing` alone is a check-then-block race: we could pass the test while
    // fully caught up (recvd == sent, nothing in flight), commit to ReceiveAsync,
    // and then the writer hits its deadline and stops without sending more — the
    // receive would wait forever on an echo that will never come (empty buffers,
    // idle connection). When caught up, exit if the writer is done, else yield.
    while (true)
    {
        if (recvd >= Interlocked.Read(ref sent))
        {
            if (!Volatile.Read(ref writing)) break; // done and fully drained
            await Task.Yield();                     // caught up but writer still active
            continue;
        }
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
