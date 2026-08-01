using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
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
    /// <example>
    /// <code>
    /// builder.WebHost.UseSocketSet(o =>
    /// {
    ///     o.Shards = 4;
    ///     o.Mode = SocketSetBridgeMode.Byo;
    /// });
    /// </code>
    /// </example>
    public static IWebHostBuilder UseSocketSet(this IWebHostBuilder hostBuilder, Action<SocketSetTransportOptions>? configure = null)
    {
        var options = new SocketSetTransportOptions();
        configure?.Invoke(options);

        return hostBuilder.ConfigureServices(services =>
        {
            // Replace Kestrel's built-in socket transport factory.
            services.RemoveAll<IConnectionListenerFactory>();
            services.AddSingleton(options);
            services.TryAddSingleton<SocketSetTransportMetrics>();
            services.AddSingleton<IConnectionListenerFactory, SocketSetTransportFactory>();
        });
    }
}
