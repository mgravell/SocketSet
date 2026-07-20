namespace FastNet.IOUring;

internal unsafe class ManagedBufferPool : IDisposable
{
    private readonly uint _entries;
    private readonly uint _bufSize;
    private void* _ringMemory;
    private byte* _dataSlab;
    
    private io_uring_buf_ring* _bufRing;
    private io_uring_buf* _bufsArray;

    public ushort GroupId { get; init; } = 1;

    public ManagedBufferPool(int ringFd, uint entries = 256, uint bufSize = 4096)
    {
        _entries = entries; // Must be power of 2
        _bufSize = bufSize;

        const int PROT_READ = 0x1;
        const int PROT_WRITE = 0x2;
        const int MAP_ANONYMOUS = 0x20;
        const int MAP_SHARED = 0x01;

        // 1. Allocate memory for the ring tracking header + entry array
        nuint ringAllocationSize = (nuint)(sizeof(io_uring_buf_ring) + (sizeof(io_uring_buf) * _entries));
        _ringMemory = LinuxSyscall.mmap(null, ringAllocationSize, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
        
        // 2. Allocate memory for the actual data byte storage slabs
        _dataSlab = (byte*)LinuxSyscall.mmap(null, _entries * _bufSize, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);

        _bufRing = (io_uring_buf_ring*)_ringMemory;
        // The array of entries starts immediately after the header structure
        _bufsArray = (io_uring_buf*)((byte*)_ringMemory + sizeof(io_uring_buf_ring));

        // 3. Populate the buffer list elements
        for (ushort i = 0; i < _entries; i++)
        {
            _bufsArray[i].addr = (ulong)(_dataSlab + (i * _bufSize));
            _bufsArray[i].len = _bufSize;
            _bufsArray[i].bid = i; // The unique integer ID for this buffer slot
        }

        // Initially, all buffers belong to the kernel, set the tail pointer
        _bufRing->tail = (ushort)_entries;

        // 4. Register the buffer ring with the kernel
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

    // Translate a Kernel-supplied BID back to a safe memory address space window
    public byte* GetBufferAddress(ushort bid) => _dataSlab + (bid * _bufSize);

    // RETURN BUFFER TO KERNEL: Simply increment the memory-mapped tail pointer!
    // No P/Invokes, no context switches.
    public void ReleaseBuffer(ushort bid)
    {
        ushort currentTail = _bufRing->tail;
        uint mask = _entries - 1;
        
        // Overwrite the slot at the current tail position with the released buffer details
        io_uring_buf* targetSlot = &_bufsArray[currentTail & mask];
        targetSlot->addr = (ulong)(_dataSlab + (bid * _bufSize));
        targetSlot->len = _bufSize;
        targetSlot->bid = bid;

        // Commit the change to the kernel by updating the tail index
        _bufRing->tail = (ushort)(currentTail + 1);
    }

    public void Dispose() { /* munmap allocations */ }
}
