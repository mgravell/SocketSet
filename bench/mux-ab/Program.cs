// SE.Redis-over-SocketSet vs classic: the CLIENT-SEAT A/B. One ConnectionMultiplexer, D concurrent
// awaiting workers (the app-blocks-on-the-call regime), identical code both legs — only the
// ConfigurationOptions differ. Latency is the full user-visible await (SE.Redis queueing included),
// stride-sampled 1-in-8; throughput is completed awaits over the timed window.
//
// Banner rule: the tunnel legs are GATED on the counting tunnel actually being asked for transports
// (>=1). A sibling checkout without transport mode silently falls back to sockets and would measure
// classic-vs-classic; the gate turns that into NORESULT instead of a clean-looking lie.
//
// usage: mux-ab <classic|tunnel|classic-tls|tunnel-tls> <host> <port> <get|set> <depth> <seconds> [trust-pem]
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using RESPite.Transports;
using SocketSets;
using SocketSets.StackExchangeRedis;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;

string leg = args[0];
string host = args[1];
int port = int.Parse(args[2]);
string op = args[3];
int depth = int.Parse(args[4]);
int seconds = int.Parse(args[5]);
string trustPem = args.Length > 6 ? args[6] : "/home/marc/code/SocketSet/bench/.tools/tls-demo/cert.pem";

int muxes = int.Parse(Environment.GetEnvironmentVariable("MUXAB_MUXES") ?? "1");
int valSize = int.Parse(Environment.GetEnvironmentVariable("MUXAB_VALSIZE") ?? "32");
ThreadPool.SetMinThreads(Math.Max(depth * muxes, 32), Math.Max(depth * muxes, 32));

bool tls = leg.EndsWith("-tls");
bool tunnel = leg.StartsWith("tunnel");

var config = new ConfigurationOptions
{
    EndPoints = { new IPEndPoint(IPAddress.Parse(host), port) },
    AbortOnConnectFail = true,
};

CountingTunnel? counting = null;
if (tunnel)
{
    var factory = Environment.GetEnvironmentVariable("MUXAB_BACKEND") switch
    {
        "epoll" => SocketSetFactory.Epoll,
        "managed" => SocketSetFactory.Managed,
        _ => SocketSetFactory.IoUring,
    };
    int shards = int.Parse(Environment.GetEnvironmentVariable("MUXAB_SHARDS") ?? "1");
    var ss = new SocketSetOptions { Shards = shards, Factory = factory };
    if (tls)
    {
        // The engine supplies the PROVIDER (trust pinned to the demo cert, verification on); the
        // INTENT now travels with the seam -- config.Ssl below is what turns TLS on for each dial, and
        // the name verified comes from config.SslHost. Both legs therefore verify the same name against
        // a pinned certificate: the fairness gap this A/B carried until 2026-08-04 stays closed, now by
        // construction rather than by remembering to set TlsClient.TargetHost.
        ss.Tls = new SocketSets.Tls.OpenSsl.OpenSslTlsProvider(trustCertPem: File.ReadAllText(trustPem));
        config.Ssl = true;
        config.SslHost = "localhost";
    }
    config.Tunnel = counting = new CountingTunnel(new SocketSetTunnel(ss));
}
else if (tls)
{
    // SslStream leg: pin the demo cert by thumbprint (CN=localhost, no SAN — name checks are not the
    // property under test; the pin keeps verification ON without a trust-store install).
    var trusted = X509Certificate2.CreateFromPem(File.ReadAllText(trustPem));
    config.Ssl = true;
    config.SslHost = "localhost";
    config.CertificateValidation += (_, cert, _, _) =>
        cert is not null && cert.GetCertHashString() == trusted.GetCertHashString();
}

var muxList = new ConnectionMultiplexer[muxes];
var dbs = new IDatabase[muxes];
for (int m = 0; m < muxes; m++)
{
    // fresh options per mux (ConnectAsync mutates), same Tunnel INSTANCE — the anchor is the point
    var c = config.Clone();
    muxList[m] = await ConnectionMultiplexer.ConnectAsync(c);
    dbs[m] = muxList[m].GetDatabase();
}
var db = dbs[0];

int tunnelConnects = counting?.Connects ?? 0;
Console.WriteLine($"[mux-ab] muxes={muxes} shards={Environment.GetEnvironmentVariable("MUXAB_SHARDS") ?? "1"} valsize={valSize} backend={(tunnel ? (Environment.GetEnvironmentVariable("MUXAB_BACKEND") ?? "io-uring") : "n/a")} leg={leg} tls={(tls ? (tunnel ? "openssl-transport" : "sslstream") : "off")} tunnel_connects={tunnelConnects} target={host}:{port} depth={depth} op={op} window={seconds}s");
if (tunnel && tunnelConnects < 1)
{
    Console.WriteLine("GATE-FAIL: tunnel leg but ConnectTransportAsync was never called (sibling checkout lacks transport mode?)");
    return 2;
}

var key = (RedisKey)$"muxab:{op}";
var value = (RedisValue)new byte[valSize];
await db.StringSetAsync(key, value);

long ops = 0;
var samples = new long[4_000_000];
int sampleCount = 0;
bool running = true, measuring = false;

async Task Worker(IDatabase db)
{
    long localOps = 0;
    int stride = 0;
    while (Volatile.Read(ref running))
    {
        long t0 = Stopwatch.GetTimestamp();
        if (op == "set") { await db.StringSetAsync(key, value).ConfigureAwait(false); }
        else { await db.StringGetAsync(key).ConfigureAwait(false); }
        if (Volatile.Read(ref measuring))
        {
            localOps++;
            if ((++stride & 7) == 0)
            {
                int i = Interlocked.Increment(ref sampleCount) - 1;
                if (i < samples.Length) samples[i] = Stopwatch.GetTimestamp() - t0;
            }
        }
    }
    Interlocked.Add(ref ops, localOps);
}

var workers = new Task[depth * muxes];
for (int i = 0; i < workers.Length; i++)
{
    var wdb = dbs[i % muxes];
    workers[i] = Task.Run(() => Worker(wdb));
}

await Task.Delay(TimeSpan.FromSeconds(2)); // warmup
Volatile.Write(ref measuring, true);
var sw = Stopwatch.StartNew();
await Task.Delay(TimeSpan.FromSeconds(seconds));
Volatile.Write(ref measuring, false);
sw.Stop();
Volatile.Write(ref running, false);
await Task.WhenAll(workers);
foreach (var m in muxList) await m.DisposeAsync();

int n = Math.Min(sampleCount, samples.Length);
Array.Sort(samples, 0, n);
double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
double P(double q) => n == 0 ? double.NaN : ToMs(samples[Math.Min(n - 1, (int)(q * n))]);
double opsPerSec = ops / sw.Elapsed.TotalSeconds;

Console.WriteLine($"RESULT,{leg},{op},{depth},{opsPerSec:F0},{P(0.50):F3},{P(0.99):F3},{P(0.999):F3},{n}");
return 0;

sealed class CountingTunnel(Tunnel inner) : Tunnel
{
    private int _connects;
    public int Connects => Volatile.Read(ref _connects);

    public override ValueTask<DuplexTransport?> ConnectTransportAsync(EndPoint endpoint, ConnectionType connectionType, TlsOptions tls, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connects);
        return inner.ConnectTransportAsync(endpoint, connectionType, tls, cancellationToken);
    }
}
