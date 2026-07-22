#if NET // Windows IOCP backend; compiled out of the netfx fallback build.
namespace SocketSets.Windows;

/// <summary>
/// Per-connection identity for the Windows IOCP backend — the analogue of <c>IoUringConnection</c>.
/// One instance exists per slot in the shard's fixed table and is reused across connection lifetimes,
/// so accept/connect never allocates. <see cref="Socket"/> doubles as the free/busy marker (0 == free)
/// and the lock-free allocation CAS target; <see cref="Generation"/> is bumped on each (re)allocation
/// so a stale reference held past close is detected (its Close/writes dropped rather than misdelivered
/// to whoever later reuses the slot).
///
/// The per-op OVERLAPPEDs do NOT live here (a managed object moves under GC) — they live in the
/// shard's native op-context slab, indexed by <see cref="Slot"/>. This object only carries loop-thread
/// bookkeeping (recv/send buffers, send serialization, teardown state).
///
/// The <see cref="IBufferWriter{T}"/> out-of-band write path is not implemented in this slice: the
/// echo data path is entirely context-driven (ctx.SendBytes / ctx.ResponseBytes), so GetSpan/Advance/
/// Flush are not exercised by basic accept/connect/send/receive. They throw until the RIO/OOB slice.
/// </summary>
internal sealed class IocpConnection : Connection
{
    public readonly IocpShard Shard;

    /// <summary>1-based table id (matches <c>IocpOp.Slot</c>). Stable for this instance.</summary>
    public readonly uint Slot;

    /// <summary>Live SOCKET handle, or 0 when the slot is free. CAS 0-&gt;handle claims the slot; the
    /// loop thread reads/clears it. Stays non-zero (the now-closed handle) through teardown so a racing
    /// <see cref="IocpShard"/> InitClient can't re-tenant a slot whose ops are still draining.</summary>
    public nint Socket;

    /// <summary>Bumped on each allocation; guards Close/writes against slot reuse (ABA).</summary>
    public uint Generation;

    /// <summary>True once <c>SetFileCompletionNotificationModes(FILE_SKIP_COMPLETION_PORT_ON_SUCCESS)</c>
    /// succeeded for this socket. When set, a synchronously-completing recv/send posts no completion
    /// packet and is handled inline; when clear (flag rejected), the socket falls back to the async
    /// model (every op posts). Set at accept/connect adoption.</summary>
    public bool SkipOnSuccess;

    // --- teardown state (loop-thread only) ---
    // The slot is NOT recycled (Socket stays non-zero) until every in-flight op has been reaped, so a
    // completion can never land on a re-tenanted slot. closesocket() aborts the pending recv/send; the
    // only ops outstanding at close are the recv (RecvArmed) and at most one send (SendBusy). When both
    // clear the slot finalizes.
    public bool Closing;
    public bool RecvArmed;

    // --- recv (loop-thread only) ---
    /// <summary>Index into the shard's recv-buffer pool; held for the connection's lifetime, -1 if none.</summary>
    public int RecvBuf = -1;

    // --- send serialization (loop-thread only) ---
    // A stream socket must not have two sends racing (they can reorder), so at most one send is in
    // flight per connection. An echo that arrives while one is busy is copied out and queued here.
    public bool SendBusy;
    public int SendBuf = -1;   // write-pool index of the in-flight send
    public int SendSent;       // bytes of the current send already acknowledged (partial-send cursor)
    public int SendTotal;      // total bytes of the current send
    public Queue<ArraySegment<byte>>? Pending;

    public IocpConnection(IocpShard shard, uint slot)
    {
        Shard = shard;
        Slot = slot;
    }

    public override void Close()
    {
        // Marshal onto the loop thread (generation-guarded there) — safe from any thread, including
        // from inside a callback (honoured on the next loop pass).
        if (Volatile.Read(ref Socket) != 0) Shard.SubmitClose(Slot, Volatile.Read(ref Generation));
    }

    // --- IBufferWriter<byte>: out-of-band write path (not in this slice) ---

    private static NotSupportedException NoWriter() => new(
        "IocpConnection: the out-of-band IBufferWriter path (GetSpan/Advance/Flush/Send) is not yet " +
        "implemented for the Windows IOCP backend. The echo data path is context-driven (ctx.SendBytes " +
        "/ ctx.ResponseBytes); out-of-band writes land with the RIO/OOB slice.");

    public override Span<byte> GetSpan(int sizeHint = 0) => throw NoWriter();
    public override Memory<byte> GetMemory(int sizeHint = 0) => throw NoWriter();
    public override void Advance(int count) => throw NoWriter();
    public override bool Flush() => throw NoWriter();
}
#endif
