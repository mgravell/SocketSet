#if NET // Windows RIO backend; compiled out of the netfx fallback build.
namespace SocketSets.Windows;

/// <summary>
/// Per-connection identity for the Windows RIO backend — the analogue of <c>IocpConnection</c>, but
/// the data path rides RIO request/completion queues instead of per-op OVERLAPPEDs. One pooled instance
/// per slot, reused across connection lifetimes. <see cref="Socket"/> doubles as the free/busy marker
/// and the lock-free allocation CAS target; <see cref="Generation"/> guards a stale Close against slot
/// reuse (ABA).
///
/// The out-of-band <see cref="System.Buffers.IBufferWriter{T}"/> path is not implemented in this slice
/// (same as the IOCP backend): the echo data path is context-driven, so GetSpan/Advance/Flush throw.
/// </summary>
internal sealed class RioConnection : Connection
{
    public readonly WindowsRioShard Shard;

    /// <summary>1-based table id (carried as the RIO SocketContext). Stable for this instance.</summary>
    public readonly uint Slot;

    /// <summary>Live SOCKET handle, or 0 when free. Stays non-zero (the closed handle) through teardown
    /// so a racing InitClient can't re-tenant a slot whose RIO ops are still draining.</summary>
    public nint Socket;

    /// <summary>Bumped on each allocation; guards Close against slot reuse.</summary>
    public uint Generation;

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

    public RioConnection(WindowsRioShard shard, uint slot)
    {
        Shard = shard;
        Slot = slot;
    }

    public override void Close()
    {
        if (Volatile.Read(ref Socket) != 0) Shard.SubmitClose(Slot, Volatile.Read(ref Generation));
    }

    private static NotSupportedException NoWriter() => new(
        "RioConnection: the out-of-band IBufferWriter path is not yet implemented for the Windows RIO " +
        "backend. The echo data path is context-driven (ctx.SendBytes / ctx.ResponseBytes).");

    public override Span<byte> GetSpan(int sizeHint = 0) => throw NoWriter();
    public override Memory<byte> GetMemory(int sizeHint = 0) => throw NoWriter();
    public override void Advance(int count) => throw NoWriter();
    public override bool Flush() => throw NoWriter();
}
#endif
