#:property TargetFramework=net10.0
#:property TargetFrameworks=net10.0
#:property PublishAot=false
#:property IsPackable=false
#:project ../src/SocketSet.StackExchange.Redis/SocketSet.StackExchange.Redis.csproj
// Functional gate for the provisional Tunnel transport shape: dial a real Garnet through
// SocketSetClientTransport, exercise every member of the contract, and assert the semantics the design
// doc claims — push receive, any-thread staged writes with explicit flush, batch-end firing, close
// notification exactly once. Exit 0 = all pass.
using System.Net;
using System.Text;
using SocketSets;
using SocketSets.StackExchangeRedis;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 7379;

int failures = 0;
void Report(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-24} {detail}");
    if (!ok) failures++;
}

var transport = await SocketSetClientTransport.ConnectAsync(
    new IPEndPoint(IPAddress.Parse(host), port),
    new SocketSetOptions { Shards = 1, Factory = SocketSetFactory.IoUring });

var rx = new Receiver();
transport.Start(rx);

// 1: single round trip — push delivery, staged write + flush
"*1\r\n$4\r\nPING\r\n"u8.ToArray().AsSpan().CopyTo(transport.Output.GetSpan(32));
transport.Output.Advance(14);
Report("flush", transport.Flush());
Report("ping-roundtrip", await rx.WaitFor("+PONG\r\n", TimeSpan.FromSeconds(5)));

// 2: pipelined burst — one flush for many commands, replies counted, batch-end observed
rx.Reset();
const int N = 1000;
for (int i = 0; i < N; i++)
{
    var cmd = Encoding.ASCII.GetBytes($"*3\r\n$3\r\nSET\r\n$6\r\ntt:{i:d3}\r\n$2\r\nok\r\n");
    var span = transport.Output.GetSpan(cmd.Length);
    cmd.CopyTo(span);
    transport.Output.Advance(cmd.Length);
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

sealed class Receiver : ITransportReceiver
{
    private readonly List<byte> _all = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int BatchEnds;
    public int ClosedCount;

    public bool OnReceived(ReadOnlySpan<byte> payload)
    {
        lock (_all) { foreach (var b in payload) _all.Add(b); }
        _signal.Release();
        return true;
    }

    public void OnBatchEnd() => Interlocked.Increment(ref BatchEnds);

    public void OnClosed(Exception? fault)
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
