#if NET // Windows IOCP backend; compiled out of the netfx fallback build.
namespace SocketSets.Windows;

/// <summary>
/// Per-connection identity for the Windows IOCP backend — the analogue of <c>IoUringConnection</c>.
/// One instance exists per slot in the shard's fixed table and is reused across connection lifetimes,
/// so accept/connect never allocates.
///
/// Identity, teardown state and send serialization live in <see cref="WindowsConnection"/>, shared with
/// the RIO backend. Only the IOCP-specific parts are here: the completion-skip flag and the send page
/// array.
///
/// The per-op OVERLAPPEDs do NOT live here (a managed object moves under GC) — they live in the
/// shard's native op-context slab, indexed by <c>Slot</c>.
/// </summary>
internal sealed class IocpConnection : WindowsConnection
{
    public readonly IocpShard Shard;

    /// <summary>True once <c>SetFileCompletionNotificationModes(FILE_SKIP_COMPLETION_PORT_ON_SUCCESS)</c>
    /// succeeded for this socket. When set, a synchronously-completing recv/send posts no completion
    /// packet and is handled inline; when clear (flag rejected), the socket falls back to the async
    /// model (every op posts). Set at accept/connect adoption.</summary>
    public bool SkipOnSuccess;

    /// <summary>
    /// Pages making up the single in-flight send, issued as ONE WSASend with this many WSABUFs.
    ///
    /// Sending one page at a time is what made large responses slow: with a 4KB page and one send in
    /// flight per connection, a 256KB response left as 64 sequential WSASends, each costing a
    /// completion-port round trip. Measured 2026-07-26, page size alone moved the bare responder from
    /// 885 to 3556 MiB/s at 256KB. WSASend has always taken a buffer ARRAY; only the call site did not.
    ///
    /// Segments are packed, not one-per-page: several small queued responses still coalesce into a page
    /// (the batching that keeps a pipelined echo cheap), and the run simply spills into further pages.
    ///
    /// This has no RIO counterpart and cannot get one: Windows caps RIOCreateRequestQueue's
    /// maxSendDataBuffers at 1, so one RIOSend is one contiguous buffer (established 2026-07-27).
    /// </summary>
    public const int MaxSendPages = 64; // 256KB per send at a 4KB page

    public readonly int[] SendPages = new int[MaxSendPages];
    public readonly int[] SendLens = new int[MaxSendPages];
    public int SendPageCount;

    // --- zero-copy send (pipe mode only; loop thread except where noted) ---
    // The in-flight send points at the CALLER's memory (pipe segments) instead of write pages, so there is
    // nothing to release back to the pool - only handles to unpin and a pump to signal. Set up on the pump
    // thread by TrySendZeroCopy (pinning is thread-agnostic), consumed on the loop thread.

    /// <summary>The in-flight send is zero-copy: <see cref="SendPages"/> is not in use and nothing is
    /// returned to the write pool on completion.</summary>
    public bool SendZeroCopy;

    /// <summary>Pins held for the duration of a zero-copy send; disposed when it completes or fails.
    /// Null entries beyond <see cref="ZcCount"/>. Only populated when the caller did NOT assert pinned
    /// memory — an already-pinned pool needs no handle.</summary>
    public System.Buffers.MemoryHandle[]? ZcHandles;

    /// <summary>
    /// Segment cap for a ZERO-COPY send. Deliberately larger than <see cref="MaxSendPages"/>, and it has
    /// to be: those two caps bound different things and only one of them was ever an OS limit.
    ///
    /// <see cref="MaxSendPages"/> bounds how many POOLED WRITE PAGES one send may span, so 64 x 4KB is a
    /// 256KB send and spilling past it just means a second send of our own pages. The zero-copy cap
    /// bounds how fragmented the CALLER's sequence may be, and the caller owns that: Kestrel's default
    /// pipe blocks are ~4KB, so a 256KB response arrives as 65 segments - measured, exactly, on
    /// 2026-07-29 - against a cap of 64. One segment over, and every such response silently fell back to
    /// copying and paid 2.2x. Sharing one constant put an OS-shaped limit and a caller-shaped limit on
    /// the same number, and the caller-shaped one lost.
    ///
    /// 256 covers a ~1MB response at 4KB blocks. It is not a guarantee - a sufficiently fragmented
    /// sequence still declines, which is why <see cref="ZcPtrs"/> is a cap and not a contract - but it
    /// moves the cliff off the payload sizes anyone measures.
    /// </summary>
    public const int MaxZeroCopySegments = 256;

    /// <summary>Segment addresses and lengths of the in-flight zero-copy send. Allocated on FIRST USE
    /// rather than per connection, which is what makes the larger cap affordable: these are useless to a
    /// callback-path connection, and there is one connection object per slot per shard. Eager at 64 cost
    /// 768 bytes on every connection whether or not it ever sent zero-copy (~37MB at 4096 sockets x 12
    /// shards); lazy at 256 costs 3KB on the connections that actually use pipe mode and nothing on the
    /// rest. Written on the pump thread before the request is enqueued, read on the loop thread after it
    /// is dequeued, so the queue's release/acquire publishes them safely.</summary>
    public nint[]? ZcPtrs;
    public int[]? ZcLens;
    public int ZcCount;

    /// <summary>Materialise the zero-copy segment arrays. Pump thread, before any segment is recorded.</summary>
    public void EnsureZcArrays()
    {
        ZcPtrs ??= new nint[MaxZeroCopySegments];
        ZcLens ??= new int[MaxZeroCopySegments];
    }

    /// <summary>Signals the outbound pump that the send completed (true) or the connection went away
    /// (false). The pump must not AdvanceTo its reader until this fires — the socket was reading its
    /// memory.</summary>
    public TaskCompletionSource<bool>? ZcCompletion;

    /// <summary>A zero-copy request accepted but not yet issued because a send was already in flight.
    /// At most one can exist: the pump issues one send at a time and waits for it.</summary>
    public bool ZcPending;

    public IocpConnection(IocpShard shard, uint slot) : base(shard, slot)
    {
        Shard = shard;
    }

    internal override bool TrySendZeroCopy(in System.Buffers.ReadOnlySequence<byte> data, bool pinned,
                                           out ValueTask<bool> completion)
        => Shard.TrySendZeroCopy(this, in data, pinned, out completion);
}
#endif
