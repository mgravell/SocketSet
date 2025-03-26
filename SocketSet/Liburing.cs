
using System.Runtime.InteropServices;

namespace Socketizer;

#pragma warning disable CS0169, CS0649 // used by underlying implementation
internal static unsafe partial class Liburing
{
    private const string liburing = "liburing.so";

    [LibraryImport(liburing)]
    public static partial int io_uring_queue_init(uint QUEUE_DEPTH, io_uring* ring, IOUringSetupFlags flags);

    // useful ref: https://github.com/torvalds/linux/blob/master/include/uapi/linux/io_uring.h
    [Flags]
    public enum IOUringSetupFlags : uint
    {
        None = 0,
        IORING_SETUP_IOPOLL = 1 << 0,
        IORING_SETUP_SQPOLL = 1 << 1,
        IORING_SETUP_SQ_AFF = 1 << 2,
        IORING_SETUP_CQSIZE = 1 << 3,
        IORING_SETUP_CLAMP = 1 << 4,
        IORING_SETUP_ATTACH_WQ = 1 << 5,
        IORING_SETUP_R_DISABLED = 1 << 6,
        IORING_SETUP_SUBMIT_ALL = 1 << 7,
        IORING_SETUP_COOP_TASKRUN = 1 << 8,
        IORING_SETUP_TASKRUN_FLAG = 1 << 9,
        IORING_SETUP_SQE128 = 1 << 10,
        IORING_SETUP_CQE32 = 1 << 11,
        IORING_SETUP_SINGLE_ISSUER = 1 << 12,
        IORING_SETUP_DEFER_TASKRUN = 1 << 13,
        IORING_SETUP_NO_MMAP = 1 << 14,
        IORING_SETUP_REGISTERED_FD_ONLY = 1 << 15,
        IORING_SETUP_NO_SQARRAY = 1 << 16,
        IORING_SETUP_HYBRID_IOPOLL = 1 << 17,
    }

    public struct io_uring
    {
        public io_uring_sq sq;
        public io_uring_cq cq;
        public uint flags;
        int ring_fd;
    }

   public struct io_uring_sq
   {
        uint *khead;
        uint *ktail;
        uint *kring_mask;
        uint *kring_entries;
        uint *kflags;
        uint *kdropped;
        uint *array;
        io_uring_sqe *sqes;

        uint sqe_head;
        uint sqe_tail;

        ulong ring_sz;
        void *ring_ptr;
    };


    public struct io_uring_cq {
        uint *khead;
        uint *ktail;
        uint *kring_mask;
        uint *kring_entries;
        uint *koverflow;
        io_uring_cqe *cqes;

        ulong ring_sz;
        void *ring_ptr;
    };

    public struct io_uring_sqe
    {}

    public struct io_uring_cqe
    {}
}