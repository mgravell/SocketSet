// Functional gate for the Tunnel transport contract (RESPite.Transports, SER009): dial a real Garnet through
// SocketSetClientTransport, exercise every member of the contract, and assert the semantics the design
// doc claims — push receive, any-thread staged writes with explicit flush, batch-end firing, close
// notification exactly once. Exit 0 = all pass.
using System.Net;
using System.Text;
using RESPite.Transports;
using SocketSets;
using SocketSets.StackExchangeRedis;
using SocketSets.Tls;

// usage: tunnel-selftest [plain|tls|uds|mux|mux-tls|mux-tls-noprovider] [host-or-@name] [port] [tls-trust-pem]
//
// CROSS-PLATFORM since the TLS-owning seam landed: the backend and the TLS provider come from this OS
// (io_uring + OpenSSL on Linux, IOCP + SChannel on Windows) rather than being hard-coded to the box it
// was written on. The seam's TLS half is Windows-relevant -- SE.Redis clients do TLS to Azure Redis
// constantly -- so a gate that could only run on Linux was gating the wrong half.
string surface = args.Length > 0 ? args[0] : "plain";
string target = args.Length > 1 ? args[1] : "127.0.0.1";
int port = args.Length > 2 ? int.Parse(args[2]) : 7379;
string trustPem = args.Length > 3 ? args[3] : FindDemoCert();

static string FindDemoCert()
{
    // Walk up from the binary to the repo, so the rig works from any working directory on either OS.
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "bench", ".tools", "tls-demo", "cert.pem");
        if (File.Exists(candidate)) return candidate;
    }
    return Path.Combine("bench", ".tools", "tls-demo", "cert.pem"); // report the miss where it is used
}

// The demo server's certificate as a TRUST ROOT for the client, on whichever provider this OS has.
// Pinning it keeps verification ON without installing anything in a machine store.
static TlsProvider Trusting(string pem) => GateBackends.IsWindows
    ? new SocketSets.Tls.SChannel.SChannelTlsProvider(
        trustCertificate: System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(pem)))
    : new SocketSets.Tls.OpenSsl.OpenSslTlsProvider(trustCertPem: File.ReadAllText(pem));

int failures = 0;
void Report(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-24} {detail}");
    if (!ok) failures++;
}

// Backend: this OS's default unless SS_GATE_BACKEND names one. The three Windows backends do not share
// a receive loop -- IOCP dequeues completion batches, RIO drains a user-mode CQ, managed rides SAEA
// callbacks with no loop at all -- and the batch-end contract has to hold on each, so the gate has to be
// able to ASK for each. (Left as an env var rather than a positional argument so existing invocations,
// which pass target/port positionally, keep working.)
var backendName = Environment.GetEnvironmentVariable("SS_GATE_BACKEND");
var picked = backendName is null
    ? GateBackends.All[0]
    : Array.Find(GateBackends.All, b => string.Equals(b.Name, backendName, StringComparison.OrdinalIgnoreCase));
if (picked.Name is null)
{
    Console.WriteLine($"unknown SS_GATE_BACKEND '{backendName}'; this OS has: {string.Join(", ", Array.ConvertAll(GateBackends.All, b => b.Name))}");
    return 2;
}
var options = new SocketSetOptions { Shards = 1, Factory = picked.Factory };

