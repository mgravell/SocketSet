// measure-parking -- what does receive parking COST while it is CONTINUOUSLY engaged?
//
// THE GAP THIS FILLS. Parking shipped 2026-08-04 measured only where it barely happens: every rig in
// this repo has a consumer that keeps up, so parks ran at 0.2% of receives and all three A/B legs came
// back flat. That establishes parking is free when it is IDLE, which is not the interesting claim. The
// interesting claim is what it costs when a consumer is persistently behind -- which is the only state
// parking exists for, and the state an adversary would sit in.
//
// WHY THERE IS NO BEFORE/AFTER HERE, and it is not an omission. The pre-parking build does not SURVIVE
// this workload: a persistently-behind consumer drives inbound staging past MaxInboundBufferBytes and
// the connection is dropped. A throughput A/B against a leg that dies is not a comparison, and the fact
// that it dies is the strongest available statement of what parking bought. So the control is not an
// older commit; it is the SAME build at an operating point where parking does not engage.
//
// THE TWO LEGS, same bytes, same consumer rate, interleaved, pass 1 discarded:
//   pressured  the producer sends as fast as the socket will take it; the CONSUMER is throttled to R.
//              Parking is engaged continuously -- park, resume, park, resume.
//   paced      the PRODUCER is throttled to R and the consumer runs free. Same bytes, same average
//              rate, same wall-clock floor (total/R), same code. The only difference is WHICH SIDE is
//              the limiter, and therefore whether back-pressure exists at all.
//
// The control was wrong on the first attempt and the rig said so, which is the argument for asserting
// it rather than eyeballing it: pacing the producer to EXACTLY the consumer's rate leaves both sides at
// the same speed, so ordinary jitter still backs the pipe up and the "control" parked 304 times against
// the pressured leg's 425. A control that reproduces the condition it is controlling for is not a
// control. Making the consumer unthrottled instead removes back-pressure by construction while keeping
// the bytes and the duration matched.
//
// WHAT SEPARATION WOULD MEAN. If parking is cheap, both legs deliver the same bytes in about the same
// wall-clock (both are bounded by the consumer, not the transport) and cost about the same CPU. If
// parking is expensive, `pressured` pays for it in CPU per MiB, or in goodput falling short of the drain
// rate it should be tracking.
//
// GATED ON THE FAST PATH BEING TAKEN (house rule 2), because otherwise this measures nothing: the
// pressured leg MUST report parks and the paced leg must report far fewer. Connection.TotalReceiveParks
// is the process-wide counter, read as a delta around each pass. A run where the pressured leg parks
// zero times is a BROKEN RIG, not a fast transport, and it says so.
//
// THERE IS NO CPU COLUMN, AND THAT IS A RESULT (2026-08-06). It had one, with a caveat saying that both
// legs share the rate-limit confound "identically, so the DIFFERENCE is meaningful in a way neither
// absolute value is". THAT CLAIM WAS WRONG, and the scored run falsified it twice over:
//
//   1. The instrument cannot see this workload AT ALL. The column reported EXACT ZEROS for 4-second
//      windows that moved 192 MiB through a byte-by-byte verifier -- arithmetically impossible -- mixed
//      with multi-second values for identical work. bench/probe-cputime.cs isolates the mechanism:
//      Windows charges CPU in whole ~15.6 ms scheduler quanta to whichever thread is running when the
//      tick fires, so CONTINUOUS burn is measured accurately (0.90-0.98x of true cost) while BURSTY work
//      of identical known cost is charged 1.89-3.86x, varying 2x between repetitions. Everything in a
//      rate-limited rig is bursty by construction. Environment.CpuUsage agrees with
//      Process.TotalProcessorTime to the millisecond -- they are the same syscall, so swapping one for
//      the other is not a fix and is not cross-validation either.
//   2. Even with a working instrument the two legs are NOT the same operating point for CPU. Which side
//      is the limiter changes the I/O GRANULARITY: the pressured leg's 1 MiB pipe threshold aggregates
//      into few large reads, while the paced leg's sleep-quantised producer drives many small ones. That
//      difference is caused by the very thing that distinguishes the legs, so it cannot be subtracted
//      out. A CPU A/B here would need a rig where both legs run unlimited, which is a different rig.
//
// So this rig answers the THROUGHPUT question (which it does cleanly, and which was the open one) and
// declines the CPU one. The raw per-window ms spread is still printed, as the evidence for declining
// rather than as a number to quote.
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using SocketSets;

