#if NET
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SocketSets.Native;
using SocketSets.Tls;

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

/// <summary>An ordered set of committed segments making up one flushed message. The first segment is
/// stored inline (<see cref="First"/>) and the rest, if any, spill to <see cref="Rest"/> — so the
/// dominant single-segment message carries no <see cref="List{T}"/> allocation at all.
/// <see cref="Count"/> is the total (1 + Rest.Count when non-empty; 0 means "no chain").</summary>
internal struct OutChain
{
    // The first segment stays inline (First) and the rest, if any, spill to Rest — so the dominant
    // single-segment message allocates no list. First/Rest are private-by-convention: everything
    // outside this struct goes through Count / the indexer / Add / CopyTo, so the inline storage can
    // be widened later (manual _seg0.._segK-1 fields, or [InlineArray] on net8+) to keep 2..K-segment
    // flushes allocation-free — without touching any caller — if a profile ever shows it hot.
    public OutSeg First;
    public List<OutSeg>? Rest;
    public int Count;

    public readonly OutSeg this[int i] => i == 0 ? First : Rest![i - 1];

    /// <summary>Append a committed segment — the first stays inline, the rest spill to the list.</summary>
    public void Add(in OutSeg seg)
    {
        if (Count == 0) First = seg;
        else (Rest ??= new()).Add(seg);
        Count++;
    }

    /// <summary>Append <paramref name="src"/>[<paramref name="start"/> .. start+<paramref name="len"/>)
    /// to this chain, without linearizing the source — the range's first element becomes this chain's
    /// inline <see cref="First"/> (when empty) and the remainder is bulk-copied from the source's list
    /// span. (Source index <c>j &gt; 0</c> maps to <c>Rest[j-1]</c>, so the remainder starts at
    /// <c>Rest[start]</c> in both the start==0 and start&gt;0 cases.)</summary>
    public void Add(in OutChain src, int start, int len)
    {
        if (len == 0) return;
        Add(src[start]); // establishes First when this chain is empty, else appends
        if (len > 1) AppendToRest(CollectionsMarshal.AsSpan(src.Rest).Slice(start, len - 1));
    }

    // Bulk-append segments to Rest. Precondition: Count >= 1 (First already set), which Add(in OutSeg)
    // above guarantees before this is reached.
    private void AppendToRest(ReadOnlySpan<OutSeg> segs)
    {
        var rest = Rest ??= new();
        int at = rest.Count;
        CollectionsMarshal.SetCount(rest, at + segs.Length);
        segs.CopyTo(CollectionsMarshal.AsSpan(rest)[at..]);
        Count += segs.Length;
    }
}

/// <summary>State of one in-flight scatter-gather send (Op.WriteV). Lives on the connection since at
/// most one send is in flight at a time. On completion the pool pages are released, the POH overflow
/// segments dropped, and the native iovec array freed.</summary>
internal unsafe struct WriteVState
{
    public LibC.iovec* Iov;   // native iovec array (TotalIov entries)
    public int TotalIov;
    public int Cursor;        // first not-fully-sent iovec (advanced on partial writes)
    public long Sent;
    public long Total;
    public OutChain Chain;    // segments backing the iovecs (to release on completion)
    public bool AppData;      // TLS: this send carried encrypted APPLICATION data → fire OnWrite on completion
                              // (handshake records / control replies / plaintext OOB flushes leave it false)
    public ZcJob? Zc;         // non-null => the iovecs point at CALLER memory, not our chain (see ZcJob)
}

/// <summary>
/// A zero-copy send: iovecs pointing straight at a caller's <see cref="ReadOnlySequence{T}"/> segments
/// (pipe memory), rather than at buffers we copied into. Built on the CALLER's thread by
/// <c>TrySendZeroCopy</c> (pinning is thread-agnostic and the sequence is only read), then handed to the
/// loop thread, which owns it from that point.
///
/// OWNERSHIP: the socket reads this memory directly, so the caller must not release it - Kestrel's pump
/// must not <c>AdvanceTo</c> - until <see cref="Completion"/> fires. Every exit path (success, send error,
/// teardown, a slot that went away before the request was drained) must complete it exactly once, or the
/// pump deadlocks holding a <c>ReadResult</c> forever.
/// </summary>
internal sealed unsafe class ZcJob
{
    public LibC.iovec* Iov;           // native iovec array (Count entries), freed by the loop thread
    public int Count;
    public long Total;
    public MemoryHandle[]? Handles;   // pins to dispose; null when the caller asserted already-pinned memory
    public TaskCompletionSource<bool>? Completion;

    /// <summary>Release everything and signal the waiting pump. Idempotent; safe on any thread that owns
    /// the job (the caller before hand-off, the loop thread after).</summary>
    public void Finish(bool ok)
    {
        if (Iov != null) { NativeMemory.Free(Iov); Iov = null; }
        if (Handles is { } hs)
        {
            for (int i = 0; i < Count; i++) hs[i].Dispose();
            Handles = null;
        }
        var tcs = Completion;
        Completion = null;
        tcs?.TrySetResult(ok);
    }
}

