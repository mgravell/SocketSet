using System.Net;
using RESPite.Transports;
using SocketSets;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;

namespace SocketSets.StackExchangeRedis;

/// <summary>
/// The Tunnel that swaps SE.Redis's IO core for SocketSet: <see cref="ConnectTransportAsync"/> returns a
/// <see cref="SocketSetClientTransport"/> and the multiplexer never touches a managed socket or a
/// <see cref="System.IO.Stream"/>. Everything else about the Tunnel contract is left at its defaults —
/// this is the "one level deeper than BeforeAuthenticateAsync" seam, hijacking the connect the same way
/// the in-proc server tunnel always has.
/// </summary>
public sealed class SocketSetTunnel : Tunnel
{
    private readonly Func<EndPoint, ConnectionType, SocketSetOptions> _options;

    /// <summary>Use <paramref name="options"/> as a per-connection factory (each dial constructs its own
    /// engine; TLS, backend and sharding all come from the returned options).</summary>
    public SocketSetTunnel(Func<EndPoint, ConnectionType, SocketSetOptions> options) => _options = options;

    /// <summary>Use the same <paramref name="options"/> for every connection this tunnel dials.</summary>
    public SocketSetTunnel(SocketSetOptions options) : this((_, _) => options) { }

    public override async ValueTask<DuplexTransport?> ConnectTransportAsync(
        EndPoint endpoint, ConnectionType connectionType, CancellationToken cancellationToken)
        => await SocketSetClientTransport.ConnectAsync(endpoint, _options(endpoint, connectionType), cancellationToken).ConfigureAwait(false);
}
