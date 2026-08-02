using Garnet.common;
using Garnet.networking;
using SocketSets;

namespace SocketSets.Garnet;

/// <summary>
/// Garnet's outbound seam (<see cref="INetworkSender"/>) over a SocketSet <see cref="Connection"/>.
///
/// The contract Garnet sessions actually use: <c>GetResponseObject</c> leases a PINNED buffer whose
/// head/tail pointers the session writes RESP into, then <c>SendResponse(offset, size)</c> ships it.
/// <see cref="Connection.Send(System.ReadOnlySpan{byte})"/> COPIES into transport buffers and is
/// callable from any thread, so the lease can be returned/reused immediately after the call — every
/// completion contract here is satisfied synchronously, which removes the callback machinery the SAEA
/// sender needs (its buffers stay in flight until the socket completion fires).
///
/// One copy per response, same as the SAEA path's kernel handoff — and the deferred-flush lesson from
/// the proxy does NOT need replicating here yet: Garnet sessions batch replies into the response buffer
/// themselves and send once per processed batch, so callback-granularity batching already exists one
/// level up. Verify with SS_URING_STATS before believing that sentence under pipelined load.
/// </summary>
internal sealed unsafe class SocketSetNetworkSender : NetworkSenderBase
{
    private readonly Connection _conn;
    private readonly LimitedFixedBufferPool _pool;
    private readonly string _remoteName, _localName;

    private PoolEntry? _reusableResponse;

    public SocketSetNetworkSender(Connection conn, NetworkBufferSettings settings, LimitedFixedBufferPool pool,
                                  string? remoteName = null, string? localName = null)
        : base(settings.sendBufferSize)
    {
        _conn = conn;
        _pool = pool;
        _remoteName = remoteName ?? "socketset";
        _localName = localName ?? "socketset";
    }

    public override string RemoteEndpointName => _remoteName;
    public override string LocalEndpointName => _localName;

    /// <summary>All SocketSet demo traffic is same-host today; revisit if this ever fronts a real NIC.</summary>
    public override bool IsLocalConnection() => true;

    // Enter/Exit guard the response object against concurrent producers. Garnet's own senders use an
    // epoch/spin scheme; a lock is the v1 that is obviously correct, and the session model mostly
    // serialises callers anyway — measure before optimising it away.
    private readonly object _lock = new();
    public override void Enter() => Monitor.Enter(_lock);
    public override void Exit() => Monitor.Exit(_lock);

    public override void EnterAndGetResponseObject(out byte* head, out byte* tail)
    {
        Monitor.Enter(_lock);
        GetResponseObject();
        head = _reusableResponse!.entryPtr;
        tail = _reusableResponse.entryPtr + _reusableResponse.entry.Length;
    }

    public override void ExitAndReturnResponseObject()
    {
        ReturnResponseObject();
        Monitor.Exit(_lock);
    }

    public override void GetResponseObject() => _reusableResponse ??= _pool.Get(serverBufferSize);

    public override void ReturnResponseObject()
    {
        _reusableResponse?.Dispose();
        _reusableResponse = null;
    }

    public override byte* GetResponseObjectHead() => _reusableResponse is { } e ? e.entryPtr : null;
    public override byte* GetResponseObjectTail() => _reusableResponse is { } e ? e.entryPtr + e.entry.Length : null;

    public override bool SendResponse(int offset, int size)
    {
        var entry = _reusableResponse;
        if (entry is null) return false;
        // Send copies synchronously, so the lease survives for the next response rather than being
        // detached per send — which is what keeps this allocation-flat under load.
        return _conn.Send(new ReadOnlySpan<byte>(entry.entryPtr + offset, size));
    }

    public override void SendResponse(byte[] buffer, int offset, int count, object context)
    {
        _conn.Send(buffer.AsSpan(offset, count));
        SendCallback(context);
    }

    public override void SendCallback(object context) { }

    public override void DisposeNetworkSender(bool waitForSendCompletion)
    {
        // Sends copied synchronously: there is nothing in flight to wait for.
        ReturnResponseObject();
    }

    public override void Throttle() { }

    public override void Dispose() => DisposeNetworkSender(waitForSendCompletion: false);
}
