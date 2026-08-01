using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using RESPite.Buffers;

// Two benches, selected by arg:
//   (no arg)      single-threaded write->commit->consume->discard CYCLE, CycleBuffer vs Pipe.
//                 Measures buffer MACHINERY cost only (NOT cross-thread coordination).
//   xthread       producer thread -> consumer thread handoff of N payloads, with backpressure.
//                 CycleBuffer here is wrapped in a lock + condition-variable SPSC (the coordination
//                 the single-thread bench omits and the real integration must add) vs Pipe, which
//                 has that coordination built in. THIS is the honest comparison for the integration:
//                 if locked-CycleBuffer still beats Pipe, the machinery win survives the sync cost.

if (args.Length > 0 && args[0] == "xthread") { CrossThread.Run(); return; }

static (double ns, double alloc) Bench(long iters, Action<long> body)
{
    body(Math.Min(50_000, iters / 10 + 1)); // warmup
    var a0 = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    body(iters);
    sw.Stop();
    var a1 = GC.GetAllocatedBytesForCurrentThread();
    return (sw.Elapsed.TotalNanoseconds / iters, (a1 - a0) / (double)iters);
}

int[] sizes = { 64, 512, 4096, 65536 };
Console.WriteLine($"{"size",8} | {"CycleBuffer",22} | {"Pipe",22} | ratio");
Console.WriteLine(new string('-', 70));
foreach (var size in sizes)
{
    long iters = size <= 512 ? 5_000_000 : size <= 4096 ? 2_000_000 : 300_000;
    var payload = new byte[size];
    new Random(42).NextBytes(payload);

    // --- CycleBuffer ---
    var cb = CycleBuffer.Create();
    var (cbNs, cbAlloc) = Bench(iters, n =>
    {
        for (long i = 0; i < n; i++)
        {
            cb.Write(payload);                // producer writes payload (chunks internally if > block)
            var seq = cb.GetAllCommitted();   // what a zero-copy consumer would walk for writev
            _ = seq.Length;
            cb.DiscardCommitted(size);
        }
    });

    // --- Pipe (single-threaded write/flush then read/advance; flush+read complete synchronously) ---
    var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false,
        pauseWriterThreshold: 0, resumeWriterThreshold: 0));
    var w = pipe.Writer; var r = pipe.Reader;
    var (pNs, pAlloc) = Bench(iters, n =>
    {
        for (long i = 0; i < n; i++)
        {
            System.Buffers.BuffersExtensions.Write(w, payload); // producer writes payload
            var ft = w.FlushAsync();
            if (!ft.IsCompleted) ft.AsTask().GetAwaiter().GetResult();
            else _ = ft.Result;
            var rt = r.ReadAsync();
            ReadResult rr = rt.IsCompleted ? rt.Result : rt.AsTask().GetAwaiter().GetResult();
            r.AdvanceTo(rr.Buffer.End);
        }
    });

    Console.WriteLine($"{size,8} | {cbNs,10:F1} ns {cbAlloc,7:F1} B | {pNs,10:F1} ns {pAlloc,7:F1} B | {pNs / cbNs,4:F2}x");
}

// ---------------------------------------------------------------------------
// Cross-thread SPSC handoff bench.
// ---------------------------------------------------------------------------
static class CrossThread
{
    const long HighWater = 256 * 1024; // backpressure threshold (bytes in flight)

    public static void Run()
    {
        int[] sizes = { 64, 512, 4096, 65536 };
        Console.WriteLine("=== cross-thread SPSC handoff (producer -> consumer), backpressure @256KB ===");
        Console.WriteLine($"{"size",8} | {"CycleBuffer+lock",26} | {"Pipe",26} | ratio");
        Console.WriteLine(new string('-', 78));
        foreach (var size in sizes)
        {
            long payloads = size <= 512 ? 4_000_000 : size <= 4096 ? 1_500_000 : 120_000;
            var payload = new byte[size];
            new Random(42).NextBytes(payload);

            // warmup + measure, twice each, keep the better (less scheduler noise)
            double cb = Math.Min(RunCycle(payloads / 8, size, payload), double.MaxValue);
            cb = Math.Min(RunCycle(payloads, size, payload), RunCycle(payloads, size, payload));
            double pp = Math.Min(RunPipe(payloads / 8, size, payload), double.MaxValue);
            pp = Math.Min(RunPipe(payloads, size, payload), RunPipe(payloads, size, payload));

            double cbMiB = payloads * (double)size / (1024 * 1024) / (cb / 1000.0);
            double ppMiB = payloads * (double)size / (1024 * 1024) / (pp / 1000.0);
            double cbNs = cb * 1e6 / payloads, ppNs = pp * 1e6 / payloads;
            Console.WriteLine($"{size,8} | {cbNs,8:F1} ns {cbMiB,9:F0} MiB/s | {ppNs,8:F1} ns {ppMiB,9:F0} MiB/s | {ppNs / cbNs,4:F2}x");
        }
    }

    // returns elapsed milliseconds
    static double RunCycle(long payloads, int size, byte[] payload)
    {
        var cb = CycleBuffer.Create();
        var gate = new object();
        long committed = 0; bool done = false;

        var consumer = new Thread(() =>
        {
            long got = 0;
            while (got < payloads)
            {
                long len;
                lock (gate)
                {
                    while (committed == 0 && !done) Monitor.Wait(gate);
                    if (committed == 0) return;
                    var seq = cb.GetAllCommitted();
                    len = seq.Length;             // a real consumer walks this for writev
                    cb.DiscardCommitted(len);
                    committed -= len;
                    Monitor.Pulse(gate);          // wake producer if it was backpressured
                }
                got += len / size;
            }
        });
        consumer.IsBackground = true;

        var sw = Stopwatch.StartNew();
        consumer.Start();
        for (long i = 0; i < payloads; i++)
        {
            lock (gate)
            {
                while (committed >= HighWater) Monitor.Wait(gate);
                cb.Write(payload);
                committed += size;
                Monitor.Pulse(gate);
            }
        }
        lock (gate) { done = true; Monitor.PulseAll(gate); }
        consumer.Join();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    static double RunPipe(long payloads, int size, byte[] payload)
    {
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false,
            pauseWriterThreshold: HighWater, resumeWriterThreshold: HighWater / 2));
        var w = pipe.Writer; var r = pipe.Reader;

        var consumer = new Thread(() =>
        {
            long got = 0;
            while (got < payloads)
            {
                var rt = r.ReadAsync();
                ReadResult rr = rt.IsCompleted ? rt.Result : rt.AsTask().GetAwaiter().GetResult();
                long len = rr.Buffer.Length;
                got += len / size;
                r.AdvanceTo(rr.Buffer.End);   // consume everything available
                if (rr.IsCompleted) break;
            }
        });
        consumer.IsBackground = true;

        var sw = Stopwatch.StartNew();
        consumer.Start();
        for (long i = 0; i < payloads; i++)
        {
            System.Buffers.BuffersExtensions.Write(w, payload);
            var ft = w.FlushAsync();
            if (!ft.IsCompleted) ft.AsTask().GetAwaiter().GetResult();
            else _ = ft.Result;
        }
        w.Complete();
        consumer.Join();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}
