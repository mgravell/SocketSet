// Shared backend/TLS selection for the cross-platform security gates (verify-tailwipe, verify-tlsname,
// verify-timeouts). Linked into each rig rather than referenced, so the rigs stay standalone projects.
//
// It exists because those rigs were written on Linux and hard-coded io_uring/epoll and the OpenSSL
// provider, which on Windows is not a wrong ANSWER but a crash: those backends throw
// PlatformNotSupportedException and libssl is usually absent. A gate that cannot run on the OS whose code
// changed is not a gate -- and the Windows half of every security change in this session is unrun.
using System.Runtime.InteropServices;
using SocketSets;
using SocketSets.Tls;

internal static class GateBackends
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>The native backends worth gating on THIS OS, plus the portable one. Managed is included
    /// on both because it is the fallback everyone gets and it behaves differently often enough to
    /// matter (it was the only backend that honoured the bind address, for instance).</summary>
    public static (SocketSetFactory Factory, string Name)[] All => IsWindows
        ? [(SocketSetFactory.WindowsIocp, "IOCP"), (SocketSetFactory.WindowsRio, "RIO"), (SocketSetFactory.Managed, "Managed")]
        : [(SocketSetFactory.IoUring, "IoUring"), (SocketSetFactory.Epoll, "Epoll"), (SocketSetFactory.Managed, "Managed")];

    /// <summary>Backends that can carry TLS. RIO is excluded on purpose: it is TCP-only and its TLS path
    /// is the one with a measured history of trouble at small pages, so gating TLS SEMANTICS on it would
    /// conflate two questions. The smoke matrix covers rio+tls separately.</summary>
    public static (SocketSetFactory Factory, string Name)[] Tls => IsWindows
        ? [(SocketSetFactory.WindowsIocp, "IOCP"), (SocketSetFactory.Managed, "Managed")]
        : [(SocketSetFactory.IoUring, "IoUring"), (SocketSetFactory.Epoll, "Epoll")];

    /// <summary>Can the SERVER report the SNI the client asked for? OpenSSL can (SSL_get_servername);
    /// SChannel has no equivalent, so <c>Connection.RequestedServerName</c> is ALWAYS null on Windows --
    /// see TODO's D1 follow-up, which lists the three routes to fixing that. This is a capability flag and
    /// not a quiet <c>if (IsWindows)</c> in the rig because the point is that the rig must SAY the
    /// assertion was not made: a cell expecting "no SNI" passes vacuously against a provider that reports
    /// no SNI ever, which is a silent degradation of exactly the kind these gates exist to catch.</summary>
    public static bool ServerSniObservable => !IsWindows;

    /// <summary>A throwaway self-signed provider for "localhost" (+ the loopback IP in its SAN), from
    /// whichever engine this OS actually has: SChannel on Windows, OpenSSL elsewhere. Both factories
    /// produce the same SAN shape, which is what lets the name cells be written once.</summary>
    public static TlsProvider SelfSigned()
        => IsWindows
            ? SocketSets.Tls.SChannel.SChannelTlsProvider.CreateSelfSignedLoopback("localhost")
            : SocketSets.Tls.OpenSsl.OpenSslTlsProvider.CreateSelfSignedLoopback("localhost");
}
