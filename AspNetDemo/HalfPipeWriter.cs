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
    private CycleBuffer _cb = CycleBuffer.Create();
    private byte[]? _scratch;      // fallback when a GetMemory hint exceeds a CycleBuffer segment
    private int _scratchLen;       // >0 while the last GetMemory handed back the scratch buffer
    private bool _completed;
    // Written by the loop thread (MarkPeerGone, on peer close/abort) and read+written by the Kestrel write
    // thread (FlushAsync); volatile for cross-thread visibility. It is the ONLY cross-thread field — the
    // CycleBuffer itself stays single-thread (Kestrel-only), which is what keeps the whole thing lock-free.
    private volatile bool _peerGone;

    // EXPERIMENT (SS_HALF_DRAIN=pool): move the SEND off the Kestrel request thread. The inline default
    // pays +12-18% p99 because drain+Send runs on that thread; this measures whether hopping Send off it
    // recovers the tail. See DrainViaPool. Default = inline (the measured, shipped behaviour).
    private static readonly bool _poolDrain = Environment.GetEnvironmentVariable("SS_HALF_DRAIN") == "pool";
    private Task _sendTail = Task.CompletedTask; // pool mode: chains sends so they stay ordered

    public HalfPipeWriter(Connection conn) => _conn = conn;

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
        if (!_peerGone)
        {
            ReadOnlySequence<byte> seq = _cb.GetAllCommitted();
            if (!seq.IsEmpty)
            {
                if (_poolDrain) DrainViaPool(seq);
                else DrainInline(seq);
            }
        }
        return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: _peerGone || _completed));
    }

    // Default: drain + Send synchronously on the calling (Kestrel) thread. Lock-free; Send copies, so the
    // CycleBuffer can be recycled the instant it returns.
    private void DrainInline(ReadOnlySequence<byte> seq)
    {
        if (_conn.Send(seq))
        {
            _cb.DiscardCommitted(seq.Length); // Send copied it — safe to recycle now
        }
        else
        {
            _peerGone = true; // socket gone: signal Kestrel to stop writing
            Interlocked.Increment(ref SocketSetConnectionListener.SendFalse);
        }
    }

    // EXPERIMENT: copy the committed bytes into a pooled array on the request thread (cheap memcpy) and
    // discard immediately — so the CycleBuffer stays single-thread/lock-free — then run the SEND on the
    // ThreadPool, chained through _sendTail so sends stay in order. Costs one extra copy + a Task per flush
    // vs inline, and has NO backpressure (fire-and-forget), so it is a diagnostic for "is request-thread
    // Send the p99 culprit?", not a shipping path. If p99 recovers, the loop-thread zero-copy drain (which
    // avoids the copy) is the real fix.
    private void DrainViaPool(ReadOnlySequence<byte> seq)
    {
        int len = checked((int)seq.Length);
        byte[] buf = ArrayPool<byte>.Shared.Rent(len);
        seq.CopyTo(buf);
        _cb.DiscardCommitted(len);
        _sendTail = _sendTail.ContinueWith(_ =>
        {
            try
            {
                if (!_conn.Send(new ReadOnlySpan<byte>(buf, 0, len)))
                {
                    _peerGone = true;
                    Interlocked.Increment(ref SocketSetConnectionListener.SendFalse);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
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
