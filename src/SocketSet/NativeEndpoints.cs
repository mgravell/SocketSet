#if NET
using System;

namespace SocketSets;

/// <summary>
/// Reads peer/local addresses off a native socket into <see cref="PeerAddress"/>, for the backends that
/// hold a raw handle (IOCP, RIO, epoll). One implementation rather than three, because the interesting
/// parts — the 128-byte <c>SOCKADDR_STORAGE</c> buffer, the by-ref length, and above all what to do when
/// the call FAILS — are identical on both platforms and are exactly the details worth getting wrong once
/// instead of three times.
///
/// EVERY FAILURE PATH LEAVES THE ADDRESS UNSET rather than throwing or guessing. A socket can be torn
/// down between accept and this call, and a connection that races a reset is normal load, not an error.
/// Unset is a state callers must already handle (tracking off, io_uring, unnamed AF_UNIX peer), and it
/// fails closed for anything deciding on <see cref="PeerAddress.IsLoopback"/>.
/// </summary>
internal static unsafe class NativeEndpoints
{
    // SOCKADDR_STORAGE. Big enough for sockaddr_in (16), sockaddr_in6 (28) and sockaddr_un (110) — the
    // same constant the accept paths already use for their own address slots.
    private const int StorageSize = 128;

    /// <summary>Windows: fill both addresses for a connected socket. Requires
    /// <c>SO_UPDATE_ACCEPT_CONTEXT</c> to have been applied to an AcceptEx socket already.</summary>
    public static void Populate(Connection conn, nint socket)
    {
        conn.RemoteAddress = ReadWindows(socket, peer: true);
        conn.LocalAddress = ReadWindows(socket, peer: false);
    }

    /// <summary>Linux: fill both addresses for a connected fd.</summary>
    public static void Populate(Connection conn, int fd)
    {
        conn.RemoteAddress = ReadLinux(fd, peer: true);
        conn.LocalAddress = ReadLinux(fd, peer: false);
    }

    private static PeerAddress ReadWindows(nint socket, bool peer)
    {
        if (socket == 0) return default;
        byte* buf = stackalloc byte[StorageSize];
        int len = StorageSize;
        int rc = peer
            ? Native.Win32.getpeername(socket, buf, &len)
            : Native.Win32.getsockname(socket, buf, &len);
        // A socket reset between accept and here returns SOCKET_ERROR; that is ordinary load, so leave
        // the address unset rather than treating it as a fault.
        if (rc != 0 || len < 2 || len > StorageSize) return default;
        return PeerAddress.FromSockAddr(new ReadOnlySpan<byte>(buf, len));
    }

    private static PeerAddress ReadLinux(int fd, bool peer)
    {
        if (fd < 0) return default;
        byte* buf = stackalloc byte[StorageSize];
        uint len = StorageSize;
        int rc = peer
            ? Native.LibC.getpeername(fd, buf, &len)
            : Native.LibC.getsockname(fd, buf, &len);
        // AF_UNIX with an UNNAMED peer legitimately returns addrlen == 2 (the family alone) - which
        // FromSockAddr maps to PeerAddress.Unix, i.e. "a Unix peer, with no name". That is the normal case
        // for an accepted UDS connection and must not be mistaken for a failure.
        if (rc != 0 || len < 2 || len > StorageSize) return default;
        return PeerAddress.FromSockAddr(new ReadOnlySpan<byte>(buf, (int)len));
    }
}
#endif
