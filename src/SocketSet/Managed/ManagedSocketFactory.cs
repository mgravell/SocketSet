namespace SocketSets.Managed;

/// <summary>
/// Portable fallback backend built on .NET <see cref="System.Net.Sockets.Socket"/> +
/// SocketAsyncEventArgs. It is callback-driven off the thread pool, so it wants no pump
/// threads, and it uses a single shard (one listener; no reuse-port). Not fast — just a
/// works-everywhere fallback for when io_uring isn't available.
/// </summary>
internal sealed class ManagedSocketFactory : SocketSetFactory
{
    public static ManagedSocketFactory Instance { get; } = new();

    private ManagedSocketFactory()
    {
    }

    public override SocketSetShard CreateShard(SocketSetOptions options) => new ManagedSocketShard(options);

    public override bool UsesWorkerThreads => false;

    public override int MaxShards => 1;
}
