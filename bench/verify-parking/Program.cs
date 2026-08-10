// verify-parking -- does a slow consumer SLOW THE PEER, or get the peer killed? (REVIEW.md D3, TODO item 1)
//
// The 2026-08-04 audit bounded inbound buffering (MaxInboundBufferBytes) and said so in the option's own
// doc: "THIS IS THE BOUND, NOT THE CURE." The bound drops a connection that runs too far ahead of the
// application, which is right for an abusive peer and wrong for a merely slow consumer. Parking is the
// cure: stop re-arming the receive, let the socket queue fill, and let the TCP window slow the SENDER.
//
// WHY A GATE, and what would be missed without one. Parking's failure mode is not a leak or a crash --
// it is a HANG. A connection that parks and never resumes stays open, healthy-looking, and silent
// forever; nothing in the smoke matrix can see it, because every cell there has a consumer that keeps up
// and so never parks at all. Cell 2 exists for exactly that.
//
// CELLS, per backend:
//   claim/reported     the library must SAY whether it can park (Connection.SupportsReceiveParking), and
//                      every later assertion is chosen from that answer. io_uring reports false by design
//                      (multishot receive; see IoUringConnection) and is asserted against the DOCUMENTED
//                      degradation instead, so its cells are not silently vacuous.
//   stalled/peer-held  with the consumer not reading, the sender must STOP MAKING PROGRESS while the
//                      connection stays ALIVE. This is the whole point, and it is what fails against the
//                      pre-parking code: there the bytes were staged in our memory until the cap, and the
//                      connection was then dropped. Two samples a second apart, so "slow" cannot pass as
//                      "stopped".
//   resume/completes   release the consumer, and every offered byte must arrive, in order, byte-exact.
//                      THE ANTI-HANG CELL: a park that never resumes fails here and nowhere else.
//   drain/control      the same client and the same volume against a consumer that DOES read must finish
//                      without ever stalling. Without this, cell "stalled/peer-held" could pass on a
//                      transport that was simply slow, or broken, rather than one that parked.
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using SocketSets;

const int Port = 19921;
const int Offer = 8 * 1024 * 1024;   // comfortably past socket buffers + pipe threshold + the 4MiB cap
const int Chunk = 64 * 1024;

int failures = 0;

