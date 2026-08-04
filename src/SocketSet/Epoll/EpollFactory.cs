#if NET // Linux epoll backend; compiled out of the netfx fallback build.
using System.Net;
using SocketSets.Native;

namespace SocketSets.Epoll;

/// <summary>
/// Linux epoll backend factory. This is the FALLBACK for hosts where io_uring is unavailable - an old
/// kernel, the <c>io_uring_disabled</c> sysctl, or (very common) a container runtime whose seccomp
/// profile blocks the io_uring syscalls outright, which is Docker's default.
///
/// It exists for uniformity as much as for speed. The managed backend would also work there, but it is
/// callback-driven with no loop thread of its own, which makes it the one backend needing a per-connection
/// lock around its TLS engine and unable to share shard-wide scratch. An epoll shard restores the
/// single-owner loop-thread model the other backends assume, so features are written once.
/// </summary>
internal sealed class EpollFactory : SocketSetFactory
{
    public static EpollFactory Instance = new();

    private EpollFactory()
    {
    }

    // Lazy so the probe's P/Invoke never runs at type-init on a non-Linux host.
    private readonly Lazy<bool> _supported = new(ProbeSupport);

    public override bool IsSupported => _supported.Value;

    public override SocketSetShard CreateShard(SocketSetOptions options)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("The epoll backend requires Linux.");
        return new EpollShard(options);
    }

    // Reuse-port multi-bind is IP-only: every shard binds the same TCP port and the kernel balances
    // accepts across them. AF_UNIX cannot multi-bind, so it stays single-listener.
    // Multi-bind IS reuse-port: every shard binds the same port and the kernel balances accepts. With
    // SocketSetOptions.ReusePort off, the second shard's bind would simply fail (EADDRINUSE), so the
    // capability has to follow the option rather than be asserted independently.
    public override bool CanMultiBind(EndPoint endpoint, SocketSetOptions options)
        => endpoint is IPEndPoint && options.ReusePort;

    /// <summary>Definitively probe rather than infer from a version string: create a throwaway epoll fd.
    /// epoll has been in Linux since 2.6 and is not gated by a sysctl, so this only really distinguishes
    /// Linux from everything else - and catches the case where libc is absent or the call is blocked.</summary>
    private static unsafe bool ProbeSupport()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            int fd = LibC.epoll_create1(LibC.EPOLL_CLOEXEC);
            if (fd < 0) return false;
            LibC.close(fd);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
#endif