int totalMib = ArgInt(args, "--mib", 96);          // bytes moved per pass, per leg
int rateMib = ArgInt(args, "--rate", 48);          // consumer drain rate, MiB/s
int passes = ArgInt(args, "--passes", 5);          // pass 1 is discarded as warm-up
int port = ArgInt(args, "--port", 19941);

long total = (long)totalMib << 20;
Console.WriteLine($"parking cost: {totalMib} MiB per leg, consumer drains at {rateMib} MiB/s, "
                + $"{passes} passes (first discarded)");
Console.WriteLine($"  expected floor: {(double)totalMib / rateMib:n2}s per pass if the transport tracks the consumer perfectly");
Console.WriteLine();

int failures = 0;

foreach (var (factory, be) in GateBackends.All)
{
    var pressured = new List<Sample>();
    var paced = new List<Sample>();

    for (int pass = 1; pass <= passes; pass++)
    {
        // Interleave and swap which leg leads on alternate passes, so neither one is always the leg that
        // runs into a still-settling host (the same discipline as Compare-Commits.ps1).
        bool pressuredFirst = (pass % 2) == 1;
        for (int i = 0; i < 2; i++)
        {
            bool doPressured = pressuredFirst == (i == 0);
            var s = Leg.Run(factory, port++, total, rateMib, pace: !doPressured);
            if (pass == 1) continue;                     // warm-up
            (doPressured ? pressured : paced).Add(s);
        }
    }

    Report(be, "pressured", pressured);
    Report(be, "paced (control)", paced);

    // --- the assertions, which are what make the numbers above mean anything -------------------------
    long pressuredParks = Median(pressured.Select(x => (double)x.Parks));
    long pacedParks = Median(paced.Select(x => (double)x.Parks));

    Check(ref failures, pressured.All(x => x.Completed) && paced.All(x => x.Completed),
        $"{be} both legs deliver every byte",
        pressured.All(x => x.Completed) && paced.All(x => x.Completed)
            ? "all passes byte-complete"
            : "a leg did not deliver all bytes -- under the pre-parking build this is where it was DROPPED");

    Check(ref failures, pressuredParks > 0,
        $"{be} pressured leg actually parked",
        pressuredParks > 0 ? $"{pressuredParks:n0} parks (median)"
                           : "ZERO parks -- the rig did not create back-pressure, so it measured nothing");

    Check(ref failures, pressuredParks > pacedParks * 4,
        $"{be} the two legs are different operating points",
        $"pressured {pressuredParks:n0} vs paced {pacedParks:n0} parks -- the control must park far less, or it is not a control");

    // THE HEADLINE, ASSERTED RATHER THAN EYEBALLED. With the CPU column gone this is the whole result:
    // continuously parked, the transport still delivers at the consumer's own drain rate. Gated at 2%
    // because a parking bug that cost real throughput would not land within 2% of the floor -- it would
    // stall on a missed resume, which is parking's actual failure mode (REVIEW.md D3) and shows up here
    // as goodput far BELOW the rate, not as jitter around it.
    double pressuredRate = pressured.Select(x => x.MiBPerSec).OrderBy(x => x).ElementAt(pressured.Count / 2);
    Check(ref failures, pressuredRate >= rateMib * 0.98,
        $"{be} parked transport tracks the drain rate",
        $"{pressuredRate:n1} MiB/s against a {rateMib} MiB/s consumer ({pressuredRate / rateMib:p1} of it)");

    Console.WriteLine();
}

