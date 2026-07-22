#if NET // Windows IOCP backend; compiled out of the netfx fallback build.
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using SocketSets.Native;

namespace SocketSets.Windows;

/// <summary>
/// A single-threaded IOCP event loop — the Windows analogue of <c>IoUringShard</c>. Exactly one
/// thread owns the completion port (created with concurrency 1); cross-thread work is marshaled in
/// and the loop woken with <see cref="Win32.PostQueuedCompletionStatus"/> (the eventfd analogue), and
/// completions are drained in batches with <see cref="Win32.GetQueuedCompletionStatusEx"/>.
///
/// SKELETON: the port, loop, wake, pinned buffer pools, op-context pool, and thread/affinity are in
/// place and runnable; the actual socket I/O (accept/connect/recv/send, and the connection table)
/// lands in the next slice — the Listen/Connect/ListenHandle entry points throw until then.
/// </summary>
internal sealed unsafe class IocpShard : SocketSetShard
{
    private const int EntryBatch = 128;              // completions dequeued per GetQueuedCompletionStatusEx
    private static readonly nuint WakeKey = unchecked((nuint)(-1)); // reserved completion key for PQCS wakes

    // Per-operation context: an OVERLAPPED (which the kernel writes and hands back) plus our own state.
    // The OVERLAPPED MUST be the first field so an OVERLAPPED* is bit-identical to an IocpOp* — we cast
    // straight back on completion (no CONTAINING_RECORD offset). Blittable, so it lives in native memory.
    internal enum OpKind : int { Accept = 0, Connect = 1, Recv = 2, Send = 3 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IocpOp
    {
        public Win32.OVERLAPPED Overlapped; // MUST be first (offset 0)
        public OpKind Kind;
        public uint Slot;
        public int Buf;                     // write-buffer index / recv bookkeeping, by Kind
    }

    // --- options snapshot ---
    private readonly int _socketsPerShard;
    private readonly int _writeCount;
    private readonly int _writeBufSize;
    private readonly int _obWriteCount;
    private readonly int _opCount;

    // --- created on the loop thread in OnInitialize() ---
    private nint _port;
    private PinnedWriteBufferPool _writeBuffer;      // IO-thread send pool
    private PinnedWriteBufferPool _obWriteBuffer;    // out-of-band (thread-safe via _obGate) send pool
    private readonly object _obGate = new();
    private Win32.OVERLAPPED_ENTRY* _entries;        // GQCSEx batch buffer
    private IocpOp* _ops;                            // op-context pool slab
    private volatile bool _portReady;

    public IocpShard(SocketSetOptions options)
    {
        _socketsPerShard = options.SocketsPerShard;
        _writeCount = options.WriteBuffersPerShard;
        _writeBufSize = options.BufferPageSize;
        _obWriteCount = options.OutOfBandWriteBuffersPerShard;
        _opCount = _socketsPerShard * 2 + 64; // recv + send per connection, plus a few accepts in flight
        // Everything native is deferred to OnInitialize (loop thread); the ctor stays inert so the
        // factory can be constructed on any OS.
    }

    // WSAStartup once per process; WSACleanup is left to process exit.
    private static int _wsaStarted;

    private static void EnsureWinsock()
    {
        if (Interlocked.CompareExchange(ref _wsaStarted, 1, 0) != 0) return;
        byte* wsaData = stackalloc byte[512]; // WSADATA — we never read it
        int rc = Win32.WSAStartup(0x0202, wsaData); // request Winsock 2.2
        if (rc != 0) throw new InvalidOperationException($"WSAStartup failed: {rc}");
    }

