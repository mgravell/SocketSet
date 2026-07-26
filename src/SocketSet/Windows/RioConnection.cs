#if NET // Windows RIO backend; compiled out of the netfx fallback build.
namespace SocketSets.Windows;

/// <summary>
/// Per-connection identity for the Windows RIO backend — the analogue of <c>IocpConnection</c>, but
/// the data path rides RIO request/completion queues instead of per-op OVERLAPPEDs. One pooled instance
/// per slot, reused across connection lifetimes. <see cref="Socket"/> doubles as the free/busy marker
/// and the lock-free allocation CAS target; <see cref="Generation"/> guards a stale Close against slot
/// reuse (ABA).
///
/// The out-of-band <see cref="System.Buffers.IBufferWriter{T}"/> path is inherited from
/// <see cref="WindowsOutboundConnection"/> (marshaled to the loop, sent via the normal Pending path).
/// </summary>
internal sealed class RioConnection : WindowsOutboundConnection
{
    public readonly WindowsRioShard Shard;

    /// <summary>1-based table id (carried as the RIO SocketContext). Stable for this instance.</summary>
    public readonly uint Slot;

    /// <summary>Live SOCKET handle, or 0 when free. Stays non-zero (the closed handle) through teardown
    /// so a racing InitClient can't re-tenant a slot whose RIO ops are still draining.</summary>
    public nint Socket;

    /// <summary>Bumped on each allocation; guards Close against slot reuse.</summary>
    public uint Generation;

    /// <summary>Which side of the TLS handshake this connection is, so the shard knows whether the
    /// deferred open fires OnConnect or OnAccept. Only meaningful when <c>Tls</c> is set.</summary>
    public bool IsClient;

    /// <summary>RIO request queue for this connection (bound to the socket + the shard CQ); 0 when none.
    /// Destroyed implicitly when the socket closes.</summary>
    public nint Rq;

    // --- teardown state (loop-thread only). closesocket aborts the outstanding RIO recv/send, which
    // complete on the CQ with an error; the slot finalizes once RecvArmed + SendBusy both clear. ---
    public bool Closing;
    public bool RecvArmed;

    // --- recv (loop-thread only) ---
    /// <summary>Index into the shard's registered recv-buffer slab; held for the connection lifetime, -1 if none.</summary>
    public int RecvBuf = -1;

    // --- send serialization (loop-thread only). At most one send in flight per connection (stream
    // ordering); an echo that arrives mid-send is copied out and queued. ---
    public bool SendBusy;
    public int SendBuf = -1;   // write-slab index of the in-flight send
    public int SendSent;       // partial-send cursor
    public int SendTotal;
    public Queue<ArraySegment<byte>>? Pending;

    /// <summary>This RQ has deferred (RIO_MSG_DEFER) submissions awaiting a commit; set while queued in
    /// the shard's per-pass commit list so it's committed (kicked) exactly once at the pass boundary.</summary>
    public bool CommitPending;

    /// <summary>Which deferred directions are pending a commit. RIO_MSG_COMMIT_ONLY only flushes the
    /// direction it's issued on — <c>RIOSend(COMMIT_ONLY)</c> commits deferred sends, <c>RIOReceive(
    /// COMMIT_ONLY)</c> commits deferred receives — so a pure-receiver (no sends) needs the receive commit
    /// or its re-armed recv never activates. Set at defer, consumed in FlushCommits.</summary>
    public bool CommitRecv, CommitSend;

    public RioConnection(WindowsRioShard shard, uint slot)
    {
        Shard = shard;
        Slot = slot;
    }

    public override void Close()
    {
        if (Volatile.Read(ref Socket) != 0) Shard.SubmitClose(Slot, Volatile.Read(ref Generation));
    }

    // --- out-of-band IBufferWriter path (accumulator in WindowsOutboundConnection) ---

    protected override bool IsClosed => Volatile.Read(ref Socket) == 0;

    protected override bool SubmitOutbound(byte[] data, int length)
    {
        if (Volatile.Read(ref Socket) == 0) return false;
        Shard.SubmitFlush(Slot, Volatile.Read(ref Generation), data, length);
        return true;
    }
}
#endif
