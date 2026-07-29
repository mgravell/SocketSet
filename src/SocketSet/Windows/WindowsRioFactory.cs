#if NET // Windows RIO backend; compiled out of the netfx fallback build.
namespace SocketSets.Windows;

/// <summary>
/// Backend factory for the Windows Registered-I/O (RIO) transport — the TCP data-path accelerator over
/// the IOCP foundation. Opt-in (not chosen by <see cref="SocketSetFactory.Default"/>): RIO is TCP-only,
/// so a caller selects it explicitly when the workload is TCP; AF_UNIX (and the universal default) stay
/// on the IOCP backend.
/// </summary>
internal sealed class WindowsRioFactory : SocketSetFactory
{
    public static WindowsRioFactory Instance { get; } = new();

    private WindowsRioFactory() { } // inert — no Winsock/RIO at construction (safe to touch on Linux)

    /// <summary>Available on any Windows (a JIT constant; safe to read on Linux — returns false).</summary>
    public override bool IsSupported => OperatingSystem.IsWindows();

    public override SocketSetShard CreateShard(SocketSetOptions options)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("The Windows RIO backend requires Windows.");
        return new WindowsRioShard(options);
    }

    public override bool UsesWorkerThreads => true;

    /// <summary>
    /// RIO is the one backend that must not take the shared default, and it is the reason
    /// <see cref="BufferGeometry"/> exists at all.
    ///
    /// **A 64KB send page**, because RIO cannot scatter-gather - Windows caps
    /// <c>maxSendDataBuffers</c> at 1 (attempted and refuted 2026-07-27), so one send is one page and
    /// the page is RIO's only lever on a large response. Two independent results:
    ///  - throughput, 2026-07-27: 2,404 -> 10,969 MiB/s at a 256KB payload (4.68x), monotonic across the
    ///    sweep, with NO penalty at 512B - so this is not a large-payload trade.
    ///  - correctness, 2026-07-29: at the 4KB default, RIO+TLS out-of-band send is the only failing cell
    ///    in the Windows smoke matrix (TODO item 0d). A default that fails the correctness gate is not a
    ///    tuning preference.
    ///
    /// **A 4KB receive buffer, NOT following the page**, because there is one per SOCKET for the whole
    /// connection lifetime and RIO *registers* it, so it is resident whether touched or not. Letting it
    /// follow a 64KB page took a 12-shard server from 283 MB to 3,164 MB resident (2026-07-27), 97% of
    /// the growth being a receive slab that gains nothing from being large.
    ///
    /// **The write pool is sized by CONCURRENCY, not by a byte budget, and that was learned the hard
    /// way.** A pure 4MB-per-pool rule gives 64 entries at a 64KB page, and 64 **wedges the smoke
    /// matrix**: `rio+tls/churn` strands connections (`live=24`, client-side, never drains). Entry count
    /// bounds how many connections per shard may have a send in flight, so shrinking it 1024 -> 64 is a
    /// 16x cut in send concurrency, not just a memory saving. Measured threshold on the churn cell: 64
    /// wedges, 128 and above pass. 256 is chosen for margin.
    ///
    /// Note the wedge is NOT specific to this page size - `--page 4096 --write-buffers 64` wedges
    /// identically, so it is a pre-existing shallow-pool defect that this geometry merely walked into.
    /// See TODO item 0e.
    ///
    /// The out-of-band and read pools keep the byte budget (64 entries = 4MB): neither is on the path
    /// that wedged, confirmed by holding write-buffers at 1024 while leaving those at 64.
    /// </summary>
    public override BufferGeometry DefaultGeometry { get; } = new(
        pageSize: 65536, receiveBufferSize: 4096,
        writeBuffersPerShard: 256, outOfBandWriteBuffersPerShard: 64, bufferPagesPerShard: 64);
}
#endif
