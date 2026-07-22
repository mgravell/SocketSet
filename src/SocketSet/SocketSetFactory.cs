#if NET
using SocketSets.IoUring;
using SocketSets.Windows;
#endif
using SocketSets.Managed;

namespace SocketSets;

public abstract class SocketSetFactory
{
    protected SocketSetFactory()
    {
    }

#if NET
    /// <summary>io_uring backend (Linux only).</summary>
    public static SocketSetFactory IoUring { get; } = IoUringFactory.Instance;

    /// <summary>Windows IOCP backend (raw Winsock, bypassing managed sockets).</summary>
    public static SocketSetFactory WindowsIocp { get; } = WindowsIocpFactory.Instance;
#endif

    /// <summary>Portable .NET managed-socket (SAEA) fallback.</summary>
    public static SocketSetFactory Managed { get; } = ManagedSocketFactory.Instance;

    private static SocketSetFactory? _default;

    /// <summary>Picks the best available backend for this host: io_uring where the
    /// kernel actually supports the features we need, otherwise the managed fallback.</summary>
    public static SocketSetFactory Default => _default ??= Detect();

    private static SocketSetFactory Detect()
#if NET
        => WindowsIocp.IsSupported ? WindowsIocp
         : IoUring.IsSupported ? IoUring
         : Managed;
#else
        => Managed;
#endif

    /// <summary>Whether this backend can actually run on the current host (OS + kernel features).
    /// Cheap and safe to read on any platform (backends probe lazily); <see cref="CreateShard"/>
    /// throws <see cref="PlatformNotSupportedException"/> if a backend is chosen where it isn't
    /// supported, so callers can pre-check here rather than fail at construction.</summary>
    public abstract bool IsSupported { get; }

    public abstract SocketSetShard CreateShard(SocketSetOptions options);

    /// <summary>
    /// Whether the backend needs the <see cref="SocketSet"/> to spin up a dedicated
    /// pump thread per shard that runs <see cref="SocketSetShard.OnRun"/>. io_uring is
    /// thread-per-shard (true); a callback-driven backend such as managed SAEA drives
    /// itself off the thread pool and wants no threads spun up for it (false).
    /// </summary>
    public virtual bool UsesWorkerThreads => true;

    /// <summary>
    /// Upper bound on the number of shards this backend wants, regardless of
    /// <see cref="SocketSetOptions.Shards"/>. io_uring scales per core (unbounded);
    /// the managed fallback reports 1, so it binds a single listener and skips the
    /// reuse-port multi-bind that only io_uring does natively.
    /// </summary>
    public virtual int MaxShards => int.MaxValue;
}
