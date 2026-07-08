using System.Reflection;
using System.Runtime.InteropServices;

namespace FastNet.Native;

/// <summary>
/// P/Invoke surface for liburing, bound against the FFI shared object
/// (liburing-ffi.so.2) which exports the otherwise-inline helpers as real,
/// callable symbols. This is what lets us call io_uring_prep_recv,
/// io_uring_get_sqe, io_uring_sqe_set_data64 etc. directly instead of
/// hand-poking SQE field offsets with magic numbers.
///
/// The ring itself (struct io_uring) is opaque: we hand liburing a zeroed
/// blob (<see cref="RingStructSize"/>) and only ever pass the pointer back.
/// </summary>
internal static unsafe class LibUring
{
    // DllImport token; resolved to the concrete .so by the resolver below.
    private const string Lib = "liburing-ffi";

    // struct io_uring is ~216 bytes on current liburing; over-allocate an
    // opaque, zero-initialised blob so we are never short across versions.
    internal const int RingStructSize = 1024;

    // cqe->flags bit: this multishot request stays armed and will produce
    // more completions. If clear on a multishot op, we must re-arm.
    internal const uint IORING_CQE_F_MORE = 1u << 1;

    // cqe->flags bit: the completion carries a provided-buffer id (in the high
    // bits, see IORING_CQE_BUFFER_SHIFT). Set on every provided-buffer recv.
    internal const uint IORING_CQE_F_BUFFER = 1u << 0;

    // The selected buffer id lives in the top 16 bits of cqe->flags.
    internal const int IORING_CQE_BUFFER_SHIFT = 16;

    // sqe->flags bit: pick the buffer from the group set via sqe_set_buf_group
    // rather than from an addr/len in the SQE. Required for provided-buffer recv.
    internal const uint IOSQE_BUFFER_SELECT = 1u << 5;
    
    // Locks submissions strictly to the creating thread, removing internal kernel locks
    public const uint IORING_SETUP_SINGLE_ISSUER = 1U << 12;

    // Defers kernel task work execution to run lazily inside your thread context
    public const uint IORING_SETUP_DEFER_TASKRUN = 1U << 13;

