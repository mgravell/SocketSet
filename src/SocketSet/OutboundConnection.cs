#if NET // Windows IOCP/RIO backends; compiled out of the netfx fallback build.
using System.Buffers;
using SocketSets.Tls;

namespace SocketSets;

/// <summary>
/// Shared out-of-band write accumulator for the loop-thread backends (IOCP, RIO, epoll). Implements the
/// <see cref="System.Buffers.IBufferWriter{T}"/> surface (GetSpan/GetMemory/Advance/Flush) over a
/// <see cref="PooledBufferWriter"/>: the writing thread stages bytes, then <see cref="Flush"/> hands the
/// accumulator's own buffer to the owning IO loop (<see cref="SubmitOutbound"/>), which feeds it through
/// the normal per-connection send path (Pending → write-page-sized sends) in order, interleaved correctly
/// with echo responses.
///
/// This is deliberately simpler than io_uring's OutChain/writev: the Windows send path already copies into
/// a (RIO-registered) write page, so the out-of-band bytes just ride the same road — no pinned OOB pool,
/// no scatter-gather, no separate RIO buffer registration.
///
/// Single-writer until Flush (the IBufferWriter contract): the thread currently writing owns the
/// accumulator; Flush detaches its buffer and marshals it, after which the loop owns it.
/// </summary>
internal abstract class OutboundConnection : Connection
{
    private PooledBufferWriter? _ob;

    /// <summary>True once the connection is torn down (its slot freed); gates Flush.</summary>
    protected abstract bool IsClosed { get; }

    /// <summary>
    /// Hand <paramref name="length"/> bytes to the owning IO loop to send, in order. Called from
    /// <see cref="Flush"/> on any thread.
    ///
    /// OWNERSHIP: <paramref name="data"/> is rented from <see cref="ArrayPool{T}"/>. Returning true
    /// transfers it to the loop, which MUST return it to the pool once drained. Returning false transfers
    /// nothing - the caller returns it instead. <paramref name="data"/> may be longer than
    /// <paramref name="length"/>, as rented arrays usually are; only the first
    /// <paramref name="length"/> bytes are meaningful.
    /// </summary>
    protected abstract bool SubmitOutbound(byte[] data, int length);

    public override Span<byte> GetSpan(int sizeHint = 0) => (_ob ??= new()).GetSpan(sizeHint);

    public override Memory<byte> GetMemory(int sizeHint = 0) => (_ob ??= new()).GetMemory(sizeHint);

    public override void Advance(int count) => _ob!.Advance(count);

    public override bool Flush()
    {
        var w = _ob;
        if (w is null || w.WrittenCount == 0) return !IsClosed; // nothing staged since the last flush
        if (IsClosed) { w.Reset(); return false; }               // closed under the writer → drop

        // HAND THE BUFFER OVER; DO NOT COPY IT. The requirement is that the loop gets memory the writing
        // thread will not touch again - not that it gets a *copy*. TakeArray detaches the accumulator's
        // own pooled array and leaves the writer empty (its next GetSpan re-rents), which satisfies that
        // without the snapshot.
        //
        // History, because this line has been wrong twice in different ways. It was `WrittenSpan.ToArray()`
        // - an unpooled allocation the size of the whole response, per flush, on the ThreadPool thread also
        // running Kestrel; `fa97dd4` measured removing that at +27% at 256KB and concluded "allocation was
        // the cost, per-byte copying is not". True as far as it went, but it replaced the allocation with a
        // rent PLUS a full copy, and the copy stayed. This removes it.
        //
        // Measured on the ASP.NET bridge at 256KB, where the copy count correlated with the bridge's cost:
        // io_uring makes 1 copy and pays 24.5%, epoll made 2 and paid 41.8% - despite epoll being the
        // FASTER of the two on the bare transport. See AspNetDemo/RESULTS.md.
        //
        // The array handed over is the accumulator's own buffer, so it may be considerably longer than
        // `length` (it grows by doubling) where the old snapshot was Rent(length). Both consumers slice by
        // the length argument explicitly and return the array to the pool, so this is safe - but a new
        // consumer must not infer the payload size from `data.Length`.
        var (data, length) = w.TakeArray();

        // Ownership passes to the loop only on success; on refusal it never entered the queue.
        if (SubmitOutbound(data, length)) return true;
        ArrayPool<byte>.Shared.Return(data);
        return false;
    }
}
#endif
