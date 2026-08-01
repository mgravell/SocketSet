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
}
