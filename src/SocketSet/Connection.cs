namespace SocketSets;

/// <summary>
/// A live connection owned by a <see cref="SocketSet"/> — one instance per accepted or connected
/// socket. The backend creates it and hands it to every callback through the context types, so it
/// is the stable per-connection identity: no separate handle is needed. It carries the
/// application's <see cref="UserToken"/> for the connection's whole lifetime (set it once, read it
/// from any callback) and is the target for out-of-band sends via <see cref="Send"/>.
///
/// Backends subclass this to hang their own per-connection state on it (fd/slot for io_uring, the
/// socket + SAEAs for managed), which also consolidates what used to be parallel per-slot arrays.
/// </summary>
public abstract class Connection
{
    /// <summary>Application state associated with this connection for its lifetime. Set it in
    /// OnAccept/OnConnect (or later) and read it from any callback; the library never touches it.</summary>
    public object? UserToken { get; set; }

    /// <summary>Send/receive-closed state, mutated through the context Close* methods.</summary>
    internal SocketSet.SocketFlags Flags;

    /// <summary>
    /// Queue <paramref name="data"/> to be sent on this connection, callable from any thread — the
    /// send is marshaled onto the owning IO context, so it is safe to call outside a callback. The
    /// bytes are copied, so the span need not stay valid after the call returns. Returns false if
    /// the connection is already closed (or the send could not be accepted).
    /// </summary>
    public abstract bool Send(ReadOnlySpan<byte> data);
}
