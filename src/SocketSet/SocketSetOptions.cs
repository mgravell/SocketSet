namespace SocketSets;
public class SocketSetOptions
{
    public int Shards { get; set; } = 4;
    public int SocketsPerShard { get; set; } = 4096;
    public bool PinWorkerThreads { get; set; } = true;
    public SocketSetFactory Factory { get; set; } = SocketSetFactory.Default;
    public int EntriesPerShard { get; set; } = 4096;
    public int BufferPageSize { get; set; } = 4096;
    public int BufferPagesPerShard { get; set; } = 256;

    /// <summary>Backlog passed to <c>listen()</c> on every backend — the kernel's queue of
    /// completed-handshake connections awaiting accept. Cheap; size it to absorb connection bursts.</summary>
    public int ListenBacklog { get; set; } = 512;

    /// <summary>IOCP/RIO only: how many <c>AcceptEx</c> to keep outstanding per listener — the pool of
    /// accept consumers draining the <see cref="ListenBacklog"/>. More absorbs connect bursts and adds
    /// resilience (one failed re-post doesn't stall accepts), but each costs a pre-created socket +
    /// buffer, so it is capped at <see cref="SocketsPerShard"/>. io_uring uses multishot accept and
    /// ignores this; the managed fallback accepts one at a time.</summary>
    public int AcceptConcurrency { get; set; } = 32;

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