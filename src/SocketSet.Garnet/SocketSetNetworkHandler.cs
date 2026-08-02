using Garnet.common;
using Garnet.networking;
using SocketSets;

namespace SocketSets.Garnet;

/// <summary>
/// Per-connection glue: Garnet's <see cref="NetworkHandler{TServerHook,TNetworkSender}"/> driven from
/// SocketSet's receive callback instead of a SocketAsyncEventArgs loop.
///
/// The base class already owns everything hard — the receive-buffer accumulation and shift for partial
/// messages, the wire-format sniff via <c>IServerHook.TryCreateMessageConsumer</c> on first bytes, the
/// consumer dispatch, and the whole <see cref="INetworkSender"/> surface (with TLS interposition we
/// deliberately never enable: SocketSet terminates TLS in-transport, so this handler only ever sees
/// plaintext — which is precisely what makes a Garnet-TLS A/B a pure their-TLS-vs-ours comparison).
/// All this class adds is (a) buffer allocation at start, mirroring <c>TcpNetworkHandlerBase</c>, and
/// (b) <see cref="Feed"/>, which lands transport bytes in the base's buffer and calls the same public
/// entrypoint the SAEA completion path calls.
/// </summary>
internal sealed unsafe class SocketSetGarnetHandler : NetworkHandler<SocketSetGarnetServer, SocketSetNetworkSender>
{
    public SocketSetGarnetHandler(SocketSetGarnetServer serverHook, SocketSetNetworkSender sender,
                                  NetworkBufferSettings settings, LimitedFixedBufferPool pool)
        : base(serverHook, sender, settings, pool, useTLS: false)
    {
    }

    /// <summary>Allocate the receive buffer the base's accumulation machinery works over — the part
    /// <c>TcpNetworkHandlerBase.Start</c> does before arming its first SAEA receive.</summary>
    public void StartReceive()
    {
        networkReceiveBufferEntry = networkPool.Get(networkBufferSettings.initialReceiveBufferSize);
        networkReceiveBuffer = networkReceiveBufferEntry.entry;
        networkReceiveBufferPtr = networkReceiveBufferEntry.entryPtr;
    }

    /// <summary>
    /// Bytes arrived on the shard loop thread. The transport owns <paramref name="payload"/> only for
    /// the duration of the callback, so it is copied into the handler's own buffer — one copy the SAEA
    /// path does not pay (it receives directly into this buffer), accepted as the v1 cost and noted in
    /// the results when measured.
    ///
    /// The chunk loop re-reads the (protected) buffer fields each pass on purpose: the base grows the
    /// buffer for messages larger than it, swapping those fields, and a cached local would quietly keep
    /// copying into the old, abandoned array.
    /// </summary>
    public bool Feed(ReadOnlySpan<byte> payload)
    {
        while (!payload.IsEmpty)
        {
            int free = networkReceiveBuffer.Length - networkBytesRead;
            if (free <= 0) return false; // consumer stalled with a full buffer the base chose not to grow
            int take = Math.Min(free, payload.Length);
            payload.Slice(0, take).CopyTo(networkReceiveBuffer.AsSpan(networkBytesRead));
            OnNetworkReceiveWithoutTLS(take);
            payload = payload.Slice(take);
        }
        return true;
    }

    public void CloseFromTransport()
    {
        try { Dispose(); }
        catch { /* teardown must not throw into the loop thread */ }
    }
}
