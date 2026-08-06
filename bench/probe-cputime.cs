// CONFOUNDER PROBE: can Windows' process CPU accounting see BURSTY work at all?
//
// measure-parking's CPU column reported EXACT ZEROS for 4-second windows that moved 192 MiB through a
// byte-by-byte verifier, interleaved with multi-second values for identical work. Two earlier probe
// cuts falsified the obvious explanations: the instrument is accurate to 1.5% for continuous burn, on
// main threads AND pool threads, and Environment.CpuUsage agrees with Process.TotalProcessorTime to
// the millisecond on every sample (they are the same syscall, so that was never cross-validation).
//
// The remaining difference between the probe and the rig is the SHAPE of the work. Windows charges CPU
// time at the ~15.6 ms scheduler tick to whichever thread is running when the tick fires. A thread that
// works for 2 ms and then sleeps is usually asleep at tick time, so it can be charged nothing at all
// while doing real work. The rig is rate-limited by construction, so every thread in it is bursty.
//
// EXPECTED IF THE HYPOTHESIS HOLDS: the continuous row lands on its known CPU cost; the bursty rows do
// real, KNOWN, identical work and report wildly wrong (often near-zero) numbers.
// FALSIFIED IF: the bursty rows report their true cost, in which case the zeros are something else and
// this explanation must be thrown away.
using System.Diagnostics;

static long SpinWork(int iterations)          // identical work per call, no sleeping inside
{
    long acc = 0;
    for (int i = 0; i < iterations; i++) for (int k = 0; k < 1_000; k++) acc += k;
    return acc;
}

// Calibrate: how many units is ~1 ms of continuous CPU on this box?
var cal = Stopwatch.StartNew();
SpinWork(2_000);
double msPerUnit = cal.Elapsed.TotalMilliseconds / 2_000;
int unitsPerBurst = (int)(2.0 / msPerUnit);   // ~2 ms of work per burst

Console.WriteLine($"calibrated: ~{msPerUnit * 1000:n2} us per unit; burst = {unitsPerBurst:n0} units (~2 ms)");
Console.WriteLine();
Console.WriteLine("shape                       true CPU (stopwatch)   process counter   ratio");

foreach (var (label, bursty) in new[] { ("continuous", false), ("bursty 2ms work/8ms sleep", true) })
{
    for (int rep = 0; rep < 3; rep++)
    {
        const int Bursts = 200;               // identical total work in both shapes
        var c0 = Environment.CpuUsage.TotalTime;
        var busy = TimeSpan.Zero;
        for (int b = 0; b < Bursts; b++)
        {
            var sw = Stopwatch.StartNew();
            GC.KeepAlive(SpinWork(unitsPerBurst));
            busy += sw.Elapsed;
            if (bursty) Thread.Sleep(8);
        }
        var counted = Environment.CpuUsage.TotalTime - c0;
        Console.WriteLine($"  {label,-26}  {busy.TotalMilliseconds,10:n0} ms"
                        + $"   {counted.TotalMilliseconds,14:n0} ms"
                        + $"   {counted.TotalMilliseconds / busy.TotalMilliseconds,6:n2}x");
    }
}
