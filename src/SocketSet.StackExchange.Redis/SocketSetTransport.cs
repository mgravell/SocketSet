using System.Net;
using SocketSets;

namespace SocketSets.StackExchangeRedis;

/// <summary>
/// The SocketSet implementation of the transport shape — the piece the eventual Tunnel subclass hands
/// to SE.Redis. One outbound connection per instance (the client shape: SE.Redis multiplexes onto ~1-2
/// connections per endpoint), driven by the same mechanisms the proxy/Garnet work measured:
/// receive callbacks feed the receiver on the loop thread with transport-owned spans; sends stage
/// through the connection's IBufferWriter surface from any thread and flush as single scatter-gather
/// submissions; and <see cref="SocketSet.OnLoopDrain"/> surfaces as <see cref="ITransportReceiver.OnBatchEnd"/>.
/// </summary>
public sealed class SocketSetClientTransport : SocketSet, IDuplexTransport
{
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Connection? _conn;
    private ITransportReceiver? _receiver;

    private SocketSetClientTransport(SocketSetOptions options) : base(options)
    {
    }

    /// <summary>Dial <paramref name="endpoint"/> (TCP or UDS incl. @abstract; TLS per options + TlsMode)
    /// and complete when the connection — including any TLS handshake — is established.</summary>
    public static async Task<SocketSetClientTransport> ConnectAsync(
        EndPoint endpoint, SocketSetOptions options, CancellationToken cancellationToken = default)
    {
        var transport = new SocketSetClientTransport(options);
        try
        {
            transport.Connect(endpoint);
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

    protected override void OnConnect(ref ConnectContext ctx)
    {
        _conn = ctx.Connection;
        _connected.TrySetResult(true);
    }

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        // Push with the transport-owned span, on the loop thread — the level-2 contract verbatim. A
        // false return is the receiver requesting close; abortive is correct (the receiver has decided
        // the stream is over; there is no reply to preserve).
        if (_receiver is { } r && !r.OnReceived(ctx.Payload)) ctx.Connection.Close();
    }

    protected override void OnLoopDrain()
    {
        _receiver?.OnBatchEnd();
    }

    protected override void OnClosed(Connection connection)
    {
        // Peer closed (or teardown). Fires once per connection; the receiver learns exactly once.
        // SocketSet's OnClosed carries no fault detail today; a faulted-close reason is a shape question
        // for the real contract (recorded in the TODO proposal's open questions).
        Interlocked.Exchange(ref _receiver, null)?.OnClosed(null);
        _connected.TrySetException(new IOException("connection closed before establishment"));
    }

    // ---- IDuplexTransport ----

    public System.Buffers.IBufferWriter<byte> Output => _conn
        ?? throw new InvalidOperationException("not connected");

    public bool Flush() => _conn is { } c && c.Flush();

    public void Start(ITransportReceiver receiver)
    {
        if (Interlocked.CompareExchange(ref _receiver, receiver, null) is not null)
            throw new InvalidOperationException("receiver already started");
    }

    public ValueTask DisposeAsync()
    {
        try { _conn?.Close(); } catch { }
        Dispose();
        return default;
    }
}
