#if NET // Windows IOCP/RIO backend hooks; Linux hooks live in LibC.cs. Compiled out of the netfx fallback.
using System.Runtime.InteropServices;

namespace SocketSets.Native;

/// <summary>
/// Winsock (ws2_32) + IOCP (kernel32) P/Invoke surface for the Windows backend. Kept strictly
/// separate from <see cref="LibC"/> (the Linux surface). These are declarations only — they compile
/// everywhere but are only ever called on Windows (the factory selects this backend by OS), so a
/// Linux build links fine and simply never invokes them.
/// </summary>
internal static unsafe partial class Win32
{
    private const string Ws2 = "ws2_32.dll";
    private const string Kernel32 = "kernel32.dll";

    // --- address families / socket types (Winsock values) ---
    internal const int AF_INET = 2;
    internal const int AF_INET6 = 23;
    internal const int AF_UNIX = 1;
    internal const int SOCK_STREAM = 1;
    internal const int IPPROTO_TCP = 6;

    // --- sentinels ---
    internal static readonly nint INVALID_SOCKET = -1;        // (SOCKET)(~0)
    internal const int SOCKET_ERROR = -1;
    internal static readonly nint INVALID_HANDLE_VALUE = -1;
    internal const uint INFINITE = 0xFFFFFFFF;

    // --- WSASocketW flags / errors ---
    internal const uint WSA_FLAG_OVERLAPPED = 0x01;
    internal const uint WSA_FLAG_REGISTERED_IO = 0x100; // required to use a socket with RIO
    internal const int WSA_IO_PENDING = 997;                  // ERROR_IO_PENDING
    internal const int WSAEWOULDBLOCK = 10035;

    // --- setsockopt levels/options ---
    internal const int SOL_SOCKET = 0xFFFF;
    internal const int SO_REUSEADDR = 0x0004;
    internal const int SO_LINGER = 0x0080;
    internal const int SO_UPDATE_ACCEPT_CONTEXT = 0x700B;
    internal const int SO_UPDATE_CONNECT_CONTEXT = 0x7010;
    internal const int TCP_NODELAY = 0x0001;

    // --- SetFileCompletionNotificationModes flags ---
    // Skip queuing a completion packet when an overlapped op completes SYNCHRONOUSLY (returns success,
    // not PENDING) — the caller handles the result inline. On loopback / warm buffers most recv/send
    // ops complete synchronously, so this elides the completion-port round-trip that otherwise dominates
    // small-message cost. SET_EVENT_ON_HANDLE skip is a companion micro-opt (we don't use hEvent).
    internal const byte FILE_SKIP_COMPLETION_PORT_ON_SUCCESS = 0x1;
    internal const byte FILE_SKIP_SET_EVENT_ON_HANDLE = 0x2;

    // --- ioctls ---
    internal const int SD_BOTH = 2;                            // shutdown(how)
    internal const uint FIONBIO = 0x8004667E;
    internal const int FIONREAD = 0x4004667F; // bytes available to read (diagnostic)
    internal const uint SIO_GET_EXTENSION_FUNCTION_POINTER = 0xC8000006;

    // WSAID_ACCEPTEX  {b5367df1-cbac-11cf-95ca-00805f48a192}
    internal static readonly Guid WSAID_ACCEPTEX =
        new(0xb5367df1, 0xcbac, 0x11cf, 0x95, 0xca, 0x00, 0x80, 0x5f, 0x48, 0xa1, 0x92);
    // WSAID_CONNECTEX {25a207b9-ddf3-4660-8ee9-76e58c74063e}
    internal static readonly Guid WSAID_CONNECTEX =
        new(0x25a207b9, 0xddf3, 0x4660, 0x8e, 0xe9, 0x76, 0xe5, 0x8c, 0x74, 0x06, 0x3e);

    // =====================================================================
    // Structs (fixed binary layout matching the Win32 headers)
    // =====================================================================

    /// <summary>OVERLAPPED. Must live at a stable address for an op's whole lifetime (the kernel
    /// writes Internal/InternalHigh) and is the per-operation identity handed back on completion.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OVERLAPPED
    {
        public nuint Internal;      // status (ULONG_PTR)
        public nuint InternalHigh;  // bytes transferred (ULONG_PTR)
        public uint Offset;         // union { struct { Offset; OffsetHigh } ; Pointer } — unused for sockets
        public uint OffsetHigh;
        public nint hEvent;         // HANDLE (unused with IOCP)
    }

