namespace SocketSets.Tls;

/// <summary>
/// The kind of a TLS record. The non-zero values are the actual TLS "content type" byte from the record
/// header (RFC 8446 §5.1) — the same value the Linux kernel hands back via the <c>TLS_GET_RECORD_TYPE</c>
/// control message when kTLS receive-offload is on. <see cref="Ciphertext"/> is our own sentinel for the
/// userspace-transform case, where the filter is handed raw (still-encrypted) bytes and parses the record
/// framing itself, so no type is known up front.
///
/// Why the shard cares: with kTLS receive-offload the kernel decrypts each record and tells us its type.
/// Application data is delivered straight to the app; but a <see cref="Handshake"/> (TLS 1.3 session
/// ticket / KeyUpdate), <see cref="Alert"/>, or <see cref="ChangeCipherSpec"/> record must be routed back
/// into the <see cref="TlsFilter"/> so the protocol state stays correct. Distinguishing them is exactly
/// why kTLS-RX needs a control-message-aware receive (design seam #2).
/// </summary>
public enum TlsContentType : byte
{
    /// <summary>Not a decrypted record: raw, still-encrypted transport bytes for the userspace transform
    /// path to parse. Used whenever inbound crypto is <see cref="TlsCryptoMode.Transform"/>.</summary>
    Ciphertext = 0,

    /// <summary>change_cipher_spec (20). Legacy/compatibility record; largely inert under TLS 1.3.</summary>
    ChangeCipherSpec = 20,

    /// <summary>alert (21). A warning or fatal condition (including the close_notify that signals an
    /// orderly shutdown).</summary>
    Alert = 21,

    /// <summary>handshake (22). During the data phase this is post-handshake traffic — TLS 1.3
    /// NewSessionTicket (routine for a client) and KeyUpdate — which must reach the engine.</summary>
    Handshake = 22,

    /// <summary>application_data (23). The actual payload. Under kTLS receive-offload these bypass the
    /// filter entirely and go straight to the app.</summary>
    ApplicationData = 23,
}