/// <summary>
/// Per-connection identity for the io_uring backend, and the <see cref="IBufferWriter{T}"/> write
/// accumulator. One instance exists per slot in the shard's fixed table and is reused across
/// connection lifetimes — accepting/connecting never allocates a Connection. <see cref="Fd"/> doubles
/// as the free/busy marker (0 == free) and the lock-free allocation CAS target; <see cref="Generation"/>
/// is bumped on each (re)allocation so a stale reference held past close is detected and its writes
/// dropped rather than delivered to whichever connection later reused the slot.
///
/// Writer state (the <c>_cur*</c> and <c>_segs</c> fields) is single-writer: the thread
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

    // Teardown state (loop-thread only). The slot is NOT recycled (Fd stays non-zero) until every
    // in-flight op has been reaped — so a completion can never land on a re-tenanted slot. The only
    // ops in flight at close time are the multishot recv (RecvArmed) and at most one send (SendBusy),
    // plus the teardown ASYNC_CANCEL (CancelPending); when all three are clear the slot finalizes.
    public bool Closing;
    public bool RecvArmed;
    public bool CancelPending;

    // --- loop-thread-only send state ---
    // A stream socket must not have two SENDs racing (they can reorder), so at most one send is in
    // flight per connection. Follow-ups — pipelined echoes and flushed writes alike — wait here.
    public bool SendBusy;
    public Queue<PendingJob>? Pending;
    public WriteVState WriteV; // the in-flight scatter-gather send, if any (Chain != null while active)

    // --- TLS (loop-thread only; all null/false unless Options.Tls is set). The filter itself lives on
    // the base Connection (Tls). TLS output rides the OOB chain send path (DispatchChain/WriteV), so no
    // separate send machinery — these are just the loop-thread scratch writers for decrypt/encrypt. ---
    public bool TlsClient;             // role: fire OnConnect (true) vs OnAccept (false) once the handshake completes
    public PooledBufferWriter? TlsPlain; // decrypt target (and greeting scratch)
    public PooledBufferWriter? TlsOut;   // ciphertext scratch (handshake / control replies / encrypt output)

    // --- kTLS path (io_uring only; distinct from the userspace TlsFilter above). OpenSSL owns the fd
    // (socket BIO) and pushed keys to the kernel at handshake completion, so the DATA path is plaintext:
    // TX rides the normal SubmitSend machinery unchanged, RX is SSL_read driven by an io_uring POLL. ---
    public nint KtlsSsl;    // OpenSSL SSL* bound to the fd; 0 == not a kTLS connection
    public bool KtlsReady;  // handshake done + kTLS active → data phase (else still handshaking)
    public byte[]? KtlsRecv; // plaintext SSL_read target (also the OnReceive/response buffer)

    // --- IBufferWriter accumulation (single-writer until Flush) ---
    // Committed segments of the message being built (first inline, rest in a list — a single-segment
    // message allocates no list). Detached and handed to the loop on Flush.
    private OutChain _chain;
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

    public override void Close()
    {
        // Marshal onto the loop thread (generation-guarded there) — safe from any thread, including
        // from inside a callback (the request is honoured on the next loop pass).
        if (Volatile.Read(ref Fd) != 0) Shard.SubmitClose(Slot, Volatile.Read(ref Generation));
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

    // io_uring sends a chain of up to IovMax (1024) segments as ONE writev, so an oversized write should
    // span pooled pages rather than force a contiguous buffer. Ignoring the hint here is what stops
    // EnsureRoom taking the `want > pageSize` branch and allocating the whole response on the pinned heap
    // once per response. Measured: 4KB page, 16KB body, 200k requests - 1.000 pinned allocs/response
    // before, 0.000 after (5 pooled segments instead), and goodput 4,363.7 -> 8,578.0 MiB/s.
    private protected override Span<byte> GetWriteSpan(int sizeHint) => GetSpan();

    /// <summary>
    /// Zero-copy send: point the writev's iovecs straight at the caller's sequence instead of copying it
    /// into pooled pages. io_uring is the natural second driver after IOCP because its send is ALREADY a
    /// vector of segments, and because it needs only a stable address - unlike RIO, which addresses
    /// registered buffer ids and can never take foreign memory.
    ///
    /// It is also the backend that can TEST the thing IOCP's result could not. IOCP measured +3.5% at 16KB
    /// and nothing at 256KB, but it caps at 64 `WSABUF`s and a 256KB response through Kestrel's 4KB pipe
    /// blocks is ~64 segments - so it plausibly declined and fell back to copying at exactly the payload
    /// where the bridge costs 24.5%. `IovMax` is 1024, so that ceiling is not in play here.
    /// </summary>
    internal override bool TrySendZeroCopy(in ReadOnlySequence<byte> data, bool pinned,
                                           out ValueTask<bool> completion)
        => Shard.TrySendZeroCopy(this, in data, pinned, out completion);

    public override bool Flush()
    {
        if (Volatile.Read(ref Fd) == 0) { DiscardWriter(); return false; }
        CommitCurrent();
        if (_chain.Count == 0) return true; // nothing written since the last flush
        var chain = _chain;
        _chain = default;
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
            _chain.Add(new OutSeg { Page = _curPage, Managed = _curManaged, Length = _curPos });
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
        for (int i = 0; i < _chain.Count; i++) ReleaseSeg(_chain[i]);
        _chain = default;
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
/// (<see cref="Seg"/>, when <c>Chain.Count == 0</c>) or a flushed out-of-band <see cref="Chain"/>.</summary>
internal struct PendingJob
{
    public ArraySegment<byte> Seg;
    public OutChain Chain; // Count > 0 => this is a flushed chain; otherwise Seg is the echo response
    public bool AppData;   // TLS: chain carries encrypted application data → fire OnWrite when it completes
    public ZcJob? Zc;      // non-null => a zero-copy send queued behind an in-flight one; takes precedence
                           // over Seg/Chain. Queued here rather than in a side-slot so a connection that
                           // mixes pipe writes with direct sends keeps ONE FIFO order for its byte stream.
}
#endif
