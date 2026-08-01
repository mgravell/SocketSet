using System.Buffers;
using System.Collections.Concurrent;

namespace SocketSets.AspNet;

/// <summary>
/// A minimal pinned-block <see cref="MemoryPool{T}"/> for the bridge's pipes — fixed-size blocks allocated
/// on the pinned object heap and recycled, which is the shape Kestrel's own <c>PinnedBlockMemoryPool</c>
/// uses.
///
/// WHY IT EXISTS. The transport's zero-copy send points the socket at the pipe's own segments. With an
/// ordinary pool those segments are movable, so each one needs a <c>GCHandle</c> pin for the duration of
/// the operation — measured 2026-07-29 at **65 pins and 65 disposes per 256KB response**, because
/// Kestrel's default blocks are 4KB. Blocks that are pinned for their whole lifetime remove that branch
/// entirely: <c>ctx.UsePipe(pipe, pinned: true)</c> then tells the backend the addresses are already
/// stable and it skips per-segment pinning.
///
/// The block size is the second lever. At a 4KB block a 256KB response is ~65 segments; at 64KB it is ~5.
/// That reduces iovec count, <c>WriteAll</c> iterations and pin count together — and 65 is also just past
/// IOCP's 64-segment <c>MaxSendPages</c> cap, which is what silently disabled zero-copy send there.
///
/// Deliberately simple: blocks are never returned to the OS (the pool only grows to the concurrent
/// high-water mark) and the rented size is always the block size. That is fine for a benchmark harness and
/// is NOT a general-purpose pool.
/// </summary>
internal sealed class PinnedBlockMemoryPool(int blockSize) : MemoryPool<byte>
{
    private readonly ConcurrentQueue<PinnedBlock> _blocks = new();
    private bool _disposed;

    public override int MaxBufferSize => blockSize;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        if (minBufferSize > blockSize)
            throw new ArgumentOutOfRangeException(nameof(minBufferSize),
                $"This pool only serves blocks of {blockSize} bytes; asked for {minBufferSize}.");

        if (_blocks.TryDequeue(out var block)) { block.Revive(); return block; }
        return new PinnedBlock(this, blockSize);
    }

    private void Return(PinnedBlock block)
    {
        if (!_disposed) _blocks.Enqueue(block);
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        while (_blocks.TryDequeue(out _)) { }
    }

    private sealed class PinnedBlock : IMemoryOwner<byte>
    {
        private readonly PinnedBlockMemoryPool _pool;
        private readonly byte[] _array;
        private bool _returned;

        public PinnedBlock(PinnedBlockMemoryPool pool, int size)
        {
            _pool = pool;
            // pinned: true → the pinned object heap. The address is stable for the array's lifetime, which
            // is exactly the guarantee `UsePipe(pinned: true)` asserts to the transport.
            _array = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        }

        public Memory<byte> Memory => _array;

        public void Revive() => _returned = false;

        public void Dispose()
        {
            if (_returned) return; // Pipe can dispose a block once; be idempotent rather than double-queue
            _returned = true;
            _pool.Return(this);
        }
    }
}