Console.WriteLine(failures == 0
    ? "=== measure-parking: assertions PASS (read the tables above for the cost) ==="
    : $"=== measure-parking: {failures} ASSERTION FAILURE(S) -- the cost numbers above are not trustworthy ===");
return failures == 0 ? 0 : 1;

static int ArgInt(string[] a, string name, int fallback)
{
    for (int i = 0; i < a.Length - 1; i++) if (a[i] == name && int.TryParse(a[i + 1], out int v)) return v;
    return fallback;
}

static long Median(IEnumerable<double> xs)
{
    var v = xs.OrderBy(x => x).ToArray();
    return v.Length == 0 ? 0 : (long)v[v.Length / 2];
}

static void Report(string backend, string leg, List<Sample> xs)
{
    if (xs.Count == 0) { Console.WriteLine($"  {backend} {leg}: no samples"); return; }
    var secs = xs.Select(x => x.Seconds).OrderBy(x => x).ToArray();
    var mibs = xs.Select(x => x.MiBPerSec).OrderBy(x => x).ToArray();
    // The CPU spread is printed as EVIDENCE, not as a measurement -- see the header. A spread that spans
    // an order of magnitude (routinely including an impossible 0) is the reason there is no core-us/MiB
    // number here, and printing it is what stops the column being quietly re-added by the next reader.
    var ms = xs.Select(x => x.CpuMs).OrderBy(x => x).ToArray();
    Console.WriteLine($"  {backend,-8} {leg,-16} {mibs[mibs.Length / 2],8:n1} MiB/s [{mibs[0]:n1}-{mibs[^1]:n1}]"
                    + $"  {secs[secs.Length / 2],6:n2}s"
                    + $"  parks {Median(xs.Select(x => (double)x.Parks)),7:n0}"
                    + $"  (cpu instrument unusable here: {ms[0]:n0}-{ms[^1]:n0} ms for identical work)");
}

static void Check(ref int failures, bool ok, string what, string detail)
{
    if (!ok) failures++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what,-44} {detail}");
}

readonly record struct Sample(double Seconds, double MiBPerSec, double CoreUsPerMib, double CpuMs, long Parks, bool Completed);

static class Leg
{
    internal static Sample Run(SocketSetFactory factory, int port, long total, int rateMib, bool pace)
    {
        var o = new SocketSetOptions { Shards = 1, Factory = factory };
        // Exactly one side is the limiter. Pressured: the consumer (so the transport is squeezed).
        // Paced: the producer (so the transport never is), with the consumer free to keep up.
        using var srv = new ThrottledSink(o, pace ? 0 : rateMib);
        srv.Listen(new IPEndPoint(IPAddress.Loopback, port));
        Thread.Sleep(250);

        long parks0 = Connection.TotalReceiveParks;
        var cpu0 = Environment.CpuUsage.TotalTime;   // retained only to print the spread; see the header
        var sw = Stopwatch.StartNew();

        var sender = new Producer(port, total, pace ? rateMib : 0);
        sender.Run();                                   // blocks until every byte is handed to the socket
        bool complete = srv.WaitReceived(total, TimeSpan.FromSeconds(120));

        sw.Stop();
        var cpu = Environment.CpuUsage.TotalTime - cpu0;
        long parks = Connection.TotalReceiveParks - parks0;
        sender.Stop();
        Thread.Sleep(150);

        double mib = total / (double)(1 << 20);
        return new Sample(sw.Elapsed.TotalSeconds, mib / sw.Elapsed.TotalSeconds,
                          cpu.TotalMilliseconds * 1000.0 / mib, cpu.TotalMilliseconds,
                          parks, complete && srv.Corruptions == 0);
    }
}

/// <summary>Pipe-mode server whose consumer drains at a FIXED rate, so the transport is held under
/// continuous back-pressure. The throttle is the whole point: an unthrottled consumer keeps up and
/// parking never fires, which is precisely the blind spot every other rig here has.</summary>
sealed class ThrottledSink : SocketSet
{
    private readonly int _rateMib;
    private long _received;
    private int _corruptions;

