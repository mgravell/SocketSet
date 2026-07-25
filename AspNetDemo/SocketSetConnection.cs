using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using SocketSets;

namespace SocketSets.AspNet;

/// <summary>
/// One Kestrel connection backed by a SocketSet <see cref="Connection"/>. Bridges SocketSet's PUSH model
/// (bytes arrive via OnReceive on the io_uring loop thread; you send via Connection.Send) to Kestrel's
/// PULL model (an <see cref="IDuplexPipe"/> of a read side + write side) using two
/// <see cref="Pipe"/>s:
///   inbound:  OnReceive → copy into <c>_inbound.Writer</c> (frees SocketSet's recv buffer immediately —
///             "unload ASAP") → Kestrel reads it from <see cref="Input"/>.
///   outbound: Kestrel writes responses to <see cref="Output"/> → a pump reads <c>_outbound.Reader</c>
///             and hands the bytes to <c>Connection.Send</c> (the thread-safe out-of-band path).
/// Pipe schedulers are ThreadPool so no Kestrel/pump work ever runs on the io_uring loop thread.
/// </summary>
internal sealed class SocketSetConnection : ConnectionContext, IDuplexPipe
{
    private readonly Connection _conn;
    private readonly Pipe _inbound;
    private readonly Pipe _outbound;
    private readonly CancellationTokenSource _closedCts = new();
    private Task? _pump;

    public SocketSetConnection(Connection conn)
    {
        _conn = conn;
        var sched = PipeScheduler.ThreadPool;
        _inbound = new Pipe(new PipeOptions(readerScheduler: sched, writerScheduler: sched,
            useSynchronizationContext: false, pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19));
        _outbound = new Pipe(new PipeOptions(readerScheduler: sched, writerScheduler: sched,
            useSynchronizationContext: false));

        Transport = this;
        ConnectionId = Guid.NewGuid().ToString("n");
        Features = new FeatureCollection();
        Items = new Dictionary<object, object?>();
    }

    /// <summary>Start the outbound pump (called once, right after accept).</summary>
    public void Start() => _pump = Task.Run(PumpOutboundAsync);

    // --- IDuplexPipe: what Kestrel reads/writes ---
    public PipeReader Input => _inbound.Reader;
    public PipeWriter Output => _outbound.Writer;

    // --- ConnectionContext surface ---
    public override IDuplexPipe Transport { get; set; }
    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features { get; }
    public override IDictionary<object, object?> Items { get; set; }
    public override CancellationToken ConnectionClosed { get => _closedCts.Token; set { } }

    /// <summary>Loop thread: copy freshly-received bytes into the inbound pipe and signal Kestrel. The copy
    /// releases SocketSet's library-owned recv buffer immediately. FlushAsync completes synchronously under
    /// the pause threshold, so this never blocks the loop (fire-and-forget beyond that — demo backpressure).</summary>
    public bool GotData;

    public void WriteInbound(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        GotData = true;
        PipeWriter w = _inbound.Writer;
        data.CopyTo(w.GetSpan(data.Length));
        w.Advance(data.Length);
        _ = w.FlushAsync();
    }

    /// <summary>Loop thread: the peer closed / the socket errored. EOF the inbound pipe so Kestrel finishes
    /// the request loop, and wake the pump.</summary>
    public void OnClosedByPeer()
    {
        _inbound.Writer.Complete();
        _outbound.Reader.CancelPendingRead();
        _closedCts.Cancel();
    }

    private async Task PumpOutboundAsync()
    {
        PipeReader reader = _outbound.Reader;
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();
                ReadOnlySequence<byte> buffer = result.Buffer;
                if (!buffer.IsEmpty && !_conn.Send(buffer)) { Interlocked.Increment(ref SocketSetConnectionListener.SendFalse); break; } // socket gone
                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch { /* connection torn down */ }
        finally
        {
            reader.Complete();
            // Deliberately NO _conn.Close() here. SocketSet's Close() is abortive (it cancels any queued/
            // in-flight send → RST), which truncates a just-written response if we race it. Instead we let
            // the connection close the graceful way: the client closes → SocketSet's recv sees EOF → its own
            // teardown runs *after* the response has already gone out. A genuine Abort() still force-closes.
        }
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        _conn.Close(); // a genuine abort IS abrupt — a RST here is correct
        _inbound.Writer.Complete(abortReason);
        _closedCts.Cancel();
    }

    public override void Abort() => Abort(new ConnectionAbortedException());

    public override async ValueTask DisposeAsync()
    {
        // Graceful: signal the pump to finish and let it drain all buffered output to the socket, THEN
        // tidy up — but do NOT abortive-Close here (see the pump's finally). The connection closes via the
        // client's close → SocketSet recv-EOF teardown, which runs after the response is already sent.
        _outbound.Writer.Complete();
        _inbound.Writer.Complete();
        if (_pump is { } p) { try { await p; } catch { /* ignore */ } }
        _closedCts.Dispose();
    }
}
