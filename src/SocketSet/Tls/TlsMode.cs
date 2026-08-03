namespace SocketSets;

/// <summary>
/// Which connection DIRECTIONS the configured <see cref="SocketSetOptions.Tls"/> provider applies to.
/// Default is <see cref="Both"/> — the pre-existing behaviour, where setting a provider encrypts every
/// connection the set makes or accepts.
///
/// The asymmetric modes exist for proxy shapes, where one instance owns both halves of the flow:
/// <see cref="Connect"/> is the TLS-ORIGINATING proxy (plaintext downstream accepts, TLS upstream — the
/// sidecar dialing a remote TLS server), and <see cref="Accept"/> is the TLS-TERMINATING one (TLS
/// downstream, plaintext upstream). Per-direction rather than per-connection on purpose: it covers both
/// real deployments without per-connection machinery, and a direction that is OFF behaves exactly as if
/// no provider were configured (including capability probes — a kTLS probe for a direction that will
/// never handshake is work and log noise for nothing).
/// </summary>
[Flags]
public enum TlsMode
{
    /// <summary>TLS on accepted (inbound) connections.</summary>
    Accept = 1,

    /// <summary>TLS on dialed (outbound) connections.</summary>
    Connect = 2,

    /// <summary>TLS in both directions — the default.</summary>
    Both = Accept | Connect,
}
