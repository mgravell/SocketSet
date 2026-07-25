using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using SocketSets;

namespace SocketSets.AspNet;

/// <summary>Kestrel transport factory: hands back a listener backed by a SocketSet io_uring
/// <see cref="SocketSet"/>. Registered as the <see cref="IConnectionListenerFactory"/> so ASP.NET Core's
/// entire HTTP stack runs its request pipeline over io_uring.</summary>
internal sealed class SocketSetTransportFactory : IConnectionListenerFactory
{
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var listener = new SocketSetConnectionListener(endpoint);
        listener.Bind();
        return ValueTask.FromResult<IConnectionListener>(listener);
    }
}

/// <summary>
/// Wraps a SocketSet <see cref="SocketSet"/> as a Kestrel <see cref="IConnectionListener"/>. SocketSet accepts
/// via a PUSH callback (OnAccept on the io_uring loop thread); Kestrel wants a PULL <see cref="AcceptAsync"/>,
/// so accepted connections are handed across an unbounded channel. Per-connection receive/close callbacks
/// are routed to the owning <see cref="SocketSetConnection"/> (stashed in <c>Connection.UserToken</c>).
/// </summary>
internal sealed class SocketSetConnectionListener : IConnectionListener
{
    private readonly Channel<ConnectionContext> _accepted =
        Channel.CreateUnbounded<ConnectionContext>(new UnboundedChannelOptions { SingleReader = true });
    private TransportSet _set = null!;

    public SocketSetConnectionListener(EndPoint endpoint) => EndPoint = endpoint;

    public EndPoint EndPoint { get; }

    public void Bind()
    {
        var options = new SocketSetOptions
        {
            Factory = SocketSetFactory.IoUring,
            Shards = 2,
            PinWorkerThreads = false,
        };
        _set = new TransportSet(options, this);
        _set.Listen(EndPoint);
    }

    // --- SocketSet callbacks (loop thread) → Kestrel bridge ---

    internal static int Accepts, Closes, ClosedEmpty, WriteFail, SendFalse;

    private void OnAccept(Connection conn)
    {
        Interlocked.Increment(ref Accepts);
        var c = new SocketSetConnection(conn);
        conn.UserToken = c;
        c.Start();
        if (!_accepted.Writer.TryWrite(c)) Interlocked.Increment(ref WriteFail);
    }

    private void OnReceive(Connection conn, ReadOnlySpan<byte> data)
    {
        if (conn.UserToken is SocketSetConnection c) c.WriteInbound(data);
    }

    private void OnClosed(Connection conn)
    {
        Interlocked.Increment(ref Closes);
        if (conn.UserToken is SocketSetConnection c)
        {
            if (!c.GotData) Interlocked.Increment(ref ClosedEmpty);
            c.OnClosedByPeer();
        }
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try { return await _accepted.Reader.ReadAsync(cancellationToken); }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return null; } // unbound → stop accepting
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _accepted.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _accepted.Writer.TryComplete();
        _set?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The SocketSet SocketSet whose per-connection callbacks fan out to the listener. All three
    /// fire on the owning shard's io_uring loop thread (one thread per connection), so they hand off to the
    /// pipes and return without blocking.</summary>
    private sealed class TransportSet(SocketSetOptions options, SocketSetConnectionListener listener) : SocketSet(options)
    {
        protected override void OnAccept(ref AcceptContext ctx) => listener.OnAccept(ctx.Connection);
        protected override void OnReceive(ref ReceiveContext ctx) => listener.OnReceive(ctx.Connection, ctx.Payload);
        protected override void OnClosed(Connection connection) => listener.OnClosed(connection);
    }
}