foreach (var (factory, be) in GateBackends.All)
{
    bool canPark = false; // learned from the first accepted connection; picks the control's cap below

    // ---- 1/2/3: a consumer that reads NOTHING until told to ------------------------------------------
    {
        var o = new SocketSetOptions { Shards = 1, Factory = factory };
        using var srv = new Sink(o, stall: true);
        srv.Listen(new IPEndPoint(IPAddress.Loopback, Port));
        Thread.Sleep(300);

        var client = new Sender(Port, Offer, Chunk);
        client.Start();

        // Give it long enough to fill everything that can legitimately absorb bytes (socket send buffer,
        // socket receive buffer, the pipe up to its pause threshold, one shard recv buffer), then take two
        // samples a second apart: a parked transport makes NO progress between them.
        Thread.Sleep(2500);
        long s1 = client.Sent;
        Thread.Sleep(1000);
        long s2 = client.Sent;

        // The claim first, because every assertion below is chosen from it. Asserting that a connection was
        // actually SEEN is what stops the whole cell block passing vacuously on a backend that never
        // accepted anything.
        Report(ref failures, srv.Claimed, $"{be} claim/reported",
            srv.Claimed ? $"SupportsReceiveParking={srv.Parks}" : "no connection was accepted, so nothing was asserted");
        canPark = srv.Parks;

        bool alive = !srv.AnyClosed && !client.Faulted;
        bool stopped = s2 == s1 && s2 < Offer;

        if (srv.Parks)
        {
            Report(ref failures, alive && stopped, $"{be} stalled/peer-held",
                $"sender held at {Mib(s2)} of {Mib(Offer)} MiB, connection {(alive ? "alive" : "DROPPED")}"
                + (s2 == s1 ? "" : $" but still advancing (+{s2 - s1} B in 1s)"));
        }
        else
        {
            // io_uring: cannot park, and says so. The DOCUMENTED behaviour is the bound -- stage until
            // MaxInboundBufferBytes and drop. Asserting that keeps the cell meaningful rather than skipped:
            // if this backend ever silently started parking (or silently stopped bounding), this fails.
            Report(ref failures, !alive, $"{be} stalled/bounded (no parking)",
                !alive ? $"dropped at the {Mib(o.MaxInboundBufferBytes)} MiB bound after {Mib(s2)} MiB, as documented"
                       : $"NEITHER parked nor dropped: {Mib(s2)} MiB absorbed and still alive");
        }

        // Now let the consumer run. Everything offered must arrive, byte-exact -- including on the backend
        // that could not park, where the connection is already gone and the cell is skipped as such.
        srv.Release();
        if (srv.Parks)
        {
            bool done = client.WaitDone(TimeSpan.FromSeconds(30)) && srv.WaitReceived(Offer, TimeSpan.FromSeconds(30));
            Report(ref failures, done && srv.Corruptions == 0, $"{be} resume/completes",
                done ? (srv.Corruptions == 0 ? $"all {Mib(Offer)} MiB delivered in order after resume"
                                             : $"{srv.Corruptions} byte(s) wrong after resume")
                     : $"STALLED: {Mib(srv.Received)} of {Mib(Offer)} MiB after resume (a park that never resumed)");
        }
        else
        {
            Console.WriteLine($"  SKIP  {be + " resume/completes",-34} no park to resume (SupportsReceiveParking=false)");
        }
        client.Stop();
    }
    Thread.Sleep(300);

    // ---- 4: THE CONTROL -- the same volume against a consumer that drains -----------------------------
    //
    // On a backend that CANNOT park, the control runs with the cap DISABLED. Found on 2026-08-10, the
    // first time the io_uring cells ever ran (this rig was written on Windows): a loopback sender bursts
    // faster than even a promptly-draining consumer is scheduled, so staging can blow through
    // MaxInboundBufferBytes and the connection is dropped BY DESIGN at 6.8-7.9 of 8 MiB -- four runs out
    // of four. That is the same documented degradation cell 2 already asserts (stalled/bounded); this
    // cell's job is to prove the TRANSPORT delivers when nothing is wedged, so the bound comes out of the
    // equation rather than the cell asserting a coin-flip. The real cure is TODO 0a (soft parking), and
    // when it lands, canPark flips to true here and the control tightens back to the default cap by
    // itself -- which is exactly the deliberate gate-flip 0a wants.
    {
        var o = new SocketSetOptions { Shards = 1, Factory = factory };
        if (!canPark) o.MaxInboundBufferBytes = 0;
        using var srv = new Sink(o, stall: false);
        srv.Listen(new IPEndPoint(IPAddress.Loopback, Port + 1));
        Thread.Sleep(300);

        var client = new Sender(Port + 1, Offer, Chunk);
        var sw = Stopwatch.StartNew();
        client.Start();
        bool done = client.WaitDone(TimeSpan.FromSeconds(30)) && srv.WaitReceived(Offer, TimeSpan.FromSeconds(30));
        sw.Stop();
        Report(ref failures, done && srv.Corruptions == 0, $"{be} drain/control",
            done ? $"{Mib(Offer)} MiB straight through in {sw.Elapsed.TotalSeconds:0.0}s, never stalled"
                   + (canPark ? "" : " (cap disabled: cannot park, so the bound would race the consumer)")
                 : $"only {Mib(srv.Received)} MiB arrived (sender at {Mib(client.Sent)}, connection "
                   + $"{(srv.AnyClosed ? "CLOSED" : "alive")}, faulted={client.Faulted}) -- the CONTROL "
                   + "failed, so the cells above prove nothing");
        client.Stop();
    }
    Thread.Sleep(300);

    // ---- 5: PARKED, THEN THE PEER VANISHES -----------------------------------------------------------
    //
    // ADDED after the 2026-08-05 churn soak came back clean and I read what it had actually covered. A
    // churn soak never parks -- its consumer is an echo callback that always keeps up -- so 1.2M churned
    // connections said nothing about the state parking newly creates: a live connection with NO RECEIVE
    // OUTSTANDING. On IOCP and RIO that is precisely the condition defer-recycle reasons about, because
    // parking clears RecvArmed with no completion coming to clear it later. A slot freed while an op is
    // still in flight, or never freed at all, is the shape of item 0e.
    //
    // And a completion backend structurally CANNOT notice the peer going away while parked: noticing
    // requires an armed receive, and there is not one. So the close is discovered only when the consumer
    // catches up and the receive is re-armed -- which is what this cell asserts actually happens, rather
    // than the connection being stranded. That is the anti-leak assertion, and nothing else here makes it.
    if (true)
    {
        var o = new SocketSetOptions { Shards = 1, Factory = factory };
        using var srv = new Sink(o, stall: true);
        srv.Listen(new IPEndPoint(IPAddress.Loopback, Port + 2));
        Thread.Sleep(300);

        var client = new Sender(Port + 2, Offer, Chunk);
        client.Start();
        Thread.Sleep(2500);                     // long enough to be parked (see cell 2's samples)

        bool parkedAndAlive = srv.Parks && !srv.AnyClosed;
        client.Stop();                          // the peer disappears WHILE PARKED
        Thread.Sleep(1000);

        // Before the consumer moves, nothing can have noticed on a completion backend. Recorded rather
        // than asserted: epoll CAN notice (EPOLLHUP is delivered whether or not it was requested), so
        // requiring either answer here would be asserting a backend's internals rather than the contract.
        bool noticedWhileParked = srv.AnyClosed;

        srv.Release();                          // consumer catches up -> receive re-arms -> EOF surfaces
        bool reaped = srv.WaitClosed(TimeSpan.FromSeconds(15));

        if (srv.Parks)
        {
            Report(ref failures, parkedAndAlive && reaped, $"{be} parked/peer-vanishes",
                reaped ? $"torn down after resume (noticed while parked: {noticedWhileParked})"
                       : "STRANDED: still open 15s after the peer vanished and the consumer caught up");
        }
        else
        {
            Console.WriteLine($"  SKIP  {be + " parked/peer-vanishes",-34} cannot park, so the state does not arise");
        }
        client.Stop();
    }
    Thread.Sleep(300);
}

