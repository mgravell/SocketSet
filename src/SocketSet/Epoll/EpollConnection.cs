#if NET // Linux epoll backend; compiled out of the netfx fallback build.
namespace SocketSets.Epoll;

/// <summary>
/// Per-connection identity for the Linux epoll backend - the readiness-model analogue of
/// <c>IoUringConnection</c>. One pooled instance per slot in the shard's fixed table, reused across
/// connection lifetimes, so accept/connect never allocates.
///
/// The shape is deliberately the same as the Windows connection types (see <see cref="OutboundConnection"/>)
/// rather than io_uring's: both are single-loop-thread backends that serialise sends behind a
/// <see cref="Pending"/> queue. What differs is WHY the queue exists. On IOCP a send is queued because one
/// is already in flight; here it is queued because the socket said EAGAIN and we must wait for EPOLLOUT
/// before trying again. Same structure, different trigger.
/// </summary>
internal sealed class EpollConnection : OutboundConnection
{
    public readonly EpollShard Shard;

    /// <summary>0-based table index. Carried in the epoll event's data word (see <c>EpollShard.Tag</c>).</summary>
    public readonly int Slot;

    /// <summary>Live socket fd, or -1 when the slot is free. Stays set (the now-closed fd) through
    /// teardown so a racing claim cannot re-tenant a slot mid-close.</summary>
    public int Fd = -1;

    /// <summary>Bumped on each allocation; guards a stale Close/flush against slot reuse (ABA).</summary>
    public uint Generation;

    /// <summary>Which side of a TLS handshake this is, so the deferred open fires OnConnect or OnAccept.</summary>
    public bool IsClient;

    /// <summary>Set while a non-blocking connect() is still in flight (EINPROGRESS). The first EPOLLOUT
    /// completes it - success or failure is read from SO_ERROR, not inferred from the wake.</summary>
    public bool Connecting;

    // --- teardown (loop thread only) ---
    public bool Closing;

    // --- receive ---
    /// <summary>Index into the shard's recv-buffer pool; held for the connection's lifetime, -1 if none.</summary>
    public int RecvBuf = -1;

    // --- kTLS (kernel TLS offload; OpenSSL owns the fd, kernel does the record crypto) ---
    /// <summary>OpenSSL <c>SSL*</c> bound to the fd, or 0 when this is not a kTLS connection. Mutually
    /// exclusive with <see cref="OutboundConnection.Tls"/> (the userspace filter): a connection is either
    /// kernel-offloaded or userspace, never both.</summary>
    public nint KtlsSsl;
    /// <summary>Handshake done + keys pushed to the kernel → data phase. While false the readiness events
    /// step <c>SSL_do_handshake</c> instead of reading application data.</summary>
    public bool KtlsReady;
    /// <summary>Plaintext <c>SSL_read</c> target, reused for the connection's lifetime; also the buffer an
    /// inline response is written back into (like the plaintext recv buffer on the non-TLS path).</summary>
    public byte[]? KtlsRecv;

    // --- send serialisation (loop thread only) ---
    // A stream socket must not have two writers racing, and a partial write must be resumed before any
    // later bytes go out. Everything outbound therefore funnels through Pending, in order.
    /// <summary>Queued outbound buffers (pooled; returned as they drain).</summary>
    public Queue<ArraySegment<byte>>? Pending;

    /// <summary>Bytes of the HEAD of <see cref="Pending"/> already written - the partial-write cursor.
    /// send() on a non-blocking socket routinely accepts only part of a buffer.</summary>
    public int SendOffset;

    /// <summary>True while EPOLLOUT is armed. Writable-interest is registered only when there is something
    /// blocked, because a level-triggered EPOLLOUT on an idle socket would wake the loop continuously.</summary>
    public bool WantWrite;

    public EpollConnection(EpollShard shard, int slot)
    {
        Shard = shard;
        Slot = slot;
    }

    public override void Close()
    {
        // Marshal onto the loop thread (generation-guarded there), so this is safe from any thread
        // including from inside a callback.
        if (Volatile.Read(ref Fd) >= 0) Shard.SubmitClose(Slot, Volatile.Read(ref Generation));
    }

    // --- out-of-band IBufferWriter path (accumulator in OutboundConnection) ---

    protected override bool IsClosed => Volatile.Read(ref Fd) < 0;

    protected override bool SubmitOutbound(byte[] data, int length)
    {
        if (Volatile.Read(ref Fd) < 0) return false;
        Shard.SubmitFlush(Slot, Volatile.Read(ref Generation), data, length);
        return true;
    }
}
#endif
