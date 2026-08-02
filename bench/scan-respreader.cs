#:property TargetFramework=net10.0
#:property PublishAot=false
#:property IsPackable=false
#:project ../../StackExchange.Redis/src/RESPite/RESPite.csproj
// ^ Directory.Build.props overrides as per verify-proxy.cs; sibling-checkout reference as per the proxy.
//
// FRAME-SCANNER BASELINE for RespReader (TODO: "tune the frame scanner"). This is the ISOLATION
// measurement that the proxy A/B cannot make: too much else is in that path to attribute a scanner
// change. Method per the TODO: representative frame mixes, single thread, MB/s and frames/s, so a
// tuning attempt has a number to beat and a shape to not regress.
//
// The mixes matter more than the mean: the scanner's cost profile differs between many tiny frames
// (per-frame overhead dominates — the -P 16 proxy regime and the SE.Redis reply stream) and fewer large
// bulks (per-byte skipping dominates). Report each mix separately; a "win" that trades one for the
// other needs to know it did that.
using System.Diagnostics;
using System.Text;
using RESPite.Messages;

const int WarmupIters = 3, Iters = 10;

var mixes = new (string Name, byte[] Payload, int Frames)[]
{
    Build("small-replies (+OK/:int/$5)", 200_000, i => i % 3 switch
    {
        0 => "+OK\r\n",
        1 => $":{i}\r\n",
        _ => "$5\r\nvalue\r\n",
    }),
    Build("get-commands (*2 $3 GET $16)", 100_000, i => $"*2\r\n$3\r\nGET\r\n$16\r\nkey:{i:d12}\r\n"),
    Build("bulk-1k", 20_000, i => $"${1024}\r\n{new string('x', 1024)}\r\n"),
    Build("bulk-16k", 2_000, i => $"${16384}\r\n{new string('x', 16384)}\r\n"),
    Build("mixed-pipeline (proxy shape)", 60_000, i => i % 4 switch
    {
        0 => "+PONG\r\n",
        1 => "$32\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n",
        2 => "*3\r\n$3\r\nSET\r\n$8\r\nkey:0001\r\n$32\r\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\r\n",
        _ => ":12345\r\n",
    }),
};

Console.WriteLine($"RespReader scan baseline — {Iters} iters after {WarmupIters} warmup, single thread");
Console.WriteLine($"{"mix",-34} {"MB/s",10} {"Mframes/s",12} {"ns/frame",10}");
foreach (var (name, payload, frames) in mixes)
{
    for (int w = 0; w < WarmupIters; w++) Scan(payload);
    long best = long.MaxValue;
    for (int it = 0; it < Iters; it++)
    {
        var sw = Stopwatch.StartNew();
        long scanned = Scan(payload);
        sw.Stop();
        if (scanned != frames) throw new InvalidOperationException($"{name}: scanned {scanned}, expected {frames}");
        if (sw.ElapsedTicks < best) best = sw.ElapsedTicks;
    }
    double secs = best / (double)Stopwatch.Frequency;
    Console.WriteLine($"{name,-34} {payload.Length / secs / 1e6,10:f0} {frames / secs / 1e6,12:f2} {secs * 1e9 / frames,10:f1}");
}

// Scan a buffer of complete frames, counting TOP-LEVEL frames — the unit the proxy forwards and the
// client dispatches. Uses only public RespReader surface, so it measures what a consumer can reach.
static long Scan(ReadOnlySpan<byte> buffer)
{
    long frames = 0;
    var reader = new RespReader(buffer);
    while (reader.TryMoveNext())
    {
        frames++;
        reader.SkipChildren();
    }
    return frames;
}

static (string, byte[], int) Build(string name, int count, Func<int, string> frame)
{
    var sb = new StringBuilder();
    for (int i = 0; i < count; i++) sb.Append(frame(i));
    return (name, Encoding.ASCII.GetBytes(sb.ToString()), count);
}
