using SocketSets.Tls;

namespace SocketSets;
public class SocketSetOptions
{
    /// <summary>Optional TLS engine factory. When set, connections are wrapped in a per-connection
    /// <see cref="TlsFilter"/> (client filters on <c>Connect</c>, server filters on accept) and the app's
    /// OnConnect/OnAccept fire only after the handshake completes. Null (default) = plaintext, as today.</summary>
    public TlsProvider? Tls { get; set; }

    /// <summary>Which directions <see cref="Tls"/> applies to; see <see cref="SocketSets.TlsMode"/>.
    /// Ignored when <see cref="Tls"/> is null.</summary>
    public TlsMode TlsMode { get; set; } = TlsMode.Both;

    // ResolveClientTls / ResolveServerTls used to live here, returning a bare provider. They moved to
    // SocketSet (2026-08-04) and grew into the OnClientAuthenticate / OnServerAuthenticate callbacks,
    // because "which provider" turned out to be only half the decision: TargetHost has to be resolved at
    // the same moment and per connection, and an options object on the ENGINE structurally cannot do
    // that for an engine that dials many endpoints. See REVIEW.md D1.

    /// <summary>The per-direction TLS gate, still the DEFAULT answer the authenticate callbacks give.</summary>
    internal bool TlsEnabled(bool isClient)
        => Tls is not null && (TlsMode & (isClient ? TlsMode.Connect : TlsMode.Accept)) != 0;

    /// <summary>Client-handshake config used when <see cref="Tls"/> is set and this set dials out.</summary>
    public TlsClientOptions TlsClient { get; set; } = new();

    /// <summary>Server-handshake config used when <see cref="Tls"/> is set and this set accepts.</summary>
    public TlsServerOptions TlsServer { get; set; } = new();

    /// <summary>Shards created up front. With <see cref="MaxShards"/> set this is the MINIMUM: the set
    /// starts here and grows on demand.</summary>
    public int Shards { get; set; } = 4;

    /// <summary>
    /// Upper bound for on-demand shard growth. <c>0</c> (the default) means <b>no growth</b> — the set is
    /// fixed at <see cref="Shards"/>, exactly as before.
    ///
    /// Growth triggers when a connection cannot be placed because every shard's slot table is full, which
    /// is the condition <see cref="SocketSet.PlacementFailures"/> counts. Per-shard memory is
    /// <see cref="SocketsPerShard"/> x the buffer sizes, pre-allocated and pinned, so growth is how you
    /// get a small <see cref="SocketsPerShard"/> (cheap shards) without a hard ceiling on connections.
    ///
    /// DELIBERATELY NOT <see cref="SocketSetFactory.MaxShards"/>, which is a backend CAPABILITY cap (the
    /// managed backend wants exactly 1) rather than a growth policy. Both apply; the effective ceiling is
    /// the lower of the two.
    ///
    /// Shrink is not implemented: reclaiming a shard means draining its connections first.
    /// </summary>
    public int MaxShards { get; set; }
    public int SocketsPerShard { get; set; } = 4096;
    public bool PinWorkerThreads { get; set; } = true;
    public SocketSetFactory Factory { get; set; } = SocketSetFactory.Default;
    public int EntriesPerShard { get; set; } = 4096;

    /// <summary>
    /// Send/write page size. <c>0</c> (the default) means <b>let the backend choose</b> — see
    /// <see cref="SocketSetFactory.DefaultGeometry"/>.
    ///
    /// The sentinel is the whole point. This was previously a plain <c>4096</c>, which made "the user
    /// asked for 4096" and "the user said nothing" indistinguishable — and they want different answers,
    /// because the right page is a property of the backend: RIO cannot scatter-gather and needs 64KB
    /// (4.68x at 256KB, and at 4KB it fails the smoke matrix outright), while IOCP is page-insensitive.
    ///
    /// Read the RESOLVED value back from <see cref="SocketSet.Options"/> after construction, never from
    /// the instance you passed in — that one still says 0, and a banner printing it would be reporting a
    /// geometry the backend is not using.
    /// </summary>
    public int BufferPageSize { get; set; }