    /// <summary>One dequeued completion from GetQueuedCompletionStatusEx.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OVERLAPPED_ENTRY
    {
        public nuint lpCompletionKey;            // per-handle key (our slot id) — ULONG_PTR
        public OVERLAPPED* lpOverlapped;         // per-op OVERLAPPED* (CONTAINING_RECORD back to our op ctx)
        public nuint Internal;                   // ULONG_PTR
        public uint dwNumberOfBytesTransferred;  // DWORD
    }

    /// <summary>WSABUF — a scatter/gather buffer descriptor for WSARecv/WSASend.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WSABUF
    {
        public uint len;   // ULONG
        public byte* buf;  // CHAR*
    }

    /// <summary>struct linger { u_short l_onoff; u_short l_linger; } — SO_LINGER{1,0} → closesocket sends RST (no TIME_WAIT).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LINGER { public ushort l_onoff; public ushort l_linger; }

    /// <summary>sockaddr_in (IPv4, 16 bytes) — same wire layout as elsewhere.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct SockAddrIn
    {
        public ushort sin_family;
        public ushort sin_port;   // network byte order
        public uint sin_addr;     // network byte order; 0 == INADDR_ANY
        // 8 bytes zero padding (Size = 16)
    }

    /// <summary>sockaddr_un (Windows AF_UNIX — filesystem path only, no abstract namespace).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 110)]
    internal struct SockAddrUn
    {
        public ushort sun_family;
        public fixed byte sun_path[108];

        /// <summary>UTF-8 encoded and length-checked; see the twin in <c>LibC.SockAddrUn.Init</c> for what
        /// the per-char cast got wrong. Windows AF_UNIX is filesystem-path only, so there is no abstract
        /// mapping here — the length includes the NUL terminator.</summary>
        public static uint Init(SockAddrUn* addr, string path)
        {
            *addr = default;
            addr->sun_family = AF_UNIX;
            int need = System.Text.Encoding.UTF8.GetByteCount(path);
            if (need + 1 > PathCapacity) // +1: the terminator has to fit too
                throw new ArgumentException(
                    $"AF_UNIX path '{path}' encodes to {need} UTF-8 bytes; sun_path holds {PathCapacity} including the NUL.",
                    nameof(path));
            int n = System.Text.Encoding.UTF8.GetBytes(path, new Span<byte>(addr->sun_path, PathCapacity));
            return (uint)(sizeof(ushort) + n + 1); // include the NUL terminator in the length
        }

        /// <summary>Bytes of <c>sun_path</c>.</summary>
        internal const int PathCapacity = 108;
    }

