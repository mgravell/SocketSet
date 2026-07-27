using SocketSets.Tls;

namespace SocketSets;
public class SocketSetOptions
{
    /// <summary>Optional TLS engine factory. When set, connections are wrapped in a per-connection
    /// <see cref="TlsFilter"/> (client filters on <c>Connect</c>, server filters on accept) and the app's
    /// OnConnect/OnAccept fire only after the handshake completes. Null (default) = plaintext, as today.</summary>
    public TlsProvider? Tls { get; set; }

    /// <summary>Client-handshake config used when <see cref="Tls"/> is set and this set dials out.</summary>
    public TlsClientOptions TlsClient { get; set; } = new();

    /// <summary>Server-handshake config used when <see cref="Tls"/> is set and this set accepts.</summary>
    public TlsServerOptions TlsServer { get; set; } = new();

    public int Shards { get; set; } = 4;
    public int SocketsPerShard { get; set; } = 4096;
    public bool PinWorkerThreads { get; set; } = true;
    public SocketSetFactory Factory { get; set; } = SocketSetFactory.Default;
    public int EntriesPerShard { get; set; } = 4096;
    public int BufferPageSize { get; set; } = 4096;
    public int BufferPagesPerShard { get; set; } = 256;

    /// <summary>
    /// IOCP/RIO: size of each per-connection receive buffer. <c>0</c> (the default) means "follow
    /// <see cref="BufferPageSize"/>", which is the historical behaviour.
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

    /// <summary>Pre-allocated, pre-pinned outbound buffers per shard. Each in-flight
    /// send holds one; sized to bound concurrent responses.</summary>
    public int WriteBuffersPerShard { get; set; } = 1024;

    /// <summary>io_uring only: pre-pinned buffers per shard for out-of-band writes (the
    /// <see cref="Connection"/> IBufferWriter/Flush path). Leased from arbitrary threads, so this
    /// pool is thread-safe — kept separate from <see cref="WriteBuffersPerShard"/> so the IO-thread
    /// echo path never pays for synchronization. Exhausting it spills to managed (ArrayPool) memory.</summary>
    public int OutOfBandWriteBuffersPerShard { get; set; } = 256;

    /// <summary>
    /// io_uring only: max read (provided) buffers a shard may hold in the write path at once,
    /// for zero-copy echoes (send straight from the buffer a receive selected, skipping the
    /// read→write copy). Above this, echoes fall back to lease+copy so the provided-buffer ring
    /// never fully drains. Clamped below <see cref="BufferPagesPerShard"/>; 0 disables it.
    /// </summary>
    public int MaxBorrowedReadBuffers { get; set; } = 128;
}