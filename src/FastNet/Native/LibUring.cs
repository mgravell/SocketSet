using System;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    internal static extern int io_uring_queue_init(uint entries, void* ring, uint flags);

    [DllImport(Lib, SetLastError = false)]
    internal static extern void io_uring_queue_exit(void* ring);

    // --- submission -------------------------------------------------------
    // These touch only the userspace SQ ring, so suppressing the GC
    // transition is safe and removes real overhead on the hot path.

    [DllImport(Lib), SuppressGCTransition]
    internal static extern IoUringSqe* io_uring_get_sqe(void* ring);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_recv(IoUringSqe* sqe, int sockfd, void* buf, nuint len, int flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_send(IoUringSqe* sqe, int sockfd, void* buf, nuint len, int flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_accept(IoUringSqe* sqe, int fd, void* addr, uint* addrlen, int flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_multishot_accept(IoUringSqe* sqe, int fd, void* addr, uint* addrlen, int flags);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_prep_close(IoUringSqe* sqe, int fd);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_sqe_set_data64(IoUringSqe* sqe, ulong data);

    // io_uring_submit / submit_and_wait may enter the kernel (io_uring_enter)
    // and submit_and_wait blocks — so NO SuppressGCTransition here.

    [DllImport(Lib)]
    internal static extern int io_uring_submit(void* ring);

    [DllImport(Lib)]
    internal static extern int io_uring_submit_and_wait(void* ring, uint waitNr);

    // --- completion -------------------------------------------------------

    [DllImport(Lib), SuppressGCTransition]
    internal static extern int io_uring_peek_cqe(void* ring, IoUringCqe** cqePtr);

    [DllImport(Lib), SuppressGCTransition]
    internal static extern void io_uring_cq_advance(void* ring, uint nr);
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
