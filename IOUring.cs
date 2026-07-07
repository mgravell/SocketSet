using System;
using System.Net;
using System.Runtime.InteropServices;
using FastNet.Abstraction;

namespace FastNet.LinuxUring;

public unsafe class IoUringEngine : IOEngine
{
    private const string LibName = "uring";

    // --- NATIVE LIBC & LIBURING P/INVOKES ---
    // We use standard P/Invoke, resolved dynamically via our CustomLoader below.
    [DllImport(LibName, SetLastError = true)]
    private static extern int io_uring_queue_init(uint entries, IntPtr ring, uint flags);

    [DllImport(LibName, SetLastError = true)]
    private static extern int io_uring_register_buffers(IntPtr ring, iovec* io_vecs, uint nr_io_vecs);

    [DllImport(LibName, SetLastError = true)]
    private static extern int io_uring_submit(IntPtr ring);

    [DllImport(LibName, SetLastError = true)]
    private static extern int io_uring_get_sqe(IntPtr ring, out IntPtr sqe);

    [DllImport(LibName, SetLastError = true)]
    private static extern int io_uring_peek_cqe(IntPtr ring, out IntPtr cqe);

    [DllImport(LibName, SetLastError = true)]
    private static extern void io_uring_cqe_seen(IntPtr ring, IntPtr cqe);

    [DllImport(LibName, SetLastError = true)]
    private static extern void io_uring_queue_exit(IntPtr ring);

    // Native liburing helpers are replicated manually since inline static inline C macros can't be P/Invoked
    private static void io_uring_prep_recv(IntPtr sqe, int fd, IntPtr buf, uint len, int flags)
    {
        byte* pSqe = (byte*)sqe;
        pSqe[0] = 18; // opcode: IORING_OP_RECV
        *(int*)(pSqe + 4) = fd; // fd field
        *(ulong*)(pSqe + 16) = (ulong)buf; // addr field
        *(uint*)(pSqe + 24) = len; // len field
        *(int*)(pSqe + 28) = flags; // msg_flags
    }

    private static void io_uring_prep_send(IntPtr sqe, int fd, IntPtr buf, uint len, int flags)
    {
        byte* pSqe = (byte*)sqe;
        pSqe[0] = 19; // opcode: IORING_OP_SEND
        *(int*)(pSqe + 4) = fd;
        *(ulong*)(pSqe + 16) = (ulong)buf;
        *(uint*)(pSqe + 24) = len;
        *(int*)(pSqe + 28) = flags;
    }

    private static void io_uring_sqe_set_data(IntPtr sqe, ulong data)
    {
        *(ulong*)((byte*)sqe + 56) = data; // user_data field
    }

    // --- NATIVE MEMORY TYPES ---
    [StructLayout(LayoutKind.Sequential)]
    private struct iovec
    {
        public IntPtr iov_base;
        public UIntPtr iov_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct io_uring_cqe
    {
        public ulong user_data;
        public int res;
        public uint flags;
    }

    // --- FIELDS ---
    private IntPtr _ringMemory; // Enforces the opaque tracker constraint
    private GCHandle _bufferHandle;
    private IntPtr _megaBufferPinnedPtr;
    private int _bufferSize;

    static IoUringEngine()
    {
        // Register a custom native resolver to seamlessly map "uring" across variable distro names
        NativeLibrary.SetDllImportResolver(typeof(IoUringEngine).Assembly, ResolveUringBinary);
    }

