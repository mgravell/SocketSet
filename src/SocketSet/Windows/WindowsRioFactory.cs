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
}
#endif
