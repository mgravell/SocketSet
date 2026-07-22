#if NET // Windows IOCP backend; compiled out of the netfx fallback build.
namespace SocketSets.Windows;

/// <summary>
/// Backend factory for the Windows IOCP transport: one completion port per shard, a dedicated loop
/// thread each, raw Winsock (bypassing managed sockets) over the shared pre-pinned buffer pool. This
/// is the AF_UNIX-capable, universally-available Windows path and the foundation the RIO data-path
/// upgrade layers onto (RIO can't do AF_UNIX and still needs IOCP for accept/connect + notify).
/// </summary>
internal sealed class WindowsIocpFactory : SocketSetFactory
{
    public static WindowsIocpFactory Instance { get; } = new();

    private WindowsIocpFactory() { } // inert — no Winsock at construction (so it's safe to touch on Linux)

    /// <summary>Available on any Windows (a JIT constant; safe to read on Linux — returns false).</summary>
    public override bool IsSupported => OperatingSystem.IsWindows();

    public override SocketSetShard CreateShard(SocketSetOptions options)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("The Windows IOCP backend requires Windows.");
        return new IocpShard(options);
    }

    // Thread-per-shard with a dedicated IO loop, like io_uring — scales per core.
    public override bool UsesWorkerThreads => true;
}
#endif
