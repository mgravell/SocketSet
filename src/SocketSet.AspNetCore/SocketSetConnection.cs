using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using SocketSets;

namespace SocketSets.AspNet;

/// <summary>Reports the ALPN id agreed by a TLS session this transport terminated. ASP.NET Core has no
/// public feature for this (ITlsHandshakeFeature carries the cipher suite, not the protocol), so this
/// library defines its own; read it from a request handler via <c>HttpContext.Features</c>.</summary>
public interface ITransportTlsFeature
{
    /// <summary>The ALPN protocol id negotiated by the transport-terminated TLS session, or null.</summary>
    string? NegotiatedProtocol { get; }
}

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
    private readonly Pipe? _outbound;          // null in half-pipe mode (the half-pipe IS the outbound leg)
    private readonly HalfPipeWriter? _half;    // non-null in half-pipe mode
    private readonly CancellationTokenSource _closedCts = new();
    private Task? _pump;

    /// <summary>
    /// BYO-buffer mode. Instead of this class copying inbound bytes into <c>_inbound</c> and running its
    /// own outbound pump, the SAME two pipes are handed to the transport via <c>ctx.UsePipe</c> and
    /// SocketSet drives them itself.
    ///
    /// Expect PARITY from this alone, not a win: the library's bridge does the same copy inbound and the
    /// same <c>Connection.Send</c> outbound, so phase 1 relocates the bridge rather than removing it. It
    /// exists as a parallel option so the same harness can measure the per-backend zero-copy work (where
    /// the transport receives straight into pipe memory and sends straight from it) against a like-for-like
    /// baseline rather than against the callback path.
    /// </summary>
    public IDuplexPipe TransportSide { get; }

    private readonly bool _byo;
    private readonly SocketSetTransportMetrics _metrics;

    public SocketSetConnection(Connection conn, bool tls, bool byo = false,
                              int pipeSegment = 0, MemoryPool<byte>? pipePool = null, bool halfPipe = false,
                              SocketSetTransportMetrics? metrics = null)
    {
        _conn = conn;
        _byo = byo;
        _metrics = metrics ?? new SocketSetTransportMetrics();
        var sched = PipeScheduler.ThreadPool;
        // EXPERIMENT (SS_PIPE_SCHED=inline): resume the OUTBOUND reader (the SocketSet pump) INLINE on the
        // thread that flushed — i.e. Kestrel's request thread — instead of hopping to the ThreadPool. This
        // is safe for the outbound pipe because only Kestrel flushes it (never the loop thread), so no pump
        // work lands on the loop thread. Tests how much of the bridge cost is the read-side thread hop.
        var outReader = Environment.GetEnvironmentVariable("SS_PIPE_SCHED") == "inline"
            ? PipeScheduler.Inline : sched;
        // minimumSegmentSize and the pool are the two levers behind the "65 segments per 256KB response"
        // measurement (see PinnedBlockMemoryPool). Both default to the framework's choices so an unflagged
        // run is byte-for-byte the old behaviour.
        var pool = pipePool ?? MemoryPool<byte>.Shared;
        int seg = pipeSegment > 0 ? pipeSegment : -1; // -1 = PipeOptions' own default
        _inbound = new Pipe(new PipeOptions(pool, readerScheduler: sched, writerScheduler: sched,
            useSynchronizationContext: false, pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19,
            minimumSegmentSize: seg));
        if (halfPipe)
        {
            // The outbound leg is a CycleBuffer-backed PipeWriter that drains itself to Connection.Send on
            // Kestrel's flush thread — no outbound Pipe, no pump, no thread hop. byo is off in this mode, so
            // TransportSide (the ctx.UsePipe view) is never consulted.
            _half = new HalfPipeWriter(conn, _metrics);
            TransportSide = null!;
        }
        else
        {
            _outbound = new Pipe(new PipeOptions(pool, readerScheduler: outReader, writerScheduler: sched,
                useSynchronizationContext: false, minimumSegmentSize: seg));
            // The transport's view is the mirror of Kestrel's: it WRITES what it receives (into the inbound
            // pipe Kestrel reads from) and READS what it should send (from the outbound pipe Kestrel writes to).
            TransportSide = new TransportDuplexPipe(_outbound.Reader, _inbound.Writer);
        }

        Transport = this;
        ConnectionId = Guid.NewGuid().ToString("n");
        Features = new FeatureCollection();
        Items = new Dictionary<object, object?>();

        if (tls)
        {
            // The transport already terminated TLS, so Kestrel sees plaintext and would otherwise report
            // the request as http://. Publishing ITlsConnectionFeature is what makes Request.IsHttps true
            // and the scheme https. Kestrel exposes connection-level features through HttpContext.Features,
            // so the ALPN one below is readable from a request handler too.
            var feature = new TransportTlsFeature(conn);
            Features.Set<ITlsConnectionFeature>(feature);
            Features.Set<ITransportTlsFeature>(feature);
        }
    }

    /// <summary>Minimal TLS features for a session terminated in the transport rather than by SslStream.
    /// Client certificates are not requested (the providers are configured server-auth only), so the
    /// certificate half is deliberately inert.</summary>
    private sealed class TransportTlsFeature(Connection conn) : ITlsConnectionFeature, ITransportTlsFeature
    {
        public X509Certificate2? ClientCertificate { get => null; set { } }

        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<X509Certificate2?>(null);

        public string? NegotiatedProtocol => conn.NegotiatedProtocol;
    }

    /// <summary>Start the outbound pump (called once, right after accept). In BYO mode there is no pump
    /// here — SocketSet reads the outbound pipe itself.</summary>
    public void Start()
    {
        // Half-pipe and BYO both drive the outbound leg without this pump: half-pipe drains on Kestrel's
        // flush thread, BYO hands the pipe to the transport. Only the classic path runs a pump here.
        if (!_byo && _outbound is not null) _pump = Task.Run(PumpOutboundAsync);
    }

    private sealed class TransportDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input => input;
        public PipeWriter Output => output;
    }

    // --- IDuplexPipe: what Kestrel reads/writes ---
    public PipeReader Input => _inbound.Reader;
    public PipeWriter Output => _half ?? _outbound!.Writer;

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
        _outbound?.Reader.CancelPendingRead();
        _half?.MarkPeerGone();
        _closedCts.Cancel();
    }

    private async Task PumpOutboundAsync()
    {
        PipeReader reader = _outbound!.Reader; // only started when _outbound is non-null (see Start)
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();
                ReadOnlySequence<byte> buffer = result.Buffer;
                if (!buffer.IsEmpty && !_conn.Send(buffer)) { _metrics.OnSendFalse(); break; } // socket gone
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
        _half?.MarkPeerGone();
        _inbound.Writer.Complete(abortReason);
        _closedCts.Cancel();
    }

    public override void Abort() => Abort(new ConnectionAbortedException());

    public override async ValueTask DisposeAsync()
    {
        // Graceful: signal the pump to finish and let it drain all buffered output to the socket, THEN
        // tidy up — but do NOT abortive-Close here (see the pump's finally). The connection closes via the
        // client's close → SocketSet recv-EOF teardown, which runs after the response is already sent.
        _outbound?.Writer.Complete();
        _half?.Complete();
        _inbound.Writer.Complete();
        if (_pump is { } p) { try { await p; } catch { /* ignore */ } }
        _closedCts.Dispose();
    }
}
