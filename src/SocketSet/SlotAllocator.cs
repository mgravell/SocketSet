namespace SocketSets;

/// <summary>
/// Loop-thread-local free-slot allocator for a fixed connection table — the claim/free structure behind
/// the single-writer slot model (see the shard backends). NOT thread-safe by design: only the owning
/// shard's loop thread claims (on accept/connect adoption) and frees (on teardown), so no locking, no
/// CAS, and no ABA tag are needed — the property the cross-thread <see cref="SocketSetShard.TryReserve"/>
/// gate buys us.
///
/// Hands out never-used indices from a high-water counter first (ascending 0,1,2,… on a cold set, no
/// init fill), and recycles freed indices from a LIFO hole-stack — most-recently-freed first, so the
/// reused <c>Connection</c> object is still cache-warm from its own teardown. Capacity is the table size;
/// a caller reserves before claiming, so <see cref="TryClaim"/> only returns false on a bug (counter
/// drift / an unreserved caller).
/// </summary>
/// Mutable struct on purpose: it is pure per-shard state held in one non-readonly field and only ever
/// touched via <c>_slots.Claim()</c>/<c>_slots.Free(...)</c> on the loop thread, so it inlines into
/// the shard (no separate heap object, no pointer-chase per claim) without the usual mutable-struct
/// hazards. The holding field MUST NOT be <c>readonly</c> — a readonly field would mutate a copy.
internal struct SlotAllocator(int capacity)
{
    private readonly int[] _holes = new int[capacity]; // freed indices awaiting reuse (LIFO); ref is fixed
    private int _top;            // hole-stack depth
    private int _highWater;      // next never-used index

    /// <summary>Claim a slot index, or -1 if the table is full (should not happen after a reservation).
    /// Recycles a freed hole (warm) if any, else advances the high-water mark.</summary>
    public int Claim()
    {
        if (_top > 0) return _holes[--_top];
        if (_highWater < capacity) return _highWater++;
        return -1;
    }

    /// <summary>Return a slot index for reuse (loop thread only, at teardown).</summary>
    public void Free(int slot) => _holes[_top++] = slot;
}