    /// <summary>Read/provided buffers per shard (io_uring's read pool). <c>0</c> = backend chooses.</summary>
    public int BufferPagesPerShard { get; set; }

    /// <summary>
    /// Size of each receive buffer, independent of the send page. <c>0</c> (the default) means "let the
    /// backend choose" — which is NOT always "follow <see cref="BufferPageSize"/>" any more, and that is
    /// deliberate: RIO asks for a 64KB send page with a 4KB receive buffer, because following the page
    /// there is what turned 283 MB of resident memory into 3,164 MB.
    ///
    /// Honoured by IOCP, RIO, epoll and io_uring - the latter two only since 2026-07-28; before that both
    /// silently used <see cref="BufferPageSize"/> for everything, while the bench banner still printed a
    /// <c>recvbuf=</c> the backend ignored. It MATTERS on the three backends whose receive buffer is
    /// per-SOCKET: IOCP, RIO and epoll all size that slab <see cref="SocketsPerShard"/> x this, so at 4096
    /// sockets a 64KB receive buffer is 256MB per shard. io_uring's read pool is per-SHARD
    /// (<see cref="BufferPagesPerShard"/> entries) and is ~16MB at the same size, so it honours the option
    /// for uniformity rather than need.
    ///
    /// It exists because those two sizes want to be different and used to be the same knob. The SEND page
    /// wants to be large on RIO: RIO cannot scatter-gather (Windows caps `maxSendDataBuffers` at 1), so
    /// one send is one page and page size is the only lever on large responses - measured 2026-07-27, a
    /// 64KB page took RIO from 2,404 to 10,969 MiB/s at a 256KB payload, 4.68x, with no penalty at 512B.
    ///
    /// The RECEIVE buffer wants to stay small, because there is one per socket for the connection's whole
    /// lifetime: at <see cref="SocketsPerShard"/> 4096 and 12 shards, a 64KB receive buffer is 3.0 GB of
    /// pinned memory against 192 MB at 4KB. Measured on the same day, raising the shared knob to 64KB took
    /// a 12-shard RIO server from 283 MB to 3,164 MB resident - 11.2x the memory for 4.68x the throughput,
    /// and 97% of that growth was the receive slab, which gains nothing from being large.
    ///
    /// Split, a 64KB send page with a 4KB receive buffer keeps the throughput and roughly the footprint.
    /// </summary>
    public int ReceiveBufferSize { get; set; }

    /// <summary>
    /// Set <c>SO_REUSEPORT</c> on IP listeners (Linux). Default true, because reuse-port multi-bind is
    /// how io_uring and epoll get one listener PER SHARD with the kernel balancing accepts across them --
    /// turning it off collapses those backends onto a single-listener path.
    ///
    /// It is an option because it is also a (small) exposure, flagged by the 2026-08-04 audit: any
    /// process running as the SAME UID can join the group and take a share of inbound connections. On a
    /// single-tenant box that is nothing; on a shared one it is worth being able to say no.
    /// </summary>
    public bool ReusePort { get; set; } = true;

    /// <summary>
    /// Permission bits applied to a FILESYSTEM Unix-domain socket after bind (Linux/macOS; ignored for
    /// abstract-namespace sockets, which have no inode, and on Windows, where AF_UNIX access is governed
    /// by directory ACLs instead).
    ///
    /// DEFAULT 0600: owner only. Before 2026-08-04 nothing chmod'd the socket at all, so the mode came
    /// from the process umask and was typically 0755 -- i.e. connectable by any local user, with no
    /// authentication anywhere in the stack to make up for it. For the sidecar shape this library is
    /// aimed at (proxy and app as the same user) 0600 is exactly right; widen it deliberately if a
    /// different user genuinely needs to connect.
    /// </summary>
    public int UnixSocketMode { get; set; } = 0b110_000_000; // 0600

