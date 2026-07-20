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

    public override SocketSetShard CreateShard(SocketSetOptions options) => new IoUringShard(options);

    public static int Bind(EndPoint endpoint) => endpoint switch
        {
            IPEndPoint ip => Bind(ip),
            UnixDomainSocketEndPoint uds => Bind(uds),
            _ => throw new NotSupportedException(endpoint.GetType().Name)
        };

    /// <summary>
    /// Create, bind and listen. When <paramref name="udsName"/> is non-null the
    /// listener is an abstract-namespace AF_UNIX socket (loopback proxy front
    /// end: no port churn, no TIME_WAIT, no socket file); otherwise it is a TCP
    /// socket on <paramref name="port"/> with Nagle disabled. TCP_NODELAY is set
    /// on the listener because Linux propagates it to accepted sockets, which
    /// keeps it off the accept hot path; UDS has no Nagle so the option is N/A.
    /// </summary>
    private static unsafe int Bind(IPEndPoint ip)
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
        if (LibC.listen(fd, 512) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "IP listen() failed");

        return fd;
    }

    private static unsafe int Bind(UnixDomainSocketEndPoint uds)
    {
        int fd = LibC.socket(LibC.AF_UNIX, LibC.SOCK_STREAM, 0);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"socket(AF_UNIX) failed");

        // No SO_REUSEPORT/REUSEADDR: an abstract address is freed as soon as the
        // last holder closes, so there is nothing to reuse or clean up. No
        // TCP_NODELAY either — AF_UNIX has no Nagle.
        LibC.SockAddrUn addr;
        uint len = LibC.SockAddrUn.Init(&addr, uds.ToString());
        if (LibC.bind(fd, &addr, len) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"UDS bind(AF_UNIX) failed");
        if (LibC.listen(fd, 512) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"UDS listen() failed");

        return fd;
    }
}