using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using SocketSets;

namespace SocketSets.AspNet;

/// <summary>Kestrel transport factory: hands back a listener backed by a SocketSet
/// <see cref="SocketSet"/>. Registered as the <see cref="IConnectionListenerFactory"/> so ASP.NET Core's
/// entire HTTP stack runs its request pipeline over io_uring / IOCP / RIO / managed sockets — and, when
/// configured, over a TLS session this transport terminates itself.</summary>
internal sealed class SocketSetTransportFactory(SocketSetTransportOptions options, SocketSetTransportMetrics metrics) : IConnectionListenerFactory
{
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var listener = new SocketSetConnectionListener(endpoint, options, metrics);
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
    private readonly SocketSetTransportOptions _options;
    private readonly SocketSetTransportMetrics _metrics;
    // One pool per listener, shared by every connection's pipes: the point of a pinned pool is that blocks
    // are recycled, so a per-connection pool would allocate pinned memory per connection and never reuse it.
    private readonly System.Buffers.MemoryPool<byte>? _pipePool;
    private readonly SocketSets.Tls.TlsProvider? _tls;
    private TransportSet _set = null!;

    private bool ByoPipe => _options.Mode == SocketSetBridgeMode.Byo;
    private bool HalfPipe => _options.Mode == SocketSetBridgeMode.HalfPipe;
    private bool PipePinned => _options.PipePinned;

    public SocketSetConnectionListener(EndPoint endpoint, SocketSetTransportOptions options, SocketSetTransportMetrics metrics)
    {
        // Created whenever pinned is requested (as before the extraction): it backs the inbound pipe in
        // every mode, and the outbound pipe in Classic/Byo. In Byo it additionally lets zero-copy send skip
        // per-segment pinning.
        _pipePool = options.PipePinned
            ? new PinnedBlockMemoryPool(options.PipeSegment > 0 ? options.PipeSegment : 4096)
            : null;
        EndPoint = endpoint;
        _options = options;
        _metrics = metrics;
        _tls = options.Tls;
    }

    public EndPoint EndPoint { get; }

    public void Bind()
    {
        var options = new SocketSetOptions
        {
            Factory = _options.Factory,
            Shards = _options.Shards,
            PinWorkerThreads = _options.PinWorkerThreads,
            Tls = _tls, // null = plaintext; otherwise TLS terminates here, below Kestrel
        };
        // 0 = leave the library default. Kept as an override rather than a value so we do not silently pin
        // a default the backend later changes.
        if (_options.PageSize > 0) options.BufferPageSize = _options.PageSize;
        if (_options.ReceiveBufferSize > 0) options.ReceiveBufferSize = _options.ReceiveBufferSize;
        if (_options.WriteBuffers > 0) options.WriteBuffersPerShard = _options.WriteBuffers;
        _set = new TransportSet(options, this);
        // Publish what the backend RESOLVED, not what was asked for. Sizes left unset are "backend chooses"
        // sentinels, and RIO picks a 64KB page nobody typed — so reporting only the explicit values would
        // describe a geometry the transport is not running.
        var g = _set.Options;
        _metrics.ResolvedGeometry = $"page={g.BufferPageSize} recvbuf={g.ReceiveBufferSize} "
            + $"writebufs={g.WriteBuffersPerShard} oobwritebufs={g.OutOfBandWriteBuffersPerShard} "
            + $"readpages={g.BufferPagesPerShard}";
        _set.Listen(EndPoint);
        Console.WriteLine($"[socketset transport] resolved: {_metrics.ResolvedGeometry}");
    }

    // --- SocketSet callbacks (loop thread) → Kestrel bridge ---

    /// <summary>Build the Kestrel-side connection. Split from <see cref="PublishConnection"/> so the
    /// caller can attach the pipe in between: <c>AcceptContext</c> is only visible to a SocketSet
    /// subclass, so the UsePipe call has to happen in <c>TransportSet</c>, and it must happen BEFORE the
    /// connection is published or Kestrel could start writing to a pipe nobody is reading yet.</summary>
    private SocketSetConnection CreateConnection(Connection conn)
    {
        // With TLS this fires only after the handshake has completed, so conn.NegotiatedProtocol is
        // already settled by the time the features are built.
        _metrics.OnAccept();
        var c = new SocketSetConnection(conn, _tls is not null, ByoPipe, _options.PipeSegment, _pipePool, HalfPipe, _metrics);
        conn.UserToken = c;
        return c;
    }

    private void PublishConnection(SocketSetConnection c)
    {
        c.Start(); // no-op in BYO/half-pipe mode: the outbound leg is not a pump here
        if (!_accepted.Writer.TryWrite(c)) _metrics.OnWriteFail();
    }

    private void OnReceive(Connection conn, ReadOnlySpan<byte> data)
    {
        if (conn.UserToken is SocketSetConnection c) c.WriteInbound(data);
    }

    private void OnClosed(Connection conn)
    {
        _metrics.OnClose();
        if (conn.UserToken is SocketSetConnection c)
        {
            if (!c.GotData) _metrics.OnClosedEmpty();
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
        (_tls as IDisposable)?.Dispose(); // credential handles / SSL_CTXs
        return ValueTask.CompletedTask;
    }

    /// <summary>The SocketSet whose per-connection callbacks fan out to the listener. All three fire on the
    /// owning shard's loop thread (one thread per connection), so they hand off to the pipes and return
    /// without blocking.</summary>
    private sealed class TransportSet(SocketSetOptions options, SocketSetConnectionListener listener) : SocketSet(options)
    {
        protected override void OnAccept(ref AcceptContext ctx)
        {
            var c = listener.CreateConnection(ctx.Connection);
            // BYO: hand Kestrel's OWN pipes to the transport, so SocketSet drives them directly instead of
            // SocketSetConnection copying inbound and pumping outbound. pinned: true asserts the addresses
            // are stable, so the backend may skip per-segment GCHandle pinning — only ever true when the
            // pipes are actually backed by the pinned-block pool.
            if (listener.ByoPipe) ctx.UsePipe(c.TransportSide, listener.PipePinned);
            listener.PublishConnection(c);
        }
        protected override void OnReceive(ref ReceiveContext ctx) => listener.OnReceive(ctx.Connection, ctx.Payload);
        protected override void OnClosed(Connection connection) => listener.OnClosed(connection);
    }
}
