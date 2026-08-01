using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocketSets.AspNet;

/// <summary>Registers the SocketSet transport with Kestrel.</summary>
public static class SocketSetBuilderExtensions
{
    /// <summary>
    /// Replace Kestrel's socket transport with SocketSet (io_uring / epoll / IOCP / RIO / managed). When a
    /// TLS provider is set on the options, TLS is terminated in the transport (below Kestrel), so Kestrel's
    /// HTTP stack sees plaintext and never constructs an SslStream.
    /// </summary>
    /// <remarks>
    /// The services are manipulated directly (not via a deferred <c>ConfigureServices</c> callback) so the
    /// replacement runs AFTER the host's default socket-transport registration — this is what guarantees the
    /// SocketSet factory wins. Resolve <see cref="SocketSetTransportMetrics"/> from DI for diagnostics.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.UseSocketSet(o => { o.Shards = 4; o.Mode = SocketSetBridgeMode.Byo; });
    /// </code>
    /// </example>
    public static WebApplicationBuilder UseSocketSet(this WebApplicationBuilder builder, Action<SocketSetTransportOptions>? configure = null)
    {
        var options = new SocketSetTransportOptions();
        configure?.Invoke(options);

        // Replace the host's built-in socket transport factory (registered by CreateBuilder's Kestrel setup).
        builder.Services.RemoveAll<IConnectionListenerFactory>();
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton<SocketSetTransportMetrics>();
        builder.Services.AddSingleton<IConnectionListenerFactory, SocketSetTransportFactory>();
        return builder;
    }
}
