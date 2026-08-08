#if NET // Windows IOCP/RIO backends; compiled out of the netfx fallback build.
namespace SocketSets.Windows;

/// <summary>
/// The cross-thread submission surface a <see cref="WindowsConnection"/> needs from its owning shard.
/// Both Windows shards marshal work onto their loop thread the same way, so the connection base can call
/// through this instead of holding a backend-typed shard reference - which is the only thing that
/// previously stopped <c>Close</c> / <c>SubmitOutbound</c> from being written once.
///
/// Interface dispatch is fine here: the marshaling members enqueue onto a <c>ConcurrentQueue</c> and poke
/// the loop, so the call is already dominated by the enqueue. <see cref="TryFlushOnLoop"/> IS on the
/// per-operation path, but it replaces strictly more work than the dispatch costs.
/// </summary>
internal interface IWindowsShard
{
    void SubmitClose(uint slot, uint generation);
    void SubmitFlush(uint slot, uint generation, byte[] data, int length);
    void SubmitResumeReceive(uint slot, uint generation);
    bool TryFlushOnLoop(uint slot, uint generation, byte[] data, int length);
}

/// <summary>
/// State and behaviour common to <c>IocpConnection</c> and <c>RioConnection</c>. Both backends run one
/// loop thread per shard over a fixed slot table whose entries are reused across connection lifetimes,
/// and both use the same identity and teardown protocol - so all of that lives here and the derived
/// types carry only what is genuinely backend-specific:
///
///   IOCP: <c>SkipOnSuccess</c> (FILE_SKIP_COMPLETION_PORT_ON_SUCCESS) and the send PAGE ARRAY that lets
///         one WSASend cover up to 64 WSABUFs.
///   RIO:  the request queue handle and its deferred-commit bookkeeping.
///
/// The send page array is deliberately NOT here. It looks like duplication that got missed, and is not:
/// RIO cannot scatter-gather at all (Windows caps RIOCreateRequestQueue's maxSendDataBuffers at 1 and
/// returns WSAEINVAL above it - established 2026-07-27), so one RIOSend is one contiguous buffer,
/// permanently. The two send state machines differ because the operating system makes them differ.
///
/// <see cref="Socket"/> doubles as the free/busy marker (0 == free) and the lock-free allocation CAS
/// target; <see cref="Generation"/> is bumped on each (re)allocation so a stale reference held past close
/// is detected and its Close/writes dropped rather than misdelivered to whoever later reuses the slot.
/// </summary>
internal abstract class WindowsConnection : OutboundConnection
{
    private readonly IWindowsShard _shard;

    /// <summary>1-based table id. Stable for this instance.</summary>
    public readonly uint Slot;

    /// <summary>Live SOCKET handle, or 0 when the slot is free. CAS 0-&gt;handle claims the slot; the loop
    /// thread reads/clears it. Stays non-zero (the now-closed handle) through teardown so a racing
    /// InitClient cannot re-tenant a slot whose ops are still draining.</summary>
    public nint Socket;

    /// <summary>Bumped on each allocation; guards Close/writes against slot reuse (ABA).</summary>
    public uint Generation;

    /// <summary>Which side of the TLS handshake this connection is, so the shard knows whether the
    /// deferred open fires OnConnect or OnAccept. Only meaningful when <c>Tls</c> is set.</summary>
    public bool IsClient;

    // --- teardown state (loop-thread only) ---
    // The slot is NOT recycled (Socket stays non-zero) until every in-flight op has been reaped, so a
    // completion can never land on a re-tenanted slot. closesocket() aborts the pending recv/send; the
    // only ops outstanding at close are the recv (RecvArmed) and at most one send (SendBusy). When both
    // clear, the slot finalizes.
    public bool Closing;
    public bool RecvArmed;

    // --- recv (loop-thread only) ---
    /// <summary>Index into the shard's recv-buffer pool; held for the connection's lifetime, -1 if none.</summary>
    public int RecvBuf = -1;

    // --- send serialization (loop-thread only) ---
    // A stream socket must not have two sends racing (they can reorder), so at most one send is in flight
    // per connection. An echo that arrives while one is busy is copied out and queued here.
    public bool SendBusy;
    public int SendBuf = -1;   // write-pool index of the in-flight send (page 0 of it, on IOCP)
    public int SendSent;       // bytes of the current send already acknowledged (partial-send cursor)
    public int SendTotal;      // total bytes of the current send
    public Queue<ArraySegment<byte>>? Pending;

    /// <summary>Bytes of the HEAD <see cref="Pending"/> segment already copied onto the wire. Non-zero only
    /// while a segment is being consumed across several send pages, which became possible when ciphertext
    /// started being staged whole (see <c>StageOutboundOwned</c>) instead of pre-chunked to the page size.
    /// Loop-thread only, and MUST be cleared wherever <see cref="Pending"/> is — a stale offset would skip
    /// the first bytes of an unrelated later segment, i.e. silently corrupt the stream rather than fail.</summary>
    public int PendingHeadOffset;

    /// <summary>This connection wanted a write page and the pool was dry, so its bytes are staged in
    /// <see cref="Pending"/> and it is queued for retry on a later loop pass. Loop-thread only. Exists
    /// because the alternative — what this replaced — was tearing down a healthy connection because a
    /// buffer happened to be unavailable for a moment.</summary>
    public bool AwaitingPage;

    protected WindowsConnection(IWindowsShard shard, uint slot)
    {
        _shard = shard;
        Slot = slot;
    }

    public override void Close()
    {
        // Marshal onto the loop thread (generation-guarded there) - safe from any thread, including from
        // inside a callback (honoured on the next loop pass).
        if (Volatile.Read(ref Socket) != 0) _shard.SubmitClose(Slot, Volatile.Read(ref Generation));
    }

    /// <summary>Both Windows backends arm ONE receive at a time (WSARecv / RIOReceive), so parking is
    /// simply "do not post the next one" — no armed operation has to be cancelled. See
    /// <see cref="Connection.SupportsReceiveParking"/> for why io_uring is not in this club.</summary>
    public override bool SupportsReceiveParking => true;

    private protected override void SubmitResumeReceive()
    {
        // Same generation-captured marshal as Close/Flush: a resume for a since-closed and re-tenanted
        // slot is dropped on the loop thread rather than arming a receive on someone else's connection.
        if (Volatile.Read(ref Socket) != 0) _shard.SubmitResumeReceive(Slot, Volatile.Read(ref Generation));
    }

    // --- out-of-band IBufferWriter path (accumulator in OutboundConnection) ---

    protected override bool IsClosed => Volatile.Read(ref Socket) == 0;

    protected override bool SubmitOutbound(byte[] data, int length)
    {
        // Generation-captured marshal onto the loop (same guard as Close): a flush for a since-closed and
        // re-tenanted slot is dropped on the loop thread rather than misdelivered.
        if (Volatile.Read(ref Socket) == 0) return false;
        uint generation = Volatile.Read(ref Generation);

        // A response written from OnReceive is ALREADY on the loop thread, so the queue-plus-wake below
        // is a round trip back to the thread we are standing on. Try to issue it directly; the shard
        // declines (returning false) whenever it cannot prove the inline order is the correct one, and we
        // fall through to the marshal unchanged. See IWindowsShard.TryFlushOnLoop for the two conditions.
        if (_shard.TryFlushOnLoop(Slot, generation, data, length)) return true;

        _shard.SubmitFlush(Slot, generation, data, length);
        return true;
    }
}
#endif
