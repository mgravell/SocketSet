using System.Net;
using RESPite.Transports;
using SocketSets;
using SocketSets.Tls;

namespace SocketSets.StackExchangeRedis;

/// <summary>
/// The ANCHOR: one engine, N loop threads TOTAL, every transport's connection multiplexed across its
/// shards — thread count is a configuration constant, not a function of topology. This is the shard
/// hybrid between the classic sync-reader backend (thread per connection: best latency, worst scaling)
/// and async workers (bounded threads, a hop per completion): receive, parse and completion run inline
/// on the owning loop thread, and the thread count never moves.
///
/// Callbacks are engine-wide, so this type is the per-connection ROUTER: <see cref="Connection.UserToken"/>
/// carries the owning <see cref="SocketSetClientTransport"/>, and batch-end fans out only to transports
/// that actually received in the batch (the touched-this-batch pattern, per loop thread — the same shape
/// the proxy's deferred flush uses).
/// </summary>
public sealed class SocketSetClientEngine(SocketSetOptions options) : SocketSet(options)
{
    internal void Dial(EndPoint endpoint, SocketSetClientTransport transport)
        => Connect(endpoint, userToken: transport);

    /// <summary>
    /// THE per-connection TLS decision, and the case this callback was designed for: one engine dials
    /// many endpoints, so the posture cannot live on the engine. <see cref="Connection.UserToken"/> is the
    /// transport, which carries the intent its own dial was given.
    ///
    /// A transport with no stated intent (the direct ConnectAsync entry points) falls through to the
    /// engine's own configuration, so nothing that worked before this existed behaves differently.
    /// </summary>
    protected override bool OnClientAuthenticate(ref TlsClientAuthenticateContext ctx)
    {
        if (ctx.Connection.UserToken is not SocketSetClientTransport t || !t.Tls.Specified)
            return base.OnClientAuthenticate(ref ctx);

        if (!t.Tls.Enabled) return false; // configured plaintext: a provider on the engine does not override it
        ctx.TargetHost = t.Tls.TargetHost; // validated non-blank when the intent was built
        return true; // Provider is seeded from the engine's options; TlsIntent refused a null one already
    }

    protected override void OnConnect(ref ConnectContext ctx)
    {
        if (ctx.Connection.UserToken is SocketSetClientTransport t) t.OnEngineConnect(ctx.Connection);
    }

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        if (ctx.Connection.UserToken is SocketSetClientTransport t)
        {
            // A false return is the receiver requesting close; abortive is correct (the receiver has
            // decided the stream is over; there is no reply to preserve).
            if (!t.OnEngineReceive(ctx.Payload))
            {
                ctx.Connection.Close();
                return;
            }
            NoteTouched(t);
        }
    }

    protected override void OnClosed(Connection connection)
    {
        if (connection.UserToken is SocketSetClientTransport t) t.OnEngineClosed();
    }

    // Batch-end routing: a connection's receives all happen on its own shard's loop thread, so the
    // touched list is thread-static per loop thread and the per-transport flag needs no interlocking.
    [ThreadStatic]
    private static List<SocketSetClientTransport>? t_touched;

    private static void NoteTouched(SocketSetClientTransport t)
    {
        if (!t.PendingBatchEnd)
        {
            t.PendingBatchEnd = true;
            (t_touched ??= new()).Add(t);
        }
    }

    protected override void OnLoopDrain()
    {
        var list = t_touched;
        if (list is { Count: > 0 })
        {
            foreach (var t in list)
            {
                t.PendingBatchEnd = false;
                t.OnEngineBatchEnd();
            }
            list.Clear();
        }
    }
}

