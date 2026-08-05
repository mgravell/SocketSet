namespace SocketSets.Tls;

/// <summary>
/// Asked, per outbound connection, on the owning loop thread, immediately before the handshake would
/// start: <em>should this connection use TLS, and if so, how?</em> See
/// <see cref="SocketSet.OnClientAuthenticate"/>.
///
/// WHY THIS EXISTS RATHER THAN MORE PARAMETERS ON <c>Connect</c> (Marc, 2026-08-04). TLS moved to
/// per-connection granularity on 2026-08-03, but only the PROVIDER did: the options object carrying
/// <see cref="TargetHost"/> stayed on the engine, and every shard read the engine-level copy. That was
/// not just untidy, it was unfixable at the call site for the case that matters most. A
/// <c>SocketSetTunnel</c> deliberately funnels MANY endpoints through ONE engine (the anchor shape), so
/// there is no moment at which engine-level options could name the host being dialled, and a
/// <c>Connect</c> overload only helps a caller who knows the host at dial time.
///
/// Asking the engine per connection fixes that: the callback runs when the connection exists, so it can
/// key off <see cref="Connection.UserToken"/> (for the tunnel, that IS the transport, which knows its
/// own endpoint) or off any state the engine holds. It is also lazy — the provider need not exist at
/// dial time — and it collapses "whether" and "how" into one decision point instead of spreading them
/// across a constructor, an options object and a call parameter.
/// </summary>
public ref struct TlsClientAuthenticateContext
{
    internal TlsClientAuthenticateContext(Connection connection, TlsProvider? provider, TlsClientOptions defaults)
    {
        Connection = connection;
        Provider = provider;
        TargetHost = defaults.TargetHost;
        ServerNameIndication = defaults.ServerNameIndication;
        AlpnProtocols = defaults.AlpnProtocols;
        AllowKernelOffload = defaults.AllowKernelOffload;
    }

    /// <summary>The connection about to be secured. <see cref="Connection.UserToken"/> is already
    /// seeded, and is the intended routing key: it is how one engine serving many endpoints tells them
    /// apart.</summary>
    public Connection Connection { get; }

    /// <summary>The engine that performs the handshake. Seeded from the engine-level provider; must be
    /// non-null if the callback returns true.</summary>
    public TlsProvider? Provider { get; set; }

    /// <summary>
    /// REQUIRED when the callback returns true. The name sent as SNI and verified against the
    /// certificate the peer presents.
    ///
    /// There is no "unset" any more, and that is the point. Null, empty or whitespace is REFUSED rather
    /// than quietly meaning "skip the name check", which is what it meant until 2026-08-04 and is the
    /// classic silent man-in-the-middle hole. To genuinely not check a name, say so:
    /// <see cref="AnyHost"/>.
    ///
    /// An IP literal is handled as an IP: it is NOT sent as SNI (RFC 6066 forbids that) and it is
    /// verified against the certificate's iPAddress SANs rather than its DNS names, which is the
    /// difference between working and mysteriously failing against a certificate that names the address.
    /// </summary>
    public string? TargetHost { get; set; }

    /// <summary>What to ANNOUNCE as SNI when that must differ from what is VERIFIED; null (the default)
    /// derives it from <see cref="TargetHost"/>, exactly as before this existed. Set it to
    /// <see cref="AnyHost"/> to announce nothing while still verifying <see cref="TargetHost"/> — the
    /// posture that was inexpressible while one field drove both. See
    /// <see cref="TlsClientOptions.ServerNameIndication"/> for the full rules.</summary>
    public string? ServerNameIndication { get; set; }

    /// <summary>ALPN ids to offer, in preference order; null or empty sends no ALPN extension.</summary>
    public IReadOnlyList<string>? AlpnProtocols { get; set; }

    /// <summary>Allow kTLS for this connection if the provider and backend support it.</summary>
    public bool AllowKernelOffload { get; set; }

    /// <summary>
    /// The one value <see cref="TargetHost"/> accepts that means "do not check the name at all": a
    /// deliberate, greppable, loud opt-out.
    ///
    /// <c>"*"</c> cannot collide with a real target. It is not a legal DNS label (the LDH charset
    /// excludes it), it is not an IP literal, and it is not legal as an SNI <c>server_name</c>. A
    /// wildcard appears in certificates as a PRESENTED identifier (<c>*.example.com</c> in a SAN); it is
    /// never the name you dial. So a host of <c>"*"</c> is unambiguous in a way that null never was:
    /// null reads as an oversight, this reads as a decision.
    ///
    /// It still turns off the check that stops an attacker with any CA-valid certificate impersonating
    /// your peer, so a connection using it reports as unverified in
    /// <see cref="SocketSet.ToString"/>.
    /// </summary>
    public const string AnyHost = "*";

    /// <summary>
    /// THE single rule turning (<paramref name="targetHost"/>, <paramref name="sni"/>) into the name to
    /// actually put in the <c>server_name</c> extension, or null for "send none". Lives here rather than
    /// in either provider because SChannel and OpenSSL each implement the announce half separately, and
    /// two copies of a security-relevant default is how they drift — which is the whole reason the D1
    /// follow-up existed in the first place.
    ///
    /// <paramref name="targetHost"/> is assumed already validated (non-blank; the engine refuses
    /// otherwise). Returns null when nothing should be announced.
    /// </summary>
    internal static string? ResolveSni(string? targetHost, string? sni)
    {
        if (sni is null)
        {
            // Derive, exactly as before this option existed: no SNI for the explicit opt-out, and none
            // for an address literal (RFC 6066 forbids one in server_name).
            if (targetHost == AnyHost) return null;
            return System.Net.IPAddress.TryParse(targetHost, out _) ? null : targetHost;
        }

        if (sni == AnyHost || sni.Length == 0) return null; // announce nothing, but keep verifying

        // Explicitly asking to announce an address is a mistake worth reporting rather than silently
        // dropping: deriving suppresses it because the caller did not ask, but this caller did.
        if (System.Net.IPAddress.TryParse(sni, out _))
            throw new ArgumentException(
                $"ServerNameIndication '{sni}' is an IP literal. RFC 6066 forbids an address in the "
                + $"server_name extension, and some servers reject the handshake. Use \"{AnyHost}\" to "
                + "announce nothing.", nameof(sni));

        return sni;
    }
}

