using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using SocketSets;

namespace SocketSets.AspNet;

/// <summary>Kestrel transport factory: hands back a listener backed by a SocketSet
/// <see cref="SocketSet"/>. Registered as the <see cref="IConnectionListenerFactory"/> so ASP.NET Core's
/// entire HTTP stack runs its request pipeline over io_uring / IOCP / RIO / managed sockets — and, when
/// configured, over a TLS session this transport terminates itself.</summary>
internal sealed class SocketSetTransportFactory(DemoConfig config, TransportTlsProvider tls) : IConnectionListenerFactory
{
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var listener = new SocketSetConnectionListener(endpoint, config, tls.Provider);
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
    private readonly DemoConfig _config;
    // One pool per listener, shared by every connection's pipes: the point of a pinned pool is that blocks
    // are recycled, so a per-connection pool would allocate pinned memory per connection and never reuse it.
    private readonly System.Buffers.MemoryPool<byte>? _pipePool;
    private readonly SocketSets.Tls.TlsProvider? _tls;
    private TransportSet _set = null!;

    public bool PipePinned => _config.PipePinned;

    public SocketSetConnectionListener(EndPoint endpoint, DemoConfig config, SocketSets.Tls.TlsProvider? tls)
    {
        _pipePool = config.PipePinned
            ? new PinnedBlockMemoryPool(config.PipeSegment > 0 ? config.PipeSegment : 4096)
            : null;
        EndPoint = endpoint;
        _config = config;
        _tls = tls;
    }

    public EndPoint EndPoint { get; }

    public void Bind()
    {
        var options = new SocketSetOptions
        {
            Factory = _config.Factory,
            Shards = _config.Shards,
            PinWorkerThreads = _config.Pin,
            Tls = _tls, // null = plaintext; otherwise TLS terminates here, below Kestrel
        };
        // 0 = leave the library default. Kept as an override rather than a value so the demo does not
        // silently pin a default that the library later changes.
        if (_config.PageSize > 0) options.BufferPageSize = _config.PageSize;
        if (_config.RecvBufferSize > 0) options.ReceiveBufferSize = _config.RecvBufferSize;
        if (_config.WriteBuffers > 0) options.WriteBuffersPerShard = _config.WriteBuffers;
        _set = new TransportSet(options, this);
        // Publish what the backend RESOLVED, not what was asked for. Sizes left unset are "backend
        // chooses" sentinels now, and RIO picks a 64KB page nobody typed - so a /config that echoed only
        // the explicit flags would report a geometry the transport is not running, which is exactly the
        // failure the harnesses gate on this string to catch.
        var g = _set.Options;
        ResolvedGeometry = $"page={g.BufferPageSize} recvbuf={g.ReceiveBufferSize} "
            + $"writebufs={g.WriteBuffersPerShard} oobwritebufs={g.OutOfBandWriteBuffersPerShard} "
            + $"readpages={g.BufferPagesPerShard}";
        _set.Listen(EndPoint);
        Console.WriteLine($"[socketset transport] {_config.Describe()} [resolved] {ResolvedGeometry}");
    }

    // --- SocketSet callbacks (loop thread) → Kestrel bridge ---

    internal static int Accepts, Closes, ClosedEmpty, WriteFail, SendFalse;

    /// <summary>The buffer geometry the backend actually resolved, for <c>/config</c>. Null on the
    /// vanilla-Kestrel leg, which has no SocketSet and therefore no geometry to report.</summary>
    internal static string? ResolvedGeometry;

    /// <summary>True when accepted connections should hand Kestrel's own pipes to the transport.</summary>
    internal bool ByoPipe => _config.ByoPipe;

    /// <summary>Build the Kestrel-side connection. Split from <see cref="PublishConnection"/> so the
    /// caller can attach the pipe in between: <c>AcceptContext</c> is only visible to a SocketSet
    /// subclass, so the UsePipe call has to happen in <c>TransportSet</c>, and it must happen BEFORE the
    /// connection is published or Kestrel could start writing to a pipe nobody is reading yet.</summary>
    private SocketSetConnection CreateConnection(Connection conn)
    {
        // With TLS this fires only after the handshake has completed, so conn.NegotiatedProtocol is
        // already settled by the time the features are built.
        Interlocked.Increment(ref Accepts);
        var c = new SocketSetConnection(conn, _tls is not null, _config.ByoPipe, _config.PipeSegment, _pipePool);
        conn.UserToken = c;
        return c;
    }

    private void PublishConnection(SocketSetConnection c)
    {
        c.Start(); // no-op in BYO mode: SocketSet reads the outbound pipe itself
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
        (_tls as IDisposable)?.Dispose(); // credential handles / SSL_CTXs
        return ValueTask.CompletedTask;
    }

    /// <summary>The SocketSet SocketSet whose per-connection callbacks fan out to the listener. All three
    /// fire on the owning shard's io_uring loop thread (one thread per connection), so they hand off to the
    /// pipes and return without blocking.</summary>
    private sealed class TransportSet(SocketSetOptions options, SocketSetConnectionListener listener) : SocketSet(options)
    {
        protected override void OnAccept(ref AcceptContext ctx)
        {
            var c = listener.CreateConnection(ctx.Connection);
            // BYO: hand Kestrel's OWN pipes to the transport, so SocketSet drives them directly instead of
            // SocketSetConnection copying inbound and pumping outbound. The same two pipes either way -
            // this relocates the bridge rather than removing it, which is the point: it is the vehicle the
            // per-backend zero-copy work needs, and a like-for-like baseline to measure that against.
            // pinned: true is an ASSERTION to the transport that these addresses are stable, so it may
            // skip per-segment GCHandle pinning. Only ever true when the pipes are actually backed by the
            // pinned-block pool - asserting it over a movable pool would hand the kernel memory the GC can
            // move underneath it.
            if (listener.ByoPipe) ctx.UsePipe(c.TransportSide, listener.PipePinned);
            listener.PublishConnection(c);
        }
        protected override void OnReceive(ref ReceiveContext ctx) => listener.OnReceive(ctx.Connection, ctx.Payload);
        protected override void OnClosed(Connection connection) => listener.OnClosed(connection);
    }
}
