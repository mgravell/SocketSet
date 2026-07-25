using System.Buffers;

namespace SocketSets.Tls;

// =====================================================================================
// TLS, in one paragraph, for wiring purposes
// -------------------------------------------------------------------------------------
// TLS is a byte-transform state machine that sits on top of TCP. It has two phases:
//   1. HANDSHAKE — a fixed multi-round-trip exchange that authenticates the peer(s) and
//      agrees on keys. No application data flows yet; both sides just feed each other
//      "handshake records" until it completes.
//   2. DATA — once keyed, the application's plaintext is chopped into "records" (<= 16KB
//      each), each encrypted+authenticated on the way out and decrypted+verified on the
//      way in. A few non-application records still flow during this phase (session
//      tickets, key updates, alerts) — see <see cref="TlsContentType"/>.
// Everything below models exactly that: feed bytes in, get bytes out. No streams, no
// threads, no pull — which is why it fits SocketSet's push loop cleanly (see tls-design).
// =====================================================================================

/// <summary>
/// Which side does the per-record bulk crypto for one direction of a TLS connection. This is the
/// per-direction "mode flip" that lets kTLS be an additive fast-follow rather than a rewrite (design
/// seam #3): the handshake is always userspace, but once keys exist, each direction is independently
/// either handled in-process (<see cref="Transform"/>) or delegated to the kernel/NIC
/// (<see cref="KernelPassthrough"/>).
/// </summary>
public enum TlsCryptoMode
{
    /// <summary>Userspace: this filter encrypts/decrypts each record itself (OpenSSL memory-BIO, or
    /// SChannel/SslStream on Windows). The baseline, and the only mode Windows ever uses.</summary>
    Transform,

    /// <summary>kTLS: the kernel (or a capable NIC) does the bulk symmetric crypto after we hand it the
    /// keys. On this direction the shard sends/receives plaintext directly on the socket; the filter is
    /// consulted only for the handshake and for non-application control records.</summary>
    KernelPassthrough,
}

/// <summary>Progress of the handshake phase. Returned by <see cref="TlsFilter.DriveHandshake"/>.</summary>
public enum TlsHandshakeStatus
{
    /// <summary>The handshake is still in flight: we've emitted whatever we could (into the output
    /// writer) and now need more bytes from the peer before we can continue.</summary>
    NeedMoreData,

    /// <summary>The handshake finished. Keys are live. Switch this connection to the data phase
    /// (<see cref="TlsFilter.ProcessInbound"/> / <see cref="TlsFilter.ProcessOutbound"/>). Any output
    /// written on this call (e.g. the client's final Finished flight) must still be sent.</summary>
    Completed,

    /// <summary>The handshake failed (bad certificate, protocol mismatch, verification failure, …).
    /// The connection must be torn down. Any output written may be a TLS alert worth sending first.</summary>
    Faulted,
}

/// <summary>Outcome of feeding inbound data to <see cref="TlsFilter.ProcessInbound"/> in the data phase.</summary>
public enum TlsInboundStatus
{
    /// <summary>Processed normally. Zero or more plaintext bytes were written to the plaintext writer,
    /// and zero or more transport bytes (a control-record response — e.g. an ack to a KeyUpdate) to the
    /// output writer. Both being empty is legal (e.g. a lone session-ticket record yields no plaintext).</summary>
    Ok,

    /// <summary>The peer sent a graceful close_notify. No more inbound plaintext will follow; begin an
    /// orderly close of this half (any output written is our close_notify reply).</summary>
    PeerClosed,

    /// <summary>A fatal alert or a decrypt/verify failure. Tear the connection down.</summary>
    Faulted,
}

/// <summary>
/// A per-connection TLS engine, shaped for a push I/O loop: you feed it transport bytes and it hands
/// back plaintext (inbound) or ciphertext (outbound), plus any protocol bytes it needs to send as a
/// side effect. It never owns the socket, never blocks, and never pulls — the owning shard loop drives
/// every call on its own thread, which is exactly where the (loop-thread-only) kTLS transition belongs.
///
/// Lifecycle:
///   handshake:  loop feeds received bytes to <see cref="DriveHandshake"/> until it returns
///               <see cref="TlsHandshakeStatus.Completed"/> (emitting handshake bytes to send along the way).
///   data:       inbound  → <see cref="ProcessInbound"/>  (ciphertext → plaintext [+ control replies])
///               outbound → <see cref="ProcessOutbound"/> (app plaintext → ciphertext)
///   close:      <see cref="Shutdown"/> emits a close_notify.
///
/// Buffering contract (design seam #5): every method CONSUMES ALL of its input. Transport records can
/// straddle socket reads, so the filter buffers a partial record INTERNALLY and surfaces plaintext only
/// once a whole record has arrived. The caller may therefore reuse/return its input buffer immediately
/// after the call. Outputs are pushed to <see cref="IBufferWriter{Byte}"/>s the caller supplies (in
/// practice: the plaintext writer is a pooled scratch buffer whose filled span the shard then hands to
/// <c>OnReceive</c>; the output/ciphertext writer is the <see cref="Connection"/> itself, which is an
/// <see cref="IBufferWriter{Byte}"/>, or a pooled send buffer).
///
/// Threading: NOT thread-safe. All calls happen on the owning shard's loop thread — the same
/// single-owner discipline as the slot table. The one cross-thread touch a connection has (its
/// out-of-band <c>Connection.Flush</c>) must be routed so the actual <see cref="ProcessOutbound"/>
/// still runs on the loop, exactly as flushed writes are already marshaled today.
/// </summary>
public abstract class TlsFilter : IDisposable
{
    /// <summary>True once <see cref="DriveHandshake"/> has returned <see cref="TlsHandshakeStatus.Completed"/>.
    /// Before this, only <see cref="DriveHandshake"/> is valid; after, only the data-phase methods.</summary>
    public bool HandshakeComplete { get; protected set; }

