#if NET // Windows Registered I/O (RIO) surface — the TCP data-path accelerator layered on the IOCP
// foundation. TCP/UDP only (no AF_UNIX). Declarations only; only ever invoked on Windows.
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SocketSets.Native;

internal static unsafe partial class Win32
{
    // RIO functions are not plain exports and not individually fetchable — the whole table is loaded
    // in one WSAIoctl using this multiplexed-extension code + GUID.
    internal const uint SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER = 0xC8000024;
    // WSAID_MULTIPLE_RIO {8509e081-96dd-4005-b165-9e2ee8c79e3f}
    internal static readonly Guid WSAID_MULTIPLE_RIO =
        new(0x8509e081, 0x96dd, 0x4005, 0xb1, 0x65, 0x9e, 0x2e, 0xe8, 0xc7, 0x9e, 0x3f);

    // RIO_NOTIFICATION_COMPLETION.Type
    internal const int RIO_EVENT_COMPLETION = 1;
    internal const int RIO_IOCP_COMPLETION = 2;

    // RIOReceive / RIOSend flags — DEFER + COMMIT_ONLY are the submission-batching lever (queue many,
    // kick once); DONT_NOTIFY suppresses the per-op CQ notification.
    internal const uint RIO_MSG_DONT_NOTIFY = 0x1;
    internal const uint RIO_MSG_DEFER = 0x2;
    internal const uint RIO_MSG_WAITALL = 0x4;
    internal const uint RIO_MSG_COMMIT_ONLY = 0x8;

    // Sentinels. Handles (CQ/RQ) are pointer-sized; an invalid buffer id is (RIO_BUFFERID)0xFFFFFFFF —
    // i.e. ZERO-extended to 0x00000000FFFFFFFF, NOT sign-extended to -1. Getting this wrong makes a
    // failed RIORegisterBuffer look like success.
    internal static readonly nint RIO_INVALID_BUFFERID = unchecked((nint)0xFFFFFFFFL);
    internal static readonly nint RIO_INVALID_CQ = 0;
    internal static readonly nint RIO_INVALID_RQ = 0;
    // RIODequeueCompletion returns this (0xFFFFFFFF) if the CQ is corrupt.
    internal const uint RIO_CORRUPT_CQ = 0xFFFFFFFF;

    /// <summary>RIO_BUF — a slice {offset, length} into a registered buffer, referenced by recv/send.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RIO_BUF
    {
        public nint BufferId; // RIO_BUFFERID from RIORegisterBuffer
        public uint Offset;
        public uint Length;
    }

    /// <summary>RIORESULT — one dequeued RIO completion. SocketContext/RequestContext are the opaque
    /// values passed to RIOCreateRequestQueue / RIOReceive|RIOSend (our slot + op identity).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RIORESULT
    {
        public int Status;             // LONG — 0 on success, else a Winsock/NT error
        public uint BytesTransferred;  // ULONG
        public ulong SocketContext;    // ULONGLONG
        public ulong RequestContext;   // ULONGLONG
    }

    /// <summary>RIO_NOTIFICATION_COMPLETION (IOCP variant). When RIONotify fires it posts a packet to
    /// IocpHandle carrying CompletionKey + Overlapped, so RIO completions surface on our existing IOCP
    /// loop; we then drain the CQ with RIODequeueCompletion. The int + pointers give the same field
    /// offsets as the C union (Type@0, pointer union @8 on x64) under Sequential layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RIO_NOTIFICATION_COMPLETION
    {
        public int Type;          // RIO_IOCP_COMPLETION
        public nint IocpHandle;   // the completion port
        public nint CompletionKey;
        public nint Overlapped;   // a real, stable OVERLAPPED* we own
    }

    // The function-pointer table filled by WSAIoctl. cbSize first, then the 13 entries in header order.
    [StructLayout(LayoutKind.Sequential)]
    private struct RIO_EXTENSION_FUNCTION_TABLE
    {
        public uint cbSize;
        public nint RIOReceive;
        public nint RIOReceiveEx;
        public nint RIOSend;
        public nint RIOSendEx;
        public nint RIOCloseCompletionQueue;
        public nint RIOCreateCompletionQueue;
        public nint RIOCreateRequestQueue;
        public nint RIODequeueCompletion;
        public nint RIODeregisterBuffer;
        public nint RIONotify;
        public nint RIORegisterBuffer;
        public nint RIOResizeCompletionQueue;
        public nint RIOResizeRequestQueue;
    }

