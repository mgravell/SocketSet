using System;
using System.Runtime.InteropServices;

namespace FastNet.Native;

/// <summary>
/// Minimal libc socket surface for the listener. Connection sockets are
/// produced by io_uring's accept, so we only need enough here to create,
/// bind and listen — the data path never touches libc.
/// </summary>
internal static unsafe class LibC
{
    private const string Lib = "libc";

    internal const int AF_INET = 2;
    internal const int SOCK_STREAM = 1;
    internal const int IPPROTO_TCP = 6;
    internal const int SOL_SOCKET = 1;
    internal const int SO_REUSEADDR = 2;
    internal const int SO_REUSEPORT = 15;

    [DllImport(Lib, SetLastError = true)]
    internal static extern int socket(int domain, int type, int protocol);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int bind(int fd, SockAddrIn* addr, uint addrlen);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int listen(int fd, int backlog);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int setsockopt(int fd, int level, int optname, void* optval, uint optlen);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int close(int fd);

    /// <summary>Host-to-network byte order for a 16-bit port.</summary>
    internal static ushort Htons(ushort value)
        => BitConverter.IsLittleEndian ? (ushort)((value << 8) | (value >> 8)) : value;
}

/// <summary>Kernel sockaddr_in (16 bytes), IPv4.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct SockAddrIn
{
    public ushort sin_family;
    public ushort sin_port;   // network byte order
    public uint sin_addr;     // network byte order; 0 == INADDR_ANY
    // trailing 8 bytes of zero padding covered by Size = 16
}