    public ThrottledSink(SocketSetOptions o, int rateMib) : base(o) => _rateMib = rateMib;

    public long Received => Interlocked.Read(ref _received);
    public int Corruptions => Volatile.Read(ref _corruptions);

    public bool WaitReceived(long target, TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < within)
        {
            if (Received >= target) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    protected override void OnAccept(ref AcceptContext ctx)
    {
        // 1 MiB pause threshold, IDENTICAL in both legs, and the size is load-bearing. At 64 KiB the
        // control also parked (343-399 times against the pressured leg's 424) because a paced producer
        // is BURSTY -- Thread.Sleep granularity means it emits a burst then waits -- and any burst past
        // the threshold trips an async flush whether or not the consumer is actually behind. Raising it
        // above the burst size makes parking mean "the consumer is behind" rather than "the producer
        // stuttered", which is the distinction this whole rig rests on. It matches the ASP.NET bridge's
        // own pauseWriterThreshold, so it is not an arbitrary number either.
        var inbound = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19,
            useSynchronizationContext: false));
        var outbound = new Pipe();                       // never written; the pump just waits
        ctx.UsePipe(new Duplex(outbound.Reader, inbound.Writer));
        _ = Task.Run(() => ConsumeAsync(inbound.Reader));
    }

    private async Task ConsumeAsync(PipeReader reader)
    {
        long seen = 0;
        var sw = Stopwatch.StartNew();
        double bytesPerMs = _rateMib * 1024.0 * 1024.0 / 1000.0;
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

                // Hold the drain to the target rate. Sleeping (rather than spinning) is deliberate: the
                // consumer must be SLOW, not BUSY, or the measurement is of a CPU-starved box.
                // _rateMib == 0 means unthrottled, which is the control leg's consumer.
                if (_rateMib > 0)
                {
                    double owedMs = seen / bytesPerMs - sw.Elapsed.TotalMilliseconds;
                    if (owedMs > 1) await Task.Delay((int)owedMs);
                }
                if (result.IsCompleted) break;
            }
        }
        catch { /* torn down */ }
    }

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input => input;
        public PipeWriter Output => output;
    }
}

/// <summary>Blocking-socket producer. <paramref name="paceMib"/> 0 means "as fast as the socket takes
/// it" (the pressured leg); non-zero paces the send to that rate, which is how the control reaches the
/// same operating point WITHOUT provoking back-pressure.</summary>
sealed class Producer(int port, long total, int paceMib)
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public void Run()
    {
        _socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        // Small chunks so the PACED leg emits smoothly rather than in sleep-quantised bursts; a bursty
        // producer looks like back-pressure to the pipe and contaminates the control.
        const int Chunk = 16 * 1024;
        var buf = ArrayPool<byte>.Shared.Rent(Chunk);
        var sw = Stopwatch.StartNew();
        double bytesPerMs = paceMib * 1024.0 * 1024.0 / 1000.0;
        try
        {
            long pos = 0;
            while (pos < total)
            {
                int n = (int)Math.Min(Chunk, total - pos);
                for (int i = 0; i < n; i++) buf[i] = (byte)((pos + i) % 251);
                int off = 0;
                while (off < n)
                {
                    int w = _socket.Send(buf, off, n - off, SocketFlags.None);
                    if (w <= 0) return;
                    off += w;
                }
                pos += n;
                if (paceMib > 0)
                {
                    double owedMs = pos / bytesPerMs - sw.Elapsed.TotalMilliseconds;
                    if (owedMs > 1) Thread.Sleep((int)owedMs);
                }
            }
            _socket.Shutdown(SocketShutdown.Send);
        }
        catch { /* connection gone; the caller's completeness check reports it */ }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    public void Stop() { try { _socket.Close(); } catch { } }
}
