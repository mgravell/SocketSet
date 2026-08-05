namespace SocketSets.Tls;

/// <summary>
/// Minimum TLS protocol version a provider will negotiate. The default is <see cref="Tls13"/>: TLS 1.3 is
/// universal on modern peers, has no renegotiation, and mandates forward secrecy, so the whole TLS 1.2
/// surface (renegotiation DoS, CBC/RSA-kex ciphers, downgrade) is opt-in rather than opt-out here. Set
/// <see cref="Tls12"/> to interoperate with a peer that cannot do 1.3.
/// </summary>
public enum TlsProtocol
{
    /// <summary>Allow TLS 1.2 and 1.3 (1.2 floor). For legacy interop only.</summary>
    Tls12,
    /// <summary>TLS 1.3 only (the default). Rejects any peer that cannot do 1.3.</summary>
    Tls13,
}

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

    /// <summary>
    /// What to ANNOUNCE as SNI, when that should differ from what is VERIFIED. Null (the default) means
    /// "derive it from <see cref="TargetHost"/>", which is the behaviour that has always applied and is
    /// what almost everyone wants.
    ///
    /// WHY THE TWO CAME APART (Marc, 2026-08-04). <see cref="TargetHost"/> drove both halves, so
    /// <c>"*"</c> meant "send no SNI" AND "run no name check" as a single indivisible choice — which made
    /// <em>"do not tell the server who I expect, but DO check what it presents"</em> inexpressible. That
    /// is a reasonable posture: SNI travels in the clear in the ClientHello, so suppressing it withholds
    /// the destination from a passive observer, and none of that is a reason to stop verifying the
    /// certificate. Conflating the two meant reaching for privacy silently bought you a downgrade.
    ///
    /// VALUES:
    ///  - <c>null</c> — derive from <see cref="TargetHost"/>: a DNS name is announced, an IP literal is
    ///    not (RFC 6066 §3 forbids it), and <see cref="TlsClientAuthenticateContext.AnyHost"/> announces
    ///    nothing. Unchanged from before this option existed.
    ///  - <see cref="TlsClientAuthenticateContext.AnyHost"/> — announce NOTHING, while
    ///    <see cref="TargetHost"/> continues to be verified. This is the case the split exists for.
    ///  - any other name — announce THAT, and still verify <see cref="TargetHost"/>. For a peer reached
    ///    through a front or a proxy, where the name that routes and the name on the certificate differ.
    ///
    /// An IP literal here is REFUSED rather than sent: RFC 6066 forbids an address in <c>server_name</c>,
    /// and some servers reject the handshake outright. Deriving suppresses it silently because the caller
    /// did not ask for it; asking for it explicitly is a mistake worth reporting.
    /// </summary>
    public string? ServerNameIndication { get; set; }

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
    // session-resumption/ticket cache. (Min protocol version is set on the provider — see
    // OpenSslTlsProvider's minProtocol parameter, TlsProtocol.Tls13 by default.)
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
    // certificate selection, optional client-certificate request (mutual TLS). (Version floor is set on the
    // provider — see OpenSslTlsProvider's minProtocol parameter, TlsProtocol.Tls13 by default.)
}
