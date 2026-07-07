using System;

namespace FastNet.WinRio;

/// <summary>
/// Windows Registered I/O backend — future work. This is a deliberate stub:
/// the previous draft fetched the RIO extension-function table from the listen
/// socket, called RIORegisterBuffer under SuppressGCTransition with a managed
/// byte[] (illegal — no marshalling allowed across a suppressed transition),
/// and located RIODequeueResults by pointer-punning "index 11" of the table.
/// All of that is discarded.
///
/// The real implementation will, in order:
///   1. WSASocket(..., WSA_FLAG_REGISTERED_IO) for each connection;
///   2. resolve the RIO table via WSAID_MULTIPLE_RIO_FUNCTIONS into named
///      fields (RIOReceive/RIOSend/RIODequeueResults — no magic indices);
///   3. RIORegisterBuffer over the shared native BufferPool block (already
///      page-aligned and pinned for exactly this);
///   4. per-connection RIOCreateRequestQueue + a shared RIOCreateCompletionQueue
///      with RIONotify feeding an IOCP for the wait, mirroring the io_uring loop.
/// </summary>
internal sealed class RioEngine
{
    public RioEngine() => throw new NotImplementedException(
        "RIO backend not implemented yet. io_uring (Transport/EchoServer.cs) is the reference.");
}
