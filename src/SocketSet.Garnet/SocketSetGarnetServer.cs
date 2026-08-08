using System.Net;
using Garnet.common;
using Garnet.networking;
using Garnet.server;
using Microsoft.Extensions.Logging;
using SocketSets;

namespace SocketSets.Garnet;

/// <summary>
/// Hosts Garnet on the SocketSet transport: an <see cref="IGarnetServer"/> whose connections are served
/// by SocketSet's shard loops instead of the built-in SocketAsyncEventArgs layer. Hand an instance to
/// <c>GarnetServer(opts, loggerFactory, servers: [...])</c> — the embedding ctor documents "If none is
/// provided, will use a GarnetServerTcp", and this is the something-else.
///
/// Session bookkeeping (Register/AddSession, the <c>activeHandlers</c> dictionary, connection counters)
/// is all inherited from <see cref="GarnetServerBase"/>, and the <see cref="IServerHook"/> flow mirrors
/// <c>GarnetServerTcp</c> byte for byte — the only thing replaced is who moves the bytes.
/// </summary>
public sealed class SocketSetGarnetServer : GarnetServerBase, IServerHook
{
    private sealed class Transport(SocketSetGarnetServer owner, SocketSetOptions options) : SocketSet(options)
    {
        protected override void OnAccept(ref AcceptContext ctx)
        {
            var conn = ctx.Connection;
            var sender = new SocketSetNetworkSender(conn, owner._bufferSettings, owner._pool,
                                                    localByConstruction: owner._unixDomain);
            var handler = new SocketSetGarnetHandler(owner, sender, owner._bufferSettings, owner._pool);
            handler.StartReceive();
            conn.UserToken = handler;
            // The BASE owns the handler dictionary; mirroring GarnetServerTcp's accept path.
            if (!owner.activeHandlers.TryAdd(handler, default))
            {
                handler.CloseFromTransport();
                return;
            }
            owner.IncrementConnectionsReceived();
        }

        protected override void OnReceive(ref ReceiveContext ctx)
        {
            if (ctx.Connection.UserToken is SocketSetGarnetHandler h && !h.Feed(ctx.Payload))
                ctx.Connection.Close();
        }

        protected override void OnClosed(Connection connection)
        {
            if (connection.UserToken is SocketSetGarnetHandler h) owner.DropHandler(h);
        }
    }

    private readonly Transport _transport;
    private readonly NetworkBufferSettings _bufferSettings;
    private readonly LimitedFixedBufferPool _pool;

    /// <summary>
    /// Whether every peer on this server is local BY CONSTRUCTION — i.e. we are listening on AF_UNIX,
    /// which is same-host by definition and has no network form. Decided once at construction from the
    /// listen endpoint, so answering <c>IsLocalConnection</c> for a UDS peer costs nothing per connection
    /// and needs no peer-address plumbing. See REVIEW.md F9 for why the TCP case still cannot be answered.
    /// </summary>
    private readonly bool _unixDomain;

    public SocketSetGarnetServer(EndPoint endpoint, SocketSetOptions options,
                                 int serverBufferSize = 1 << 17, ILogger? logger = null)
        : base(endpoint, serverBufferSize, logger)
    {
        _unixDomain = endpoint is System.Net.Sockets.UnixDomainSocketEndPoint;
        _bufferSettings = new NetworkBufferSettings(serverBufferSize, serverBufferSize);
        _pool = _bufferSettings.CreateBufferPool(ownerType: PoolOwnerType.ServerNetwork, logger: logger);
        _transport = new Transport(this, options);
    }

    public override void Start() => _transport.Listen(EndPoint);

    public override void Close() => _transport.Dispose();

    public override IEnumerable<IMessageConsumer> ActiveConsumers()
    {
        foreach (var kvp in activeHandlers)
        {
            var consumer = kvp.Key.Session;
            if (consumer != null) yield return consumer;
        }
    }

    public override IEnumerable<IClusterSession> ActiveClusterSessions()
    {
        // RespServerSession (and its clusterSession) is internal to Garnet.server, so the enumeration
        // GarnetServerTcp does is unreachable from outside the assembly. The testbed does not run
        // cluster mode; if this library ever hosts a cluster node, this needs an upstream accessor.
        yield break;
    }

    // ---- IServerHook: mirrors GarnetServerTcp.TryCreateMessageConsumer, including the >=4-byte gate
    //      (a shorter first packet must return false so the handler calls back with more bytes) and the
    //      throw-on-missing-provider semantics. ----

    public bool TryCreateMessageConsumer(Span<byte> bytes, INetworkSender networkSender, out IMessageConsumer session)
    {
        session = null!;

        // We need at least 4 bytes to determine the session's wire format.
        if (bytes.Length < 4) return false;

        WireFormat protocol = WireFormat.ASCII;
        if (!GetSessionProviders().TryGetValue(protocol, out var provider))
        {
            var input = System.Text.Encoding.ASCII.GetString(bytes);
            logger?.LogError("Cannot identify wire protocol {bytes}", input);
            throw new Exception($"Unsupported incoming wire format {protocol} {input}");
        }

        if (!AddSession(protocol, ref provider, networkSender, out session))
            throw new Exception("Unable to add session");

        return true;
    }

    public void DisposeMessageConsumer(INetworkHandler session)
    {
        if (session is SocketSetGarnetHandler h) DropHandler(h);
    }

    private void DropHandler(SocketSetGarnetHandler handler)
    {
        if (activeHandlers.TryRemove(handler, out _))
        {
            IncrementConnectionsDisposed();
            try { handler.Session?.Dispose(); } catch { }
            handler.CloseFromTransport();
        }
    }

    public override void Dispose()
    {
        Close();
        foreach (var kvp in activeHandlers)
        {
            if (kvp.Key is SocketSetGarnetHandler h) DropHandler(h);
        }
        base.Dispose();
        _pool.Dispose();
    }
}
