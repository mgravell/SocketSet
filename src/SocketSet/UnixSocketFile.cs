using System.IO;

namespace SocketSets;

/// <summary>Helpers for filesystem-path AF_UNIX sockets, shared across backends (io_uring, managed,
/// and the coming Windows backend — Windows AF_UNIX is filesystem-path only).</summary>
internal static class UnixSocketFile
{
    /// <summary>
    /// Reject a Linux abstract-namespace name (<c>'@'</c> prefix) on a platform that has no such
    /// namespace. Windows AF_UNIX is filesystem-path only, so <c>@foo</c> is not "the abstract socket
    /// foo" there — it is a FILE literally named <c>@foo</c>, created wherever the process happens to be
    /// running. That is silent and surprising in both directions: the server creates a stray file and a
    /// Linux-shaped client never finds it.
    ///
    /// Verified 2026-07-31 before adding this: `-u @ss-abstract` on Windows created `./@ss-abstract` and
    /// carried on. TODO's UDS entry had asked for exactly this check.
    /// </summary>
    internal static string ValidatePath(string path)
    {
        // OperatingSystem.IsLinux() is net-only; the netfx build has to ask the old way.
#if NET
        bool isLinux = OperatingSystem.IsLinux();
#else
        bool isLinux = Environment.OSVersion.Platform == PlatformID.Unix;
#endif
        if (path.Length > 0 && (path[0] == '@' || path[0] == '\0') && !isLinux)
            throw new PlatformNotSupportedException(
                $"'{path}' names the Linux abstract socket namespace, which this platform does not have. " +
                "AF_UNIX here is filesystem-path only, so this would silently create a file with that " +
                "literal name. Use a filesystem path instead.");
        return path;
    }

    /// <summary>
    /// Remove any stale file at a filesystem AF_UNIX path before binding. A socket file is left
    /// behind when a process exits without unlinking it (a crash, say), and a subsequent
    /// <c>bind()</c> to the same path then fails with EADDRINUSE — so, like nginx et al., we unlink
    /// first. No-op for Linux abstract-namespace sockets (leading <c>'@'</c> in our convention, or a
    /// literal leading NUL), which have no filesystem presence and auto-clean on close.
    ///
    /// Best-effort and unconditional: whatever is at the path is removed, so point UDS at a dedicated
    /// path, not something you care about. (We deliberately don't stat for S_ISSOCK — that's more
    /// cross-platform P/Invoke than it's worth here.)
    /// </summary>
    public static void PrepareForBind(string? path)
    {
        if (path is not { Length: > 0 }) return;
        if (path[0] is '@' or '\0') return; // abstract namespace — no file
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort — if we can't remove it, let bind() surface the real error.
        }
    }
}