    /// <summary>Backlog passed to <c>listen()</c> on every backend — the kernel's queue of
    /// completed-handshake connections awaiting accept. Cheap; size it to absorb connection bursts.</summary>
    public int ListenBacklog { get; set; } = 512;

    /// <summary>IOCP/RIO only: how many <c>AcceptEx</c> to keep outstanding per listener — the pool of
    /// accept consumers draining the <see cref="ListenBacklog"/>. More absorbs connect bursts and adds
    /// resilience (one failed re-post doesn't stall accepts), but each costs a pre-created socket +
    /// buffer, so it is capped at <see cref="SocketsPerShard"/>. io_uring uses multishot accept and
    /// ignores this; the managed fallback accepts one at a time.</summary>
    public int AcceptConcurrency { get; set; } = 32;

    /// <summary>Close connections abortively (SO_LINGER{on,0} → RST) instead of a graceful FIN. Skips
    /// TIME_WAIT on the active closer — mainly a churn/benchmark aid so rapid connect/close doesn't
    /// exhaust the client ephemeral-port pool. Not for production (a RST can drop in-flight data).</summary>
    public bool ResetOnClose { get; set; }

    /// <summary>Pre-allocated, pre-pinned outbound buffers per shard. Each in-flight send holds one, so
    /// this also bounds how many connections per shard may have a send in flight at once. <c>0</c> =
    /// backend chooses.</summary>
    public int WriteBuffersPerShard { get; set; }

    /// <summary>io_uring only: pre-pinned buffers per shard for out-of-band writes (the
    /// <see cref="Connection"/> IBufferWriter/Flush path). Leased from arbitrary threads, so this
    /// pool is thread-safe — kept separate from <see cref="WriteBuffersPerShard"/> so the IO-thread
    /// echo path never pays for synchronization. Exhausting it spills to managed (ArrayPool) memory.
    /// <c>0</c> = backend chooses.</summary>
    public int OutOfBandWriteBuffersPerShard { get; set; }

    /// <summary>
    /// io_uring only: max read (provided) buffers a shard may hold in the write path at once,
    /// for zero-copy echoes (send straight from the buffer a receive selected, skipping the
    /// read→write copy). Above this, echoes fall back to lease+copy so the provided-buffer ring
    /// never fully drains. Clamped below <see cref="BufferPagesPerShard"/>; 0 disables it.
    /// </summary>
    public int MaxBorrowedReadBuffers { get; set; } = 128;

    /// <summary>
    /// Fill every "backend chooses" sentinel from <paramref name="factory"/>'s geometry, returning a COPY
    /// so the caller's instance is never mutated behind its back. Called once by the
    /// <see cref="SocketSet"/> constructor before any shard is created, which is why no backend has to
    /// know the sentinels exist — a shard only ever sees resolved values.
    ///
    /// A copy rather than in-place, for a reason worth keeping: options objects get reused across sets
    /// (every bench rig builds one and constructs several), and resolving in place would silently bake
    /// the FIRST backend's geometry into every later set — an A/B that then measured two backends with
    /// one backend's buffers and reported the difference as theirs.
    /// </summary>
    internal SocketSetOptions ResolvedFor(SocketSetFactory factory)
    {
        var g = factory.DefaultGeometry;
        var copy = (SocketSetOptions)MemberwiseClone();
        if (copy.BufferPageSize <= 0) copy.BufferPageSize = g.PageSize;
        if (copy.ReceiveBufferSize <= 0) copy.ReceiveBufferSize = g.ReceiveBufferSize;
        if (copy.WriteBuffersPerShard <= 0) copy.WriteBuffersPerShard = g.WriteBuffersPerShard;
        if (copy.OutOfBandWriteBuffersPerShard <= 0) copy.OutOfBandWriteBuffersPerShard = g.OutOfBandWriteBuffersPerShard;
        if (copy.BufferPagesPerShard <= 0) copy.BufferPagesPerShard = g.BufferPagesPerShard;
        return copy;
    }
}