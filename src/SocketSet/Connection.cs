using System.Buffers;
using SocketSets.Tls;

namespace SocketSets;

/// <summary>
/// A live connection owned by a <see cref="SocketSet"/> — one instance per accepted or connected
/// socket. The backend creates it and hands it to every callback through the context types, so it
/// is the stable per-connection identity: no separate handle is needed. It carries the
/// application's <see cref="UserToken"/> for the connection's whole lifetime (set it once, read it
/// from any callback) and is the sink for out-of-band writes.
///
/// A connection is an <see cref="IBufferWriter{T}"/>: write outbound bytes with
/// <see cref="GetSpan"/>/<see cref="Advance"/> (they land directly in library-owned, backend-pinned
/// buffers — no intermediate copy) and then <see cref="Flush"/> to hand the accumulated buffers to
/// the IO loop as a single (scatter-gather) send. This is callable from any thread; one writer at a
/// time per connection (the caller serializes its own writes, as a multiplexer does). The
/// <see cref="Send(System.ReadOnlySpan{byte})"/> helpers are sugar over write-then-flush.
///
/// Backends subclass this to hang their own per-connection state on it (fd/slot + write accumulator
/// for io_uring, the socket + SAEAs for managed), which also consolidates what used to be parallel
/// per-slot arrays.
/// </summary>
public abstract class Connection : IBufferWriter<byte>
{
    /// <summary>Application state associated with this connection for its lifetime. Set it in
    /// OnAccept/OnConnect (or later) and read it from any callback; the library never touches it.</summary>
    public object? UserToken { get; set; }

    /// <summary>Per-connection TLS provider override (set by the connect path when the caller passed
    /// one; null = engine options decide). Seeded alongside <see cref="UserToken"/> at init/adopt so a
    /// recycled slot never leaks the previous tenant's provider.</summary>
    internal SocketSets.Tls.TlsProvider? TlsOverride;

    /// <summary>Send/receive-closed state, mutated through the context Close* methods.</summary>
    internal SocketSet.SocketFlags Flags;

    /// <summary>Non-null when the application opted this connection into pipe mode with
    /// <c>ctx.UsePipe(...)</c> from OnAccept/OnConnect. When set, received data goes to the pipe instead
    /// of <see cref="SocketSet.OnReceive"/>, and an outbound pump sends whatever the application writes.
    /// Null - the default - leaves every existing code path untouched.</summary>
#if NET
    internal PipeIoBridge? PipeIo;
#endif

    /// <summary>The per-connection TLS engine, or null for a plaintext connection. Owned and touched only
    /// by the owning shard's IO loop (created at accept/connect; handshake driven, then encrypt/decrypt).
    /// Backends store it here so the shared receive/send interception is uniform across transports.</summary>
    internal TlsFilter? Tls;

    /// <summary>The negotiated ALPN protocol for the kTLS bypass path, where there is no
    /// <see cref="Tls"/> filter to ask (the handshake ran on a raw <c>SSL*</c> bound to the fd). Set by the
    /// shard at handshake completion.</summary>
    internal string? KernelAlpn { get; set; }

    /// <summary>
    /// The ALPN protocol agreed during the TLS handshake ("h2", "http/1.1", …), or null for a plaintext
    /// connection, or when ALPN was not configured / not negotiated. Valid from OnAccept/OnConnect onwards
    /// — those fire only after the handshake completes — so protocol dispatch can happen right there.
    /// </summary>
    public string? NegotiatedProtocol => Tls?.NegotiatedProtocol ?? KernelAlpn;

    /// <summary>The kTLS-path twin of <see cref="TlsFilter.RequestedServerName"/>.</summary>
    internal string? KernelServerName { get; set; }

