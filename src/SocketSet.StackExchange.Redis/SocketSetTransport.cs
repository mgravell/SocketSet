using System.Net;
using RESPite.Transports;
using SocketSets;

namespace SocketSets.StackExchangeRedis;

/// <summary>
/// The SocketSet implementation of <see cref="DuplexTransport"/> — the piece <see cref="SocketSetTunnel"/>
/// hands to SE.Redis. One outbound connection per instance (the client shape: SE.Redis multiplexes onto
/// ~1-2 connections per endpoint), driven by the same mechanisms the proxy/Garnet work measured: receive
/// callbacks feed the receiver on the loop thread with transport-owned spans; the transport IS the
/// <see cref="System.Buffers.IBufferWriter{T}"/> (forwarding to the connection, which converged on the
/// same shape independently); and <see cref="SocketSet.OnLoopDrain"/> surfaces as
/// <see cref="TransportReceiver.OnBatchEnd"/>.
///
/// The engine is CONTAINED, not derived: <see cref="DuplexTransport"/> is an abstract class and so is
/// <see cref="SocketSet"/>, and single inheritance makes the previous is-a-engine shape impossible — a
/// consequence of the abstract-class contract decision, recorded in TODO. The nested engine forwards
/// its callbacks to the owning transport.
/// </summary>
public sealed class SocketSetClientTransport : DuplexTransport
{
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Engine _engine;
    private Connection? _conn;
    private TransportReceiver? _receiver;

    private SocketSetClientTransport(SocketSetOptions options) => _engine = new Engine(this, options);

    /// <summary>Dial <paramref name="endpoint"/> (TCP or UDS incl. @abstract; TLS per options + TlsMode)
    /// and complete when the connection — including any TLS handshake — is established.</summary>
    public static async Task<SocketSetClientTransport> ConnectAsync(
        EndPoint endpoint, SocketSetOptions options, CancellationToken cancellationToken = default)
    {
        var transport = new SocketSetClientTransport(options);
        try
        {
            transport._engine.Connect(endpoint);
            using var reg = cancellationToken.Register(static s => ((SocketSetClientTransport)s!)._connected.TrySetCanceled(), transport);
            await transport._connected.Task.ConfigureAwait(false);
            return transport;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Engine(SocketSetClientTransport owner, SocketSetOptions options) : SocketSet(options)
    {
        protected override void OnConnect(ref ConnectContext ctx)
        {
            owner._conn = ctx.Connection;
            owner._connected.TrySetResult(true);
        }

        protected override void OnReceive(ref ReceiveContext ctx)
        {
            // Push with the transport-owned span, on the loop thread — the level-2 contract verbatim. A
            // false return is the receiver requesting close; abortive is correct (the receiver has
            // decided the stream is over; there is no reply to preserve).
            if (owner._receiver is { } r && !r.OnReceived(ctx.Payload)) ctx.Connection.Close();
        }

        protected override void OnLoopDrain()
        {
            owner._receiver?.OnBatchEnd();
        }

        protected override void OnClosed(Connection connection)
        {
            // Peer closed (or teardown). Fires once per connection; the receiver learns exactly once.
            // SocketSet's OnClosed carries no fault detail today; a faulted-close reason is a shape
            // question for the real contract (recorded in the TODO proposal's open questions).
            Interlocked.Exchange(ref owner._receiver, null)?.OnClosed(null);
            owner._connected.TrySetException(new IOException("connection closed before establishment"));
        }
    }

    // ---- DuplexTransport ----

    // The transport is the writer: forward the IBufferWriter face to the connection. One null-check +
    // delegation per call, replacing the pre-revision zero-forwarding `Output => _conn` hand-out.

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
        _engine.Dispose();
        return default;
    }

    private Connection ConnectedOrThrow() => _conn
        ?? throw new InvalidOperationException("not connected");
}
