using SocketSets.Tls;

namespace SocketSets.AspNet;

/// <summary>How the outbound (application → socket) leg of the bridge is driven.</summary>
public enum SocketSetBridgeMode
{
    /// <summary>Each connection copies inbound into a <see cref="System.IO.Pipelines.Pipe"/> and runs an
    /// outbound pump task that hands buffers to <c>Connection.Send</c>. The universal fallback — the only
    /// mode available on backends that cannot do zero-copy send (RIO, managed).</summary>
    Classic,

    /// <summary>Kestrel's own pipes are handed to the transport via <c>ctx.UsePipe</c>, so the backend can
    /// send straight from the pipe's memory (zero-copy <c>writev</c> where the backend supports it). Best
    /// for large responses. The default.</summary>
    Byo,

    /// <summary>The outbound leg is a <c>CycleBuffer</c>-backed <see cref="System.IO.Pipelines.PipeWriter"/>
    /// that drains itself to <c>Connection.Send</c> on Kestrel's flush thread — no pump task, no thread hop.
    /// Copies on send, so it wins small/mid payloads but is beaten by <see cref="Byo"/> for large ones.</summary>
    HalfPipe,
}

/// <summary>
/// Options for the SocketSet Kestrel transport, configured via
/// <see cref="SocketSetBuilderExtensions.UseSocketSet"/>. Sizes left at 0 keep the backend's own default.
/// </summary>
public sealed class SocketSetTransportOptions
{
    /// <summary>Which backend to use. Defaults to <see cref="SocketSetFactory.Default"/> (io_uring on Linux,
    /// IOCP on Windows).</summary>
    public SocketSetFactory Factory { get; set; } = SocketSetFactory.Default;

    /// <summary>Number of shards (one loop thread each). 0 lets the backend choose.</summary>
    public int Shards { get; set; }

    /// <summary>Pin each shard's loop thread to a core.</summary>
    public bool PinWorkerThreads { get; set; }

    /// <summary>Terminate TLS inside the transport (below Kestrel) with this provider; null = plaintext.
    /// Build one with <c>OpenSslTlsProvider</c> (Linux) or the SChannel provider (Windows).</summary>
    public TlsProvider? Tls { get; set; }

    /// <summary>Outbound-leg strategy. See <see cref="SocketSetBridgeMode"/>. Default <see cref="SocketSetBridgeMode.Byo"/>.</summary>
    public SocketSetBridgeMode Mode { get; set; } = SocketSetBridgeMode.Byo;

    /// <summary>Back the bridge's pipes with a pinned-block pool so a zero-copy send can skip per-segment
    /// GCHandle pinning. Matches vanilla Kestrel's default and is what reaches parity/ahead at large
    /// payloads. Only meaningful in <see cref="SocketSetBridgeMode.Byo"/>.</summary>
    public bool PipePinned { get; set; } = true;

    /// <summary>Block size for the bridge's pipes (0 = framework default, ~4 KB).</summary>
    public int PipeSegment { get; set; }

    /// <summary>Send/write page size; 0 leaves the backend default.</summary>
    public int PageSize { get; set; }

    /// <summary>Per-socket receive buffer size; 0 follows <see cref="PageSize"/>.</summary>
    public int ReceiveBufferSize { get; set; }

    /// <summary>Write buffers per shard; 0 leaves the backend default.</summary>
    public int WriteBuffers { get; set; }

    /// <summary>
    /// Cap on inbound bytes buffered for one connection while Kestrel is not draining. Default 4 MiB;
    /// 0 disables. See <c>SocketSetConnection.WriteInbound</c>: the flush result used to be discarded,
    /// which made the pipe's own pauseWriterThreshold inert and let a fast uploader grow it without
    /// bound (REVIEW.md D3). Exceeding this ABORTS the connection, which is the bound rather than the
    /// cure -- receive parking is the real fix and is recorded in TODO.
    /// </summary>
    public long MaxInboundBufferBytes { get; set; } = 4 * 1024 * 1024;
}