/// <summary>
/// The SocketSet implementation of <see cref="DuplexTransport"/>: a thin per-connection router over a
/// shared <see cref="SocketSetClientEngine"/>. The transport IS the <see cref="System.Buffers.IBufferWriter{T}"/>
/// (forwarding to the connection, which converged on the same shape independently); inbound is fed by
/// the engine's callbacks; <see cref="SocketSet.OnLoopDrain"/> surfaces as
/// <see cref="TransportReceiver.OnBatchEnd"/> for exactly the transports touched in the batch.
/// </summary>
public sealed class SocketSetClientTransport : DuplexTransport
{
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SocketSetClientEngine? _ownedEngine; // only when the convenience overload built it
    private Connection? _conn;
    private volatile TransportReceiver? _receiver;
    private volatile bool _encrypted; // captured at connect; see IsEncrypted
    private readonly object _startGate = new(); // orders staged-vs-live inbound around Start
    private System.Buffers.ArrayBufferWriter<byte>? _staged; // inbound that beat Start; see OnEngineReceive

    /// <summary>How much inbound may be staged before <see cref="Start"/>. Generous next to any
    /// handshake, small next to a flood: a peer cannot use the gap between connect and Start to make us
    /// buffer without bound.</summary>
    private const int MaxStagedBeforeStart = 256 * 1024;
    internal bool PendingBatchEnd; // loop-thread-local via the engine's touched list

    /// <summary>What this dial was told to do about TLS, read back by the engine's
    /// <see cref="SocketSetClientEngine.OnClientAuthenticate"/> when the handshake is about to start.</summary>
    internal readonly TlsIntent Tls;

    private SocketSetClientTransport(SocketSetClientEngine? ownedEngine, TlsIntent tls)
    {
        _ownedEngine = ownedEngine;
        Tls = tls;
    }

    /// <summary>Dial <paramref name="endpoint"/> on a SHARED <paramref name="engine"/> (TCP or UDS incl.
    /// @abstract; TLS per the engine's options + TlsMode) and complete when the connection — including
    /// any TLS handshake — is established.</summary>
    public static Task<SocketSetClientTransport> ConnectAsync(
        EndPoint endpoint, SocketSetClientEngine engine, CancellationToken cancellationToken = default)
        => ConnectAsync(endpoint, engine, TlsIntent.EngineDefault, cancellationToken);

    /// <summary>As above, but with the TLS posture stated per connection (the tunnel path) rather than
    /// taken from the engine's options.</summary>
    internal static async Task<SocketSetClientTransport> ConnectAsync(
        EndPoint endpoint, SocketSetClientEngine engine, TlsIntent tls, CancellationToken cancellationToken)
    {
        var transport = new SocketSetClientTransport(ownedEngine: null, tls);
        engine.Dial(endpoint, transport);
        using var reg = cancellationToken.Register(static s => ((SocketSetClientTransport)s!)._connected.TrySetCanceled(), transport);
        await transport._connected.Task.ConfigureAwait(false);
        return transport;
    }

