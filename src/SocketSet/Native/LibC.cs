#if NET // io_uring is a Linux + modern-.NET backend; compiled out of the netfx fallback build.
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SocketSets.Native;

internal static unsafe partial class LibC
{
    private const string Lib = "libc";

    internal const int AF_INET = 2;
    internal const int AF_UNIX = 1;
    internal const int SOCK_STREAM = 1;
    internal const int IPPROTO_TCP = 6; // also the setsockopt level for TCP options
    internal const int SOL_SOCKET = 1;
    internal const int SO_REUSEADDR = 2;
    internal const int SO_REUSEPORT = 15;
    internal const int SO_LINGER = 13;
    internal const int TCP_NODELAY = 1;

    /// <summary>struct linger { int l_onoff; int l_linger; } — SO_LINGER{1,0} makes close() send RST (no TIME_WAIT).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Linger { public int l_onoff; public int l_linger; }
    
    public const int EAGAIN = 11, EINTR = 4, EBUSY = 16, ENOBUFS = 105;

    // send()/recv() message flags
    internal const int MSG_NOSIGNAL = 0x4000; // don't raise SIGPIPE on a broken pipe; report -EPIPE instead

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int socket(int domain, int type, int protocol);

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int bind(int fd, SockAddrIn* addr, uint addrlen);

    // Overload for AF_UNIX; DllImport resolves both to the same "bind" symbol.
    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int bind(int fd, SockAddrUn* addr, uint addrlen);

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int listen(int fd, int backlog);

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int setsockopt(int fd, int level, int optname, void* optval, uint optlen);

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int close(int fd);

    internal const int SHUT_RDWR = 2;

    // Force a FIN and unblock any in-flight io_uring recv on this fd. Needed before close(): an armed
    // (multishot) recv pins the underlying struct file, so close() alone would drop the fd-table slot
    // without sending a FIN — the socket would linger and the peer would never see EOF.
    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int shutdown(int fd, int how);

    // fcntl(fd, cmd, arg) — used to flip a socket to non-blocking for the kTLS handshake, so OpenSSL's
    // SSL_do_handshake/SSL_read return WANT_READ/WANT_WRITE (driven by io_uring POLL) instead of blocking
    // the shared loop thread.
    internal const int F_GETFL = 3;
    internal const int F_SETFL = 4;
    internal const int O_NONBLOCK = 0x800; // Linux x86_64

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int fcntl(int fd, int cmd, int arg);

    /// <summary>Set a socket non-blocking (best-effort; kTLS handshake needs it).</summary>
    internal static void SetNonBlocking(int fd)
    {
        int flags = fcntl(fd, F_GETFL, 0);
        if (flags >= 0) fcntl(fd, F_SETFL, flags | O_NONBLOCK);
    }

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int sched_setaffinity(int pid, nuint cpusetsize, void* mask);

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int sched_getaffinity(int pid, nuint cpusetsize, void* mask);

    [SuppressGCTransition]
    [LibraryImport(Lib, EntryPoint = "eventfd", SetLastError = true)]
    public static partial int eventfd(uint initval, int flags);
    
    [SuppressGCTransition]
    [LibraryImport(Lib, EntryPoint = "write", SetLastError = true)]
    public static unsafe partial nint write(int fd, void* buf, nuint count);

    [SuppressGCTransition]
    [LibraryImport(Lib, EntryPoint = "read", SetLastError = true)]
    public static unsafe partial nint read(int fd, void* buf, nuint count);

    /// <summary>Host-to-network byte order for a 16-bit port.</summary>
    internal static ushort Htons(ushort value)
        => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;