    protected override void OnInitialize()
    {
        EnsureWinsock();

        // Fresh port, concurrency 1 (a single dedicated thread services it). NULL/0 on failure.
        _port = Win32.CreateIoCompletionPort(Win32.INVALID_HANDLE_VALUE, 0, 0, 1);
        if (_port == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort failed");

        _writeBuffer = new PinnedWriteBufferPool(_writeCount, _writeBufSize);
        _obWriteBuffer = new PinnedWriteBufferPool(_obWriteCount, _writeBufSize);
        _entries = (Win32.OVERLAPPED_ENTRY*)NativeMemory.AllocZeroed(EntryBatch * (nuint)sizeof(Win32.OVERLAPPED_ENTRY));
        _ops = (IocpOp*)NativeMemory.AllocZeroed((nuint)_opCount * (nuint)sizeof(IocpOp)); // TODO: freelist wiring with the data path
        _portReady = true;
    }

    protected override void OnRun()
    {
        PinLoopThread();

        while (IsActive)
        {
            uint removed = 0;
            bool ok = Win32.GetQueuedCompletionStatusEx(_port, _entries, EntryBatch, &removed, Win32.INFINITE, alertable: false);
            if (!ok)
            {
                // Port closed during shutdown (ERROR_ABANDONED_WAIT_0) surfaces here; the IsActive
                // check ends the loop. Anything else is transient — re-check and retry.
                continue;
            }

            for (uint i = 0; i < removed; i++)
            {
                ref Win32.OVERLAPPED_ENTRY e = ref _entries[i];
                if (e.lpCompletionKey == WakeKey)
                {
                    // Wake: cross-thread work was marshaled in. Draining it is the data-path slice.
                    continue;
                }

                // Real I/O completion: OVERLAPPED is the first field, so the OVERLAPPED* IS the op ctx.
                IocpOp* op = (IocpOp*)e.lpOverlapped;
                switch (op->Kind)
                {
                    case OpKind.Accept: /* TODO HandleAccept(op, e.dwNumberOfBytesTransferred); */ break;
                    case OpKind.Connect: /* TODO HandleConnect(op); */ break;
                    case OpKind.Recv: /* TODO HandleRecv(op, e.dwNumberOfBytesTransferred); */ break;
                    case OpKind.Send: /* TODO HandleSend(op, e.dwNumberOfBytesTransferred); */ break;
                }
            }
        }
    }

    protected override void OnStop() => Poke(); // wake the loop so it observes !IsActive

    protected override void OnShutdown()
    {
        _portReady = false;
        // TODO: close all live connection sockets here (with the data path).
        if (_ops != null) { NativeMemory.Free(_ops); _ops = null; }
        if (_entries != null) { NativeMemory.Free(_entries); _entries = null; }
        _writeBuffer.Dispose();
        _obWriteBuffer.Dispose();
        if (_port != 0) { Win32.CloseHandle(_port); _port = 0; }
    }

    /// <summary>Queue a wake packet (verbatim key+overlapped, no I/O) so the loop re-checks its
    /// cross-thread queues / IsActive. The eventfd analogue.</summary>
    private void Poke()
    {
        if (!_portReady) return;
        Win32.PostQueuedCompletionStatus(_port, 0, WakeKey, null);
    }

    // Pin the loop thread to a core (best-effort). The base Run() pins on Linux; Windows is done here
    // since it needs SetThreadAffinityMask. TODO: intersect with the process affinity mask (the Linux
    // path already respects an externally-applied set; this simple version doesn't yet).
    private void PinLoopThread()
    {
        if (!Parent.Options.PinWorkerThreads || !OperatingSystem.IsWindows()) return;
        nuint mask = (nuint)1 << (Shard % Environment.ProcessorCount);
        Win32.SetThreadAffinityMask(Win32.GetCurrentThread(), mask);
    }

    // --- entry points: data path not yet implemented (skeleton) ---

    private static NotImplementedException NotYet([System.Runtime.CompilerServices.CallerMemberName] string? m = null)
        => new($"IocpShard.{m}: IOCP data path not yet implemented (skeleton).");

    public override void Listen(EndPoint endpoint, object? userToken, bool local) => throw NotYet();
    public override void Connect(EndPoint endpoint, object? userToken) => throw NotYet();
    public override void ListenHandle(nint handle, object? userToken) => throw NotYet();
}
#endif
