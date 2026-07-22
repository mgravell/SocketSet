#if NET // io_uring is a Linux + modern-.NET backend; compiled out of the netfx fallback build.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SocketSets.Native;

namespace SocketSets.IoUring;

internal readonly unsafe struct RawIOUringRing : IDisposable
{
    public readonly int RingFd;
    public readonly uint* SqHead; // probably needs volatile read?
    public readonly uint* SqTail;
    public readonly uint SqMask;
    public readonly LibC.io_uring_sqe* Sqes;
    public readonly uint* SqArray;

    public readonly uint* CqHead;
    public readonly uint* CqTail;
    public readonly uint CqMask;
    public readonly LibC.io_uring_cqe* Cqes;

    private readonly void* _sqPtr;
    private readonly void* _cqPtr;
    private readonly void* _sqesPtr;
    private readonly nuint _sqSize;
    private readonly nuint _cqSize;
    private readonly nuint _sqesSize;

    public RawIOUringRing(uint entries)
    {
        LibC.io_uring_params p = default;
        // Configure the kernel flags for isolated thread execution
        p.flags = LibC.IORING_SETUP_SINGLE_ISSUER | LibC.IORING_SETUP_DEFER_TASKRUN;
        nint fd = LibC.io_uring_setup(LibC.SYS_io_uring_setup, entries, &p);
        if (fd < 0) throw new InvalidOperationException($"Failed io_uring_setup (errno {Marshal.GetLastPInvokeError()})");
        RingFd = (int)fd;

        const int PROT_READ = 0x1;
        const int PROT_WRITE = 0x2;
        const int MAP_SHARED = 0x01;
        const long IORING_OFF_SQ_RING = 0L;
        const long IORING_OFF_CQ_RING = 0x8000000L;
        const long IORING_OFF_SQES = 0x10000000L;

        _sqSize = (nuint)(p.sq_off.array + p.sq_entries * sizeof(uint));
        _cqSize = (nuint)(p.cq_off.cqes + p.cq_entries * sizeof(LibC.io_uring_cqe));
        _sqesSize = p.sq_entries * (nuint)sizeof(LibC.io_uring_sqe);

        _sqPtr = LibC.mmap(null, _sqSize, PROT_READ | PROT_WRITE, MAP_SHARED, RingFd, IORING_OFF_SQ_RING);
        _cqPtr = LibC.mmap(null, _cqSize, PROT_READ | PROT_WRITE, MAP_SHARED, RingFd, IORING_OFF_CQ_RING);
        _sqesPtr = LibC.mmap(null, _sqesSize, PROT_READ | PROT_WRITE, MAP_SHARED, RingFd, IORING_OFF_SQES);

        SqHead = (uint*)((byte*)_sqPtr + p.sq_off.head);
        SqTail = (uint*)((byte*)_sqPtr + p.sq_off.tail);
        SqMask = *(uint*)((byte*)_sqPtr + p.sq_off.ring_mask);
        SqArray = (uint*)((byte*)_sqPtr + p.sq_off.array);

        CqHead = (uint*)((byte*)_cqPtr + p.cq_off.head);
        CqTail = (uint*)((byte*)_cqPtr + p.cq_off.tail);
        CqMask = *(uint*)((byte*)_cqPtr + p.cq_off.ring_mask);
        Cqes = (LibC.io_uring_cqe*)((byte*)_cqPtr + p.cq_off.cqes);
        Sqes = (LibC.io_uring_sqe*)_sqesPtr;
    }

    public void Dispose()
    {
        if (RingFd <= 0) return;
        LibC.munmap(_sqPtr, _sqSize);
        LibC.munmap(_cqPtr, _cqSize);
        LibC.munmap(_sqesPtr, _sqesSize);
        LibC.close(RingFd);
        Unsafe.AsRef(in this) = default; // nuke from orbit
    }

    /// <summary>
    /// Try to place one SQE. Returns false (without faulting) when the submission
    /// queue is full, so the caller can defer the work instead of crashing the loop.
    /// </summary>
    public bool TryPush(in LibC.io_uring_sqe sqe)
    {
        // 1. Read local tail and volatile head
        uint tail = *SqTail;
        uint head = Volatile.Read(ref *SqHead);

        // 2. Full check: distance equals total ring capacity
        if (tail - head >= (SqMask + 1)) return false;

        // 3. Write SQE and array entry
        uint index = tail & SqMask;
        Sqes[index] = sqe;
        SqArray[index] = index;

        // 4. Release barrier: ensures SQE data is visible before kernel sees updated tail
        Volatile.Write(ref *SqTail, tail + 1);
        return true;
    }

    public uint Push(ConcurrentQueue<LibC.io_uring_sqe> sqes)
    {
        if (sqes.IsEmpty) return 0;

        uint count = 0;
        uint tail = *SqTail;
        uint head = Volatile.Read(ref *SqHead);
        uint capacity = SqMask + 1;

        // Loop continues only while we have data AND ring buffer has space
        while (tail - head < capacity)
        {
            uint index = tail & SqMask;
        
            if (!sqes.TryDequeue(out Sqes[index]))
            {
                break; // No more items in the input queue
            }

            count++;
            SqArray[index] = index;
            tail++; // Increment exactly once per item
        }

        // Release barrier: updates tail to kernel once after the whole batch is written
        Volatile.Write(ref *SqTail, tail);
        return count;
    }

    /// <summary>Number of SQEs written but not yet consumed by the kernel — the exact
    /// value to pass as io_uring_enter's to_submit. Derived from the ring, so it can't
    /// drift the way a hand-maintained counter can.</summary>
    public uint SqReady => *SqTail - Volatile.Read(ref *SqHead);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSubmissionQueueFull() => *SqTail - Volatile.Read(ref *SqHead) >= (SqMask + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsCompletionQueueAvailable()
    {
        // fetch sequence is deliberate for instruction ordering; do not one-line this
        uint head = *CqHead;
        uint tail = Volatile.Read(ref *CqTail);
        return head != tail;
    }
}

/// <summary>
/// A kernel-provided buffer ring (IORING_REGISTER_PBUF_RING). Receives select a
/// buffer automatically via IOSQE_BUFFER_SELECT; the completion reports which
/// buffer id was used and we hand it back to the kernel with <see cref="ReleaseBuffer"/>.
/// The ring header aliases io_uring_buf[0] exactly as the kernel expects: buffer
/// entries live at offset 0 and the tail is the resv field of entry 0.
/// </summary>
internal unsafe struct ManagedBufferPool : IDisposable
{
    private readonly uint _entries;
    private readonly uint _bufSize;
    private readonly void* _ringMemory;
    private readonly byte* _dataSlab;
    private readonly LibC.io_uring_buf_ring* _bufRing;
    private readonly LibC.io_uring_buf* _bufs;
    private readonly nuint _ringAllocSize;
    private readonly nuint _dataAllocSize;

    public ushort GroupId { get; init; } = 1;
    public int BufferSize => (int)_bufSize;

    public ManagedBufferPool(int ringFd, int entries = 256, int bufSize = 4096)
    {
        if ((entries & (entries - 1)) != 0) throw new ArgumentException("entries must be a power of two", nameof(entries));
        _entries = (uint)entries;
        _bufSize = (uint)bufSize;

        const int PROT_READ = 0x1;
        const int PROT_WRITE = 0x2;
        const int MAP_ANONYMOUS = 0x20;
        const int MAP_SHARED = 0x01;

        // The ring is exactly one io_uring_buf slot per entry; the header shares
        // slot 0 with the kernel (see io_uring_buf_ring's union). No extra slot.
        _ringAllocSize = (nuint)(sizeof(LibC.io_uring_buf) * _entries);
        _ringMemory = LibC.mmap(null, _ringAllocSize, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);

        _dataAllocSize = _entries * _bufSize;
        _dataSlab = (byte*)LibC.mmap(null, _dataAllocSize, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);

        _bufRing = (LibC.io_uring_buf_ring*)_ringMemory; // used only for ->tail
        _bufs = (LibC.io_uring_buf*)_ringMemory;         // buffer entries (bufs[0] aliases the header)

        for (ushort i = 0; i < _entries; i++)
        {
            _bufs[i].addr = (ulong)(_dataSlab + (i * _bufSize));
            _bufs[i].len = _bufSize;
            _bufs[i].bid = i;
            _bufs[i].resv = 0;
        }

        // Publish all buffers as available. tail aliases bufs[0].resv (offset 14),
        // so this leaves bufs[0].addr/len/bid intact.
        Volatile.Write(ref _bufRing->tail, (ushort)_entries);

        var reg = new LibC.io_uring_buf_reg
        {
            ring_addr = (ulong)_ringMemory,
            ring_entries = _entries,
            bgid = GroupId,
            flags = 0
        };

        int res = LibC.io_uring_register(LibC.SYS_io_uring_register, ringFd, LibC.IORING_REGISTER_PBUF_RING, &reg, 1);
        if (res < 0) throw new InvalidOperationException($"PBUF registration failed: {res} (errno {Marshal.GetLastPInvokeError()})");
    }

    public byte* GetBufferAddress(ushort bid) => _dataSlab + (bid * _bufSize);

    /// <summary>Return a consumed buffer to the kernel. Loop-thread only (single consumer).</summary>
    public void ReleaseBuffer(ushort bid)
    {
        uint mask = _entries - 1;
        ushort tail = _bufRing->tail; // we are the only writer, plain read is fine
        LibC.io_uring_buf* slot = &_bufs[tail & mask];
        slot->addr = (ulong)(_dataSlab + (bid * _bufSize));
        slot->len = _bufSize;
        slot->bid = bid;
        // Release-store the tail so the kernel sees the populated slot first.
        Volatile.Write(ref _bufRing->tail, (ushort)(tail + 1));
    }

    public void Dispose()
    {
        if (_ringMemory == null) return;
        LibC.munmap(_ringMemory, _ringAllocSize);
        LibC.munmap(_dataSlab, _dataAllocSize);
        Unsafe.AsRef(in this) = default;
    }
}

#endif