using System.Net;
using RESPite.Transports;
using SocketSets;

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
    private TransportReceiver? _receiver;
    internal bool PendingBatchEnd; // loop-thread-local via the engine's touched list

    private SocketSetClientTransport(SocketSetClientEngine? ownedEngine) => _ownedEngine = ownedEngine;

    /// <summary>Dial <paramref name="endpoint"/> on a SHARED <paramref name="engine"/> (TCP or UDS incl.
    /// @abstract; TLS per the engine's options + TlsMode) and complete when the connection — including
    /// any TLS handshake — is established.</summary>
    public static async Task<SocketSetClientTransport> ConnectAsync(
        EndPoint endpoint, SocketSetClientEngine engine, CancellationToken cancellationToken = default)
    {
        var transport = new SocketSetClientTransport(ownedEngine: null);
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
            var transport = new SocketSetClientTransport(ownedEngine: engine);
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
        _connected.TrySetResult(true);
    }

    internal bool OnEngineReceive(ReadOnlySpan<byte> payload)
        => _receiver is not { } r || r.OnReceived(payload); // no receiver yet: pre-Start data is dropped

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

    public override void Start(TransportReceiver receiver)
    {
        if (Interlocked.CompareExchange(ref _receiver, receiver, null) is not null)
            throw new InvalidOperationException("receiver already started");
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