    /// <summary>
    /// SERVER SIDE: the name this client asked for via SNI, or null if it sent none (and always null on
    /// an outbound connection, and on plaintext). Valid from OnAccept onwards.
    ///
    /// PROVIDER SUPPORT IS NOT UNIFORM, and null is therefore ambiguous on one of them. OpenSSL reports
    /// the name (SSL_get_servername); SChannel offers no server-side equivalent, so this is ALWAYS null
    /// under the SChannel provider — "we cannot tell", not "the client sent none". Do not make a security
    /// decision on the difference: code that routes or admits on this value will see every Windows client
    /// as having sent no SNI. Verified 2026-08-04 on Windows by bench/verify-tlsname, which now declines
    /// to assert the announce half rather than passing it vacuously.
    ///
    /// Deliberately NOT on <c>TlsServerAuthenticateContext</c>: that callback runs before the receive is
    /// armed, so no ClientHello exists yet and the value there would always be null. See
    /// <see cref="TlsFilter.RequestedServerName"/>.
    /// </summary>
    public string? RequestedServerName => Tls?.RequestedServerName ?? KernelServerName;

    /// <summary>When this connection was created, and when it last carried a byte (Clock.Millis).
    /// Written only by the owning loop thread; read by the sweep, which is also the loop thread, so no
    /// synchronisation is needed. See <see cref="SocketSetOptions.HandshakeTimeout"/>.</summary>
    internal long StartedTicks;
    internal long LastActivityTicks;

    /// <summary>Copy of <see cref="SocketSetOptions.MaxInboundBufferBytes"/>, carried here because
    /// <c>ctx.UsePipe</c> runs from a ref-struct context that can reach the Connection but not the
    /// engine options. Seeded by the owning set.</summary>
    internal int MaxInboundBufferBytes;

    /// <summary>True once the app has seen this connection open (OnAccept/OnConnect fired); gates
    /// <see cref="SocketSet.OnClosed"/> so it pairs with an open and never fires for a connection the
    /// app never saw (e.g. a failed connect). Backend-managed on the owning IO thread.</summary>
    internal bool Opened;

    /// <summary>
    /// Request that this connection be closed, callable from any thread — the teardown is marshaled
    /// onto the owning IO context, which retracts everything associated with the socket.
    /// Idempotent; <see cref="SocketSet.OnClosed"/> fires once when it is actually torn down. After
    /// Close, further <see cref="Send(System.ReadOnlySpan{byte})"/>/<see cref="Flush"/> are no-ops.
    /// </summary>
    public abstract void Close();

    // --- IBufferWriter<byte>: write directly into library-owned outbound buffers, then Flush ---

    /// <summary>Get a span to write outbound bytes into. It lands in a library-owned buffer; write up
    /// to <c>span.Length</c> bytes, call <see cref="Advance"/>, and repeat until done, then
    /// <see cref="Flush"/>. <paramref name="sizeHint"/> is advisory — the returned span may be smaller
    /// (loop) or larger.</summary>
    public abstract Span<byte> GetSpan(int sizeHint = 0);

    /// <inheritdoc cref="GetSpan"/>
    public abstract Memory<byte> GetMemory(int sizeHint = 0);

    /// <summary>Commit <paramref name="count"/> bytes written into the last <see cref="GetSpan"/>/
    /// <see cref="GetMemory"/> buffer.</summary>
    public abstract void Advance(int count);

    /// <summary>Hand everything written since the last flush to the IO loop as one send (a
    /// scatter-gather write when it spans multiple buffers). Safe to call from any thread; the
    /// submission is marshaled onto the owning IO context. Returns false if the connection is
    /// closed (buffers are dropped in that case).</summary>
    public abstract bool Flush();

    /// <summary>
    /// Write <paramref name="data"/> and flush it as one send — sugar over
    /// <see cref="GetSpan"/>/<see cref="Advance"/>/<see cref="Flush"/>. Callable from any thread; the
    /// bytes are copied into library buffers, so the span need not stay valid after the call. Returns
    /// false if the connection is closed.
    /// </summary>
    public virtual bool Send(ReadOnlySpan<byte> data)
    {
        WriteAll(data);
        return Flush();
    }