if (surface is "mux" or "mux-tls" or "mux-tls-noprovider" or "mux-classic")
{
    if (surface is "mux-tls")
    {
        // TLS lives in the TRANSPORT -- but the INTENT now travels with the seam: config.Ssl=true is
        // what turns it on (it used to THROW alongside a transport tunnel), and the engine supplies only
        // the provider. Nothing here sets TlsClient.TargetHost any more: the name comes from the
        // configuration, per dial, which is the whole point of the change.
        options.Tls = Trusting(trustPem);
    }
    // mux-tls-noprovider: the same demand with NO provider on the engine. It must FAIL. Without that
    // cell the TLS cell above passes just as happily against a transport that reports "encrypted"
    // because it was asked to be, rather than because it is.
    // The END-TO-END cell: a real ConnectionMultiplexer whose IO core is SocketSet, via
    // Tunnel.ConnectTransportAsync -> SocketSetTunnel -> PhysicalConnection transport mode (push-feed).
    // No socket, no Stream, no SslStream and no reader thread exist on the SE.Redis side; if these
    // pass, the whole chain (handshake HELLO/AUTH included) ran through the transport.
    Console.WriteLine($"=== tunnel-selftest: surface={surface} backend={picked.Name} target={target} ===");
    var muxCfg = new StackExchange.Redis.ConfigurationOptions
    {
        EndPoints = { new IPEndPoint(IPAddress.Parse(target), port) },
        AbortOnConnectFail = true,
        // mux-classic is the CONTROL: identical cells, ordinary sockets, no tunnel at all. Without it a
        // failure here cannot be attributed -- "the tunnel is broken" and "this server/rig cannot do
        // that" look identical.
        Tunnel = surface is "mux-classic" ? null : new TracingTunnel(new SocketSetTunnel(options)),
        // The TLS intent, stated where SE.Redis states it. SslHost is what the certificate is verified
        // against (and announced as SNI); without it the endpoint's host is used, which for an IP
        // literal means iPAddress-SAN verification and no SNI -- both legitimate, but the demo
        // certificate is named, not addressed.
        Ssl = surface is "mux-tls" or "mux-tls-noprovider",
        SslHost = "localhost",
    };

    if (surface is "mux-tls-noprovider")
    {
        // THE DISCRIMINATING CELL: TLS demanded, nothing to do it with. Either the tunnel refuses the
        // dial (it does, naming the missing provider) or it hands back a plaintext transport and the
        // library refuses THAT (IsEncrypted false against Ssl=true). Both are failures; a connection is
        // not.
        bool refused = false;
        string detail = "connected in the clear";
        try
        {
            await using var bad = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(muxCfg);
            refused = !bad.IsConnected;
        }
        catch (Exception ex)
        {
            refused = true;
            detail = ex.GetBaseException().Message;
            if (detail.Length > 90) detail = detail.Substring(0, 90) + "...";
        }
        Report("tls-demanded/refused", refused, detail);
        Console.WriteLine(failures == 0 ? "=== tunnel-selftest: ALL PASS ===" : $"=== tunnel-selftest: {failures} FAILURE(S) ===");
        return failures == 0 ? 0 : 1;
    }

    // Capture the multiplexer's own connect log and print it ONLY on failure: a failed connect here
    // reports "not possible to connect" and nothing else, which is useless against a seam whose failure
    // modes are all configuration-shaped (no provider, unmappable option, name mismatch).
    var connectLog = new StringWriter();
    StackExchange.Redis.ConnectionMultiplexer mux;
    try
    {
        mux = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(muxCfg, connectLog);
    }
    catch (Exception ex)
    {
        Report("mux-connect", false, ex.GetBaseException().Message);
        Console.WriteLine(connectLog.ToString());
        Console.WriteLine($"=== tunnel-selftest: {failures} FAILURE(S) ===");
        return 1;
    }
    await using var owned = mux;

    // The multiplexer reports per-CONNECTION trouble through events, not through ConnectAsync: the
    // subscription connection is opened later, and its failures are otherwise visible only as a
    // timed-out command with no reason attached.
    mux.ConnectionFailed += (_, e) => Console.WriteLine($"  ....  ConnectionFailed {e.ConnectionType} {e.FailureType}: {e.Exception?.GetBaseException().Message}");
    mux.ConnectionRestored += (_, e) => Console.WriteLine($"  ....  ConnectionRestored {e.ConnectionType}");
    mux.InternalError += (_, e) => Console.WriteLine($"  ....  InternalError {e.ConnectionType} {e.Origin}: {e.Exception?.GetBaseException().Message}");
    mux.ErrorMessage += (_, e) => Console.WriteLine($"  ....  ErrorMessage: {e.Message}");

    // Under Ssl=true this is already the assertion that the transport reported ENCRYPTED: the library
    // fails the connection outright when a tunnel's transport says otherwise.
    Report("mux-connect", mux.IsConnected);
    var db = mux.GetDatabase();
    var rtt = await db.PingAsync();
    Report("mux-ping", rtt < TimeSpan.FromSeconds(1), $"rtt={rtt.TotalMilliseconds:f2}ms");
    var key = $"tt:mux:{Guid.NewGuid():N}";
    var value = Guid.NewGuid().ToString("N");
    Report("mux-set", await db.StringSetAsync(key, value));
    Report("mux-get", await db.StringGetAsync(key) == value);
    var tasks = new Task<bool>[500];
    for (int i = 0; i < tasks.Length; i++) tasks[i] = db.StringSetAsync($"{key}:{i}", i.ToString());
    await Task.WhenAll(tasks);
    Report("mux-burst x500", Array.TrueForAll(tasks, t => t.Result));
    Report("mux-burst-verify", await db.StringGetAsync($"{key}:499") == "499");

    // PUB/SUB: the SECOND connection. Everything above rides the Interactive connection, so a tunnel
    // that works exactly once would pass all of it -- and the anchor's whole claim is that N connections
    // share ONE engine. A subscription is how the multiplexer asks for another transport, and the
    // message has to come back over it.
    var sub = mux.GetSubscriber();
    var channel = StackExchange.Redis.RedisChannel.Literal($"tt:chan:{Guid.NewGuid():N}");
    var got = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    await sub.SubscribeAsync(channel, (_, v) => got.TrySetResult(v));
    await sub.PublishAsync(channel, "hello");
    var delivered = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(5))) == got.Task;
    Report("mux-pubsub", delivered && got.Task.Result == "hello", delivered ? $"got \"{got.Task.Result}\"" : "no message in 5s");
    Console.WriteLine(failures == 0 ? "=== tunnel-selftest: ALL PASS ===" : $"=== tunnel-selftest: {failures} FAILURE(S) ===");
    return failures == 0 ? 0 : 1;
}

