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

    public IocpConnection(IocpShard shard, uint slot) : base(shard, slot)
    {
        Shard = shard;
    }
}
#endif