    /// <summary>
    /// Write a multi-segment <paramref name="data"/> and flush it as one (scatter-gather) send. Each
    /// segment is written straight into library buffers — no concatenation. See
    /// <see cref="Send(System.ReadOnlySpan{byte})"/> for the threading/copy contract.
    /// </summary>
    public virtual bool Send(in ReadOnlySequence<byte> data)
    {
        var position = data.Start;
        while (data.TryGet(ref position, out ReadOnlyMemory<byte> mem))
            WriteAll(mem.Span);
        return Flush();
    }

#if NET
    /// <summary>
    /// Zero-copy send: hand the backend the caller's own memory instead of copying it into library
    /// buffers. Used by pipe mode (<c>ctx.UsePipe</c>) so a response can go out straight from the
    /// application's pipe segments.
    ///
    /// Returns the number of BYTES ACCEPTED, which may be a PREFIX of <paramref name="data"/>; 0 means
    /// the backend declined entirely and the caller must fall back to
    /// <see cref="Send(in System.Buffers.ReadOnlySequence{byte})"/>, which copies. Refusing rather than
    /// silently copying is deliberate: the fallback has to stay the obvious, always-correct path.
    ///
    /// WHY BYTES RATHER THAN A BOOL, since this was a bool until 2026-07-30. Every backend caps how
    /// fragmented a sequence it can take in one operation (IOCP: <c>WSABUF</c>s per <c>WSASend</c>;
    /// io_uring: iovecs per writev), and all-or-nothing turns that cap into a CLIFF - one segment over
    /// the line and the whole response silently falls back to copying. That was worth 2.2x on IOCP at a
    /// 256KB response, because Kestrel's ~4KB pipe blocks made it 65 segments against a cap of 64.
    /// Reporting a prefix converts the cliff into a slope: the backend sends what it can, the caller
    /// advances by that much and offers the rest.
    ///
    /// OWNERSHIP: on a non-zero return, the first <c>accepted</c> bytes of <paramref name="data"/> must
    /// stay valid and unmodified until <paramref name="completion"/> completes — the socket is reading
    /// them directly. The caller must not <c>AdvanceTo</c> past that point before then.
    /// <paramref name="pinned"/> says the memory is already pinned (a pinned-object-heap pool), letting
    /// the backend skip per-operation pinning.
    /// </summary>
    internal virtual long TrySendZeroCopy(in ReadOnlySequence<byte> data, bool pinned, out ValueTask<bool> completion)
    {
        completion = default;
        return 0;
    }
#endif

    /// <summary>
    /// The buffer <see cref="WriteAll"/> writes into. Defaults to asking for the whole remaining length as
    /// one contiguous span, which is what a backend that cannot scatter-gather needs.
    ///
    /// A backend whose send takes a segment vector should override this to return its NATURAL buffer and
    /// let an oversized write chain across pooled pages. <see cref="WriteAll"/> has always coped with a
    /// short span, so it never required contiguity - but asking for the full length made the backend treat
    /// it as a REQUIREMENT, and a response larger than one pool page then spilled to a right-sized pinned
    /// GC allocation, one per response. That measured at exactly 1.000 pinned allocations per response and
    /// half the goodput of the fitting case on io_uring.
    ///
    /// Deliberately opt-in per backend rather than global: RIO caps <c>maxSendDataBuffers</c> at 1, so
    /// chaining segments there would turn one send into N sequential sends - the exact quantisation that
    /// already costs RIO 2.2-2.5x at large payloads. Backends keep the contiguous default until measured.
    /// </summary>
    private protected virtual Span<byte> GetWriteSpan(int sizeHint) => GetSpan(sizeHint);

    private void WriteAll(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var dst = GetWriteSpan(data.Length);
            int n = Math.Min(dst.Length, data.Length);
            data.Slice(0, n).CopyTo(dst);
            Advance(n);
            data = data.Slice(n);
        }
    }
}
