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
    /// Returns false when this backend cannot do it — RIO addresses registered buffer ids rather than raw
    /// addresses, the managed backend has no such path, and even IOCP declines a sequence with more
    /// segments than one WSASend can carry. The caller must then fall back to
    /// <see cref="Send(in System.Buffers.ReadOnlySequence{byte})"/>, which copies. Refusing rather than
    /// silently copying is deliberate: the fallback has to stay the obvious, always-correct path.
    ///
    /// OWNERSHIP: on true, <paramref name="data"/> must stay valid and unmodified until
    /// <paramref name="completion"/> completes — the socket is reading it directly. The caller must not
    /// <c>AdvanceTo</c> its pipe reader before then. <paramref name="pinned"/> says the memory is already
    /// pinned (a pinned-object-heap pool), letting the backend skip per-operation pinning.
    /// </summary>
    internal virtual bool TrySendZeroCopy(in ReadOnlySequence<byte> data, bool pinned, out ValueTask<bool> completion)
    {
        completion = default;
        return false;
    }
#endif

    private void WriteAll(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var dst = GetSpan(data.Length);
            int n = Math.Min(dst.Length, data.Length);
            data.Slice(0, n).CopyTo(dst);
            Advance(n);
            data = data.Slice(n);
        }
    }
}
