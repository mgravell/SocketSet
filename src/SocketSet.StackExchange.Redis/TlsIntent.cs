using System.Net;
using SocketSets.Tls;
using StackExchange.Redis.Configuration;

namespace SocketSets.StackExchangeRedis;

/// <summary>
/// What SE.Redis's configuration asks of ONE dial, translated into what SocketSet's per-connection TLS
/// callback needs — and, just as importantly, a refusal for every part of that configuration this
/// transport cannot carry out.
///
/// WHY THIS EXISTS. When the transport seam was first built, TLS could not travel with it: a transport
/// tunnel plus <c>config.Ssl</c> threw, and the posture came from the ENGINE's options instead. The
/// merged API moved TLS to the transport's side of the line — the tunnel owns connect and TLS
/// end-to-end, and is handed a <see cref="TlsOptions"/> view per dial precisely because "a transport
/// cannot honour an intent it cannot see". That closes the anchor shape's old wart from both ends: one
/// engine can now serve MANY endpoints at DIFFERENT postures, because the decision arrives per
/// connection rather than sitting on the engine, and it lands in
/// <see cref="TlsClientAuthenticateContext"/>, whose whole reason for existing was this case.
///
/// THE HALF THAT DOES NOT MAP IS REFUSED, NOT IGNORED. SE.Redis's TLS configuration is expressed in
/// <c>SslStream</c> terms — a validation callback, a client-certificate selector, a revocation flag, a
/// protocol mask — and SocketSet's providers take those decisions at PROVIDER construction (trust
/// roots, version floor) rather than per connection. Silently dropping any of them would hand back a
/// connection that looks configured and is not, which is the failure mode this repo keeps finding.
/// Every one of them therefore throws with the provider-level knob that DOES express it.
/// </summary>
internal readonly struct TlsIntent
{
    private TlsIntent(bool specified, bool enabled, string? targetHost)
    {
        Specified = specified;
        Enabled = enabled;
        TargetHost = targetHost;
    }

    /// <summary>Did a caller state an intent? False for the direct
    /// <see cref="SocketSetClientTransport.ConnectAsync(EndPoint, SocketSetClientEngine, CancellationToken)"/>
    /// entry points, which keep the pre-existing behaviour: the engine's own options decide.</summary>
    public bool Specified { get; }

    /// <summary>TLS for this connection. Authoritative when <see cref="Specified"/>: an engine carrying a
    /// provider still dials PLAINTEXT for a connection whose configuration did not ask for TLS.</summary>
    public bool Enabled { get; }

    /// <summary>The name to verify the certificate against (and, unless it is an address, to announce as
    /// SNI): <c>SslHost</c> when set, else the host portion of the endpoint — the same fallback the
    /// library's own TLS path uses, so the two agree by construction.</summary>
    public string? TargetHost { get; }

    /// <summary>No intent stated; the engine's configuration decides, exactly as before.</summary>
    public static TlsIntent EngineDefault => default;

    /// <summary>Translate one dial's configuration, or throw naming what cannot be carried.</summary>
    public static TlsIntent From(TlsOptions tls, EndPoint endpoint, SocketSetClientEngine engine)
    {
        if (!tls.IsEnabled) return new(specified: true, enabled: false, targetHost: null);

        // The provider is the engine's: it carries trust roots and the version floor, both of which are
        // construction-time decisions there. A dial that needs TLS from an engine that has none is a
        // configuration error worth naming here, rather than a plaintext connection that the caller's own
        // IsEncrypted check will refuse a moment later with less to say about why.
        if (engine.Options.Tls is null)
            throw new InvalidOperationException(
                "TLS was requested for this connection (Ssl=true), but the SocketSet engine behind this "
                + "tunnel has no TlsProvider. Set SocketSetOptions.Tls (e.g. new OpenSslTlsProvider(...) "
                + "or new SChannelTlsProvider(...)) on the options the tunnel was built with.");

        var host = tls.ResolveHost(endpoint);
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException(
                "TLS was requested for this connection, but no host could be resolved to verify the "
                + $"certificate against ({endpoint}). Set ConfigurationOptions.SslHost, or "
                + $"\"{TlsClientAuthenticateContext.AnyHost}\" to state explicitly that no name check is "
                + "wanted.");

        // Everything below is a setting we would otherwise silently drop.
        if (tls.CertificateValidationCallback is not null)
            throw new NotSupportedException(
                "ConfigurationOptions.CertificateValidationCallback (or the SERedis_IssuerCertPath "
                + "environment fallback) cannot be honoured by this transport: it is an SslStream "
                + "callback, and chain validation here happens inside the TLS provider. Express the same "
                + "trust decision when constructing the provider — OpenSslTlsProvider(trustCertPem: ...) "
                + "pins an issuer, and SChannelTlsProvider takes the equivalent.");
        if (tls.CertificateSelectionCallback is not null)
            throw new NotSupportedException(
                "ConfigurationOptions.CertificateSelectionCallback (client certificates / mutual TLS) is "
                + "not supported by this transport yet: TlsClientOptions has no client-certificate field. "
                + "Use the classic socket path for a mutual-TLS endpoint.");
        // CheckCertificateRevocation is deliberately NOT refused, and it is the one silent gap here.
        // It defaults to TRUE in SE.Redis, and the view exposes only the resolved value -- there is no
        // way to tell "the default came along for the ride" from "this deployment asked for revocation
        // checking". Refusing it would make every TLS dial through this tunnel fail on a default nobody
        // typed (it did, first run). So revocation stays where it is expressible: the provider
        // (SChannelTlsProvider takes an X509RevocationMode). The honest statement is that this flag is
        // not applied per connection, which is recorded in TODO.md rather than left to be rediscovered.
        if (tls.SslProtocols is { } protocols)
            throw new NotSupportedException(
                $"ConfigurationOptions.SslProtocols ({protocols}) cannot be applied per connection here: "
                + "the version floor belongs to the provider (both providers take a TlsProtocol, "
                + "defaulting to TLS 1.3). Set it there and leave this unset — a floor that is configured "
                + "but not applied is exactly what bench/verify-tls-floor exists to catch.");
#if NET
        if (tls.GetSslClientAuthenticationOptions(host) is not null)
            throw new NotSupportedException(
                "ConfigurationOptions.SslClientAuthenticationOptions describes an SslStream handshake, "
                + "which this transport does not perform (there is no SslStream anywhere on this path). "
                + "Configure the equivalent on the TlsProvider.");
#endif

        return new(specified: true, enabled: true, targetHost: host);
    }
}