if (surface is "twin")
{
    // THE ANCHOR CLAIM, on its own: TWO transports, ONE engine, both live at once. A multiplexer
    // opens an interactive AND a subscription connection through the same tunnel, so if that shape
    // is broken the failure surfaces three layers up as a timed-out SUBSCRIBE, where it cannot be
    // attributed to the seam.
    Console.WriteLine($"=== tunnel-selftest: surface=twin backend={picked.Name} target={target} ===");
    var twinEngine = new SocketSetClientEngine(options);
    var twinEp = new IPEndPoint(IPAddress.Parse(target), port);
    var ta = await SocketSetClientTransport.ConnectAsync(twinEp, twinEngine);
    var tb = await SocketSetClientTransport.ConnectAsync(twinEp, twinEngine);
    var rxa = new Receiver();
    var rxb = new Receiver();
    ta.Start(rxa);
    tb.Start(rxb);

    static void Ping(SocketSetClientTransport t)
    {
        "*1\r\n$4\r\nPING\r\n"u8.ToArray().AsSpan().CopyTo(t.GetSpan(32));
        t.Advance(14);
        t.Flush();
    }

    Ping(ta);
    Report("twin-a-ping", await rxa.WaitFor("+PONG\r\n", TimeSpan.FromSeconds(5)));
    Ping(tb);
    Report("twin-b-ping", await rxb.WaitFor("+PONG\r\n", TimeSpan.FromSeconds(5)));
    // A again, to prove B arriving did not steal A's routing (UserToken dispatch is the whole design)
    rxa.Reset();
    Ping(ta);
    Report("twin-a-again", await rxa.WaitFor("+PONG\r\n", TimeSpan.FromSeconds(5)));
    await ta.DisposeAsync();
    await tb.DisposeAsync();
    twinEngine.Dispose();
    Console.WriteLine(failures == 0 ? "=== tunnel-selftest: ALL PASS ===" : $"=== tunnel-selftest: {failures} FAILURE(S) ===");
    return failures == 0 ? 0 : 1;
}

EndPoint endpoint;
switch (surface)
{
    case "tls":
        // The SE.Redis-to-managed-Redis shape: TLS client dial, trust pinned to the server's cert,
        // verification ON. ConnectAsync completes only after the handshake (the OnConnect contract).
        // This is the ENGINE-configured path (no intent stated), which the seam change deliberately
        // leaves untouched -- so this cell is also the regression test for that promise.
        options.Tls = Trusting(trustPem);
        options.TlsClient.TargetHost = "localhost"; // mandatory since 2026-08-04; the demo cert is named
        endpoint = new IPEndPoint(IPAddress.Parse(target), port);
        break;
    case "uds":
        // The sidecar shape: @abstract via SocketSet's own mapping.
        endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(target);
        break;
    default:
        endpoint = new IPEndPoint(IPAddress.Parse(target), port);
        break;
}
Console.WriteLine($"=== tunnel-selftest: surface={surface} backend={picked.Name} target={target} ===");
var transport = await SocketSetClientTransport.ConnectAsync(endpoint, options);

var rx = new Receiver();
transport.Start(rx);

// 1: single round trip — push delivery, staged write + flush
"*1\r\n$4\r\nPING\r\n"u8.ToArray().AsSpan().CopyTo(transport.GetSpan(32));
transport.Advance(14);
Report("flush", transport.Flush());
Report("ping-roundtrip", await rx.WaitFor("+PONG\r\n", TimeSpan.FromSeconds(5)));

