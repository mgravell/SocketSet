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