    /// <summary>Host-to-network byte order for a 16-bit port.</summary>
    internal static ushort Htons(ushort value)
        => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value); // Windows is always little-endian

    /// <summary>IPv4 address as the network-order <c>sin_addr</c> word. See <see cref="RequireIPv4"/> for
    /// why the four-byte assertion matters. Twin of <c>LibC.ToSinAddr</c>; kept here so the Windows
    /// interop surface stays self-contained (the two files never reference each other).</summary>
    internal static uint ToSinAddr(System.Net.IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out int written) || written != 4)
            throw new NotSupportedException($"'{address}' is not an IPv4 address.");
        return System.Runtime.InteropServices.MemoryMarshal.Read<uint>(bytes);
    }

    /// <summary>Reject a non-IPv4 endpoint LOUDLY rather than truncating it. Twin of
    /// <c>LibC.RequireIPv4</c> — see there for the failure this prevents.</summary>
    internal static void RequireIPv4(System.Net.IPEndPoint ip, string operation)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new NotSupportedException(
                $"{operation}: the native backends are IPv4-only today, but '{ip}' is " +
                $"{ip.AddressFamily}. Use an IPv4 endpoint, or the managed backend " +
                $"(SocketSetFactory.Managed), which goes through System.Net.Sockets and supports IPv6.");
    }

    // =====================================================================
    // AcceptEx / ConnectEx — loaded at runtime via WSAIoctl (they aren't plain exports).
    // =====================================================================

    // BOOL AcceptEx(listen, accept, outBuf, recvLen, localAddrLen, remoteAddrLen, out bytesRecvd, overlapped)
    internal static delegate* unmanaged<nint, nint, void*, uint, uint, uint, uint*, OVERLAPPED*, int> AcceptEx;
    // BOOL ConnectEx(sock, name, namelen, sendBuf, sendLen, out bytesSent, overlapped)
    internal static delegate* unmanaged<nint, void*, int, void*, uint, uint*, OVERLAPPED*, int> ConnectEx;

    /// <summary>Load the AcceptEx/ConnectEx function pointers (once per process) using any socket.</summary>
    internal static void LoadExtensions(nint anySocket)
    {
        if (AcceptEx != null) return;
        void* fn;
        uint bytes;

        Guid acceptGuid = WSAID_ACCEPTEX;
        if (WSAIoctl(anySocket, SIO_GET_EXTENSION_FUNCTION_POINTER, &acceptGuid, (uint)sizeof(Guid),
                &fn, (uint)sizeof(void*), &bytes, null, null) != 0)
            throw new System.ComponentModel.Win32Exception(WSAGetLastError(), "WSAIoctl(AcceptEx) failed");
        AcceptEx = (delegate* unmanaged<nint, nint, void*, uint, uint, uint, uint*, OVERLAPPED*, int>)fn;

        Guid connectGuid = WSAID_CONNECTEX;
        if (WSAIoctl(anySocket, SIO_GET_EXTENSION_FUNCTION_POINTER, &connectGuid, (uint)sizeof(Guid),
                &fn, (uint)sizeof(void*), &bytes, null, null) != 0)
            throw new System.ComponentModel.Win32Exception(WSAGetLastError(), "WSAIoctl(ConnectEx) failed");
        ConnectEx = (delegate* unmanaged<nint, void*, int, void*, uint, uint*, OVERLAPPED*, int>)fn;
    }

    // =====================================================================
    // Winsock (ws2_32)
    // =====================================================================

    [LibraryImport(Ws2, EntryPoint = "WSAStartup", SetLastError = false)]
    internal static partial int WSAStartup(ushort wVersionRequested, void* lpWSAData);

    [LibraryImport(Ws2, EntryPoint = "WSACleanup", SetLastError = false)]
    internal static partial int WSACleanup();

    // Create an overlapped-capable socket (WSA_FLAG_OVERLAPPED) — the IOCP prerequisite. Returns a
    // SOCKET (== INVALID_SOCKET on failure). WSASocketW to avoid ANSI marshaling.
    [LibraryImport(Ws2, EntryPoint = "WSASocketW", SetLastError = true)]
    internal static partial nint WSASocketW(int af, int type, int protocol, void* lpProtocolInfo, uint g, uint dwFlags);

    [LibraryImport(Ws2, EntryPoint = "closesocket", SetLastError = true)]
    internal static partial int closesocket(nint s);

    [LibraryImport(Ws2, EntryPoint = "shutdown", SetLastError = true)]
    internal static partial int shutdown(nint s, int how);

    [LibraryImport(Ws2, EntryPoint = "bind", SetLastError = true)]
    internal static partial int bind(nint s, void* addr, int namelen);

    [LibraryImport(Ws2, EntryPoint = "listen", SetLastError = true)]
    internal static partial int listen(nint s, int backlog);

    /// <summary>
    /// Peer and local address of a connected socket, for <see cref="SocketSetOptions.TrackEndpoints"/>.
    ///
    /// WHY THESE RATHER THAN THE AcceptEx BUFFER, since AcceptEx did already write both sockaddrs: the
    /// accept completes on the LISTENER's shard and the connection is adopted on a DIFFERENT one, so the
    /// buffer's contents do not survive the handoff — and parsing it in place needs
    /// <c>GetAcceptExSockaddrs</c>, whose offsets are implementation-defined rather than something to
    /// assume. Two syscalls per ACCEPT (not per operation) on a path that already makes several, only
    /// when tracking is on, is the honest trade. Valid here because <c>SO_UPDATE_ACCEPT_CONTEXT</c> has
    /// already been applied — without it these fail on an AcceptEx socket.
    /// </summary>
    [LibraryImport(Ws2, EntryPoint = "getpeername", SetLastError = true)]
    internal static partial int getpeername(nint s, void* addr, int* namelen);

    /// <inheritdoc cref="getpeername"/>
    [LibraryImport(Ws2, EntryPoint = "getsockname", SetLastError = true)]
    internal static partial int getsockname(nint s, void* addr, int* namelen);

    [SuppressGCTransition]
    [LibraryImport(Ws2, EntryPoint = "setsockopt", SetLastError = true)]
    internal static partial int setsockopt(nint s, int level, int optname, void* optval, int optlen);

    [SuppressGCTransition]
    [LibraryImport(Ws2, EntryPoint = "ioctlsocket", SetLastError = true)]
    internal static partial int ioctlsocket(nint s, int cmd, uint* argp);

    // Used to load AcceptEx/ConnectEx function pointers via SIO_GET_EXTENSION_FUNCTION_POINTER.
    [LibraryImport(Ws2, EntryPoint = "WSAIoctl", SetLastError = true)]
    internal static partial int WSAIoctl(nint s, uint code, void* inBuf, uint inLen, void* outBuf, uint outLen,
        uint* bytesReturned, void* overlapped, void* completionRoutine);

    // Overlapped recv/send. Return 0 on immediate completion, SOCKET_ERROR with WSA_IO_PENDING when queued.
    [LibraryImport(Ws2, EntryPoint = "WSARecv", SetLastError = true)]
    internal static partial int WSARecv(nint s, WSABUF* buffers, uint bufferCount, uint* bytesRecvd, uint* flags,
        OVERLAPPED* overlapped, void* completionRoutine);

    [LibraryImport(Ws2, EntryPoint = "WSASend", SetLastError = true)]
    internal static partial int WSASend(nint s, WSABUF* buffers, uint bufferCount, uint* bytesSent, uint flags,
        OVERLAPPED* overlapped, void* completionRoutine);

    [SuppressGCTransition]
    [LibraryImport(Ws2, EntryPoint = "WSAGetLastError")]
    internal static partial int WSAGetLastError();

    // =====================================================================
    // IOCP (kernel32)
    // =====================================================================

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial nint CreateIoCompletionPort(nint fileHandle, nint existingPort, nuint completionKey, uint concurrentThreads);

    // Two declarations of the same batch-dequeue import, differing only in GC-transition handling
    // (mirrors LibC's io_uring_enter_blocking / _nonblocking). The blocking form (INFINITE / non-zero
    // timeout) parks the thread, so it must NOT SuppressGCTransition — a thread parked inside a
    // suppressed transition can't be suspended for GC and would stall it.
    [LibraryImport(Kernel32, EntryPoint = "GetQueuedCompletionStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetQueuedCompletionStatusExBlocking(nint port, OVERLAPPED_ENTRY* entries, uint count,
        uint* removed, uint timeoutMs, [MarshalAs(UnmanagedType.Bool)] bool alertable);

    // The non-blocking (timeout 0) poll on the inline-drain path never parks, so it skips the
    // cooperative→preemptive transition like other short syscalls here.
    [SuppressGCTransition]
    [LibraryImport(Kernel32, EntryPoint = "GetQueuedCompletionStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetQueuedCompletionStatusExNonBlocking(nint port, OVERLAPPED_ENTRY* entries, uint count,
        uint* removed, uint timeoutMs, [MarshalAs(UnmanagedType.Bool)] bool alertable);

    // Register per-handle completion behaviour (see FILE_SKIP_* flags). Called on a socket before I/O.
    [SuppressGCTransition]
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileCompletionNotificationModes(nint handle, byte flags);

    // The eventfd analog: queue a completion packet verbatim (key + overlapped are passed through, no I/O).
    [SuppressGCTransition]
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostQueuedCompletionStatus(nint port, uint bytesTransferred, nuint completionKey, void* overlapped);

    [SuppressGCTransition]
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint h);

    [SuppressGCTransition]
    [LibraryImport(Kernel32)]
    internal static partial nint GetCurrentThread();

    [SuppressGCTransition]
    [LibraryImport(Kernel32)]
    internal static partial nuint SetThreadAffinityMask(nint thread, nuint mask);

    [SuppressGCTransition]
    [LibraryImport(Kernel32)]
    internal static partial nint GetCurrentProcess();

    // Both masks are DWORD_PTR out-params (the CPUs this process, and the whole system, may run on).
    [SuppressGCTransition]
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessAffinityMask(nint process, nuint* processMask, nuint* systemMask);
}
#endif
