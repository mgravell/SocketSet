#if NET // native (pinned) memory pool — used by the transport backends (io_uring today, IOCP/RIO on Windows next), compiled out of the netfx fallback.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SocketSets;

/// <summary>
/// Library-owned, pre-pinned outbound buffers backed by a single native slab. The caller leases a
/// slot, writes into it, and the send completion releases it — no per-operation pinning of arbitrary
/// user memory. This is the model RIO/DK need (the transport owns the memory registered with the NIC)
/// and that the Windows IOCP backend will reuse; io_uring does not strictly require it, but sharing
/// the model keeps the surface uniform across backends.
///
/// Not thread-safe: a single owner (a shard's loop thread) leases/releases. The out-of-band path that
/// needs cross-thread leasing guards its own instance with a lock at the shard level.
/// </summary>
internal unsafe struct PinnedWriteBufferPool : IDisposable
{
    private readonly byte* _slab;
    private readonly int _count;
    private readonly int _bufSize;
    private readonly int[] _free;
    private int _freeTop; // number of free indices in [0.._freeTop)

    public int BufferSize => _bufSize;

    public PinnedWriteBufferPool(int count, int bufSize)
    {
        _count = count;
        _bufSize = bufSize;
        _slab = (byte*)NativeMemory.AllocZeroed((nuint)count * (nuint)bufSize);
        _free = new int[count];
        for (int i = 0; i < count; i++) _free[i] = i;
        _freeTop = count;
    }

    public bool TryLease(out int index, out byte* ptr)
    {
        if (_freeTop == 0)
        {
            index = -1;
            ptr = null;
            return false;
        }

        index = _free[--_freeTop];
        ptr = _slab + ((long)index * _bufSize);
        return true;
    }

    /// <summary>Return a leased page. Guarded for the same reason as <c>SlotAllocator.Free</c>: a double
    /// release duplicates the index in the free list, so two in-flight sends end up writing the same
    /// pinned page and each transmits a slice of the other's payload. Added 2026-08-04.</summary>
    public void Release(int index)
    {
        if ((uint)index >= (uint)_count || _freeTop >= _count)
            throw new InvalidOperationException(
                $"PinnedWriteBufferPool.Release({index}) is out of range or would overflow the free list " +
                $"(count {_count}, free {_freeTop}) — almost certainly a double release.");
        _free[_freeTop++] = index;
    }

    public byte* Address(int index) => _slab + ((long)index * _bufSize);

    public void Dispose()
    {
        if (_slab == null) return;
        NativeMemory.Free(_slab);
        Unsafe.AsRef(in this) = default;
    }
}
#endif
