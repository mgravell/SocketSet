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
                              SocketSetTransportMetrics? metrics = null, long maxInboundBuffer = 4 * 1024 * 1024)
    {
        _conn = conn;
        _byo = byo;
        _metrics = metrics ?? new SocketSetTransportMetrics();
        _maxUnflushed = maxInboundBuffer;
        var sched = PipeScheduler.ThreadPool;
        // EXPERIMENT (SS_PIPE_SCHED): which pipe reader resumes INLINE instead of hopping to the ThreadPool.
        //
        //   inline       — the OUTBOUND reader (the SocketSet pump) resumes on the thread that flushed,
        //                  i.e. Kestrel's request thread. Safe: only Kestrel flushes that pipe, so no pump
        //                  work lands on a loop thread. Measured on Linux/io_uring: −28%.
        //   inline-read  — the INBOUND reader resumes on whoever wrote, i.e. the TRANSPORT LOOP THREAD.
        //                  This is the READ-side hop, and it had never been tested on any OS: the knob
        //                  above only ever touched the outbound pipe, so every "thread hop" number on file
        //                  is about the write side.
        //   inline-both  — both of the above.
        //
        // DANGER, and why this is an experiment rather than an option: an inline INBOUND reader runs
        // Kestrel's whole request pipeline on the transport's loop thread, which blocks that loop for every
        // backend that owns one (all but managed). Kestrel runs its own IO queues for exactly this reason.
        // So a WIN here is not directly shippable — its value is that it UPPER-BOUNDS what removing the
        // read-side hop could ever be worth, which is what decides whether an inbound half-pipe (a real
        // fix, no loop blocking) is worth building. A null result deprioritises that work outright.
        string? pipeSched = Environment.GetEnvironmentVariable("SS_PIPE_SCHED");
        var outReader = pipeSched is "inline" or "inline-both" ? PipeScheduler.Inline : sched;
        var inReader = pipeSched is "inline-read" or "inline-both" ? PipeScheduler.Inline : sched;
        // minimumSegmentSize and the pool are the two levers behind the "65 segments per 256KB response"
        // measurement (see PinnedBlockMemoryPool). Both default to the framework's choices so an unflagged
        // run is byte-for-byte the old behaviour.
        var pool = pipePool ?? MemoryPool<byte>.Shared;
        int seg = pipeSegment > 0 ? pipeSegment : -1; // -1 = PipeOptions' own default
        _inbound = new Pipe(new PipeOptions(pool, readerScheduler: inReader, writerScheduler: sched,
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

    public bool GotData;

    // Bytes written to the inbound pipe whose flush has not completed. The loop thread cannot WAIT for a
    // flush, so this is the only measure of how far ahead of Kestrel we are running.
    private long _unflushed;
    private readonly long _maxUnflushed;

    /// <summary>
    /// Loop thread: copy freshly-received bytes into the inbound pipe and signal Kestrel. The copy
    /// releases SocketSet's library-owned recv buffer immediately.
    ///
    /// THE FLUSH RESULT USED TO BE DISCARDED (<c>_ = w.FlushAsync()</c>), which meant the
    /// <c>pauseWriterThreshold</c> configured two lines away in the constructor did NOTHING: a client
    /// uploading faster than the handler drained grew the pipe without bound, which is memory exhaustion
    /// needing no protocol trickery (REVIEW.md D3). It is now tracked, and a connection that runs more
    /// than <c>MaxInboundBufferBytes</c> ahead of Kestrel is ABORTED.
    ///
    /// RECEIVE PARKING landed 2026-08-04 and is now the primary mechanism: an async flush means Kestrel is
    /// behind, so the transport stops reading (<c>Connection.TryPauseReceive</c>) and the TCP window slows
    /// the CLIENT instead of us buffering on its behalf. The abort above survives as the backstop for the
    /// overshoot, and as the whole mechanism on io_uring, whose multishot receive cannot park and which
    /// says so through <c>SupportsReceiveParking</c>.
    /// </summary>
    public void WriteInbound(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        GotData = true;
        PipeWriter w = _inbound.Writer;
        data.CopyTo(w.GetSpan(data.Length));
        w.Advance(data.Length);

        int n = data.Length;
        var flush = w.FlushAsync();
        if (flush.IsCompletedSuccessfully)
        {
            _ = flush.Result; // consume; nothing outstanding
            return;
        }

        // ORDER MATTERS: park BEFORE publishing the outstanding byte count. TrackFlush's completion is the
        // thing that resumes, and it can only run once _unflushed has been incremented — so parking first
        // guarantees the resumer sees the park. Parking after would race a fast flush into a connection
        // that is parked with nobody left to wake it, which is a silent hang rather than a visible fault.
        if (_conn.TryPauseReceive()) { Volatile.Write(ref _parked, 1); _metrics.OnReceivePark(); }

        long pending = Interlocked.Add(ref _unflushed, n);
        if (_maxUnflushed > 0 && pending > _maxUnflushed)
        {
            _metrics.OnInboundOverflow();
            Abort(new ConnectionAbortedException(
                $"inbound buffer exceeded {_maxUnflushed} bytes with the application not draining"));
            return;
        }
        TrackFlush(flush, n);
    }

    /// <summary>1 while this connection's receive is parked behind an outstanding inbound flush.</summary>
    private int _parked;

    private async void TrackFlush(ValueTask<FlushResult> flush, int bytes)
    {
        try { await flush; }
        catch { /* the pipe is done; teardown runs elsewhere */ }
        finally
        {
            // Resume only when the LAST outstanding flush drains. Several can overlap on a backend that
            // could not park (or during the overshoot before a park takes effect), and resuming on the
            // first would start reading again while Kestrel is still behind.
            if (Interlocked.Add(ref _unflushed, -bytes) == 0 && Interlocked.Exchange(ref _parked, 0) == 1)
                _conn.ResumeReceive();
        }
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
