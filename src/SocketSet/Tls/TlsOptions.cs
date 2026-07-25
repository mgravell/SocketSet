namespace SocketSets.Tls;

/// <summary>
/// Per-connection configuration for an outbound (client) TLS handshake. Kept deliberately small for now;
/// the fields that exist are the ones with correctness/security weight, and the TODOs mark the knobs a
/// real implementation will need.
/// </summary>
public sealed class TlsClientOptions
{
    /// <summary>
    /// The server name to send as SNI AND to validate the presented certificate against. SECURITY-CRITICAL:
    /// with a raw TLS engine, hostname verification is OUR job — a null/blank host that disables the check
    /// is the classic silent man-in-the-middle hole (a valid cert for any host would be accepted). For a
    /// client dialling a real off-box Redis this must be set and enforced. Leave the "skip verification"
    /// escape hatch (if one is ever added) loud and opt-in.
    /// </summary>
    public string? TargetHost { get; set; }

    /// <summary>Allow this connection to use kTLS if the provider supports it. Default true; set false to
    /// force the userspace transform (e.g. to A/B the paths, or work around a driver).</summary>
    public bool AllowKernelOffload { get; set; } = true;

    // TODO: CA trust source (system store vs explicit roots), client certificate for mutual TLS,
    // ALPN protocol list, min/max protocol version, session-resumption/ticket cache.
}

/// <summary>
/// Per-listener configuration for an inbound (server) TLS handshake.
/// </summary>
public sealed class TlsServerOptions
{
    /// <summary>Allow accepted connections to use kTLS if the provider supports it. Default true.</summary>
    public bool AllowKernelOffload { get; set; } = true;

    // TODO: server certificate + private key (the one non-optional piece for a real server), SNI-based
    // certificate selection, ALPN, optional client-certificate request (mutual TLS), version floor/ceiling.
}
