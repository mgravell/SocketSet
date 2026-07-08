using FastNet.Native;

namespace FastNet.Transport;

/// <summary>
/// Runs N independent <see cref="EchoServer"/> shards behind a single port via
/// SO_REUSEPORT. Each shard owns its own listening socket, ring, buffer pool
/// and dedicated thread; the kernel load-balances new connections across the
/// REUSEPORT sockets. Share-nothing — there is no cross-thread SQE submission
/// or fd handoff, so throughput scales with cores instead of being bottlenecked
/// on one drain loop.
///
/// This is why REUSEPORT beats round-robin-at-accept: round-robin would make the
/// acceptor hand each accepted fd to another shard's ring, and an SQ ring is only
/// safe to touch from its owning thread. REUSEPORT pushes the balancing into the
/// kernel and keeps every shard fully independent.
/// </summary>
internal sealed class ShardedEchoServer : IDisposable
{
    private readonly EchoServer[] _shards;
    private readonly Thread[] _threads;
    private readonly bool _pin;

    public ShardedEchoServer(int port, int shards, int maxConnections, int bufferSize, bool pin, string? udsName = null)
    {
        if (shards < 1) shards = 1;

        // note SO_REUSEPORT sharding is an AF_INET(6) feature: a single abstract UDS
        // name can only be bound once (a second bind gets EADDRINUSE), so the
        // kernel can't fan connections across per-shard listeners; instead, in UDS mode
        // only the first shard acts as the listener - we give it the array of peers, and
        // allow it to throw work at them
        _pin = pin;

        // maxConnections is the total across the server; REUSEPORT only balances
        // approximately, so give each shard its share plus headroom.
        int perShard = Math.Max(64, (maxConnections + shards - 1) / shards * 2);

        _shards = new EchoServer[shards];
        _threads = new Thread[shards];
        for (int i = 0; i < shards; i++)
            _shards[i] = new EchoServer(port, perShard, bufferSize, shardId: i, udsName: udsName,
                udsName is null ? null : _shards);
    }

    public void Initialize()
    {
        foreach (var s in _shards) s.Initialize();
    }

    public void Run()
    {
        int cpuCount = Environment.ProcessorCount;
        for (int i = 0; i < _threads.Length; i++)
        {
            int idx = i;
            var t = new Thread(() =>
            {
                // One ring per core: optionally pin the drain loop so it is not
                // migrated off the CPU whose run queue its completions land on.
                if (_pin) LibC.PinCurrentThreadToCpu(idx % cpuCount);
                _shards[idx].Run();
            })
            {
                Name = $"io_uring-shard-{idx}",
            };
            _threads[i] = t;
            t.Start();
        }
        foreach (var t in _threads) t.Join();
    }

    public void Stop()
    {
        foreach (var s in _shards) s.Stop();
    }

    public void Dispose()
    {
        foreach (var s in _shards) s?.Dispose();
    }
}
