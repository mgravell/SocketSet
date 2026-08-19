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
/// options and the tunnel lazily builds one engine for its own lifetime.
///
/// ONE ENGINE IS NO LONGER ONE TLS POSTURE (this used to say mixed targets wanted two tunnels). TLS is
/// now the transport's job end-to-end, and the seam hands us the configured intent per dial, so the
/// engine supplies the PROVIDER (trust roots, version floor - construction-time decisions) while each
/// connection carries its own decision: on or off, and which name to verify. See <see cref="TlsIntent"/>
/// for what maps and what is refused rather than dropped.
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

    /// <summary>Supply the WHOLE transport for one SE.Redis connection: a SocketSet dial on the shared
    /// engine, TLS included. <paramref name="tls"/> is the configuration's TLS intent, which this
    /// transport owns outright - the library applies none of it on our behalf, and checks
    /// <see cref="DuplexTransport.IsEncrypted"/> afterwards to make sure we did.</summary>
    public override async ValueTask<DuplexTransport?> ConnectTransportAsync(
        EndPoint endpoint, ConnectionType connectionType, TlsOptions tls, CancellationToken cancellationToken)
    {
        // Translate (and refuse the unmappable) BEFORE dialling, so a configuration we cannot honour
        // fails as a configuration error rather than as a connection that quietly is not what it says.
        var intent = TlsIntent.From(tls, endpoint, Engine);
        return await SocketSetClientTransport.ConnectAsync(endpoint, Engine, intent, cancellationToken)
            .ConfigureAwait(false);
    }
}
