namespace SocketSets;

/// <summary>
/// The buffer sizes and pool depths a backend wants when the caller has not chosen them.
///
/// WHY THIS EXISTS. <c>BufferPageSize</c> used to be one global with a real default of 4096, so there was
/// no way to distinguish "the user asked for 4096" from "the user said nothing" - and the two want
/// different answers, because the right page size is a property of the BACKEND, not of the workload:
///
///  - RIO cannot scatter-gather (Windows caps <c>maxSendDataBuffers</c> at 1), so one send is one page
///    and page size is its only lever on large responses. Measured 2026-07-27: a 64KB page took RIO from
///    2,404 to 10,969 MiB/s at a 256KB payload, 4.68x, with no penalty at 512B. Measured again
///    2026-07-29, and this time it was not a tuning result but a CORRECTNESS one - at the 4KB default,
///    RIO+TLS out-of-band send fails the smoke matrix by timing out (TODO item 0d).
///  - IOCP issues one <c>WSASend</c> over up to 64 <c>WSABUF</c>s, so it is page-INSENSITIVE and has no
///    reason to want anything other than the small default.
///
/// WHY IT IS A GEOMETRY AND NOT JUST A PAGE SIZE, which is the part that has bitten before. The receive
/// buffer and the pool depths are derived from the page unless they are set, so moving the page alone
/// moves memory with it: coupling them once took a 12-shard RIO server from 283 MB to 3,164 MB resident
/// (2026-07-27), 97% of it the receive slab, which gains nothing from being large. A backend that wants a
/// big send page therefore has to say what it wants the OTHER sizes to be in the same breath.
/// </summary>
public sealed class BufferGeometry
{
    /// <param name="pageSize">Send/write page size.</param>
    /// <param name="receiveBufferSize">Per-socket receive buffer. Deliberately independent of the page:
    /// there is one per SOCKET for the connection's lifetime, so it multiplies by SocketsPerShard.</param>
    /// <param name="writeBuffersPerShard">Pre-pinned outbound buffers per shard. Each in-flight send holds
    /// one, so this also bounds how many connections per shard may have a send in flight at once.</param>
    /// <param name="outOfBandWriteBuffersPerShard">Ditto for the out-of-band (Flush) path.</param>
    /// <param name="bufferPagesPerShard">Read/provided buffers per shard (io_uring's read pool).</param>
    public BufferGeometry(int pageSize, int receiveBufferSize, int writeBuffersPerShard,
                          int outOfBandWriteBuffersPerShard, int bufferPagesPerShard)
    {
        PageSize = pageSize;
        ReceiveBufferSize = receiveBufferSize;
        WriteBuffersPerShard = writeBuffersPerShard;
        OutOfBandWriteBuffersPerShard = outOfBandWriteBuffersPerShard;
        BufferPagesPerShard = bufferPagesPerShard;
    }

    public int PageSize { get; }
    public int ReceiveBufferSize { get; }
    public int WriteBuffersPerShard { get; }
    public int OutOfBandWriteBuffersPerShard { get; }
    public int BufferPagesPerShard { get; }

    /// <summary>What every backend got before backends could choose: a 4KB page, a receive buffer that
    /// follows it, and the historical pool depths. Kept as the base so that adopting the mechanism
    /// changes NO behaviour for any backend that does not override it.</summary>
    public static BufferGeometry Default { get; } = new(
        pageSize: 4096, receiveBufferSize: 4096,
        writeBuffersPerShard: 1024, outOfBandWriteBuffersPerShard: 256, bufferPagesPerShard: 256);

    /// <summary>A geometry built around <paramref name="pageSize"/> that holds each pool's BYTE budget at
    /// <paramref name="poolBytes"/> rather than its entry count, so a bigger page does not multiply into
    /// gigabytes of pinned memory per shard. The receive buffer is given explicitly because it is the one
    /// size that must NOT follow the page.</summary>
    public static BufferGeometry ForPage(int pageSize, int receiveBufferSize, int poolBytes = 4 * 1024 * 1024)
    {
        int entries = Math.Max(16, poolBytes / pageSize);
        return new BufferGeometry(pageSize, receiveBufferSize, entries, entries, entries);
    }
}
