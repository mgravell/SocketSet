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

    /// <summary>
    /// ALPN protocol ids to OFFER, in the client's preference order (e.g. <c>["h2", "http/1.1"]</c>). Null
    /// or empty (the default) means the ALPN extension is not sent at all — which is not the same as
    /// offering an empty list, and is what you want when the peer does not speak ALPN.
    ///
    /// The agreed result, if any, is on <see cref="Connection.NegotiatedProtocol"/> by the time OnConnect
    /// fires. A server that speaks ALPN but shares no id with this list fails the handshake (RFC 7301's
    /// <c>no_application_protocol</c> alert) rather than silently falling back to something unexpected.
    /// </summary>
    public IReadOnlyList<string>? AlpnProtocols { get; set; }

    // TODO: CA trust source (system store vs explicit roots), client certificate for mutual TLS,
    // min/max protocol version, session-resumption/ticket cache.
}

/// <summary>
/// Per-listener configuration for an inbound (server) TLS handshake.
/// </summary>
public sealed class TlsServerOptions
{
    /// <summary>Allow accepted connections to use kTLS if the provider supports it. Default true.</summary>
    public bool AllowKernelOffload { get; set; } = true;

    /// <summary>
    /// ALPN protocol ids this server SUPPORTS, in the server's preference order. Null or empty (the
    /// default) means ALPN is not negotiated: a client that offers it gets no reply and falls back to
    /// whatever it does without ALPN.
    ///
    /// Selection is server-preference — the first id here that the client also offered wins — and a client
    /// that offers ALPN with no overlap is rejected rather than served an unagreed protocol. The result is
    /// on <see cref="Connection.NegotiatedProtocol"/> by the time OnAccept fires.
    ///
    /// NOTE for the OpenSSL provider: OpenSSL's ALPN selection callback is per-SSL_CTX, not per-connection,
    /// so one <c>OpenSslTlsProvider</c> serves exactly one server list.
    /// </summary>
    public IReadOnlyList<string>? AlpnProtocols { get; set; }

    // TODO: server certificate + private key (the one non-optional piece for a real server), SNI-based
    // certificate selection, optional client-certificate request (mutual TLS), version floor/ceiling.
}
