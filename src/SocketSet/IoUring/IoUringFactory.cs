#if NET // io_uring is a Linux + modern-.NET backend; compiled out of the netfx fallback build.
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SocketSets.Native;

namespace SocketSets.IoUring;

internal sealed class IoUringFactory : SocketSetFactory
{
    public static IoUringFactory Instance = new();

    private IoUringFactory()
    {
    }

    // Probe once, lazily. The Lazy is created at construction but doesn't run ProbeSupport until
    // IsSupported is first read — crucial because Instance is built at type-init on every platform
    // (via SocketSetFactory.IoUring), and the probe's syscall P/Invoke would throw on Windows.
    private readonly Lazy<bool> _supported = new(ProbeSupport);

    public override bool IsSupported => _supported.Value;

    public override SocketSetShard CreateShard(SocketSetOptions options)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "The io_uring backend requires a Linux kernel with io_uring (SINGLE_ISSUER | DEFER_TASKRUN) support.");
        return new IoUringShard(options);
    }

    // Reuse-port multi-bind is IP-only: every shard binds the same TCP port and the kernel balances
    // accepts. AF_UNIX can't multi-bind (a second bind to the path fails), so it stays single-listener.
    public override bool CanMultiBind(EndPoint endpoint) => endpoint is IPEndPoint;

    /// <summary>
    /// True only if this host can actually run the io_uring backend with the features we
    /// need. We don't parse kernel version strings — we respect the disable sysctl and
    /// then definitively probe by creating a throwaway ring with the exact setup flags we
    /// use (SINGLE_ISSUER | DEFER_TASKRUN, ~6.1+). Anything less falls back to managed.
    /// </summary>
    private static unsafe bool ProbeSupport()
    {
        if (!OperatingSystem.IsLinux()) return false;

        // /proc/sys/kernel/io_uring_disabled: 1 = disabled for unprivileged, 2 = fully off.
        try
        {
            var disabled = System.IO.File.ReadAllText("/proc/sys/kernel/io_uring_disabled").Trim();
            if (disabled is "1" or "2") return false;
        }
        catch
        {
            // Knob absent (older kernel) — not disabled; fall through to the probe.
        }

        try
        {
            LibC.io_uring_params p = default;
            p.flags = LibC.IORING_SETUP_SINGLE_ISSUER | LibC.IORING_SETUP_DEFER_TASKRUN;
            nint fd = LibC.io_uring_setup(LibC.SYS_io_uring_setup, 2, &p);
            if (fd < 0) return false;
            LibC.close((int)fd);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static int Bind(EndPoint endpoint, int backlog) => endpoint switch
        {
            IPEndPoint ip => Bind(ip, backlog),
            UnixDomainSocketEndPoint uds => Bind(uds, backlog),
            _ => throw new NotSupportedException(endpoint.GetType().Name)
        };

    /// <summary>
    /// Create, bind and listen on <paramref name="ip"/> — a TCP socket with Nagle
    /// disabled. TCP_NODELAY is set on the listener because Linux propagates it to
    /// accepted sockets, which keeps it off the accept hot path. (The AF_UNIX
    /// overload has no Nagle, so the option is N/A there.)
    /// </summary>
    private static unsafe int Bind(IPEndPoint ip, int backlog)
    {
        int fd = LibC.socket((ushort)ip.AddressFamily, LibC.SOCK_STREAM, LibC.IPPROTO_TCP);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "socket() failed");

        int one = 1;
        LibC.setsockopt(fd, LibC.SOL_SOCKET, LibC.SO_REUSEADDR, &one, sizeof(int));
        LibC.setsockopt(fd, LibC.SOL_SOCKET, LibC.SO_REUSEPORT, &one, sizeof(int));
        // Disable Nagle so request/response echoes are not held for coalescing
        // (Nagle + delayed-ACK is the classic ~40ms ping-pong stall). Inherited
        // by every socket accept() returns from this listener.
        LibC.setsockopt(fd, LibC.IPPROTO_TCP, LibC.TCP_NODELAY, &one, sizeof(int));

        var addr = new LibC.SockAddrIn
        {
            sin_family = checked((ushort)ip.AddressFamily),
            sin_port = LibC.Htons(checked((ushort)ip.Port)),
            sin_addr = 0, // INADDR_ANY TODO: use the actual IP
        };
        if (LibC.bind(fd, &addr, 16) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "IP bind() failed");
        if (LibC.listen(fd, backlog) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "IP listen() failed");

        return fd;
    }

    private static unsafe int Bind(UnixDomainSocketEndPoint uds, int backlog)
    {
        int fd = LibC.socket(LibC.AF_UNIX, LibC.SOCK_STREAM, 0);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"socket(AF_UNIX) failed");

        // No SO_REUSEPORT/REUSEADDR: an abstract address is freed as soon as the
        // last holder closes, so there is nothing to reuse or clean up. No
        // TCP_NODELAY either — AF_UNIX has no Nagle.
        UnixSocketFile.PrepareForBind(uds.ToString()); // clear a stale filesystem socket file (no-op for abstract)
        LibC.SockAddrUn addr;
        uint len = LibC.SockAddrUn.Init(&addr, uds.ToString());
        if (LibC.bind(fd, &addr, len) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"UDS bind(AF_UNIX) failed");
        if (LibC.listen(fd, backlog) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"UDS listen() failed");

        return fd;
    }
}
#endif