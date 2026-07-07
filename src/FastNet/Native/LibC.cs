using System.Buffers.Binary;
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
    internal const int AF_UNIX = 1;
    internal const int SOCK_STREAM = 1;
    internal const int IPPROTO_TCP = 6; // also the setsockopt level for TCP options
    internal const int SOL_SOCKET = 1;
    internal const int SO_REUSEADDR = 2;
    internal const int SO_REUSEPORT = 15;
    internal const int TCP_NODELAY = 1;

    [DllImport(Lib, SetLastError = true)]
    internal static extern int socket(int domain, int type, int protocol);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int bind(int fd, SockAddrIn* addr, uint addrlen);

    // Overload for AF_UNIX; DllImport resolves both to the same "bind" symbol.
    [DllImport(Lib, SetLastError = true, EntryPoint = "bind")]
    internal static extern int bind(int fd, SockAddrUn* addr, uint addrlen);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int listen(int fd, int backlog);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int setsockopt(int fd, int level, int optname, void* optval, uint optlen);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int close(int fd);

    [DllImport(Lib, SetLastError = true)]
    internal static extern int sched_setaffinity(int pid, nuint cpusetsize, void* mask);

    /// <summary>Host-to-network byte order for a 16-bit port.</summary>
    internal static ushort Htons(ushort value)
        => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;

    /// <summary>
    /// Pin the calling thread to a single CPU (best-effort; false on failure).
    /// pid 0 targets the current thread. The mask is a kernel cpu_set_t bitmap;
    /// 128 bytes matches glibc's cpu_set_t and covers up to 1024 CPUs.
    /// </summary>
    internal static bool PinCurrentThreadToCpu(int cpu)
    {
        const int SetBytes = 128;
        Span<byte> mask = stackalloc byte[SetBytes];
        mask.Clear();
        mask[cpu >> 3] = (byte)(1 << (cpu & 7));
        fixed (byte* p = mask)
            return sched_setaffinity(0, (nuint)SetBytes, p) == 0;
    }
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

/// <summary>
/// Kernel sockaddr_un (110 bytes): a 2-byte family plus a 108-byte path.
/// For an <em>abstract</em> socket the path's first byte is NUL and the name
/// follows (not NUL-terminated); the address length passed to bind() — not a
/// terminator — is what bounds the name, so distinct lengths are distinct
/// addresses. Abstract sockets live in a kernel namespace, not the filesystem:
/// no inode to create, unlink, or leave stale, and they vanish when the last
/// reference closes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 110)]
internal unsafe struct SockAddrUn
{
    public ushort sun_family;
    public fixed byte sun_path[108];

    /// <summary>
    /// Fill in an abstract-namespace address for <paramref name="name"/> and
    /// return the exact address length bind() expects: family + leading NUL +
    /// the name bytes (ASCII).
    /// </summary>
    public static uint InitAbstract(SockAddrUn* addr, string name)
    {
        *addr = default;
        addr->sun_family = LibC.AF_UNIX;
        // sun_path[0] stays NUL (the abstract marker); name starts at [1].
        for (int i = 0; i < name.Length; i++)
            addr->sun_path[1 + i] = (byte)name[i];
        return (uint)(sizeof(ushort) + 1 + name.Length);
    }
}
