#if NET
namespace SocketSets.IoUring;

/// <summary>
/// Per-connection identity for the io_uring backend. One instance exists per slot in the shard's
/// fixed table and is <em>reused</em> across connection lifetimes — the table is pre-allocated once,
/// so accepting/connecting never allocates a Connection. <see cref="Fd"/> doubles as the free/busy
/// marker (0 == free) and the lock-free allocation CAS target; <see cref="Generation"/> is bumped on
/// each (re)allocation so a stale reference held past close can be detected and its out-of-band sends
/// dropped rather than delivered to whichever connection later reused the slot.
/// </summary>
internal sealed class IoUringConnection : Connection
{
    public readonly IoUringShard Shard;

    /// <summary>1-based table id (matches the packed <c>id</c> in user_data). Stable for this instance.</summary>
    public readonly uint Slot;

    /// <summary>Live fd, or 0 when the slot is free. CAS 0-&gt;fd claims the slot; loop thread reads/clears it.</summary>
    public int Fd;

    /// <summary>Bumped on each allocation; guards out-of-band sends against slot reuse (ABA).</summary>
    public uint Generation;

    // --- loop-thread-only send state (never touched off the loop thread) ---
    // A stream socket must not have two SENDs racing (they can reorder), so at most one send is in
    // flight per connection. Follow-ups — pipelined echoes and out-of-band sends alike — wait here.
    public bool SendBusy;
    public Queue<ArraySegment<byte>>? Pending;

    public IoUringConnection(IoUringShard shard, uint slot)
    {
        Shard = shard;
        Slot = slot;
    }

    /// <summary>Out-of-band send from any thread: the bytes are copied and marshaled onto the loop
    /// thread (which owns the ring), where they queue behind any in-flight send for this connection.</summary>
    public override bool Send(ReadOnlySpan<byte> data)
    {
        if (Volatile.Read(ref Fd) == 0) return false; // definitely closed; loop re-checks generation too
        Shard.SubmitExternal(Slot, Volatile.Read(ref Generation), data.ToArray());
        return true;
    }
}
#endif