/// <summary>
/// The inbound twin of <see cref="TlsClientAuthenticateContext"/>: asked per accepted connection,
/// before the handshake, on the owning loop thread. See <see cref="SocketSet.OnServerAuthenticate"/>.
///
/// There is no TargetHost here: a server does not choose a name, it is TOLD one by the client's SNI —
/// and that arrives DURING the handshake, after this callback has already returned. Selecting a
/// certificate by SNI therefore cannot be done here; it needs a real SNI callback inside the provider
/// (<c>SSL_CTX_set_tlsext_servername_callback</c> and the SChannel equivalent), which is still open.
/// </summary>
public ref struct TlsServerAuthenticateContext
{
    internal TlsServerAuthenticateContext(Connection connection, TlsProvider? provider, TlsServerOptions defaults)
    {
        Connection = connection;
        Provider = provider;
        AlpnProtocols = defaults.AlpnProtocols;
        AllowKernelOffload = defaults.AllowKernelOffload;
    }

    /// <summary>The connection about to be secured; <see cref="Connection.UserToken"/> is seeded from
    /// the listener that accepted it, so it is how one engine tells its listeners apart.</summary>
    public Connection Connection { get; }

    /// <summary>The engine that performs the handshake; must be non-null if the callback returns true.</summary>
    public TlsProvider? Provider { get; set; }

    /// <summary>ALPN ids this server supports, in preference order.</summary>
    public IReadOnlyList<string>? AlpnProtocols { get; set; }

    /// <summary>Allow kTLS for this connection if the provider and backend support it.</summary>
    public bool AllowKernelOffload { get; set; }
}

/// <summary>
/// What the engine decided for one connection. <see cref="Refused"/> is a THIRD state, distinct from
/// "no TLS": the callback threw, or returned true without a usable configuration. Those must drop the
/// connection rather than fall back to plaintext, because falling back is the failure mode that turns a
/// misconfiguration into a silent downgrade.
/// </summary>
internal readonly struct TlsResolution
{
    private TlsResolution(TlsProvider? provider, TlsClientOptions? client, TlsServerOptions? server,
                          bool allowKernelOffload, bool refused)
    {
        Provider = provider;
        Client = client;
        Server = server;
        AllowKernelOffload = allowKernelOffload;
        Refused = refused;
    }

    public TlsProvider? Provider { get; }
    public TlsClientOptions? Client { get; }
    public TlsServerOptions? Server { get; }
    public bool AllowKernelOffload { get; }
    public bool Refused { get; }

    public bool Enabled => Provider is not null;

    public static TlsResolution None => default;
    public static TlsResolution Deny => new(null, null, null, false, refused: true);

    public static TlsResolution ForClient(TlsProvider p, TlsClientOptions o, bool ktls)
        => new(p, o, null, ktls, false);

    public static TlsResolution ForServer(TlsProvider p, TlsServerOptions o, bool ktls)
        => new(p, null, o, ktls, false);
}
