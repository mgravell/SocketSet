#if NET
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SocketSets.Native;

namespace SocketSets.IoUring;

/// <summary>One committed chunk of an outbound message: either a pinned out-of-band pool page
/// (<see cref="Page"/> &gt;= 0) or a pinned-heap (POH) overflow buffer (<see cref="Page"/> &lt; 0,
/// held in <see cref="Managed"/>, never GC-moved so its address is stable). <see cref="Length"/> is
/// the committed byte count.</summary>
internal struct OutSeg
{
    public int Page;
    public byte[]? Managed;
    public int Length;
}

/// <summary>State of one in-flight scatter-gather send (Op.WriteV). Lives on the connection since at
/// most one send is in flight at a time. On completion the pool pages are released, the managed
/// segments' pins are freed and their buffers returned, and the native iovec array is freed.</summary>
internal unsafe struct WriteVState
{
    public LibC.iovec* Iov;   // native iovec array (TotalIov entries)
    public int TotalIov;
    public int Cursor;        // first not-fully-sent iovec (advanced on partial writes)
    public long Sent;
    public long Total;
    public List<OutSeg> Chain;    // segments backing the iovecs (to release on completion)
}

/// <summary>
/// Per-connection identity for the io_uring backend, and the <see cref="IBufferWriter{T}"/> write
/// accumulator. One instance exists per slot in the shard's fixed table and is reused across
/// connection lifetimes — accepting/connecting never allocates a Connection. <see cref="Fd"/> doubles
/// as the free/busy marker (0 == free) and the lock-free allocation CAS target; <see cref="Generation"/>
/// is bumped on each (re)allocation so a stale reference held past close is detected and its writes
/// dropped rather than delivered to whichever connection later reused the slot.
///
/// Writer state (the <c>_cur*</c> fields and <see cref="_segs"/>) is single-writer: the thread
/// currently writing to this connection owns it until <see cref="Flush"/>. Flush detaches the chain
/// and marshals it onto the loop thread, which owns it from then on.
/// </summary>
internal sealed unsafe class IoUringConnection : Connection
{
    public readonly IoUringShard Shard;

    /// <summary>1-based table id (matches the packed <c>id</c> in user_data). Stable for this instance.</summary>
    public readonly uint Slot;

    /// <summary>Live fd, or 0 when the slot is free. CAS 0-&gt;fd claims the slot; loop thread reads/clears it.</summary>
    public int Fd;

    /// <summary>Bumped on each allocation; guards writes against slot reuse (ABA).</summary>
    public uint Generation;

    // --- loop-thread-only send state ---
    // A stream socket must not have two SENDs racing (they can reorder), so at most one send is in
    // flight per connection. Follow-ups — pipelined echoes and flushed writes alike — wait here.
    public bool SendBusy;
    public Queue<PendingJob>? Pending;
    public WriteVState WriteV; // the in-flight scatter-gather send, if any (Chain != null while active)

    // --- IBufferWriter accumulation (single-writer until Flush) ---
    private List<OutSeg>? _segs;   // committed segments for the message being built
    private int _curPage = -1;     // current segment: pool page index, or -1 if managed / none
    private byte[]? _curManaged;   // current segment's managed buffer (when _curPage < 0)
    private byte* _curBase;        // native base of the current pool page (valid iff _curPage >= 0)
    private int _curCap;           // current segment capacity (0 == no current segment)
    private int _curPos;           // bytes written into the current segment
    private WriteMemoryManager? _curMemMgr; // backs GetMemory over the current buffer; invalidated on switch

    public IoUringConnection(IoUringShard shard, uint slot)
    {
        Shard = shard;
        Slot = slot;
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureRoom(sizeHint <= 0 ? 1 : sizeHint);
        return _curPage >= 0
            ? new Span<byte>(_curBase + _curPos, _curCap - _curPos)
            : new Span<byte>(_curManaged!, _curPos, _curCap - _curPos);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        // Same acquisition as GetSpan (pool page preferred, POH overflow), then wrap the current
        // buffer in a MemoryManager so a Memory can front either backing. The manager is created once
        // per buffer-epoch and invalidated when we move off the buffer, so a stashed Memory throws
        // rather than reading a recycled page.
        EnsureRoom(sizeHint <= 0 ? 1 : sizeHint);
        _curMemMgr ??= _curPage >= 0
            ? new WriteMemoryManager(_curBase, _curCap)
            : new WriteMemoryManager(_curManaged!, _curCap);
        return _curMemMgr.Memory.Slice(_curPos, _curCap - _curPos);
    }

    public override void Advance(int count) => _curPos += count;

    public override bool Flush()
    {
        if (Volatile.Read(ref Fd) == 0) { DiscardWriter(); return false; }
        CommitCurrent();
        if (_segs is not { Count: > 0 }) return true; // nothing written since the last flush
        var chain = _segs;
        _segs = null;
        Shard.SubmitFlush(Slot, Volatile.Read(ref Generation), chain);
        return true;
    }

