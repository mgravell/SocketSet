using System.Net;
using RESPite.Transports;
using SocketSets;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;

namespace SocketSets.StackExchangeRedis;

/// <summary>
/// The Tunnel that swaps SE.Redis's IO core for SocketSet — and the ANCHOR for everything that shares
/// the engine: every <see cref="ConnectTransportAsync"/> dial lands on the ONE
/// <see cref="SocketSetClientEngine"/> this instance holds, so total loop-thread count is the engine's
/// shard count regardless of how many endpoints/connections the multiplexer opens. (An advanced Tunnel
/// can do whatever it likes; this one takes in a socket-set.)
///
/// Lifetime: pass an engine you own (shareable across tunnels/multiplexers; you dispose it), or pass
/// options and the tunnel lazily builds one engine for its own lifetime. One engine = one TLS posture
/// (the provider + TlsMode live on the engine's options); mixed TLS/plaintext targets want two tunnels.
/// </summary>
public sealed class SocketSetTunnel : Tunnel
{
    private readonly object _lock = new();
    private SocketSetClientEngine? _engine;
    private readonly SocketSetOptions? _options; // deferred engine construction, tunnel-owned

    /// <summary>Anchor on <paramref name="engine"/>, which the CALLER owns (and may share with other
    /// tunnels or multiplexers; dispose it after everything using it is done).</summary>
    public SocketSetTunnel(SocketSetClientEngine engine) => _engine = engine;

    /// <summary>Build one engine from <paramref name="options"/> on first dial, owned by this tunnel
    /// for its lifetime.</summary>
    public SocketSetTunnel(SocketSetOptions options) => _options = options;

    private SocketSetClientEngine Engine
    {
        get
        {
            if (_engine is { } e) return e;
            lock (_lock)
            {
                return _engine ??= new SocketSetClientEngine(_options ?? new SocketSetOptions());
            }
        }
    }

    public override async ValueTask<DuplexTransport?> ConnectTransportAsync(
        EndPoint endpoint, ConnectionType connectionType, CancellationToken cancellationToken)
        => await SocketSetClientTransport.ConnectAsync(endpoint, Engine, cancellationToken).ConfigureAwait(false);
}
