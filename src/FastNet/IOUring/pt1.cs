using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FastNet.IOUring;

internal static unsafe partial class LinuxSyscall
{
    // =========================================================================
    // Raw Linux x86_64 Syscall Numbers (Bypasses glibc version tracking)
    // =========================================================================
    public const int SYS_io_uring_setup = 425;
    public const int SYS_io_uring_enter = 426;
    public const int SYS_io_uring_register = 427;

    // =========================================================================
    // Core io_uring Syscall P/Invoke Mappings
    // =========================================================================
    
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    public static partial nint io_uring_setup(int sysno, uint entries, io_uring_params* p);

    // Blocking Variant: Used when thread needs to go to sleep waiting for events. 
    // Do NOT suppress the GC transition here so background GC work can happen while we sleep.
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    public static partial int io_uring_enter_blocking(int sysno, int fd, uint to_submit, uint min_complete, uint flags, void* sig, nint sigsz);

    // Hot-Path Non-Blocking Variant: Used to quickly flush submissions without sleeping (min_complete = 0).
    // Suppressing GC transition lowers the P/Invoke boundary overhead to near zero.
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [SuppressGCTransition]
    public static partial int io_uring_enter_nonblocking(int sysno, int fd, uint to_submit, uint min_complete, uint flags, void* sig, nint sigsz);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [SuppressGCTransition]
    public static partial int io_uring_register(int sysno, int fd, uint opcode, void* arg, uint nr_args);

    // =========================================================================
    // Standard POSIX Memory and File Descriptors (Direct libc Symbols)
    // =========================================================================
    
    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    public static partial void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);
    
    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    public static partial int munmap(void* addr, nuint length);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    public static partial int eventfd(uint initval, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    [SuppressGCTransition]
    public static partial nint write(int fd, void* buf, nuint count);
    
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    [SuppressGCTransition]
    public static partial nint read(int fd, void* buf, nuint count);

    // =========================================================================
    // Shared Kernel Opcode and Flag Configuration Constants
    // =========================================================================
    
    // io_uring Core Opcodes
    public const uint IORING_REGISTER_EVENTFD = 2; // Added
    public const byte IORING_OP_READ = 22;
    public const byte IORING_OP_SEND = 26;
    public const byte IORING_OP_ACCEPT = 28;

    // io_uring Setup Flags
    public const uint IORING_SETUP_SINGLE_ISSUER = 1U << 12; // Locks ring to single loop thread (Linux 6.0+)
    public const uint IORING_SETUP_DEFER_TASKRUN = 1U << 13; // Defers processing tasks to enter loop (Linux 6.1+)

    // io_uring Enter Flags
    public const uint IORING_ENTER_GETEVENTS = 1U << 0;

    // SQE / CQE Bitwise Modifiers
    public const byte IOSQE_BUFFER_SELECT = 1 << 5;     // Directs SQE to pull from auto-provided buffers
    public const uint IORING_CQE_F_MORE = 1U << 0;       // Validates if Multishot Accept remains armed
    public const uint IORING_CQE_F_BUFFER = 1U << 16;   // Signifies presence of a kernel buffer ID allocation

    // Kernel Buffer Ring Registration
    public const uint IORING_REGISTER_PBUF_RING = 22;   // Provided buffer ring tracking identifier (Linux 5.19+)

    // =========================================================================
    // Interop Data Structures (Fixed Binary Layout matching linux/io_uring.h)
    // =========================================================================
    
    [StructLayout(LayoutKind.Sequential)]
    public struct iovec
    {
        public void* iov_base;
        public nuint iov_len;
    }
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
internal unsafe struct io_uring_sqe
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

[StructLayout(LayoutKind.Sequential)]
internal struct io_uring_buf_ring
{
    public uint resv1;
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
