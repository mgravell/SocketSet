namespace SocketSets.Tls;

/// <summary>
/// Creates <see cref="TlsFilter"/> instances — the seam where the platform/implementation choice lives,
/// so nothing above it (the shard loop, the connection) knows or cares whether TLS is backed by OpenSSL
/// memory-BIOs (Linux, the kTLS-capable path), SSPI/SChannel (Windows, in-box, no SslStream involved), or a
/// test double. One provider is configured per <see cref="SocketSet"/> that wants TLS; the shard asks it for a
/// client filter (on <c>Connect</c>) or a server filter (on accept), once per connection.
///
/// A connection is either raw (no filter) or filtered; the provider is the thing that decides "filtered,
/// and here's the engine." The proxy use-case — plaintext UDS on one leg, TLS TCP on the other — falls
/// out naturally: the plaintext leg simply has no filter.
/// </summary>
public abstract class TlsProvider
{
    /// <summary>Create the engine for an outbound (client) connection — e.g. SocketSet dialling a Redis
    /// server over TLS. The filter will speak first (ClientHello) when the loop drives its handshake.</summary>
    public abstract TlsFilter CreateClientFilter(TlsClientOptions options);

    /// <summary>Create the engine for an inbound (server) connection — e.g. terminating TLS for the
    /// aspnetcore server side. The filter waits for the peer's ClientHello.</summary>
    public abstract TlsFilter CreateServerFilter(TlsServerOptions options);

    /// <summary>Whether this provider can hand keys to the kernel/NIC (kTLS) after the handshake. False
    /// for the Windows SChannel provider (Windows has no kTLS) and for any pure-userspace provider; a
    /// filter it produces will then keep both directions in <see cref="TlsCryptoMode.Transform"/>. Even
    /// when true, whether a given connection actually offloads is a per-connection, per-direction
    /// decision the filter makes at handshake completion (cipher support, options, socket capability).</summary>
    public virtual bool SupportsKernelOffload => false;

    /// <summary>Short display name for banners and diagnostics ("openssl", "schannel"); defaults to the
    /// type name for third-party providers.</summary>
    public virtual string Name => GetType().Name;
}
