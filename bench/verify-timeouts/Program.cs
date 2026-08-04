// verify-timeouts — is a peer that connects and goes quiet actually reaped? (REVIEW.md D2)
//
// Nothing reaped one before 2026-08-04: at the default 4096 slots x 4 shards, 16k half-open TLS
// handshakes exhausted the set and further accepts were dropped with only PlacementFailures moving to
// say so. Worse under SocketSet.AspNetCore, where TLS terminates BELOW Kestrel so Kestrel's own
// HandshakeTimeout never sees the connection.
//
// CELLS. The first is the one that proves anything; the rest exist so a pass is not vacuous.
//   handshake/reaped   a TCP peer that connects to a TLS listener and sends NOTHING must be dropped
//                      inside the budget. This is the DoS shape, and the only cell that fails against
//                      the pre-fix code.
//   handshake/spared   a peer that completes its handshake normally must SURVIVE well past the same
//                      budget -- otherwise "reaped" could just mean "we drop everything".
//   idle/off-by-default an established connection idle past the handshake budget must SURVIVE, because
//                      IdleTimeout defaults to off. A SE.Redis link or an HTTP keep-alive socket is
//                      legitimately quiet for minutes; reaping those would be a correctness bug wearing
//                      a security hat.
//   idle/reaped        ...but WITH IdleTimeout set, the same connection must be dropped. Otherwise the
//                      option would be inert and the cell above could not tell "off" from "broken".
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SocketSets;
using SocketSets.Tls.OpenSsl;

int failures = 0;
using var provider = OpenSslTlsProvider.CreateSelfSignedLoopback("localhost");

foreach (var factory in new[] { SocketSetFactory.IoUring, SocketSetFactory.Epoll })
{
    string be = factory.GetType().Name.Replace("Factory", "");

    // --- 1/2: a silent TCP peer against a TLS listener, vs one that handshakes properly -------------
    {
        var o = new SocketSetOptions
        {
            Shards = 1, Factory = factory, Tls = provider,
            HandshakeTimeout = TimeSpan.FromSeconds(2),
        };
        using var srv = new Srv(o);
        srv.Listen(new IPEndPoint(IPAddress.Loopback, 19911));
        Thread.Sleep(300);

        using (var mute = new TcpClient())
        {
            mute.Connect("127.0.0.1", 19911);          // connect, then say nothing at all
            var sw = Stopwatch.StartNew();
            bool dropped = WaitDropped(mute, TimeSpan.FromSeconds(8));
            Report(ref failures, dropped, $"{be} handshake/reaped",
                dropped ? $"dropped after {sw.Elapsed.TotalSeconds:0.0}s (budget 2s)" : "STILL OPEN after 8s");
        }

        var co = new SocketSetOptions { Shards = 1, Factory = factory, Tls = provider, HandshakeTimeout = TimeSpan.FromSeconds(2) };
        co.TlsClient.TargetHost = "localhost";
        using var good = new Dialer(co);
        good.Connect(new IPEndPoint(IPAddress.Loopback, 19911));
        bool up = good.Opened.Wait(TimeSpan.FromSeconds(5));
        Thread.Sleep(4000);                             // twice the handshake budget
        Report(ref failures, up && !good.Closed, $"{be} handshake/spared",
            up ? (good.Closed ? "a COMPLETED handshake was reaped anyway" : "survived 4s (budget 2s)") : "never connected");
    }
    Thread.Sleep(300);

    // --- 3/4: idle, off by default then on ---------------------------------------------------------
    foreach (var (idle, mustDrop, cell) in new[]
             { (TimeSpan.Zero, false, "idle/off-by-default"), (TimeSpan.FromSeconds(2), true, "idle/reaped") })
    {
        var o = new SocketSetOptions { Shards = 1, Factory = factory, HandshakeTimeout = TimeSpan.FromSeconds(2), IdleTimeout = idle };
        using var srv = new Srv(o);
        srv.Listen(new IPEndPoint(IPAddress.Loopback, 19912));
        Thread.Sleep(300);
        using var c = new TcpClient();
        c.Connect("127.0.0.1", 19912);
        c.GetStream().Write("hi"u8);                    // establish, then go quiet
        c.GetStream().ReadByte();
        bool dropped = WaitDropped(c, TimeSpan.FromSeconds(6));
        Report(ref failures, dropped == mustDrop, $"{be} {cell}",
            dropped ? "dropped" : "survived");
        Thread.Sleep(300);
    }
}

Console.WriteLine(failures == 0 ? "\n=== verify-timeouts: ALL PASS ===" : $"\n=== verify-timeouts: {failures} FAILURE(S) ===");
return failures == 0 ? 0 : 1;

static bool WaitDropped(TcpClient c, TimeSpan within)
{
    var sw = Stopwatch.StartNew();
    var s = c.GetStream();
    s.ReadTimeout = 250;
    var one = new byte[1];
    while (sw.Elapsed < within)
    {
        try { if (s.Read(one, 0, 1) == 0) return true; }   // graceful FIN
        catch (IOException) { if (!c.Client.Connected) return true; }  // RST / timeout
        catch (ObjectDisposedException) { return true; }
    }
    return false;
}

static void Report(ref int failures, bool ok, string cell, string detail)
{
    if (!ok) failures++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {cell,-32} {detail}");
}

sealed class Srv(SocketSetOptions o) : SocketSet(o)
{
    protected override void OnReceive(ref ReceiveContext ctx) { if (!ctx.IsEof) ctx.ResponseBytes = ctx.PayloadBytes; }
}

sealed class Dialer(SocketSetOptions o) : SocketSet(o)
{
    public readonly ManualResetEventSlim Opened = new(false);
    public volatile bool Closed;
    protected override void OnConnect(ref ConnectContext ctx) => Opened.Set();
    protected override void OnClosed(Connection c) => Closed = true;
}
