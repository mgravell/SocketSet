using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using RESPite.Buffers;

// Isolation micro-bench: the write -> commit -> consume(whole sequence) -> discard CYCLE, single-threaded,
// for CycleBuffer vs System.IO.Pipelines.Pipe. This measures the buffer MACHINERY cost (Pipe's async
// flush/read state machine + scheduler vs CycleBuffer's direct Commit/GetAllCommitted), not cross-thread
// coordination (that needs the SPSC wrapper). Pre-registered question: is CycleBuffer's per-op cost +
// allocations meaningfully lower? If not, the integration won't move the concurrency numbers.

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