    /// <summary>Who decrypts inbound records. <see cref="TlsCryptoMode.Transform"/> until (and unless) the
    /// filter enables kTLS RX during the handshake transition. Baseline + Windows: always Transform.</summary>
    public TlsCryptoMode InboundCrypto { get; protected set; } = TlsCryptoMode.Transform;

    /// <summary>Who encrypts outbound records. <see cref="TlsCryptoMode.Transform"/> until (and unless) the
    /// filter enables kTLS TX during the handshake transition. TX and RX flip independently (TX is the
    /// easy half and may land first).</summary>
    public TlsCryptoMode OutboundCrypto { get; protected set; } = TlsCryptoMode.Transform;

    /// <summary>Design seam #2: does the inbound path need per-record content-type information? True only
    /// when inbound is kTLS-passthrough — the kernel has already decrypted, so the shard must use a
    /// control-message-aware receive (io_uring RECVMSG + <c>TLS_GET_RECORD_TYPE</c> cmsg) to tell an
    /// application-data record from a control record. In the baseline this is always false, so the shard
    /// keeps its plain multishot RECV; the fast-follow just flips this and adds the RECVMSG arm.</summary>
    public bool NeedsRecordType => InboundCrypto == TlsCryptoMode.KernelPassthrough;

    /// <summary>
    /// Drive the handshake one step. Runs on the loop thread. Feed any bytes just received from the peer
    /// (empty on the very first call for a client, which must speak first with a ClientHello); the filter
    /// consumes them and writes any bytes-to-send into <paramref name="output"/>.
    ///
    /// <paramref name="transportHandle"/> is the raw socket handle (fd on Linux, SOCKET on Windows) — the
    /// filter needs it ONLY to enable kTLS at the moment keys become live (design seam #6: the kTLS
    /// setsockopt must run on the fd-owning loop thread, which is exactly here). A userspace-transform
    /// filter ignores it; a kTLS filter, on completing the handshake, calls setsockopt(TLS) and flips
    /// <see cref="OutboundCrypto"/>/<see cref="InboundCrypto"/> to <see cref="TlsCryptoMode.KernelPassthrough"/>
    /// before returning <see cref="TlsHandshakeStatus.Completed"/>.
    ///
    /// After this returns Completed, call <see cref="ProcessInbound"/> once (even with an empty input) to
    /// drain any application data that arrived in the same segment as the peer's final handshake flight.
    /// </summary>
    public abstract TlsHandshakeStatus DriveHandshake(
        ReadOnlySpan<byte> input, nint transportHandle, IBufferWriter<byte> output);

    /// <summary>
    /// Data phase, inbound. Runs on the loop thread. This is the unified dispatch point (design seam #1:
    /// a receive can emit sends):
    ///  - <see cref="TlsCryptoMode.Transform"/> inbound: pass raw ciphertext; <paramref name="contentType"/>
    ///    is <see cref="TlsContentType.Ciphertext"/> (the filter parses records itself). It decrypts
    ///    application data into <paramref name="plaintext"/> and services control records internally,
    ///    writing any protocol reply into <paramref name="output"/>.
    ///  - <see cref="TlsCryptoMode.KernelPassthrough"/> inbound: the shard only calls this for CONTROL
    ///    records (application data was delivered straight from the kernel and never reaches the filter),
    ///    passing the kernel-reported <paramref name="contentType"/>. The filter updates its protocol
    ///    state (e.g. processes a KeyUpdate / session ticket) and writes any reply to <paramref name="output"/>.
    /// </summary>
    public abstract TlsInboundStatus ProcessInbound(
        ReadOnlySpan<byte> input, TlsContentType contentType,
        IBufferWriter<byte> plaintext, IBufferWriter<byte> output);

    /// <summary>
    /// Data phase, outbound. Runs on the loop thread. Encrypt application <paramref name="plaintext"/> into
    /// transport records written to <paramref name="output"/>. Only called when
    /// <see cref="OutboundCrypto"/> is <see cref="TlsCryptoMode.Transform"/>; in kernel-passthrough TX the
    /// shard sends the plaintext directly on the socket and never calls this.
    /// </summary>
    public abstract void ProcessOutbound(ReadOnlySpan<byte> plaintext, IBufferWriter<byte> output);

    /// <summary>Emit a close_notify into <paramref name="output"/> for an orderly TLS shutdown (sent before
    /// the socket is closed). Best-effort; a filter with nothing to say writes nothing.</summary>
    public virtual void Shutdown(IBufferWriter<byte> output) { }

    public virtual void Dispose() { }
}