    static LibUring()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibUring).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        foreach (var candidate in new[] { "liburing-ffi.so.2", "liburing-ffi.so", "liburing-ffi" })
        {
            if (NativeLibrary.TryLoad(candidate, asm, path, out var h)) return h;
        }
        throw new DllNotFoundException(
            "liburing-ffi not found. Install liburing2 (provides liburing-ffi.so.2).");
    }

    // --- ring lifecycle ---------------------------------------------------

    [DllImport(Lib, SetLastError = false)]
    internal static extern int io_uring_queue_init(uint entries, IoUringOpaque* ring, uint flags);

    [DllImport(Lib, SetLastError = false)]
    internal static extern void io_uring_queue_exit(IoUringOpaque* ring);

    // --- submission -------------------------------------------------------
    // These touch only the userspace SQ ring, so suppressing the GC
    // transition is safe and removes real overhead on the hot path.

    [DllImport(Lib), SuppressGCTransition]
    internal static extern IoUringSqe* io_uring_get_sqe(IoUringOpaque* ring);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_recv(IoUringSqe* sqe, int sockfd, byte* buf, nuint len, int flags);

    // Multishot recv: stays armed and posts a CQE (each carrying a provided
    // buffer) per arrival, until EOF/error/buffer exhaustion. buf/len are
    // ignored — buffers come from the group named by sqe_set_buf_group.
    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_recv_multishot(IoUringSqe* sqe, int sockfd, byte* buf, nuint len, int flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_sqe_set_flags(IoUringSqe* sqe, uint flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_sqe_set_buf_group(IoUringSqe* sqe, int bgid);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_send(IoUringSqe* sqe, int sockfd, byte* buf, nuint len, int flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_accept(IoUringSqe* sqe, int fd, byte* addr, uint* addrlen, int flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_multishot_accept(IoUringSqe* sqe, int fd, byte* addr, uint* addrlen, int flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_close(IoUringSqe* sqe, int fd);

    // Post a completion to another ring (IORING_MSG_RING / IORING_MSG_DATA). The
    // target CQE lands on ring `fd` with res = `len` and user_data = `data`, and
    // the wait on that ring is woken. We use it to hand an accepted socket fd
    // (packed into `len`) from the single accept ring to a worker ring — the one
    // cross-thread signal in the sharded-UDS design, and the only reason it is
    // safe: each thread still only ever touches its own SQ. Touches only the
    // caller's SQE, so the GC transition is pure overhead here.
    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_msg_ring(IoUringSqe* sqe, int fd, uint len, ulong data, uint flags);

    [DllImport(Lib), SuppressGCTransition]
    public static extern void io_uring_prep_poll_add(
        IoUringSqe*  sqe,       // The raw pointer to the SQE slot acquired from your ring
        int fd,           // Your 32-bit _eventFd integer
        uint poll_mask    // The event mask to look for (use IoUringNative.POLLIN)
    );

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_sqe_set_data64(IoUringSqe* sqe, ulong data);

    // io_uring_submit / submit_and_wait may enter the kernel (io_uring_enter)
    // and submit_and_wait blocks — so NO SuppressGCTransition here.

    [DllImport(Lib), SuppressGCTransition]
    internal static extern int io_uring_submit(IoUringOpaque* ring);

    internal static int io_uring_submit_and_wait(IoUringOpaque* ring, uint waitNr)
    {
        return waitNr is 0
            ? io_uring_submit_and_wait_nonblocking(ring, waitNr)
            : io_uring_submit_and_wait_blocking(ring, waitNr);

        // don't use SuppressGCTransition if we're going to block/sleep in unmanaged code - it could
        // prevent GC indefinitely
        [DllImport(Lib, EntryPoint = nameof(io_uring_submit_and_wait))]
        static extern int io_uring_submit_and_wait_blocking(IoUringOpaque* ring, uint waitNr);

        [DllImport(Lib, EntryPoint = nameof(io_uring_submit_and_wait)), SuppressGCTransition]
        static extern int io_uring_submit_and_wait_nonblocking(IoUringOpaque* ring, uint waitNr);
    }
    
    

    // --- completion -------------------------------------------------------

    [DllImport(Lib), SuppressGCTransition]
    internal static extern int io_uring_peek_cqe(IoUringOpaque* ring, IoUringCqe** cqePtr);
    
    // Fetches up to 'count' CQE pointer addresses in a single memory read
    [DllImport(Lib, EntryPoint = "io_uring_peek_batch_cqe", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint io_uring_peek_batch_cqe( IoUringOpaque* ring, IoUringCqe** cqePtrs, uint count);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_cq_advance(IoUringOpaque* ring, uint nr);

    // --- provided buffer rings (multishot recv) ---------------------------
    // A buf ring is a kernel-shared SPSC array of {addr,len,bid} the kernel
    // draws from when a BUFFER_SELECT recv completes. setup/free mmap+register
    // the ring; add/advance publish buffers back to the kernel for reuse.

    [DllImport(Lib)]
    internal static extern IoUringBufRing* io_uring_setup_buf_ring(IoUringOpaque* ring, uint nentries, int bgid, uint flags, int* ret);

    [DllImport(Lib)]
    internal static extern int io_uring_free_buf_ring(IoUringOpaque* ring, IoUringBufRing* br, uint nentries, int bgid);

    // Stage one buffer into the ring at (tail + buf_offset). Touches only the
    // shared ring memory, so the GC transition is pure overhead here.
    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_buf_ring_add(IoUringBufRing* br, byte* addr, uint len, ushort bid, int mask, int buf_offset);

    // Publish `count` staged buffers by advancing the ring tail.
    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_buf_ring_advance(IoUringBufRing* br, int count);
}

/// <summary>
/// Kernel ABI completion entry. Layout is part of the io_uring uapi and
/// stable, so reading these offsets directly is safe (unlike liburing's
/// internal structs, which we never touch).
///
/// The <see cref="user_data"/> field is opaque to the kernel; our
/// application-level encode/decode of it lives apart from this ABI mirror, in
/// <c>FastNet.Transport.IoUringCqeExtensions</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringCqe
{
    public ulong user_data; // @0
    public int res;         // @8  bytes transferred, or -errno
    public uint flags;      // @12
}

/// <summary>Opaque SQE handle — we only ever fill it via prep_* helpers.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringSqe
{
    // Intentionally empty: liburing owns the layout; we pass the pointer back.
}

/// <summary>Opaque provided-buffer-ring handle — filled by io_uring_setup_buf_ring,
/// only ever passed back to buf_ring_add/advance/free.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringBufRing
{
    // Intentionally empty: liburing owns the layout; we pass the pointer back.
}

[StructLayout(LayoutKind.Sequential, Size = LibUring.RingStructSize)]
internal struct IoUringOpaque
{
}