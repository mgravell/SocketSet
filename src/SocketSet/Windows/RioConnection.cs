#if NET // Windows RIO backend; compiled out of the netfx fallback build.
namespace SocketSets.Windows;

/// <summary>
/// Per-connection identity for the Windows RIO backend — the analogue of <c>IocpConnection</c>, but the
/// data path rides RIO request/completion queues instead of per-op OVERLAPPEDs. One pooled instance per
/// slot, reused across connection lifetimes.
///
/// Identity, teardown state and send serialization live in <see cref="WindowsConnection"/>, shared with
/// the IOCP backend. Only the RIO-specific parts are here: the request queue and its deferred-commit
/// bookkeeping.
///
/// Note there is no send page array here, unlike <c>IocpConnection</c>. That is not an omission: RIO
/// cannot scatter-gather (Windows caps RIOCreateRequestQueue's maxSendDataBuffers at 1 and returns
/// WSAEINVAL above it, established 2026-07-27), so one RIOSend is one contiguous buffer and page size is
/// RIO's only lever on large responses.
/// </summary>
internal sealed class RioConnection : WindowsConnection
{
    public readonly WindowsRioShard Shard;

    /// <summary>RIO request queue for this connection (bound to the socket + the shard CQ); 0 when none.
    /// Destroyed implicitly when the socket closes.</summary>
    public nint Rq;

    /// <summary>This RQ has deferred (RIO_MSG_DEFER) submissions awaiting a commit; set while queued in
    /// the shard's per-pass commit list so it's committed (kicked) exactly once at the pass boundary.</summary>
    public bool CommitPending;

    /// <summary>Which deferred directions are pending a commit. RIO_MSG_COMMIT_ONLY only flushes the
    /// direction it's issued on — <c>RIOSend(COMMIT_ONLY)</c> commits deferred sends, <c>RIOReceive(
    /// COMMIT_ONLY)</c> commits deferred receives — so a pure-receiver (no sends) needs the receive commit
    /// or its re-armed recv never activates. Set at defer, consumed in FlushCommits.</summary>
    public bool CommitRecv, CommitSend;

    public RioConnection(WindowsRioShard shard, uint slot) : base(shard, slot)
    {
        Shard = shard;
    }
}
#endif