    /// <summary>
    /// An IPv4 address as the network-order <c>sin_addr</c> word. <see cref="System.Net.IPAddress.GetAddressBytes"/>
    /// already returns network order, so this is a straight 4-byte read — the point is that it is a read of
    /// FOUR bytes from an address asserted to HAVE four (see <see cref="RequireIPv4"/>), rather than the
    /// first four bytes of whatever was passed.
    /// </summary>
    internal static uint ToSinAddr(System.Net.IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out int written) || written != 4)
            throw new NotSupportedException($"'{address}' is not an IPv4 address.");
        // MemoryMarshal.Read, not a Read{Little,Big}Endian: we want the uint whose IN-MEMORY bytes are
        // these four unchanged, on either endianness, since sin_addr is a raw network-order word.
        return MemoryMarshal.Read<uint>(bytes);
    }

    /// <summary>
    /// Reject a non-IPv4 endpoint at the door, LOUDLY.
    ///
    /// The native backends speak <c>sockaddr_in</c> only. Before the 2026-08-04 audit an IPv6 endpoint was
    /// not rejected: the connect paths hard-coded <c>AF_INET</c> and copied the FIRST FOUR bytes of a
    /// sixteen-byte address, so dialling <c>2001:db8::1</c> quietly connected to <c>32.1.13.184</c> — a
    /// successful connection to an entirely different host, which for a TLS client is the worst possible
    /// shape of "it worked". IPv6 support is real work (a <c>sockaddr_in6</c> path on every backend, see
    /// TODO); until then this fails instead of guessing.
    /// </summary>
    internal static void RequireIPv4(System.Net.IPEndPoint ip, string operation)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new NotSupportedException(
                $"{operation}: the native backends are IPv4-only today, but '{ip}' is " +
                $"{ip.AddressFamily}. Use an IPv4 endpoint, or the managed backend " +
                $"(SocketSetFactory.Managed), which goes through System.Net.Sockets and supports IPv6.");
    }

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

    /// <summary>
    /// Pin the calling thread to the <paramref name="index"/>-th CPU within the process's
    /// <em>current allowed set</em> (from sched_getaffinity), wrapping if there are fewer CPUs
    /// than shards. This composes with an externally-applied affinity (taskset / --cpus): with
    /// the process restricted to CPUs 6–11, shard 0 pins to 6, shard 1 to 7, and so on — rather
    /// than the absolute <c>index % nproc</c>, which would ignore the restriction and collide.
    /// </summary>
    internal static bool PinCurrentThreadToNthAllowedCpu(int index)
    {
        const int SetBytes = 128;
        Span<byte> mask = stackalloc byte[SetBytes];
        mask.Clear();
        fixed (byte* p = mask)
        {
            if (sched_getaffinity(0, (nuint)SetBytes, p) != 0) return false;
        }

        int allowed = 0;
        for (int cpu = 0; cpu < SetBytes * 8; cpu++)
            if ((mask[cpu >> 3] & (1 << (cpu & 7))) != 0) allowed++;
        if (allowed == 0) return false;

        int target = index % allowed;
        int chosen = -1, seen = 0;
        for (int cpu = 0; cpu < SetBytes * 8; cpu++)
        {
            if ((mask[cpu >> 3] & (1 << (cpu & 7))) == 0) continue;
            if (seen++ == target) { chosen = cpu; break; }
        }

        return PinCurrentThreadToCpu(chosen);
    }

    // =========================================================================
    // epoll + readiness-mode socket calls (the epoll backend)
    // -------------------------------------------------------------------------
    // io_uring is a COMPLETION model: you submit a read and are told how many bytes arrived. epoll is a
    // READINESS model: you are told the fd is readable and must then call recv() yourself, and handle the
    // fact that it may still return EAGAIN (a spurious wake) or short reads.
    // =========================================================================

    internal const int EPOLL_CLOEXEC = 0x80000;
    internal const int EPOLL_CTL_ADD = 1;
    internal const int EPOLL_CTL_DEL = 2;
    internal const int EPOLL_CTL_MOD = 3;

    internal const uint EPOLLIN = 0x001;
    internal const uint EPOLLOUT = 0x004;
    internal const uint EPOLLERR = 0x008;
    internal const uint EPOLLHUP = 0x010;
    internal const uint EPOLLRDHUP = 0x2000; // peer closed its write half — a clean half-close, not an error
    internal const uint EPOLLET = 0x80000000;

    internal const int SOCK_NONBLOCK = 0x800;    // 04000 octal in the kernel headers
    internal const int SOCK_CLOEXEC = 0x80000;   // 02000000 octal
    internal const int SO_ERROR = 4;
    internal const int EINPROGRESS = 115;
    internal const int ECONNRESET = 104;
    internal const int EPIPE = 32;

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int epoll_create1(int flags);

    /// <summary>Register/modify/remove interest. <paramref name="ev"/> points at a struct epoll_event —
    /// build it with <see cref="WriteEpollEvent"/>, never by blitting a managed struct (see the note there).</summary>
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int epoll_ctl(int epfd, int op, int fd, void* ev);

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int epoll_wait(int epfd, void* events, int maxevents, int timeout);

    /// <summary>
    /// SIZE OF struct epoll_event IS ARCHITECTURE-DEPENDENT. On x86-64 the kernel declares it
    /// <c>__attribute__((packed))</c> so it is 12 bytes (uint32 events + uint64 data, no padding); every
    /// other Linux architecture leaves it naturally aligned at 16 bytes. Blitting one managed struct on
    /// both would silently corrupt the data word on one of them, so the layout is computed here and the
    /// fields are read/written by offset.
    /// </summary>
    internal static readonly int EpollEventSize =
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 12 : 16;

    private static readonly int EpollDataOffset = EpollEventSize == 12 ? 4 : 8;

    internal static void WriteEpollEvent(void* p, uint events, ulong data)
    {
        *(uint*)p = events;
        *(ulong*)((byte*)p + EpollDataOffset) = data;
    }

    internal static uint ReadEpollEvents(void* p, int index)
        => *(uint*)((byte*)p + (index * EpollEventSize));

    internal static ulong ReadEpollData(void* p, int index)
        => *(ulong*)((byte*)p + (index * EpollEventSize) + EpollDataOffset);

    /// <summary>accept4 rather than accept: SOCK_NONBLOCK|SOCK_CLOEXEC atomically, so there is no window
    /// where an accepted fd is blocking or leaks across an exec.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int accept4(int fd, void* addr, uint* addrlen, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int connect(int fd, SockAddrIn* addr, uint addrlen);

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int connect(int fd, SockAddrUn* addr, uint addrlen);

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int getsockopt(int fd, int level, int optname, void* optval, uint* optlen);

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial nint recv(int fd, void* buf, nuint len, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial nint send(int fd, void* buf, nuint len, int flags);

    // Scatter-gather send: one syscall over an array of iovecs pointing at arbitrary (here: pinned pipe)
    // memory — no buffer registration, unlike RIO. Used by epoll's zero-copy send. Returns bytes written,
    // or -1 with errno (EAGAIN when the socket buffer is full → drain the rest on EPOLLOUT).
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial nint writev(int fd, iovec* iov, int iovcnt);

    /// <summary>Kernel sockaddr_in (16 bytes), IPv4.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct SockAddrIn
    {
        public ushort sin_family;
        public ushort sin_port; // network byte order

        public uint sin_addr; // network byte order; 0 == INADDR_ANY
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
    internal struct SockAddrUn
    {
        public ushort sun_family;
        public fixed byte sun_path[108];

        /// <summary>
        /// Fill a sockaddr_un for <paramref name="name"/>, mapping a leading '@' to the abstract
        /// namespace's leading NUL. Returns the address length to pass to bind()/connect().
        ///
        /// UTF-8 ENCODED, AND LENGTH-CHECKED — both fixed 2026-08-04. It previously wrote one byte per
        /// UTF-16 char (<c>(byte)name[i]</c>), which for a non-ASCII path is a DIFFERENT address than the
        /// one requested: U+0141 truncates to 'A', and anything ending in 00 truncates to NUL, which
        /// silently shortens the path (or, at index 0, flips a filesystem socket into the abstract
        /// namespace). And nothing bounded the write against the 108-byte sun_path — safe only because
        /// UnixDomainSocketEndPoint happens to validate its own path length, which is an invariant
        /// borrowed from another type rather than one enforced here. Two of the three call sites write
        /// into a STACK sockaddr_un, so the borrowed invariant was the only thing between this and a
        /// stack smash.
        /// </summary>
        public static uint Init(SockAddrUn* addr, string name)
        {
            *addr = default;
            addr->sun_family = AF_UNIX;

            bool abstractNs = name.Length > 0 && name[0] == '@';
            var span = new Span<byte>(addr->sun_path, PathCapacity);
            int written = System.Text.Encoding.UTF8.GetByteCount(name) is var need && need <= PathCapacity
                ? System.Text.Encoding.UTF8.GetBytes(name, span)
                : throw new ArgumentException(
                    $"AF_UNIX path '{name}' encodes to {need} UTF-8 bytes; sun_path holds {PathCapacity}.",
                    nameof(name));

            if (abstractNs) span[0] = 0; // '@name' -> '\0name': the Linux abstract namespace
            return (uint)(sizeof(ushort) + written);
        }

        /// <summary>Bytes of <c>sun_path</c>. The kernel's own limit, not ours.</summary>
        internal const int PathCapacity = 108;
    }
    
        // =========================================================================
    // Raw Linux x86_64 Syscall Numbers (Bypasses glibc version tracking)
    // =========================================================================
    public const int SYS_io_uring_setup = 425;
    public const int SYS_io_uring_enter = 426;
    public const int SYS_io_uring_register = 427;

    // =========================================================================
    // Core io_uring Syscall P/Invoke Mappings
    // =========================================================================

    [SuppressGCTransition]
    [LibraryImport(Lib, EntryPoint = "syscall", SetLastError = true)]
    public static partial nint io_uring_setup(int sysno, uint entries, io_uring_params* p);

    // Blocking Variant: Used when thread needs to go to sleep waiting for events. 
    // Do NOT suppress the GC transition here so background GC work can happen while we sleep.
    [LibraryImport(Lib, EntryPoint = "syscall", SetLastError = true)]
    public static partial int io_uring_enter_blocking(int sysno, int fd, uint to_submit, uint min_complete, uint flags,
        void* sig, nint sigsz);

    // Hot-Path Non-Blocking Variant: Used to quickly flush submissions without sleeping (min_complete = 0).
    // Suppressing GC transition lowers the P/Invoke boundary overhead to near zero.
    [SuppressGCTransition]
    [LibraryImport(Lib, EntryPoint = "syscall", SetLastError = true)]
    public static partial int io_uring_enter_nonblocking(int sysno, int fd, uint to_submit, uint min_complete,
        uint flags, void* sig, nint sigsz);

    [SuppressGCTransition]
    [LibraryImport(Lib, EntryPoint = "syscall", SetLastError = true)]
    public static partial int io_uring_register(int sysno, int fd, uint opcode, void* arg, uint nr_args);

    // =========================================================================
    // Standard POSIX Memory and File Descriptors (Direct libc Symbols)
    // =========================================================================

    [SuppressGCTransition]
    [LibraryImport(Lib, EntryPoint = "mmap", SetLastError = true)]
    public static partial void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    [SuppressGCTransition]
    [LibraryImport(Lib, EntryPoint = "munmap", SetLastError = true)]
    public static partial int munmap(void* addr, nuint length);
    
    // =========================================================================
    // Shared Kernel Opcode and Flag Configuration Constants
    // =========================================================================

    // io_uring register opcodes (linux/io_uring.h enum io_uring_register_op)
    public const uint IORING_REGISTER_EVENTFD = 4;

    // io_uring operation opcodes (linux/io_uring.h enum io_uring_op). These are
    // ordinals into that enum; getting them wrong silently issues the wrong op.
    public const byte IORING_OP_WRITEV = 2;
    public const byte IORING_OP_ACCEPT = 13;
    public const byte IORING_OP_ASYNC_CANCEL = 14;
    public const byte IORING_OP_CONNECT = 16;
    public const byte IORING_OP_CLOSE = 19;
    public const byte IORING_OP_POLL_ADD = 6;
    public const byte IORING_OP_READ = 22;
    public const byte IORING_OP_SEND = 26;
    public const byte IORING_OP_RECV = 27;

    // poll() event bits (for IORING_OP_POLL_ADD's poll32_events, which aliases the rw_flags union). The
    // completion's res carries the revents that fired (or a negative errno). One-shot by default: it fires
    // once, then must be re-armed — which is what the kTLS readiness loop wants.
    public const uint POLLIN = 0x0001;
    public const uint POLLOUT = 0x0004;
    public const uint POLLERR = 0x0008;
    public const uint POLLHUP = 0x0010;

    // IORING_OP_ASYNC_CANCEL cancel_flags (go in the sqe's op-flags union). CANCEL_FD matches by fd
    // (not user_data); CANCEL_ALL cancels every matching op, not just the first.
    public const uint IORING_ASYNC_CANCEL_ALL = 1 << 0;
    public const uint IORING_ASYNC_CANCEL_FD = 1 << 1;

    // ioprio multishot modifiers
    public const ushort IORING_ACCEPT_MULTISHOT = 1 << 0;
    public const ushort IORING_RECV_MULTISHOT = 1 << 1;

    // io_uring Setup Flags
    public const uint IORING_SETUP_SINGLE_ISSUER = 1U << 12; // Locks ring to single loop thread (Linux 6.0+)
    public const uint IORING_SETUP_DEFER_TASKRUN = 1U << 13; // Defers processing tasks to enter loop (Linux 6.1+)

    // io_uring Enter Flags
    public const uint IORING_ENTER_GETEVENTS = 1U << 0;

    // SQE / CQE Bitwise Modifiers
    public const byte IOSQE_BUFFER_SELECT = 1 << 5; // Directs SQE to pull from auto-provided buffers
    public const uint IORING_CQE_F_BUFFER = 1U << 0; // cqe.flags: upper 16 bits carry the selected buffer id
    public const uint IORING_CQE_F_MORE = 1U << 1; // cqe.flags: parent (multishot) SQE will emit more CQEs
    public const int IORING_CQE_BUFFER_SHIFT = 16; // shift to extract the buffer id from cqe.flags

    // Kernel Buffer Ring Registration
    public const uint IORING_REGISTER_PBUF_RING = 22; // Provided buffer ring tracking identifier (Linux 5.19+)

    // =========================================================================
    // Interop Data Structures (Fixed Binary Layout matching linux/io_uring.h)
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable CS8981 // The type name only contains lower-cased ascii characters. Such names may become reserved for the language.
    public struct iovec
#pragma warning restore CS8981 // The type name only contains lower-cased ascii characters. Such names may become reserved for the language.
    {
        public void* iov_base;
        public nuint iov_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct io_uring_params
    {
        public uint sq_entries;
        public uint cq_entries;
        public uint flags;
        public uint sq_thread_cpu;
        public uint sq_thread_idle;
        public uint features;
        public uint wq_fd;
        public fixed uint resv[3];
        public io_sqring_offsets sq_off;
        public io_cqring_offsets cq_off;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct io_sqring_offsets
    {
        public uint head;
        public uint tail;
        public uint ring_mask;
        public uint ring_entries;
        public uint flags;
        public uint dropped;
        public uint array;
        public uint resv1;
        public ulong resv2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct io_cqring_offsets
    {
        public uint head;
        public uint tail;
        public uint ring_mask;
        public uint ring_entries;
        public uint overflow;
        public uint cqes;
        public uint flags;
        public uint resv1;
        public ulong resv2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct io_uring_sqe
    {
        public byte opcode;
        public byte flags;
        public ushort ioprio;
        public int fd;
        public ulong off;
        public ulong addr;
        public uint len;
        public union32 rw_flags;
        public ulong user_data;
        public ushort buf_index; // Overlaps with buf_group in kernel definitions
        public ushort personality;
        public uint file_index;
        public fixed ulong __pad2[2];

        [StructLayout(LayoutKind.Explicit)]
        public struct union32
        {
            [FieldOffset(0)] public uint rw_flags;
            [FieldOffset(0)] public uint accept_flags;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct io_uring_cqe
    {
        public ulong user_data;
        public int res;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct io_uring_buf
    {
        public ulong addr;
        public uint len;
        public ushort bid;
        public ushort resv;
    }

    // Kernel overlays this header with io_uring_buf[0] via a union: 'tail' occupies
    // the same two bytes as io_uring_buf.resv, so the header is exactly 16 bytes and
    // 'tail' sits at offset 14. resv1 MUST be 8 bytes (__u64) for that to hold.
    [StructLayout(LayoutKind.Sequential)]
    internal struct io_uring_buf_ring
    {
        public ulong resv1;
        public uint resv2;
        public ushort resv3;
        public ushort tail;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct io_uring_buf_reg
    {
        public ulong ring_addr;
        public uint ring_entries;
        public ushort bgid;
        public ushort flags;
        public unsafe fixed ulong resv[3];
    }
}
#endif