    /// <summary>Convenience: dial with a PRIVATE single-purpose engine built from <paramref name="options"/>
    /// and owned (and disposed) by the transport. One engine per connection is the wrong shape at scale —
    /// prefer the shared-engine overload (or <see cref="SocketSetTunnel"/>, which anchors one engine for
    /// all its dials); this exists for single-connection tools and tests.</summary>
    public static async Task<SocketSetClientTransport> ConnectAsync(
        EndPoint endpoint, SocketSetOptions options, CancellationToken cancellationToken = default)
    {
        var engine = new SocketSetClientEngine(options);
        try
        {
            var transport = new SocketSetClientTransport(ownedEngine: engine, TlsIntent.EngineDefault);
            engine.Dial(endpoint, transport);
            using var reg = cancellationToken.Register(static s => ((SocketSetClientTransport)s!)._connected.TrySetCanceled(), transport);
            await transport._connected.Task.ConfigureAwait(false);
            return transport;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    internal void OnEngineConnect(Connection connection)
    {
        _conn = connection;
        // Capture rather than read through later: Connection objects are POOLED, so a live read after this
        // one closed would report whatever the next tenant negotiated. Set before the connect completes,
        // so IsEncrypted is already true by the time the consumer checks it.
        _encrypted = connection.IsEncrypted;
        _connected.TrySetResult(true);
    }

    /// <summary>
    /// Inbound from the engine. Bytes that arrive BEFORE <see cref="Start"/> are STAGED and replayed to
    /// the receiver the moment it is set -- they used to be dropped, and dropping them loses a handshake.
    ///
    /// WHY THE RACE IS REAL rather than theoretical (found 2026-08-18, by the first gate cell that ever
    /// subscribed through this seam). The contract says a receiver is set "before any data is expected",
    /// but the consumer's own order is: take the transport, init the output, WRITE its handshake, and
    /// only then start reading. So the reply can be on the wire before Start, and whether it lands first
    /// is a race the consumer cannot see. SE.Redis's interactive connection happened to win it; its
    /// SUBSCRIPTION connection lost it every time -- 325 bytes of handshake reply dropped, the connection
    /// never became usable, and the symptom three layers up was "SUBSCRIBE timed out in the backlog".
    ///
    /// Staging is bounded: a peer that talks before we are listening cannot make us buffer without limit,
    /// and hitting the bound closes the connection rather than quietly truncating it.
    /// </summary>
    internal bool OnEngineReceive(ReadOnlySpan<byte> payload)
    {
        if (_receiver is { } fast) return fast.OnReceived(payload); // steady state: no lock, no staging

        lock (_startGate)
        {
            // Re-check under the lock: Start publishes the receiver INSIDE it, after replaying, so this
            // either stages ahead of a replay that has not happened yet, or delivers live behind one that
            // has. Ordering is what the lock is for; the fast path above is why it is not a cost.
            if (_receiver is { } now) return now.OnReceived(payload);

            _staged ??= new();
            if (_staged.WrittenCount + payload.Length > MaxStagedBeforeStart) return false; // close, loudly
            payload.CopyTo(_staged.GetSpan(payload.Length));
            _staged.Advance(payload.Length);
            return true;
        }
    }

    internal void OnEngineBatchEnd() => _receiver?.OnBatchEnd();

    internal void OnEngineClosed()
    {
        // Fires once per connection (peer close, failed connect, or teardown); the receiver learns
        // exactly once. SocketSet's OnClosed carries no fault detail today; a faulted-close reason is a
        // shape question for the contract (recorded in the TODO proposal's open questions).
        Interlocked.Exchange(ref _receiver, null)?.OnClosed(null);
        _connected.TrySetException(new IOException("connection closed before establishment"));
    }

    // ---- DuplexTransport ----

    public override Memory<byte> GetMemory(int sizeHint = 0) => ConnectedOrThrow().GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => ConnectedOrThrow().GetSpan(sizeHint);

    public override void Advance(int count) => ConnectedOrThrow().Advance(count);

    public override bool Flush() => _conn is { } c && c.Flush();

    /// <summary>Whether this connection's bytes are encrypted on the wire — the OUTCOME of the handshake,
    /// read off the connection at connect, not the intent that asked for it. A consumer refuses a
    /// transport that reports plaintext when its configuration demanded TLS, and that check is worthless
    /// against a value derived from the same configuration it is checking.</summary>
    public override bool IsEncrypted => _encrypted;

    public override void Start(TransportReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_startGate)
        {
            if (_receiver is not null) throw new InvalidOperationException("receiver already started");

            // Replay BEFORE publishing, so the first thing this receiver sees is the earliest byte that
            // arrived, and no live delivery can slip in front of the staged ones.
            if (_staged is { WrittenCount: > 0 } staged)
            {
                _staged = null;
                if (!receiver.OnReceived(staged.WrittenSpan))
                {
                    _conn?.Close(); // the receiver asked to close on the staged bytes; honour it
                    return;
                }
            }
            _staged = null;
            _receiver = receiver;
        }
    }

    public override ValueTask DisposeAsync()
    {
        try { _conn?.Close(); } catch { }
        _ownedEngine?.Dispose(); // shared engines belong to their owner; only the convenience path disposes
        return default;
    }

    private Connection ConnectedOrThrow() => _conn
        ?? throw new InvalidOperationException("not connected");
}
