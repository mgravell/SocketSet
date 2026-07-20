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
    internal const int TCP_NODELAY = 1;
    
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

    [SuppressGCTransition]
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int sched_setaffinity(int pid, nuint cpusetsize, void* mask);

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

        public static uint Init(SockAddrUn* addr, string name)
        {
            *addr = default;
            addr->sun_family = AF_UNIX;

            for (int i = 0; i < name.Length; i++)
            {
                // interpret @abc as abstract
                addr->sun_path[i] = (byte)(i is 0 & name[i] is '@' ? '\0' : name[i]);
            }

            return (uint)(sizeof(ushort) + name.Length);
        }
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
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    public static partial nint io_uring_setup(int sysno, uint entries, io_uring_params* p);

    // Blocking Variant: Used when thread needs to go to sleep waiting for events. 
    // Do NOT suppress the GC transition here so background GC work can happen while we sleep.
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    public static partial int io_uring_enter_blocking(int sysno, int fd, uint to_submit, uint min_complete, uint flags,
        void* sig, nint sigsz);

    // Hot-Path Non-Blocking Variant: Used to quickly flush submissions without sleeping (min_complete = 0).
    // Suppressing GC transition lowers the P/Invoke boundary overhead to near zero.
    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    public static partial int io_uring_enter_nonblocking(int sysno, int fd, uint to_submit, uint min_complete,
        uint flags, void* sig, nint sigsz);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    public static partial int io_uring_register(int sysno, int fd, uint opcode, void* arg, uint nr_args);

    // =========================================================================
    // Standard POSIX Memory and File Descriptors (Direct libc Symbols)
    // =========================================================================

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    public static partial void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    [SuppressGCTransition]
    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    public static partial int munmap(void* addr, nuint length);
    
    // =========================================================================
    // Shared Kernel Opcode and Flag Configuration Constants
    // =========================================================================

    // io_uring register opcodes (linux/io_uring.h enum io_uring_register_op)
    public const uint IORING_REGISTER_EVENTFD = 4;

    // io_uring operation opcodes (linux/io_uring.h enum io_uring_op). These are
    // ordinals into that enum; getting them wrong silently issues the wrong op.
    public const byte IORING_OP_ACCEPT = 13;
    public const byte IORING_OP_CONNECT = 16;
    public const byte IORING_OP_CLOSE = 19;
    public const byte IORING_OP_READ = 22;
    public const byte IORING_OP_SEND = 26;
    public const byte IORING_OP_RECV = 27;

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
    public struct iovec
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