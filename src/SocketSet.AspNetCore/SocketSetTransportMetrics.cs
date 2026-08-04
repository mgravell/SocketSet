namespace SocketSets.AspNet;

/// <summary>
/// Per-registration diagnostics for the SocketSet transport, registered as a singleton by
/// <see cref="SocketSetBuilderExtensions.UseSocketSet"/>. Resolve it from DI to observe accept/close counts
/// and the buffer geometry the backend actually resolved. Counters are updated with <see cref="Interlocked"/>
/// from the loop and request threads; read them with the properties.
/// </summary>
public sealed class SocketSetTransportMetrics
{
    private long _accepts, _closes, _closedEmpty, _writeFail, _sendFalse;

    /// <summary>Connections accepted.</summary>
    public long Accepts => Interlocked.Read(ref _accepts);
    /// <summary>Connections closed.</summary>
    public long Closes => Interlocked.Read(ref _closes);
    /// <summary>Connections closed before any bytes were received.</summary>
    public long ClosedEmpty => Interlocked.Read(ref _closedEmpty);
    /// <summary>Accepted connections that could not be handed to Kestrel (the accept channel rejected them).</summary>
    public long WriteFail => Interlocked.Read(ref _writeFail);
    /// <summary>Sends that failed because the socket was already gone.</summary>
    public long SendFalse => Interlocked.Read(ref _sendFalse);

    /// <summary>The buffer geometry the backend resolved (page/recv/write sizes), set once at bind. Null
    /// until the first listener binds.</summary>
    public string? ResolvedGeometry { get; internal set; }

    internal void OnAccept() => Interlocked.Increment(ref _accepts);
    internal void OnClose() => Interlocked.Increment(ref _closes);
    internal void OnClosedEmpty() => Interlocked.Increment(ref _closedEmpty);
    internal void OnWriteFail() => Interlocked.Increment(ref _writeFail);
    internal void OnSendFalse() => Interlocked.Increment(ref _sendFalse);
    private long _inboundOverflow;

    /// <summary>Connections aborted for running too far ahead of Kestrel on the inbound half
    /// (REVIEW.md D3). Non-zero means either an abusive peer or a cap set too low, and those want
    /// telling apart -- which is why it is a counter and not only a log line.</summary>
    public long InboundOverflow => Interlocked.Read(ref _inboundOverflow);

    internal void OnInboundOverflow() => Interlocked.Increment(ref _inboundOverflow);

    private long _receiveParks;

    /// <summary>Times the transport stopped reading a connection because Kestrel had not drained the
    /// inbound pipe (REVIEW.md D3 / TODO item 1). This is the HEALTHY form of the same pressure
    /// <see cref="InboundOverflow"/> counts: parking slows the peer through the TCP window, overflow drops
    /// it. A deployment seeing parks and no overflows is working as intended; one seeing overflows on a
    /// backend that can park is either overshooting badly or running on io_uring, which cannot.
    ///
    /// SCOPE, AND IT IS NOT THE WHOLE PICTURE: this counts the CLASSIC and HALF-PIPE paths only, because
    /// only those run through <c>SocketSetConnection.WriteInbound</c>. In BYO mode the transport drives
    /// the pipe itself through the library's own <c>PipeIoBridge</c>, which cannot reach this type, so
    /// this reads ZERO there however hard that connection is parking. Measured 2026-08-04, a 1 MiB-body
    /// upload parked ~4,300 times in six seconds on BYO while this counter sat at 0 -- and a zero that
    /// means "not counted" is indistinguishable from one that means "never happened", which is exactly
    /// the trap house rule 2 exists for. BYO parks are counted inside the library and reported on the
    /// <c>SS_BRIDGE_STATS=1</c> line as <c>PARKED=</c>.</summary>
    public long ReceiveParks => Interlocked.Read(ref _receiveParks);

    internal void OnReceivePark() => Interlocked.Increment(ref _receiveParks);
}
