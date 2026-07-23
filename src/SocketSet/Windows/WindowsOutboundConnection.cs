#if NET // Windows IOCP/RIO backends; compiled out of the netfx fallback build.
using System.Buffers;

namespace SocketSets.Windows;

/// <summary>
/// Shared out-of-band write accumulator for the Windows connection types (IOCP + RIO). Implements the
/// <see cref="System.Buffers.IBufferWriter{T}"/> surface (GetSpan/GetMemory/Advance/Flush) over a
/// managed <see cref="ArrayBufferWriter{T}"/>: the writing thread stages bytes, then <see cref="Flush"/>
/// snapshots them and hands them to the owning IO loop (<see cref="SubmitOutbound"/>), which feeds them
/// through the normal per-connection send path (Pending → write-page-sized sends) in order, interleaved
/// correctly with echo responses.
///
/// This is deliberately simpler than io_uring's zero-copy OutChain/writev: the Windows send path already
/// copies into a (RIO-registered) write page, so the out-of-band bytes just ride the same road — no
/// pinned OOB pool, no scatter-gather, no separate RIO buffer registration. Out-of-band writes are not
/// the throughput hot path, so the extra copy is a fair trade for reusing the validated send machinery.
///
/// Single-writer until Flush (the IBufferWriter contract): the thread currently writing owns the
/// accumulator; Flush detaches a private snapshot and marshals it, after which the loop owns it.
/// </summary>
internal abstract class WindowsOutboundConnection : Connection
{
    private ArrayBufferWriter<byte>? _ob;

    /// <summary>True once the connection is torn down (its slot freed); gates Flush.</summary>
    protected abstract bool IsClosed { get; }

    /// <summary>Hand <paramref name="length"/> bytes (a private copy the loop now owns) to the owning IO
    /// loop to send, in order. Called from <see cref="Flush"/> on any thread. Returns false if closed.</summary>
    protected abstract bool SubmitOutbound(byte[] data, int length);

    public override Span<byte> GetSpan(int sizeHint = 0) => (_ob ??= new()).GetSpan(sizeHint);

    public override Memory<byte> GetMemory(int sizeHint = 0) => (_ob ??= new()).GetMemory(sizeHint);

    public override void Advance(int count) => _ob!.Advance(count);

    public override bool Flush()
    {
        var w = _ob;
        if (w is null || w.WrittenCount == 0) return !IsClosed; // nothing staged since the last flush
        if (IsClosed) { w.Clear(); return false; }               // closed under the writer → drop
        var data = w.WrittenSpan.ToArray();                      // private snapshot; w's buffer is reused after Clear
        w.Clear();
        return SubmitOutbound(data, data.Length);
    }
}
#endif