    private static IntPtr ResolveUringBinary(string libraryName, System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (libraryName != LibName) return IntPtr.Zero;

        // Enumerate known liburing variants found across Ubuntu/Debian/Arch/RHEL targets
        string[] probeNames = { "liburing.so.2", "liburing.so.1", "liburing.so", "uring" };
        foreach (var name in probeNames)
        {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException("Could not locate a valid liburing binary on this kernel instance.");
    }

    public void Initialize(IPEndPoint endpoint, int maxConnections, int bufferSize)
    {
        _bufferSize = bufferSize;

        // Allocate memory block for the opaque io_uring tracking struct 
        // liburing's 'struct io_uring' typically spans 224 bytes on 64-bit architecture
        _ringMemory = Marshal.AllocHGlobal(256);

        // Initialize the submission and completion rings (Queue size = 256 entries)
        int result = io_uring_queue_init(256, _ringMemory, 0);
        if (result < 0) throw new Exception($"io_uring initialization failed: {result}");

        Console.WriteLine("[io_uring] Engine initialized and dynamic system library linked successfully.");
    }

    public void RegisterBuffers(byte[] megaBuffer)
    {
        _bufferHandle = GCHandle.Alloc(megaBuffer, GCHandleType.Pinned);
        _megaBufferPinnedPtr = _bufferHandle.AddrOfPinnedObject();

        iovec vec = new iovec
        {
            iov_base = _megaBufferPinnedPtr,
            iov_len = (UIntPtr)megaBuffer.Length
        };

        // Pre-registers memory with the kernel to ensure zero-copy memory access optimization
        io_uring_register_buffers(_ringMemory, &vec, 1);
    }

    public void PostAccept()
    {
        // Similar to RIO, incoming edge connection handles are harvested via standard non-blocking loops,
        // then registered descriptors are handed off directly to the asynchronous io_uring worker engine.
    }

    public void PostReceive(IntPtr socketContext, BufferSlice slice)
    {
        if (io_uring_get_sqe(_ringMemory, out IntPtr sqe) == 0 || sqe == IntPtr.Zero) return;

        int clientFd = socketContext.ToInt32();
        IntPtr bufferTarget = IntPtr.Add(_megaBufferPinnedPtr, slice.Offset);

        // Populate the Submission Queue Entry using the opaque layouts
        io_uring_prep_recv(sqe, clientFd, bufferTarget, (uint)slice.Length, 0);

        // Pack structural routing metadata into user_data tracking
        ulong contextId = PackContextToken(OpType.Receive, slice.Id, slice.Offset);
        io_uring_sqe_set_data(sqe, contextId);

        io_uring_submit(_ringMemory);
    }

    public void PostSend(IntPtr socketContext, BufferSlice slice)
    {
        if (io_uring_get_sqe(_ringMemory, out IntPtr sqe) == 0 || sqe == IntPtr.Zero) return;

        int clientFd = socketContext.ToInt32();
        IntPtr bufferSource = IntPtr.Add(_megaBufferPinnedPtr, slice.Offset);

        io_uring_prep_send(sqe, clientFd, bufferSource, (uint)slice.Length, 0);

        ulong contextId = PackContextToken(OpType.Send, slice.Id, slice.Offset);
        io_uring_sqe_set_data(sqe, contextId);

        io_uring_submit(_ringMemory);
    }

    public void PollCompletions(Action<AsyncResult> onComplete)
    {
        // Peek at the Completion Queue Ring to see if any asynchronous operations finished
        while (io_uring_peek_cqe(_ringMemory, out IntPtr cqePtr) == 0 && cqePtr != IntPtr.Zero)
        {
            io_uring_cqe* cqe = (io_uring_cqe*)cqePtr;

            // Decode user_data back into structural engine properties
            UnpackContextToken(cqe->user_data, out OpType operation, out int sliceId, out int offset);

            AsyncResult result = new AsyncResult
            {
                Operation = operation,
                SocketContext = new IntPtr(12), // Placeholder client socket file descriptor
                BytesTransferred = cqe->res, // cqe->res holds bytes processed (or negative errno on failure)
                Success = cqe->res >= 0,
                Slice = new BufferSlice { Id = sliceId, Offset = offset, Length = _bufferSize }
            };

            // Notify abstraction application layer loop
            onComplete(result);

            // Reclaim the processed CQE slot back to the kernel ring
            io_uring_cqe_seen(_ringMemory, cqePtr);
        }
    }

    // --- COMPACT WORK CONTEXT PACKING ---
    // Encodes multi-field metadata safely within a native 64-bit integer
    private static ulong PackContextToken(OpType op, int id, int offset) =>
        ((ulong)op << 48) | ((ulong)(id & 0xFFFF) << 32) | (ulong)(uint)offset;

    private static void UnpackContextToken(ulong token, out OpType op, out int id, out int offset)
    {
        op = (OpType)(token >> 48);
        id = (int)((token >> 32) & 0xFFFF);
        offset = (int)(uint)(token & 0xFFFFFFFF);
    }

    public void Dispose()
    {
        if (_ringMemory != IntPtr.Zero)
        {
            io_uring_queue_exit(_ringMemory);
            Marshal.FreeHGlobal(_ringMemory);
            _ringMemory = IntPtr.Zero;
        }

        if (_bufferHandle.IsAllocated) _bufferHandle.Free();
    }
}