Console.WriteLine(failures == 0 ? "\n=== verify-parking: ALL PASS ===" : $"\n=== verify-parking: {failures} FAILURE(S) ===");
return failures == 0 ? 0 : 1;

static string Mib(long bytes) => (bytes / (double)(1 << 20)).ToString("0.00");

static void Report(ref int failures, bool ok, string cell, string detail)
{
    if (!ok) failures++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {cell,-34} {detail}");
}

/// <summary>A pipe-mode server whose consumer either reads immediately or not until released. The
/// payload is a byte counter (i mod 251) so out-of-order or dropped bytes are detectable, not merely a
/// wrong total.</summary>
sealed class Sink : SocketSet
{
    private readonly bool _stall;
    private readonly ManualResetEventSlim _gate = new(false);
    private long _received;
    private int _corruptions;

    public Sink(SocketSetOptions o, bool stall) : base(o) => _stall = stall;

    /// <summary>What the transport CLAIMS about parking, read from the live connection at accept. Every
    /// assertion in this rig is chosen from it, so a backend cannot pass by being judged on the wrong
    /// contract.</summary>
    public volatile bool Parks;
    public volatile bool AnyClosed;
    public volatile bool Claimed;

    public long Received => Interlocked.Read(ref _received);
    public int Corruptions => Volatile.Read(ref _corruptions);

    public void Release() => _gate.Set();

    public bool WaitClosed(TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < within)
        {
            if (AnyClosed) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    public bool WaitReceived(long target, TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < within)
        {
            if (Received >= target) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    protected override void OnAccept(ref AcceptContext ctx)
    {
        Parks = ctx.Connection.SupportsReceiveParking;
        Claimed = true;

        // A SMALL pause threshold on purpose: it is what makes the inbound flush go async early, which is
        // the signal the bridge parks on. With the framework default the rig would have to push megabytes
        // before anything interesting happened, and the stall sample would be measuring buffer sizes.
        var inbound = new Pipe(new PipeOptions(
            pauseWriterThreshold: 64 * 1024, resumeWriterThreshold: 32 * 1024,
            useSynchronizationContext: false));
        // Nothing is ever sent back; the transport still needs an Input to read from, and an open,
        // never-written pipe is simply a pump that waits forever.
        var outbound = new Pipe();

        ctx.UsePipe(new Duplex(outbound.Reader, inbound.Writer));
        _ = Task.Run(() => ConsumeAsync(inbound.Reader));
    }

    protected override void OnClosed(Connection connection) => AnyClosed = true;

    private async Task ConsumeAsync(PipeReader reader)
    {
        if (_stall) await Task.Run(() => _gate.Wait());

        long seen = Interlocked.Read(ref _received);
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync();
                foreach (var seg in result.Buffer)
                {
                    var span = seg.Span;
                    for (int i = 0; i < span.Length; i++, seen++)
                        if (span[i] != (byte)(seen % 251)) Interlocked.Increment(ref _corruptions);
                }
                Interlocked.Exchange(ref _received, seen);
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch { /* torn down; the cells read Received/Corruptions */ }
    }

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input => input;
        public PipeWriter Output => output;
    }
}

/// <summary>A plain blocking-socket sender on its own thread. Blocking is the point: when the server's
/// window closes, Send() stops returning and <see cref="Sent"/> stops moving, which is the observable the
/// stall cell samples.</summary>
sealed class Sender(int port, int total, int chunk)
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private Thread _thread;
    private long _sent;
    private volatile bool _done, _faulted, _stop;

    public long Sent => Interlocked.Read(ref _sent);
    public bool Faulted => _faulted;

    public void Start()
    {
        _socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        _thread = new Thread(Pump) { IsBackground = true };
        _thread.Start();
    }

    private void Pump()
    {
        var buf = ArrayPool<byte>.Shared.Rent(chunk);
        try
        {
            long pos = 0;
            while (pos < total && !_stop)
            {
                int n = (int)Math.Min(chunk, total - pos);
                for (int i = 0; i < n; i++) buf[i] = (byte)((pos + i) % 251);
                int off = 0;
                while (off < n && !_stop)
                {
                    int w = _socket.Send(buf, off, n - off, SocketFlags.None);
                    if (w <= 0) { _faulted = true; return; }
                    off += w;
                    Interlocked.Add(ref _sent, w);
                }
                pos += n;
            }
            _done = true;
            _socket.Shutdown(SocketShutdown.Send); // EOF, so the consumer's ReadAsync completes
        }
        catch { _faulted = true; }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    public bool WaitDone(TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < within)
        {
            if (_done) return true;
            if (_faulted) return false;
            Thread.Sleep(25);
        }
        return false;
    }

    public void Stop()
    {
        _stop = true;
        try { _socket.Close(); } catch { }
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
