#if NET
using System.Buffers;

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
    // Queued sends waiting behind the in-flight one. Each carries an optional ArrayPool buffer to
    // return once the segment's bytes have been copied into a write page.
    public Queue<(ArraySegment<byte> Seg, byte[]? PoolReturn)>? Pending;

    public IoUringConnection(IoUringShard shard, uint slot)
    {
        Shard = shard;
        Slot = slot;
    }

    /// <summary>Out-of-band send from any thread: the bytes are copied into a pooled scratch buffer
    /// and marshaled onto the loop thread (which owns the ring), where they queue behind any in-flight
    /// send for this connection. The loop returns the scratch to the pool once it has been copied out
    /// into the native write pages.</summary>
    public override bool Send(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return Volatile.Read(ref Fd) != 0;
        if (Volatile.Read(ref Fd) == 0) return false; // definitely closed; loop re-checks generation too
        int len = data.Length;
        var scratch = ArrayPool<byte>.Shared.Rent(len);
        data.CopyTo(scratch);
        Shard.SubmitExternal(Slot, Volatile.Read(ref Generation), scratch, len);
        return true;
    }

    public override bool Send(in ReadOnlySequence<byte> data)
    {
        if (data.IsSingleSegment) return Send(data.First.Span);
        if (Volatile.Read(ref Fd) == 0) return false;
        // Flatten into one pooled scratch for the cross-thread hand-off (the segments live in caller
        // memory we can't hold). The loop then scatters it across write pages and sends one writev.
        int len = checked((int)data.Length);
        var scratch = ArrayPool<byte>.Shared.Rent(len);
        data.CopyTo(scratch);
        Shard.SubmitExternal(Slot, Volatile.Read(ref Generation), scratch, len);
        return true;
    }
}
#endif