// IsEncrypted is the OUTCOME, and it is what a consumer refuses a transport on, so assert both
// directions: a plaintext dial must not claim encryption, and a TLS dial must not fail to claim it.
// One direction alone passes against a hard-coded answer.
Report("encrypted-reported", transport.IsEncrypted == (surface == "tls"),
    $"IsEncrypted={transport.IsEncrypted} on surface={surface}");

// 2: pipelined burst — one flush for many commands, replies counted, batch-end observed
rx.Reset();
const int N = 1000;
for (int i = 0; i < N; i++)
{
    var cmd = Encoding.ASCII.GetBytes($"*3\r\n$3\r\nSET\r\n$6\r\ntt:{i:d3}\r\n$2\r\nok\r\n");
    var span = transport.GetSpan(cmd.Length);
    cmd.CopyTo(span);
    transport.Advance(cmd.Length);
}
Report("burst-flush", transport.Flush());
Report($"burst x{N}", await rx.WaitForCount("+OK\r\n"u8.ToArray(), N, TimeSpan.FromSeconds(10)),
    $"got {rx.Count("+OK\r\n"u8.ToArray())} replies, batch-ends={rx.BatchEnds}");
Report("batch-end fired", rx.BatchEnds > 0, $"count={rx.BatchEnds}");

// 3: close notification — exactly once, from our side
await transport.DisposeAsync();
Report("closed exactly once", await rx.WaitClosed(TimeSpan.FromSeconds(5)) && rx.ClosedCount == 1,
    $"count={rx.ClosedCount}");

Console.WriteLine(failures == 0 ? "=== tunnel-selftest: ALL PASS ===" : $"=== tunnel-selftest: {failures} FAILURE(S) ===");
return failures == 0 ? 0 : 1;

sealed class Receiver : TransportReceiver
{
    private readonly List<byte> _all = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int BatchEnds;
    public int ClosedCount;

    public override bool OnReceived(ReadOnlySpan<byte> payload)
    {
        lock (_all) { foreach (var b in payload) _all.Add(b); }
        _signal.Release();
        return true;
    }

    public override void OnBatchEnd() => Interlocked.Increment(ref BatchEnds);

    public override void OnClosed(Exception? fault)
    {
        Interlocked.Increment(ref ClosedCount);
        _closed.TrySetResult();
    }

    public void Reset() { lock (_all) _all.Clear(); }

    public int Count(byte[] needle)
    {
        lock (_all)
        {
            var hay = _all.ToArray().AsSpan();
            int n = 0, i;
            while ((i = hay.IndexOf(needle)) >= 0) { n++; hay = hay.Slice(i + needle.Length); }
            return n;
        }
    }

    public async Task<bool> WaitFor(string text, TimeSpan timeout)
        => await WaitForCount(Encoding.ASCII.GetBytes(text), 1, timeout);

    public async Task<bool> WaitForCount(byte[] needle, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Count(needle) < count)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await _signal.WaitAsync(left)) return Count(needle) >= count;
        }
        return true;
    }

    public async Task<bool> WaitClosed(TimeSpan timeout)
        => await Task.WhenAny(_closed.Task, Task.Delay(timeout)) == _closed.Task;
}


/// <summary>Announces every dial the multiplexer makes through the tunnel, and what came back. A
/// multiplexer opens MORE than one connection (interactive, then subscription on first subscribe), and
/// a seam that works for the first and silently never completes the second looks, from the outside,
/// like "SUBSCRIBE timed out" -- which is where the diagnosis stops without this.</summary>
sealed class TracingTunnel(StackExchange.Redis.Configuration.Tunnel inner) : StackExchange.Redis.Configuration.Tunnel
{
    public override async ValueTask<DuplexTransport?> ConnectTransportAsync(
        EndPoint endpoint, StackExchange.Redis.ConnectionType connectionType,
        StackExchange.Redis.Configuration.TlsOptions tls, CancellationToken cancellationToken)
    {
        Console.WriteLine($"  ....  dial {connectionType} ssl={tls.IsEnabled}");
        try
        {
            var t = await inner.ConnectTransportAsync(endpoint, connectionType, tls, cancellationToken);
            Console.WriteLine($"  ....  dial {connectionType} -> {(t is null ? "null (socket path)" : $"transport encrypted={t.IsEncrypted}")}");
            return t;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ....  dial {connectionType} -> THREW {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
