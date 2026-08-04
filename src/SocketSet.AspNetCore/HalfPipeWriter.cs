using System.Buffers;
using System.IO.Pipelines;
using RESPite.Buffers;
using SocketSets;

namespace SocketSets.AspNet;

/// <summary>
/// The outbound HALF of the "two half-pipes rather than two full pipes" idea. Instead of the outbound
/// <see cref="Pipe"/> + a pump task (classic) or the transport reading the outbound pipe (BYO), this is a
/// <see cref="PipeWriter"/> Kestrel writes to that is backed directly by a <see cref="CycleBuffer"/> — and
/// on <see cref="FlushAsync"/> it drains itself straight to <c>Connection.Send</c> on the CALLING (Kestrel)
/// thread. "We cheat and go direct": no async read loop, no pump task, no ThreadPool hop.
///
/// The lifetime that makes this safe: <c>Connection.Send(sequence)</c> COPIES each segment into library
/// buffers synchronously (see its contract), so once it returns we can DiscardCommitted and recycle the
/// segments immediately. And because the producer (GetMemory/Advance) and the consumer (FlushAsync drain)
/// are BOTH the single Kestrel write thread, nothing else ever touches this CycleBuffer — so it needs no
/// lock, and the single-thread machinery numbers (2.3-3.4x cheaper than Pipe) apply, not the cross-thread
/// ones. The trade vs BYO: this copies on send (Connection.Send) rather than doing a zero-copy writev, so
/// it targets the small/mid payloads and the per-connection pump/hop cost, not 256KB throughput. A future
/// zero-copy half-pipe (loop thread drains via writev) is the deeper step this de-risks.
/// </summary>
internal sealed class HalfPipeWriter : PipeWriter
{
    private readonly Connection _conn;
    private readonly SocketSetTransportMetrics _metrics;
    private CycleBuffer _cb = CycleBuffer.Create();
    private byte[]? _scratch;      // fallback when a GetMemory hint exceeds a CycleBuffer segment
    private int _scratchLen;       // >0 while the last GetMemory handed back the scratch buffer
    private bool _completed;
    // Written by the loop thread (MarkPeerGone, on peer close/abort) and read+written by the Kestrel write
    // thread (FlushAsync); volatile for cross-thread visibility. It is the ONLY cross-thread field — the
    // CycleBuffer itself stays single-thread (Kestrel-only), which is what keeps the whole thing lock-free.
    private volatile bool _peerGone;

    public HalfPipeWriter(Connection conn, SocketSetTransportMetrics metrics)
    {
        _conn = conn;
        _metrics = metrics;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        Memory<byte> m = _cb.GetUncommittedMemory(sizeHint);
        if (m.Length >= Math.Max(1, sizeHint)) { _scratchLen = 0; return m; }
        // Rare: the hint is bigger than a CycleBuffer segment (8KB pages). PipeWriter must return >= hint
        // contiguous, so hand back a scratch array and fold it into the buffer on Advance. Kestrel writes
        // the transport in small chunks, so this stays cold.
        if (_scratch is null || _scratch.Length < sizeHint)
        {
            if (_scratch is not null) ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = ArrayPool<byte>.Shared.Rent(sizeHint);
        }
        _scratchLen = sizeHint;
        return _scratch.AsMemory(0, sizeHint);
    }

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public override void Advance(int bytes)
    {
        if (_scratchLen > 0) { _cb.Write(_scratch.AsSpan(0, bytes)); _scratchLen = 0; return; }
        // Kestrel's Http1OutputProducer does GetMemory ONCE then Advance MANY (headers, then body, into the
        // same retained buffer). CycleBuffer.Commit assumes every Commit follows a fresh GetUncommittedMemory
        // that set `leasedStart` to the current end; a stale lease makes it think a discard happened mid-write
        // and relocate bytes (it copied the response's first bytes over the body). Re-establishing the lease
        // at the current committed end — where Kestrel's contiguous writes actually land — before each Commit
        // makes the multi-Advance pattern safe. hint=0 returns the current segment's remainder (no new alloc
        // while there is room), so the just-written bytes are exactly what Commit(bytes) will publish.
        if (bytes > 0) _cb.GetUncommittedMemory(0);
        _cb.Commit(bytes);
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        // _completed as well as _peerGone: Complete() calls _cb.Release(), handing the CycleBuffer's
        // segments back to the MemoryPool, so draining after it would read RECYCLED pool memory straight
        // into Connection.Send. The FlushResult below already reported _completed; the drain did not check
        // it (2026-08-04 audit). Kestrel should not flush after completing, but "should not" is not a
        // reason to leave a use-after-recycle reachable.
        if (!_peerGone && !_completed)
        {
            ReadOnlySequence<byte> seq = _cb.GetAllCommitted();
            if (!seq.IsEmpty)
            {
                if (_conn.Send(seq))
                {
                    _cb.DiscardCommitted(seq.Length); // Send copied it — safe to recycle now
                }
                else
                {
                    _peerGone = true; // socket gone: signal Kestrel to stop writing
                    _metrics.OnSendFalse();
                }
            }
        }
        return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: _peerGone || _completed));
    }

    /// <summary>Loop thread saw the peer close: stop draining (Kestrel is being torn down anyway).</summary>
    public void MarkPeerGone() => _peerGone = true;

    public override void Complete(Exception? exception = null)
    {
        if (_completed) return;
        _completed = true;
        _cb.Release();
        if (_scratch is not null) { ArrayPool<byte>.Shared.Return(_scratch); _scratch = null; }
    }

    // Kestrel's Http1OutputProducer AND System.Text.Json's flush-decision both read UnflushedBytes to
    // decide when to flush; leaving it unsupported throws NotSupportedException and breaks every response.
    // The half-pipe knows the count exactly: bytes committed to the CycleBuffer but not yet drained.
    public override bool CanGetUnflushedBytes => true;
    public override long UnflushedBytes => _cb.GetCommittedLength();

    // FlushAsync is synchronous (no writer ever blocks; there is no PipeReader side), so cancel is a no-op.
    public override void CancelPendingFlush() { }
}
