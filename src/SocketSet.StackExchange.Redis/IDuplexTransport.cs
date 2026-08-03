using System.Buffers;

namespace SocketSets.StackExchangeRedis;

/// <summary>
/// PROVISIONAL-INTERNAL copy of the proposed Tunnel transport shape (see SocketSet TODO.md, "TUNNEL
/// TRANSPORT SHAPE — DESIGN PROPOSAL"). The real contract ships [Experimental] in an SE.Redis rev (or
/// RESPite — open question); this copy exists so the SocketSet-side implementation and its gates can be
/// built and measured NOW, and becomes a using-alias/delete when the real type lands. Every member is
/// derived from a measured lesson; do not add members here without adding the lesson.
/// </summary>
public interface IDuplexTransport : IAsyncDisposable
{
    /// <summary>Stage outbound bytes: callable from any thread; bytes are copied during the call.</summary>
    IBufferWriter<byte> Output { get; }

    /// <summary>Hand everything staged since the last flush to the wire as one send. False = closed.
    /// Batching at the caller's natural boundaries is the single biggest lever this project measured.</summary>
    bool Flush();

    /// <summary>Begin inbound delivery to <paramref name="receiver"/> — exactly one, set once. Push,
    /// not pull: every pull adapter measured cost 24-40%.</summary>
    void Start(ITransportReceiver receiver);
}

/// <summary>Consumer half; runs on the transport's loop thread — bounded, non-blocking work only.</summary>
public interface ITransportReceiver
{
    /// <summary><paramref name="payload"/> is transport-owned, valid only for the call. Return false to
    /// request close.</summary>
    bool OnReceived(ReadOnlySpan<byte> payload);

    /// <summary>Batch-end (the transport's loop-iteration drain point). Flush replies staged during a
    /// burst ONCE here — the 3x send-amplification fix depends on this hook existing.</summary>
    void OnBatchEnd();

    void OnClosed(Exception? fault);
}
