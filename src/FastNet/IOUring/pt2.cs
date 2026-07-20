using FastNet.IOUring;

internal unsafe class RawIOUringRing : IDisposable
{
    public int RingFd { get; private set; }
    public uint* SqHead;
    public uint* SqTail;
    public uint SqMask;
    public io_uring_sqe* Sqes;
    public uint* SqArray;

    public uint* CqHead;
    public uint* CqTail;
    public uint CqMask;
    public io_uring_cqe* Cqes;

    private void* _sqPtr;
    private void* _cqPtr;
    private void* _sqesPtr;
    private nuint _sqSize;
    private nuint _cqSize;
    private nuint _sqesSize;

    public RawIOUringRing(uint entries)
    {
        io_uring_params p = default;
        // Configure the kernel flags for isolated thread execution
        p.flags = LinuxSyscall.IORING_SETUP_SINGLE_ISSUER | LinuxSyscall.IORING_SETUP_DEFER_TASKRUN;
        nint fd = LinuxSyscall.io_uring_setup(LinuxSyscall.SYS_io_uring_setup, entries, &p);
        if (fd < 0) throw new InvalidOperationException("Failed io_uring_setup");
        RingFd = (int)fd;

        const int PROT_READ = 0x1;
        const int PROT_WRITE = 0x2;
        const int MAP_SHARED = 0x01;
        const long IORING_OFF_SQ_RING = 0L;
        const long IORING_OFF_CQ_RING = 0x8000000L;
        const long IORING_OFF_SQES = 0x10000000L;

        _sqSize = (nuint)(p.sq_off.array + p.sq_entries * sizeof(uint));
        _cqSize = (nuint)(p.cq_off.cqes + p.cq_entries * sizeof(io_uring_cqe));
        _sqesSize = p.sq_entries * (nuint)sizeof(io_uring_sqe);

        _sqPtr = LinuxSyscall.mmap(null, _sqSize, PROT_READ | PROT_WRITE, MAP_SHARED, RingFd, IORING_OFF_SQ_RING);
        _cqPtr = LinuxSyscall.mmap(null, _cqSize, PROT_READ | PROT_WRITE, MAP_SHARED, RingFd, IORING_OFF_CQ_RING);
        _sqesPtr = LinuxSyscall.mmap(null, _sqesSize, PROT_READ | PROT_WRITE, MAP_SHARED, RingFd, IORING_OFF_SQES);

        SqHead = (uint*)((byte*)_sqPtr + p.sq_off.head);
        SqTail = (uint*)((byte*)_sqPtr + p.sq_off.tail);
        SqMask = *(uint*)((byte*)_sqPtr + p.sq_off.ring_mask);
        SqArray = (uint*)((byte*)_sqPtr + p.sq_off.array);

        CqHead = (uint*)((byte*)_cqPtr + p.cq_off.head);
        CqTail = (uint*)((byte*)_cqPtr + p.cq_off.tail);
        CqMask = *(uint*)((byte*)_cqPtr + p.cq_off.ring_mask);
        Cqes = (io_uring_cqe*)((byte*)_cqPtr + p.cq_off.cqes);
        Sqes = (io_uring_sqe*)_sqesPtr;
    }

    public void Dispose()
    {
        if (RingFd <= 0) return;
        LinuxSyscall.munmap(_sqPtr, _sqSize);
        LinuxSyscall.munmap(_cqPtr, _cqSize);
        LinuxSyscall.munmap(_sqesPtr, _sqesSize);
        LinuxSyscall.close(RingFd);
        RingFd = 0;
    }
}

internal unsafe class ManagedBufferPool : IDisposable
{
    private readonly uint _entries;
    private readonly uint _bufSize;
    private void* _ringMemory;
    private byte* _dataSlab;
    private io_uring_buf_ring* _bufRing;
    private io_uring_buf* _bufsArray;
    private nuint _ringAllocSize;
    private nuint _dataAllocSize;

    public ushort GroupId { get; init; } = 1;

    public ManagedBufferPool(int ringFd, uint entries = 256, uint bufSize = 4096)
    {
        _entries = entries; // Must be power of 2
        _bufSize = bufSize;

        const int PROT_READ = 0x1;
        const int PROT_WRITE = 0x2;
        const int MAP_ANONYMOUS = 0x20;
        const int MAP_SHARED = 0x01;

        _ringAllocSize = (nuint)(sizeof(io_uring_buf_ring) + (sizeof(io_uring_buf) * _entries));
        _ringMemory = LinuxSyscall.mmap(null, _ringAllocSize, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);

        _dataAllocSize = _entries * _bufSize;
        _dataSlab = (byte*)LinuxSyscall.mmap(null, _dataAllocSize, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);

        _bufRing = (io_uring_buf_ring*)_ringMemory;
        _bufsArray = (io_uring_buf*)((byte*)_ringMemory + sizeof(io_uring_buf_ring));

        for (ushort i = 0; i < _entries; i++)
        {
            _bufsArray[i].addr = (ulong)(_dataSlab + (i * _bufSize));
            _bufsArray[i].len = _bufSize;
            _bufsArray[i].bid = i;
        }

        _bufRing->tail = (ushort)_entries;

        var reg = new io_uring_buf_reg
        {
            ring_addr = (ulong)_ringMemory,
            ring_entries = _entries,
            bgid = GroupId,
            flags = 0
        };

        int res = LinuxSyscall.io_uring_register(LinuxSyscall.SYS_io_uring_register, ringFd, LinuxSyscall.IORING_REGISTER_PBUF_RING, &reg, 1);
        if (res < 0) throw new InvalidOperationException($"PBUF Registration failed: {res}");
    }

    public byte* GetBufferAddress(ushort bid) => _dataSlab + (bid * _bufSize);

    public void ReleaseBuffer(ushort bid)
    {
        ushort currentTail = _bufRing->tail;
        uint mask = _entries - 1;
        
        io_uring_buf* targetSlot = &_bufsArray[currentTail & mask];
        targetSlot->addr = (ulong)(_dataSlab + (bid * _bufSize));
        targetSlot->len = _bufSize;
        targetSlot->bid = bid;

        _bufRing->tail = (ushort)(currentTail + 1);
    }

    public void Dispose()
    {
        if (_ringMemory == null) return;
        LinuxSyscall.munmap(_ringMemory, _ringAllocSize);
        LinuxSyscall.munmap(_dataSlab, _dataAllocSize);
        _ringMemory = null;
    }
}