    // Typed entry points populated by LoadRio (once per process). Signatures follow the RIO headers;
    // RIO_BUFFERID / RIO_CQ / RIO_RQ are all pointer-sized opaque handles (nint).
    internal static delegate* unmanaged<byte*, uint, nint> RIORegisterBuffer;                          // (buf,len) -> RIO_BUFFERID
    internal static delegate* unmanaged<nint, void> RIODeregisterBuffer;                               // (bufferId)
    internal static delegate* unmanaged<uint, RIO_NOTIFICATION_COMPLETION*, nint> RIOCreateCompletionQueue; // (size,notify) -> RIO_CQ
    internal static delegate* unmanaged<nint, void> RIOCloseCompletionQueue;                           // (cq)
    internal static delegate* unmanaged<nint, uint, uint, uint, uint, nint, nint, void*, nint> RIOCreateRequestQueue; // (sock,maxRecv,maxRecvBuf,maxSend,maxSendBuf,recvCq,sendCq,sockCtx) -> RIO_RQ
    internal static delegate* unmanaged<nint, RIO_BUF*, uint, uint, void*, int> RIOReceive;            // (rq,buf,count,flags,reqCtx) -> BOOL
    internal static delegate* unmanaged<nint, RIO_BUF*, uint, uint, void*, int> RIOSend;               // (rq,buf,count,flags,reqCtx) -> BOOL
    internal static delegate* unmanaged<nint, RIORESULT*, uint, uint> RIODequeueCompletion;            // (cq,array,size) -> count
    internal static delegate* unmanaged<nint, int> RIONotify;                                          // (cq) -> INT

    /// <summary>Load the RIO function table (once per process) using any RIO-capable (WSA_FLAG_REGISTERED_IO) socket.</summary>
    internal static void LoadRio(nint anySocket)
    {
        if (RIOReceive != null) return;

        RIO_EXTENSION_FUNCTION_TABLE table = default;
        table.cbSize = (uint)sizeof(RIO_EXTENSION_FUNCTION_TABLE);
        Guid rioGuid = WSAID_MULTIPLE_RIO;
        uint bytes;
        if (WSAIoctl(anySocket, SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER, &rioGuid, (uint)sizeof(Guid),
                &table, table.cbSize, &bytes, null, null) != 0)
            throw new Win32Exception(WSAGetLastError(), "WSAIoctl(WSAID_MULTIPLE_RIO) failed");

        RIORegisterBuffer = (delegate* unmanaged<byte*, uint, nint>)table.RIORegisterBuffer;
        RIODeregisterBuffer = (delegate* unmanaged<nint, void>)table.RIODeregisterBuffer;
        RIOCreateCompletionQueue = (delegate* unmanaged<uint, RIO_NOTIFICATION_COMPLETION*, nint>)table.RIOCreateCompletionQueue;
        RIOCloseCompletionQueue = (delegate* unmanaged<nint, void>)table.RIOCloseCompletionQueue;
        RIOCreateRequestQueue = (delegate* unmanaged<nint, uint, uint, uint, uint, nint, nint, void*, nint>)table.RIOCreateRequestQueue;
        RIODequeueCompletion = (delegate* unmanaged<nint, RIORESULT*, uint, uint>)table.RIODequeueCompletion;
        RIONotify = (delegate* unmanaged<nint, int>)table.RIONotify;
        // RIOReceive last: it's the flag other threads test to see the table is fully populated.
        RIOSend = (delegate* unmanaged<nint, RIO_BUF*, uint, uint, void*, int>)table.RIOSend;
        RIOReceive = (delegate* unmanaged<nint, RIO_BUF*, uint, uint, void*, int>)table.RIOReceive;
    }

    // Shared WSAStartup-once guard (both the IOCP and RIO shards need Winsock up before any socket call).
    private static int _wsaStarted;

    internal static void EnsureWinsock()
    {
        if (Volatile.Read(ref _wsaStarted) != 0) return;
        lock (WsaGate)
        {
            if (_wsaStarted != 0) return;
            byte* wsaData = stackalloc byte[512]; // WSADATA — we never read it
            int rc = WSAStartup(0x0202, wsaData);
            if (rc != 0) throw new InvalidOperationException($"WSAStartup failed: {rc}");
            Volatile.Write(ref _wsaStarted, 1);
        }
    }

    private static readonly object WsaGate = new();
}
#endif
