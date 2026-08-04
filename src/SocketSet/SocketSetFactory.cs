using System.Net;
#if NET
using SocketSets.Epoll;
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
    public static SocketSetFactory IoUring => IoUringFactory.Instance;

    /// <summary>Windows IOCP backend (raw Winsock, bypassing managed sockets).</summary>
    public static SocketSetFactory WindowsIocp => WindowsIocpFactory.Instance;

    /// <summary>Windows RIO backend (Registered I/O; TCP-only, opt-in; not selected by <see cref="Default"/>).
    /// Targets low latency: it drains completions in user mode and services each inbound the instant it
    /// arrives. That eagerness is a deliberate trade - under deep-pipelined small-message load it reads
    /// ~one message per <c>recv</c> where a batching backend (e.g. <see cref="Managed"/>) coalesces several, so it
    /// wins latency but trails on bulk throughput. Best for latency-sensitive request/response traffic, or where
    /// the source does not suffer excessive packet fragmentation.</summary>
    public static SocketSetFactory WindowsRio => WindowsRioFactory.Instance;
#endif

#if NET
    /// <summary>Linux epoll backend - the fallback for hosts where io_uring is unavailable (old kernel,
    /// the io_uring_disabled sysctl, or a container seccomp profile that blocks it, which is Docker's
    /// default). Readiness-based rather than completion-based, but keeps the single-owner loop-thread
    /// shard model the other native backends use.</summary>
    public static SocketSetFactory Epoll => EpollFactory.Instance;
#endif

    /// <summary>Portable .NET managed-socket (SAEA) fallback.</summary>
    public static SocketSetFactory Managed => ManagedSocketFactory.Instance;

    private static SocketSetFactory? _default;

    /// <summary>Picks the best available backend for this host: io_uring where the
    /// kernel actually supports the features we need, otherwise the managed fallback.</summary>
    public static SocketSetFactory Default => _default ??= Detect();

    private static SocketSetFactory Detect()
#if NET
        // epoll sits between io_uring and managed: on Linux without io_uring (old kernel, the disable
        // sysctl, or a container seccomp profile) it keeps the native loop-thread shard model rather
        // than dropping all the way to the callback-driven managed backend.
        => WindowsIocp.IsSupported ? WindowsIocp
         : IoUring.IsSupported ? IoUring
         : Epoll.IsSupported ? Epoll
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
    /// Whether <see cref="SocketSet.Listen"/> should bind <paramref name="endpoint"/> on <em>every</em>
    /// shard — reuse-port multi-bind, where the kernel load-balances accepts across the per-shard
    /// listeners — rather than binding it on a single shard that bounces accepted connections
    /// round-robin. Only io_uring does this (Linux <c>SO_REUSEPORT</c>), and only for IP: Windows has no
    /// reuse-port, and AF_UNIX can't multi-bind on any platform. Default false (single listener), which
    /// also naturally spreads multiple <em>distinct</em> listen endpoints across shards instead of
    /// piling them all on one.
    /// </summary>
    public virtual bool CanMultiBind(EndPoint endpoint, SocketSetOptions options) => false;

    /// <summary>
    /// Upper bound on the number of shards this backend wants, regardless of
    /// <see cref="SocketSetOptions.Shards"/>. io_uring scales per core (unbounded);
    /// the managed fallback reports 1, so it binds a single listener and skips the
    /// reuse-port multi-bind that only io_uring does natively.
    /// </summary>
    public virtual int MaxShards => int.MaxValue;

    /// <summary>
    /// The buffer sizes and pool depths this backend wants when the caller has not chosen them. Every
    /// zero-valued size on <see cref="SocketSetOptions"/> is filled from here, once, in the
    /// <see cref="SocketSet"/> constructor - so a shard never sees a sentinel and no backend has to know
    /// this mechanism exists.
    ///
    /// The base returns <see cref="BufferGeometry.Default"/>, which is exactly what every backend got
    /// before backends could choose. **Override it only with a measurement**: the one override that
    /// exists (RIO) is backed by a 4.68x throughput result AND a correctness-gate failure at the old
    /// default, and the entry that motivated it spent days blocked on this mechanism rather than on
    /// evidence.
    /// </summary>
    public virtual BufferGeometry DefaultGeometry => BufferGeometry.Default;
}