    // Ensure the current segment has room for at least `want` bytes, acquiring a new segment if not.
    private void EnsureRoom(int want)
    {
        if (_curCap - _curPos >= want) return;
        CommitCurrent();

        int pageSize = Shard.WritePageSize;
        if (want > pageSize)
        {
            // Too big for a single pool page — spill straight to a right-sized pinned-heap buffer.
            _curManaged = GC.AllocateUninitializedArray<byte>(want, pinned: true);
            _curPage = -1;
            _curBase = null;
            _curCap = _curManaged.Length;
        }
        else if (Shard.LeaseOutOfBand(out int idx, out byte* p))
        {
            _curPage = idx;
            _curBase = p;
            _curManaged = null;
            _curCap = pageSize;
        }
        else
        {
            // Native pool exhausted → pinned-heap overflow.
            _curManaged = GC.AllocateUninitializedArray<byte>(pageSize, pinned: true);
            _curPage = -1;
            _curBase = null;
            _curCap = _curManaged.Length;
        }
        _curPos = 0;
    }

    // Push the current segment onto the chain (or release it if empty) and clear the current slot.
    private void CommitCurrent()
    {
        // Any Memory handed out over this buffer is now stale — make its use throw, not silently read
        // a recycled page.
        if (_curMemMgr is { } mgr) { mgr.Invalidate(); _curMemMgr = null; }

        if (_curCap == 0) return;
        if (_curPos > 0)
        {
            (_segs ??= new()).Add(new OutSeg { Page = _curPage, Managed = _curManaged, Length = _curPos });
        }
        else
        {
            // Acquired but never written — release a pool page (POH overflow buffers just drop).
            if (_curPage >= 0) Shard.ReleaseOutOfBand(_curPage);
        }
        _curPage = -1;
        _curManaged = null;
        _curBase = null;
        _curCap = 0;
        _curPos = 0;
    }

    // Drop everything staged since the last flush (connection closed under the writer).
    private void DiscardWriter()
    {
        CommitCurrent();
        if (_segs is { } segs)
        {
            foreach (var seg in segs) ReleaseSeg(seg);
            segs.Clear();
        }
    }

    internal void ReleaseSeg(in OutSeg seg)
    {
        // Only native pool pages need returning; POH overflow buffers are just dropped for GC.
        if (seg.Page >= 0) Shard.ReleaseOutOfBand(seg.Page);
    }

    /// <summary>Fronts one write buffer — a native pool page (pointer) or a pinned-heap array — as a
    /// <see cref="Memory{T}"/>, so <see cref="GetMemory"/> can hand one back over either backing.
    /// A fresh instance is created per buffer-epoch and <see cref="Invalidate"/>d when the writer moves
    /// off that buffer; a <see cref="Memory{T}"/> retained past that point throws on use rather than
    /// aliasing a recycled buffer.</summary>
    private sealed class WriteMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _ptr;
        private readonly byte[]? _array; // POH-pinned when non-null (address is stable)
        private readonly int _length;
        private bool _valid = true;

        public WriteMemoryManager(byte* ptr, int length) { _ptr = ptr; _length = length; }
        public WriteMemoryManager(byte[] array, int length) { _array = array; _length = length; }

        public void Invalidate() => _valid = false;

        public override Span<byte> GetSpan()
        {
            if (!_valid) ThrowStale();
            return _array is not null ? _array.AsSpan(0, _length) : new Span<byte>(_ptr, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (!_valid) ThrowStale();
            // Both backings are already pinned (native pool page / POH array), so no GCHandle needed.
            byte* p = _array is not null
                ? (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_array))
                : _ptr;
            return new MemoryHandle(p + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) => _valid = false;

        private static void ThrowStale() => throw new ObjectDisposedException(nameof(WriteMemoryManager),
            "This Memory is stale: writer buffers are valid only until the next buffer switch or Flush.");

        // Exposes the POH overflow array for perf (avoids a copy/pin in callers that special-case
        // arrays). No more dangerous than Pin: both hand out a handle the manager can't invalidate
        // once taken. Safe today because POH overflow arrays are per-use garbage (a stashed segment
        // just roots the array). CAVEAT: if we ever pool/recycle POH arrays, an extracted segment
        // becomes a use-after-free — exclude TryGetArray-reachable arrays from that pool.
        protected override bool TryGetArray(out ArraySegment<byte> segment)
        {
            if (_valid & _array is not null)
            {
                segment = new(_array!, 0, _length);
                return true;
            }
            segment = default;
            return false;
        }
    }
}

/// <summary>A queued send waiting behind the in-flight one: either a single-buffer echo response
/// (<see cref="Seg"/>) or a flushed out-of-band <see cref="Chain"/>.</summary>
internal struct PendingJob
{
    public ArraySegment<byte> Seg;
    public List<OutSeg>? Chain;
}
#endif
