#if NET // Windows IOCP backend; compiled out of the netfx fallback build.
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SocketSets.Native;

namespace SocketSets.Windows;

/// <summary>
/// A single-threaded IOCP event loop — the Windows analogue of <c>IoUringShard</c>. Exactly one
/// thread owns the completion port (created with concurrency 1); cross-thread work (accept hand-off,
/// Close) is marshaled in and the loop woken with <see cref="Win32.PostQueuedCompletionStatus"/> (the
/// eventfd analogue), and completions are drained in batches with
/// <see cref="Win32.GetQueuedCompletionStatusEx"/>. <see cref="Listen"/>/<see cref="Connect"/> submit
/// their overlapped ops directly (Winsock allows this from any thread); only per-connection state
/// mutation and completion processing are loop-thread-exclusive.
///
/// Data path (this slice — first light; not yet exercised on Windows):
///  - accept: one outstanding <c>AcceptEx</c> per listener, re-posted on completion; the accepted
///    socket is bounced round-robin to a shard which associates + arms it (IOCP has no reuse-port
///    load balancing, so there is a single acceptor — TODO: post N accepts / accept from N threads).
///  - connect: <c>ConnectEx</c> (requires a bound socket).
///  - recv: one <c>WSARecv</c> per connection, continuously re-armed; a per-connection recv buffer.
///  - send: copy-based echo through the write-buffer pool, one send in flight per connection
///    (SendBusy), an echo that arrives mid-send is copied out and queued (no no-copy path yet).
///  - close: <c>closesocket</c> aborts the pending recv/send; the slot is held (defer-recycle) until
///    those completions drain, so no stale completion lands on a re-tenanted slot.
/// </summary>
internal sealed unsafe class IocpShard : SocketSetShard
{
    private const int EntryBatch = 128;              // completions dequeued per GetQueuedCompletionStatusEx
    private const int AddrStride = 128;              // per-address storage for AcceptEx (covers sockaddr_in and _un)
    private const int AcceptBufSize = 2 * AddrStride; // AcceptEx output buffer: local + remote sockaddr, no initial data
    private static readonly nuint WakeKey = unchecked((nuint)(-1)); // reserved completion key for PQCS wakes

    // Per-operation context: an OVERLAPPED (which the kernel writes and hands back) plus our own state.
    // The OVERLAPPED MUST be the first field so an OVERLAPPED* is bit-identical to the op ctx* — we cast
    // straight back on completion (no CONTAINING_RECORD offset). Blittable, so it lives in native memory.
    internal enum OpKind : int { Accept = 0, Connect = 1, Recv = 2, Send = 3 }

    // Recv/Send/Connect op context. Kind sits right after the OVERLAPPED, at the same offset as in
    // AcceptOp, so the loop can read Kind through an IocpOp* regardless of the real op type.
    [StructLayout(LayoutKind.Sequential)]
    internal struct IocpOp
    {
        public Win32.OVERLAPPED Overlapped; // MUST be first (offset 0)
        public OpKind Kind;
        public uint Slot;
        public int Buf;                     // recv-pool index (Recv) / write-pool index (Send)
    }

    // Accept op context. No slot yet (there is no connection until the accept completes), so it carries
    // a GCHandle to its managed AcceptState instead. Same {OVERLAPPED, Kind} prefix as IocpOp.
    [StructLayout(LayoutKind.Sequential)]
    internal struct AcceptOp
    {
        public Win32.OVERLAPPED Overlapped; // MUST be first (offset 0)
        public OpKind Kind;
        public nint Handle;                 // GCHandle.ToIntPtr(AcceptState)
    }

    // Everything a single outstanding AcceptEx needs. Reused across accepts on one listener.
    private sealed class AcceptState
    {
        public nint Listener;
        public nint AcceptSocket;
        public nint Buf;      // (byte*) native AcceptEx output buffer (AcceptBufSize)
        public nint Op;       // (AcceptOp*) native op context
        public object? Token; // default UserToken for connections accepted here
        public int Af;        // family/proto for creating the next accept socket
        public int Proto;
        public GCHandle Gc;   // keeps this instance alive + gives a stable identity in AcceptOp.Handle
    }

    // --- slot table (1-based ids; id 0 == "none"). Connections are pooled and reused. ---
    private readonly IocpConnection[] _conns;
    private uint _clientStart;

    // --- options snapshot ---
    private readonly int _socketsPerShard;
    private readonly int _writeCount;
    private readonly int _writeBufSize;
    private readonly int _recvCount;
    private readonly int _recvBufSize;
    private readonly int _opCount;
    private readonly int _listenBacklog;
    private readonly int _acceptConcurrency;

    // --- created on the loop thread in OnInitialize() ---
    private nint _port;
    private PinnedWriteBufferPool _writeBuffer;      // IO-thread send pool (out-of-band writes ride this too, via Pending)
    private PinnedWriteBufferPool _recvBuffer;       // one buffer per live connection
    private Win32.OVERLAPPED_ENTRY* _entries;        // GQCSEx batch buffer
    private IocpOp* _ops;                            // op-context slab: recv=[2i], send=[2i+1] per slot i
    private byte* _connectAddrs;                     // per-slot stable sockaddr storage for ConnectEx
    private volatile bool _portReady;

    // Accept states (one per listener). Only mutated under _acceptGate; iterated at shutdown (loop stopped).
    private readonly List<AcceptState> _acceptStates = [];
    private readonly object _acceptGate = new();

    // --- cross-thread queues drained on the loop thread ---
    // Accepted sockets handed to this shard by the single acceptor. The default token travels with the
    // socket since the target shard has no listener of its own to look it up on.
    private readonly ConcurrentQueue<(nint Socket, object? Token)> _incoming = [];
    // Close requests marshaled from arbitrary threads. Generation-guarded so a request can't retract a
    // slot that has since been closed and re-tenanted.
    private readonly ConcurrentQueue<(uint Slot, uint Generation)> _closes = [];
    // Out-of-band flushed writes (Connection.Flush from any thread): a private byte[] + length + the
    // capturing generation, sent on the loop through the normal Pending path. Generation-guarded.
    private readonly ConcurrentQueue<(uint Slot, uint Generation, byte[] Data, int Len)> _flush = [];

    // Synchronous (FILE_SKIP_COMPLETION_PORT_ON_SUCCESS) recv/send completions that posted no port
    // packet. Loop-thread-only. Deferred here and drained ITERATIVELY (not by calling the handler
    // recursively): a saturated connection completes recv→echo→recv→… synchronously and would otherwise
    // recurse straight into a stack overflow. Bounded per pass (InlineBurst) so one busy or flooding
    // connection can't starve the port, other connections, or the IsActive/shutdown check.
    private struct InlineOp { public OpKind Kind; public uint Slot; public uint Bytes; public bool Failed; }
    private const int InlineBurst = 512;
    private readonly Queue<InlineOp> _inline = new();

    public IocpShard(SocketSetOptions options)
    {
        _socketsPerShard = options.SocketsPerShard;
        _writeCount = options.WriteBuffersPerShard;
        _writeBufSize = options.BufferPageSize;
        _recvBufSize = options.BufferPageSize;
        _recvCount = _socketsPerShard;         // one recv buffer per connection (recv is always armed)
        _opCount = _socketsPerShard * 2;       // recv + send per connection
        _listenBacklog = options.ListenBacklog;
        _acceptConcurrency = Math.Max(1, Math.Min(options.AcceptConcurrency, _socketsPerShard));
        // Everything native is deferred to OnInitialize (loop thread); the ctor stays inert so the
        // factory can be constructed on any OS.

        // Pre-allocate the connection table: one pooled instance per slot, reused across connection
        // lifetimes so accept/connect never allocates. The slot count is a hard cap on concurrent
        // connections per shard (InitClient returns null when full). A Connection is therefore a lease
        // on a slot, not an ownable object — a reference held past OnClosed may by then be a different
        // logical connection that reused the slot. Reuse is safe because every use is gated by the
        // per-slot Generation token (bumped on each InitClient): Close/writes capture the generation and
        // are dropped, not misdelivered, if the slot has since been re-tenanted — the same pattern as
        // IValueTaskSource's token, which validates a stashed ValueTask against the source's current
        // version so a stale await can't observe a pooled source's next result.
        _conns = new IocpConnection[_socketsPerShard];
        for (int i = 0; i < _conns.Length; i++)
            _conns[i] = new IocpConnection(this, (uint)i + 1);
        SetShardCapacity(_conns.Length); // reservation ceiling == slot-table size
    }

    // WSAStartup once per process; WSACleanup is left to process exit.
    private static readonly object _wsaGate = new();
    private static bool _wsaStarted;

    private static void EnsureWinsock()
    {
        // Double-checked: the flag is published only AFTER WSAStartup returns success, so (a) a failed
        // startup stays retryable rather than poisoning the flag, and (b) concurrent shard inits block
        // on the gate until the winner has truly finished — a fast return here must mean Winsock is up,
        // not merely "being brought up" (otherwise a racing caller hits WSANOTINITIALISED).
        if (Volatile.Read(ref _wsaStarted)) return;
        lock (_wsaGate)
        {
            if (_wsaStarted) return;
            byte* wsaData = stackalloc byte[512]; // WSADATA — we never read it
            int rc = Win32.WSAStartup(0x0202, wsaData); // request Winsock 2.2
            if (rc != 0) throw new InvalidOperationException($"WSAStartup failed: {rc}");
            Volatile.Write(ref _wsaStarted, true);
        }
    }

    protected override void OnInitialize()
    {
        EnsureWinsock();

        // Fresh port, concurrency 1 (a single dedicated thread services it). NULL/0 on failure.
        _port = Win32.CreateIoCompletionPort(Win32.INVALID_HANDLE_VALUE, 0, 0, 1);
        if (_port == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort failed");

        _writeBuffer = new PinnedWriteBufferPool(_writeCount, _writeBufSize);
        _recvBuffer = new PinnedWriteBufferPool(_recvCount, _recvBufSize);
        _entries = (Win32.OVERLAPPED_ENTRY*)NativeMemory.AllocZeroed(EntryBatch * (nuint)sizeof(Win32.OVERLAPPED_ENTRY));
        // Op-context OVERLAPPEDs are zeroed once here and never re-zeroed per op: we never set hEvent (it
        // stays null → IOCP notification), Offset/OffsetHigh are ignored for socket I/O, and the kernel
        // overwrites Internal/InternalHigh on every completion. So the submit paths set only Kind/Slot/Buf.
        _ops = (IocpOp*)NativeMemory.AllocZeroed((nuint)_opCount * (nuint)sizeof(IocpOp));
        _connectAddrs = (byte*)NativeMemory.AllocZeroed((nuint)_socketsPerShard * AddrStride);
        _portReady = true;
    }

    protected override void OnRun()
    {
        PinLoopThread();

        while (IsActive)
        {
            // Honour marshaled cross-thread work before blocking for completions (a wake packet is what
            // unblocks GQCSEx when new work is enqueued).
            DrainCrossThread();

            // Process synchronous (FILE_SKIP) completions before we consider blocking — a recv/send that
            // completed inline posts no packet, so leaving one queued while we block would strand it.
            DrainInline();

            // Block only when there's no pending inline work; otherwise poll (timeout 0, GC-transition
            // suppressed) so inline bursts interleave with the port and IsActive stays responsive.
            uint removed = 0;
            bool ok = _inline.Count > 0
                ? Win32.GetQueuedCompletionStatusExNonBlocking(_port, _entries, EntryBatch, &removed, 0, alertable: false)
                : Win32.GetQueuedCompletionStatusExBlocking(_port, _entries, EntryBatch, &removed, Win32.INFINITE, alertable: false);
            if (!ok)
            {
                // WAIT_TIMEOUT (nothing ready on a poll), or the port closed during shutdown
                // (ERROR_ABANDONED_WAIT_0) — the IsActive check ends the loop. Re-loop either way.
                continue;
            }

            for (uint i = 0; i < removed; i++)
            {
                ref Win32.OVERLAPPED_ENTRY e = ref _entries[i];
                if (e.lpCompletionKey == WakeKey || e.lpOverlapped == null)
                    continue; // wake packet: work is drained at the top of the next iteration

                // Real I/O completion: OVERLAPPED is the first field, so the OVERLAPPED* IS the op ctx.
                // Kind sits at the same offset in every op-ctx type, so read it through IocpOp*.
                IocpOp* op = (IocpOp*)e.lpOverlapped;
                bool failed = e.lpOverlapped->Internal != 0; // NTSTATUS; 0 == STATUS_SUCCESS
                uint bytes = e.dwNumberOfBytesTransferred;
                switch (op->Kind)
                {
                    case OpKind.Accept: HandleAccept((AcceptOp*)e.lpOverlapped, failed); break;
                    case OpKind.Connect: HandleConnect(op->Slot, failed); break;
                    case OpKind.Recv: HandleRecv(op->Slot, bytes, failed); break;
                    case OpKind.Send: HandleSend(op->Slot, bytes, failed); break;
                }
            }
        }
    }

    private void DrainCrossThread()
    {
        while (_incoming.TryDequeue(out var inbound))
            AdoptAccepted(inbound.Socket, inbound.Token);

        while (_closes.TryDequeue(out var c))
        {
            var conn = _conns[c.Slot - 1];
            if (conn.Generation == c.Generation && conn.Socket != 0) CloseClient(c.Slot);
        }

        while (_flush.TryDequeue(out var f))
            PumpFlush(f.Slot, f.Generation, f.Data, f.Len);
    }

    // Defer a synchronously-completed recv/send (no port packet was posted for it). Loop-thread-only.
    private void QueueInline(OpKind kind, uint slot, uint bytes, bool failed)
        => _inline.Enqueue(new InlineOp { Kind = kind, Slot = slot, Bytes = bytes, Failed = failed });

    // Drain deferred synchronous completions iteratively (handlers may enqueue more as they re-arm/echo).
    // Bounded per pass so the loop periodically re-checks the port and IsActive.
    private void DrainInline()
    {
        for (int budget = InlineBurst; budget > 0 && _inline.Count > 0; budget--)
        {
            var io = _inline.Dequeue();
            switch (io.Kind)
            {
                case OpKind.Recv: HandleRecv(io.Slot, io.Bytes, io.Failed); break;
                case OpKind.Send: HandleSend(io.Slot, io.Bytes, io.Failed); break;
            }
        }
    }

    protected override void OnStop() => Poke(); // wake the loop so it observes !IsActive

    protected override void OnShutdown()
    {
        _portReady = false;

        // Close listeners + outstanding accept sockets and free their native state (loop stopped → no race).
        lock (_acceptGate)
        {
            foreach (var st in _acceptStates)
            {
                if (st.AcceptSocket != 0) Win32.closesocket(st.AcceptSocket);
                if (st.Listener != 0) Win32.closesocket(st.Listener);
                if (st.Buf != 0) NativeMemory.Free((void*)st.Buf);
                if (st.Op != 0) NativeMemory.Free((void*)st.Op);
                if (st.Gc.IsAllocated) st.Gc.Free();
            }
            _acceptStates.Clear();
        }

        // Close any still-live connection sockets.
        for (int i = 0; i < _conns.Length; i++)
        {
            nint s = Interlocked.Exchange(ref _conns[i].Socket, 0);
            if (s != 0) Win32.closesocket(s);
        }

        if (_ops != null) { NativeMemory.Free(_ops); _ops = null; }
        if (_entries != null) { NativeMemory.Free(_entries); _entries = null; }
        if (_connectAddrs != null) { NativeMemory.Free(_connectAddrs); _connectAddrs = null; }
        _writeBuffer.Dispose();
        _recvBuffer.Dispose();
        if (_port != 0) { Win32.CloseHandle(_port); _port = 0; }
    }

    /// <summary>Queue a wake packet (verbatim key, no I/O, null overlapped) so the loop re-checks its
    /// cross-thread queues / IsActive. The eventfd analogue.</summary>
    private void Poke()
    {
        if (!_portReady) return;
        Win32.PostQueuedCompletionStatus(_port, 0, WakeKey, null);
    }

    /// <summary>Marshal an accepted socket onto this shard's loop (called from the acceptor shard's
    /// loop). The socket is process-global so any port can adopt it.</summary>
    internal void EnqueueInbound(nint socket, object? token)
    {
        _incoming.Enqueue((socket, token));
        Poke();
    }

    /// <summary>Marshal a close request onto the loop thread (from <see cref="IocpConnection.Close"/>).</summary>
    internal void SubmitClose(uint slot, uint generation)
    {
        _closes.Enqueue((slot, generation));
        Poke();
    }

    /// <summary>Marshal an out-of-band flushed write onto the loop thread (from <see cref="WindowsOutboundConnection.Flush"/>).</summary>
    internal void SubmitFlush(uint slot, uint generation, byte[] data, int length)
    {
        _flush.Enqueue((slot, generation, data, length));
        Poke();
    }

    // =====================================================================
    // Slot table
    // =====================================================================

    // Claim a free slot for a socket. Lock-free (CAS on the pooled connection's Socket), so callable
    // from the loop thread (accept) or an arbitrary thread (connect). Returns null if the table is full.
    private IocpConnection? InitClient(nint socket, object? userToken, SocketSet.SocketFlags flags)
    {
        var conns = _conns;
        // Callers reserve (TryReserve) before claiming, so a free row is guaranteed; retry past a
        // concurrent claimer that grabbed the spotted row. Bounded backstop (returns null → caller
        // releases + drops) against an unreserved caller / counter drift.
        for (int pass = 0; pass < 32; pass++)
        {
            var offset = (uint)Interlocked.Increment(ref _clientStart);
            for (int i = 0; i < conns.Length; i++)
            {
                var conn = conns[(i + offset) % (uint)conns.Length];
                if (Interlocked.CompareExchange(ref conn.Socket, socket, 0) == 0)
                {
                    conn.UserToken = userToken;
                    conn.Flags = flags;
                    conn.Opened = false;
                    conn.Closing = false;
                    conn.RecvArmed = false;
                    conn.SendBusy = false;
                    conn.SkipOnSuccess = false;
                    conn.RecvBuf = -1;
                    conn.SendBuf = -1;
                    conn.Pending?.Clear();
                    // Publish a fresh generation last: any out-of-band Close captured against the previous
                    // tenant now mismatches and is dropped rather than misapplied.
                    Volatile.Write(ref conn.Generation, conn.Generation + 1);
                    return conn;
                }
            }
            Thread.Yield(); // every row contended this pass; let claimers settle
        }

        return null; // no free slot despite a reservation → counter drift / unreserved caller (a bug)
    }

    /// <summary>Begin tearing a connection down (loop thread). Idempotent. Fires OnClosed now, but does
    /// NOT free the slot yet — closesocket aborts the in-flight recv/send, and the slot is finalized
    /// only once those completions have drained (see <see cref="TryFinalize"/>), so no stale completion
    /// lands on a re-tenanted slot.</summary>
    private void CloseClient(uint slot)
    {
        if (slot == 0) return;
        var conn = _conns[slot - 1];
        if (conn.Socket == 0 || conn.Closing) return; // free / already tearing down
        conn.Closing = true;

        if (conn.Opened)
        {
            conn.Opened = false;
            try { Parent.OnClosed(conn); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        // shutdown sends the FIN; closesocket aborts the pending recv/send (they complete with an error,
        // which clears RecvArmed/SendBusy). Socket stays non-zero (the now-closed handle) as the claimed
        // marker until TryFinalize publishes the slot free.
        if (Parent.Options.ResetOnClose)
        {
            // Abortive: SO_LINGER{1,0} → closesocket sends RST, no FIN, no TIME_WAIT on the active closer.
            var lg = new Win32.LINGER { l_onoff = 1, l_linger = 0 };
            Win32.setsockopt(conn.Socket, Win32.SOL_SOCKET, Win32.SO_LINGER, &lg, sizeof(Win32.LINGER));
        }
        else
        {
            Win32.shutdown(conn.Socket, Win32.SD_BOTH);
        }
        Win32.closesocket(conn.Socket);
        TryFinalize(conn, slot); // nothing in flight → finalize immediately
    }

    // Finalize once all in-flight ops for a closing slot have drained: release its buffers and publish
    // the slot free LAST (only now may a racing InitClient claim it).
    private void TryFinalize(IocpConnection conn, uint slot)
    {
        if (!conn.Closing || conn.RecvArmed || conn.SendBusy) return;

        if (conn.RecvBuf >= 0) { _recvBuffer.Release(conn.RecvBuf); conn.RecvBuf = -1; }
        if (conn.SendBuf >= 0) { _writeBuffer.Release(conn.SendBuf); conn.SendBuf = -1; }
        // Return any queued (pooled) echo staging buffers before recycling the slot.
        if (conn.Pending is { } pending)
            while (pending.Count > 0) ArrayPool<byte>.Shared.Return(pending.Dequeue().Array!);
        conn.UserToken = null;
        conn.Flags = 0;
        conn.Closing = false;
        Volatile.Write(ref conn.Socket, 0); // publish free last (socket already closed in CloseClient)
        ReleaseReservation();               // paired with the TryReserve that placed this connection
    }

    private IocpOp* RecvOp(uint slot) => &_ops[(slot - 1) * 2];
    private IocpOp* SendOp(uint slot) => &_ops[(slot - 1) * 2 + 1];

    // =====================================================================
    // Public entry points
    // =====================================================================

    public override void Listen(EndPoint endpoint, object? userToken, bool local)
    {
        EnsureWinsock();
        // IOCP has no reuse-port load balancing, so this is always a single listener (the factory reports
        // CanMultiBind == false, so SocketSet.Listen routes each endpoint to just one round-robin shard).
        // This shard drives the accept and bounces each accepted connection round-robin, so `local` is
        // moot here — we always bounce.
        var (listener, af, proto) = CreateListener(endpoint);
        Win32.LoadExtensions(listener);
        if (Win32.CreateIoCompletionPort(listener, _port, 0, 0) == 0)
        {
            Win32.closesocket(listener);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort(listener) failed");
        }

        StartAccept(listener, af, proto, userToken);
    }

    public override void ListenHandle(nint handle, object? userToken)
    {
        EnsureWinsock();
        if (handle == 0 || handle == Win32.INVALID_SOCKET)
            throw new ArgumentOutOfRangeException(nameof(handle), "Invalid socket handle.");
        // Handed-over listener, assumed already bound + listen()ed. We don't know the family for sure;
        // assume TCP/IPv4 for the accept sockets (matches the only bind path we build today).
        Win32.LoadExtensions(handle);
        if (Win32.CreateIoCompletionPort(handle, _port, 0, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort(listener) failed");
        StartAccept(handle, Win32.AF_INET, Win32.IPPROTO_TCP, userToken);
    }

    public override void Connect(EndPoint endpoint, object? userToken)
    {
        EnsureWinsock();
        (int af, int proto) = endpoint switch
        {
            IPEndPoint => (Win32.AF_INET, Win32.IPPROTO_TCP),
            UnixDomainSocketEndPoint => (Win32.AF_UNIX, 0),
            _ => throw new NotSupportedException($"{nameof(Connect)} on {endpoint.GetType().Name} is not supported."),
        };

        // This shard holds a reservation (TryPlace took it before dispatching here); release it on any
        // failure before the slot is claimed, so a rejected connect doesn't permanently consume capacity.
        nint s = Win32.WSASocketW(af, Win32.SOCK_STREAM, proto, null, 0, Win32.WSA_FLAG_OVERLAPPED);
        if (s == Win32.INVALID_SOCKET) { ReleaseReservation(); throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW failed"); }
        Win32.LoadExtensions(s);

        int one = 1;
        if (af == Win32.AF_INET)
        {
            Win32.setsockopt(s, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int));
            // ConnectEx requires the socket be explicitly bound first.
            Win32.SockAddrIn any = default;
            any.sin_family = (ushort)Win32.AF_INET;
            Win32.bind(s, &any, 16);
        }

        var conn = InitClient(s, userToken, SocketSet.SocketFlags.None);
        if (conn is null) { Win32.closesocket(s); ReleaseReservation(); throw new InvalidOperationException("Shard socket table is full."); }
        uint slot = conn.Slot;

        if (Win32.CreateIoCompletionPort(s, _port, slot, 0) == 0)
        {
            Win32.closesocket(s);
            Volatile.Write(ref conn.Socket, 0);
            ReleaseReservation();
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort(connect) failed");
        }

        // Build the target sockaddr into this slot's stable native storage (the kernel dereferences it
        // asynchronously once ConnectEx is posted).
        byte* addrPtr = _connectAddrs + (nint)(slot - 1) * AddrStride;
        uint addrLen;
        switch (endpoint)
        {
            case IPEndPoint ip:
            {
                var sa = (Win32.SockAddrIn*)addrPtr;
                *sa = default;
                sa->sin_family = (ushort)Win32.AF_INET;
                sa->sin_port = Win32.Htons((ushort)ip.Port);
                var b = ip.Address.GetAddressBytes(); // 4 bytes, network order
                byte* dst = (byte*)&sa->sin_addr;
                dst[0] = b[0]; dst[1] = b[1]; dst[2] = b[2]; dst[3] = b[3];
                addrLen = 16;
                break;
            }
            case UnixDomainSocketEndPoint uds:
                addrLen = Win32.SockAddrUn.Init((Win32.SockAddrUn*)addrPtr, uds.ToString());
                break;
            default:
                Win32.closesocket(s);
                Volatile.Write(ref conn.Socket, 0);
                ReleaseReservation();
                throw new NotSupportedException(endpoint.GetType().Name);
        }

        // Connect reuses the slot's recv op-ctx (no recv is armed yet); re-armed as a recv on completion.
        IocpOp* op = RecvOp(slot);
        op->Kind = OpKind.Connect;
        op->Slot = slot;
        op->Buf = 0;

        uint sent = 0;
        int ok = Win32.ConnectEx(s, addrPtr, (int)addrLen, null, 0, &sent, &op->Overlapped);
        if (ok == 0 && Win32.WSAGetLastError() != Win32.WSA_IO_PENDING)
        {
            Win32.closesocket(s);
            Volatile.Write(ref conn.Socket, 0);
            ReleaseReservation();
            throw new Win32Exception(Win32.WSAGetLastError(), "ConnectEx failed");
        }
        // ok != 0 (immediate) or WSA_IO_PENDING → a completion is queued to the port.
    }

    // Create, bind and listen a Winsock socket for the endpoint. Throws on failure.
    private (nint socket, int af, int proto) CreateListener(EndPoint endpoint)
    {
        switch (endpoint)
        {
            case IPEndPoint ip:
            {
                nint s = Win32.WSASocketW(Win32.AF_INET, Win32.SOCK_STREAM, Win32.IPPROTO_TCP, null, 0, Win32.WSA_FLAG_OVERLAPPED);
                if (s == Win32.INVALID_SOCKET) throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW failed");
                int one = 1;
                Win32.setsockopt(s, Win32.SOL_SOCKET, Win32.SO_REUSEADDR, &one, sizeof(int));
                Win32.setsockopt(s, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int));
                Win32.SockAddrIn addr = default;
                addr.sin_family = (ushort)Win32.AF_INET;
                addr.sin_port = Win32.Htons((ushort)ip.Port);
                addr.sin_addr = 0; // INADDR_ANY  TODO: honour the actual IP
                if (Win32.bind(s, &addr, 16) == Win32.SOCKET_ERROR)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "IP bind() failed");
                if (Win32.listen(s, _listenBacklog) == Win32.SOCKET_ERROR)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "IP listen() failed");
                return (s, Win32.AF_INET, Win32.IPPROTO_TCP);
            }
            case UnixDomainSocketEndPoint uds:
            {
                nint s = Win32.WSASocketW(Win32.AF_UNIX, Win32.SOCK_STREAM, 0, null, 0, Win32.WSA_FLAG_OVERLAPPED);
                if (s == Win32.INVALID_SOCKET) throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW(AF_UNIX) failed");
                UnixSocketFile.PrepareForBind(uds.ToString()); // clear a stale socket file (Windows AF_UNIX is filesystem-only)
                Win32.SockAddrUn addr;
                uint len = Win32.SockAddrUn.Init(&addr, uds.ToString());
                if (Win32.bind(s, &addr, (int)len) == Win32.SOCKET_ERROR)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UDS bind(AF_UNIX) failed");
                if (Win32.listen(s, _listenBacklog) == Win32.SOCKET_ERROR)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UDS listen() failed");
                return (s, Win32.AF_UNIX, 0);
            }
            default:
                throw new NotSupportedException(endpoint.GetType().Name);
        }
    }

    // Arm a pool of AcceptConcurrency outstanding AcceptEx on the listener — a backlog of accept
    // consumers so connect bursts don't serialize on one accept-at-a-time, and one failed re-post
    // doesn't stall the whole listener. Each completion re-posts its own state (see HandleAccept).
    private void StartAccept(nint listener, int af, int proto, object? token)
    {
        for (int i = 0; i < _acceptConcurrency; i++)
        {
            var st = new AcceptState
            {
                Listener = listener,
                Token = token,
                Af = af,
                Proto = proto,
                Buf = (nint)NativeMemory.AllocZeroed(AcceptBufSize),
                Op = (nint)NativeMemory.AllocZeroed((nuint)sizeof(AcceptOp)),
            };
            st.Gc = GCHandle.Alloc(st);
            ((AcceptOp*)st.Op)->Handle = GCHandle.ToIntPtr(st.Gc);
            lock (_acceptGate) _acceptStates.Add(st);
            PostAccept(st);
        }
    }

    // Create a fresh accept socket and post AcceptEx into the listener. Called on Listen (any thread)
    // and on each accept completion (loop thread); AcceptEx submission is thread-safe either way.
    private void PostAccept(AcceptState st)
    {
        nint acc = Win32.WSASocketW(st.Af, Win32.SOCK_STREAM, st.Proto, null, 0, Win32.WSA_FLAG_OVERLAPPED);
        if (acc == Win32.INVALID_SOCKET)
        {
            System.Diagnostics.Debug.WriteLine($"WSASocketW(accept) failed: {Marshal.GetLastPInvokeError()}");
            st.AcceptSocket = 0;
            return; // accept stalls on this listener; TODO: retry/backoff
        }

        st.AcceptSocket = acc;
        var op = (AcceptOp*)st.Op;
        op->Kind = OpKind.Accept;
        // Handle already set at StartAccept.

        uint recvd = 0;
        int ok = Win32.AcceptEx(st.Listener, acc, (void*)st.Buf, 0, AddrStride, AddrStride, &recvd, &op->Overlapped);
        if (ok == 0 && Win32.WSAGetLastError() != Win32.WSA_IO_PENDING)
        {
            System.Diagnostics.Debug.WriteLine($"AcceptEx failed: {Win32.WSAGetLastError()}");
            Win32.closesocket(acc);
            st.AcceptSocket = 0;
            // TODO: retry/backoff rather than silently stalling this listener.
        }
        // ok != 0 (immediate) or WSA_IO_PENDING → a completion is queued.
    }

    // =====================================================================
    // Completion handlers (loop thread)
    // =====================================================================

    private void HandleAccept(AcceptOp* op, bool failed)
    {
        var st = (AcceptState)GCHandle.FromIntPtr(op->Handle).Target!;
        nint acc = st.AcceptSocket;

        if (failed || acc == 0)
        {
            if (acc != 0) Win32.closesocket(acc);
            PostAccept(st);
            return;
        }

        // Required before an AcceptEx socket can be used: inherit the listener's properties/state.
        nint listener = st.Listener;
        Win32.setsockopt(acc, Win32.SOL_SOCKET, Win32.SO_UPDATE_ACCEPT_CONTEXT, &listener, sizeof(nint));

        // Single acceptor → place on the first shard with a free slot (capacity-aware; drops only if
        // every shard is full).
        var target = (IocpShard?)Parent.TryPlace();
        if (target is not null) target.EnqueueInbound(acc, st.Token);
        else Win32.closesocket(acc); // every shard full → drop (runtime shard growth would expand here)

        PostAccept(st); // keep the listener saturated
    }

    // Associate an accepted socket with THIS shard's port, run OnAccept, arm recv, fire any initial send.
    private void AdoptAccepted(nint socket, object? token)
    {
        int one = 1;
        Win32.setsockopt(socket, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int)); // harmless on AF_UNIX

        var conn = InitClient(socket, token, SocketSet.SocketFlags.None);
        if (conn is null) { Win32.closesocket(socket); ReleaseReservation(); return; }
        uint slot = conn.Slot;

        if (Win32.CreateIoCompletionPort(socket, _port, slot, 0) == 0)
        {
            Win32.closesocket(socket);
            Volatile.Write(ref conn.Socket, 0);
            ReleaseReservation();
            return;
        }

        // Handle synchronous recv/send completions inline (skip the completion-port round-trip). If the
        // flag is rejected, SkipOnSuccess stays false and the socket keeps the always-async model.
        conn.SkipOnSuccess = Win32.SetFileCompletionNotificationModes(socket,
            (byte)(Win32.FILE_SKIP_COMPLETION_PORT_ON_SUCCESS | Win32.FILE_SKIP_SET_EVENT_ON_HANDLE));

        if (!_recvBuffer.TryLease(out int ri, out _))
        {
            // Recv pool exhausted (should not happen: sized to the connection table). Drop the connection.
            Win32.closesocket(socket);
            Volatile.Write(ref conn.Socket, 0);
            ReleaseReservation();
            return;
        }
        conn.RecvBuf = ri;

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _writeBufSize : 0);
        conn.Opened = true;
        Parent.OnAccept(ref ctx);

        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) ArmRecv(conn);

        int sb = ctx.SendBytes;
        if (leased && sb > 0 && conn.Socket != 0 && !conn.Closing) SubmitSendBuffer(conn, slot, wi, sb);
        else if (leased) _writeBuffer.Release(wi);
    }

    private void HandleConnect(uint slot, bool failed)
    {
        var conn = _conns[slot - 1];
        if (failed || conn.Socket == 0) { CloseClient(slot); return; }

        Win32.setsockopt(conn.Socket, Win32.SOL_SOCKET, Win32.SO_UPDATE_CONNECT_CONTEXT, null, 0);

        // Handle synchronous recv/send completions inline (see AdoptAccepted). Set after connect
        // completed, so ConnectEx itself stayed on the always-async path.
        conn.SkipOnSuccess = Win32.SetFileCompletionNotificationModes(conn.Socket,
            (byte)(Win32.FILE_SKIP_COMPLETION_PORT_ON_SUCCESS | Win32.FILE_SKIP_SET_EVENT_ON_HANDLE));

        if (!_recvBuffer.TryLease(out int ri, out _)) { CloseClient(slot); return; }
        conn.RecvBuf = ri;

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _writeBufSize : 0);
        conn.Opened = true;
        Parent.OnConnect(ref ctx);

        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) ArmRecv(conn);

        int sb = ctx.SendBytes;
        if (leased && sb > 0 && conn.Socket != 0 && !conn.Closing) SubmitSendBuffer(conn, slot, wi, sb);
        else if (leased) _writeBuffer.Release(wi);
    }

    private void HandleRecv(uint slot, uint bytes, bool failed)
    {
        var conn = _conns[slot - 1];

        if (conn.Closing) { conn.RecvArmed = false; TryFinalize(conn, slot); return; }
        if (failed || bytes == 0)
        {
            // error, or graceful EOF (0 bytes). RecvArmed cleared first so CloseClient's TryFinalize can
            // proceed once the send (if any) also drains.
            conn.RecvArmed = false;
            CloseClient(slot);
            return;
        }

        // RecvArmed stays TRUE across DeliverReceive so that if it closes the connection (e.g. write pool
        // exhausted), TryFinalize won't finalize (and re-tenant) the slot out from under us here.
        bool keep = DeliverReceive(conn, slot, (int)bytes);

        if (keep && conn.Socket != 0 && !conn.Closing && (conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0)
        {
            ArmRecv(conn); // re-arm (RecvArmed remains true)
        }
        else
        {
            conn.RecvArmed = false;   // this recv op is done; no re-arm
            TryFinalize(conn, slot);  // finalize now if closing and the send has drained
        }
    }

    // Dispatch OnReceive and, if it set a response, send it (copy through the write pool). Returns false
    // only if it tore the connection down (so the caller stops receiving).
    private bool DeliverReceive(IocpConnection conn, uint slot, int bytes)
    {
        byte* rp = _recvBuffer.Address(conn.RecvBuf);
        var ctx = new SocketSet.ReceiveContext(conn, rp, _recvBufSize, bytes);
        Parent.OnReceive(ref ctx);

        int rb = ctx.ResponseBytes;
        if (rb <= 0 || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0) return true;

        if (conn.SendBusy)
        {
            // A send is already in flight: stash the response and queue it behind the current one. The
            // staging buffer is pooled (loop-thread rent/return hits the per-thread cache), so a pipelined
            // echo doesn't allocate per message; it's returned when drained (CompleteWrite) or dropped
            // (TryFinalize). Rent may over-size, so the ArraySegment carries the true length.
            var copy = ArrayPool<byte>.Shared.Rent(rb);
            Marshal.Copy((nint)rp, copy, 0, rb);
            (conn.Pending ??= new()).Enqueue(new ArraySegment<byte>(copy, 0, rb));
            return true;
        }

        return SendResponse(conn, slot, rp, rb);
    }

    // Copy a response into a leased write buffer and send it. Closes (returns false) if no buffer is free.
    private bool SendResponse(IocpConnection conn, uint slot, byte* src, int len)
    {
        if (!_writeBuffer.TryLease(out int wi, out byte* wp))
        {
            System.Diagnostics.Debug.WriteLine("Write buffer pool exhausted; closing connection.");
            CloseClient(slot);
            return false;
        }

        Buffer.MemoryCopy(src, wp, _writeBufSize, len);
        SubmitSendBuffer(conn, slot, wi, len); // sets SendBusy; closes on synchronous failure
        return !conn.Closing;
    }

    // Send the whole of an already-filled write buffer (initial send / echo). Manages send state.
    private void SubmitSendBuffer(IocpConnection conn, uint slot, int wi, int len)
    {
        conn.SendBuf = wi;
        conn.SendSent = 0;
        conn.SendTotal = len;
        conn.SendBusy = true;
        IssueSend(conn, slot, _writeBuffer.Address(wi), len, wi);
    }

    // Post a WSASend for p[0..len]; op->Buf carries the write-pool index. On a synchronous outcome
    // (success with FILE_SKIP, or any failure) no packet posts, so the completion is deferred inline —
    // a synchronous failure flows through as HandleSend(failed) → FailSend, same as an async error.
    private void IssueSend(IocpConnection conn, uint slot, byte* p, int len, int wi)
    {
        IocpOp* op = SendOp(slot);
        op->Kind = OpKind.Send;
        op->Slot = slot;
        op->Buf = wi;
        Win32.WSABUF b; b.len = (uint)len; b.buf = p;
        uint sent = 0;
        int rc = Win32.WSASend(conn.Socket, &b, 1, &sent, 0, &op->Overlapped, null);
        if (rc == 0)
        {
            if (conn.SkipOnSuccess) QueueInline(OpKind.Send, slot, sent, failed: false);
            return;
        }
        if (Marshal.GetLastPInvokeError() != Win32.WSA_IO_PENDING)
            QueueInline(OpKind.Send, slot, 0, failed: true);
        // WSA_IO_PENDING → an async completion will arrive.
    }

    // A send failed synchronously: release its buffer, clear the send slot, tear the connection down.
    private void FailSend(IocpConnection conn, uint slot)
    {
        if (conn.SendBuf >= 0) { _writeBuffer.Release(conn.SendBuf); conn.SendBuf = -1; }
        conn.SendBusy = false;
        CloseClient(slot);
    }

    private void HandleSend(uint slot, uint bytes, bool failed)
    {
        var conn = _conns[slot - 1];
        int wi = conn.SendBuf;

        if (conn.Closing)
        {
            if (wi >= 0) { _writeBuffer.Release(wi); conn.SendBuf = -1; }
            conn.SendBusy = false;
            TryFinalize(conn, slot);
            return;
        }

        if (failed) { FailSend(conn, slot); return; }

        conn.SendSent += (int)bytes;
        if (conn.SendSent < conn.SendTotal)
        {
            if (bytes == 0) { FailSend(conn, slot); return; } // no progress → dead peer
            // Partial send: resubmit the remainder from the same buffer (still one send in flight).
            byte* p = _writeBuffer.Address(wi) + conn.SendSent;
            IssueSend(conn, slot, p, conn.SendTotal - conn.SendSent, wi);
            return;
        }

        CompleteWrite(conn, slot);
    }

    // A send fully completed. Offer the freed buffer to OnWrite (pipeline the next message straight back
    // into it); failing that, drain a queued echo into it; failing both, release it and go idle.
    private void CompleteWrite(IocpConnection conn, uint slot)
    {
        int wi = conn.SendBuf;
        byte* wp = _writeBuffer.Address(wi);
        var ctx = new SocketSet.WriteContext(conn, wp, _writeBufSize);
        Parent.OnWrite(ref ctx);

        int next = ctx.SendBytes;
        if (next == 0 && conn.Pending is { Count: > 0 } pending)
        {
            // Coalesce as many queued responses as fit into the write page into ONE WSASend. Under
            // pipelining this is the batching lever: it cuts send syscalls N:1, and — since the peer
            // then drains a bigger chunk per recv — its recv-op count too (the measured deficit vs the
            // managed backend). No added latency: it only batches work already waiting. The first item
            // always fits (a response never exceeds the recv page, which equals this write page), so the
            // loop never stalls at next == 0.
            while (pending.Count > 0)
            {
                var seg = pending.Peek();
                if (next + seg.Count > _writeBufSize) break;
                pending.Dequeue();
                Marshal.Copy(seg.Array!, seg.Offset, (nint)(wp + next), seg.Count);
                next += seg.Count;
                ArrayPool<byte>.Shared.Return(seg.Array!); // done with the pooled staging buffer
            }
        }

        if (next > 0)
        {
            conn.SendSent = 0;
            conn.SendTotal = next; // reuse wi; SendBusy stays set
            IssueSend(conn, slot, wp, next, wi);
        }
        else
        {
            _writeBuffer.Release(wi);
            conn.SendBuf = -1;
            conn.SendBusy = false;
        }
    }

    // Deliver a flushed out-of-band write (loop thread): chunk the bytes into write-page-sized Pending
    // segments — exactly the shape the echo path already drains — then kick a send if idle. They queue
    // behind any in-flight/queued echo, preserving stream order. Dropped if the slot was re-tenanted or
    // its send half is closed.
    private void PumpFlush(uint slot, uint generation, byte[] data, int len)
    {
        var conn = _conns[slot - 1];
        if (conn.Generation != generation || conn.Socket == 0 || conn.Closing
            || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0)
            return;

        var pending = conn.Pending ??= new();
        for (int off = 0; off < len;)
        {
            int n = Math.Min(_writeBufSize, len - off);
            var buf = ArrayPool<byte>.Shared.Rent(n);      // pooled staging (uniform with echo; returned on drain)
            Array.Copy(data, off, buf, 0, n);
            pending.Enqueue(new ArraySegment<byte>(buf, 0, n));
            off += n;
        }
        if (!conn.SendBusy) StartPendingSend(conn, slot);
    }

    // Start draining Pending into a freshly-leased write page (precondition: !SendBusy). Coalesces as in
    // CompleteWrite; used to kick an out-of-band flush when the connection is otherwise idle.
    private void StartPendingSend(IocpConnection conn, uint slot)
    {
        if (conn.Pending is not { Count: > 0 } pending) return;
        if (!_writeBuffer.TryLease(out int wi, out byte* wp))
        {
            System.Diagnostics.Debug.WriteLine("Write buffer pool exhausted; closing connection.");
            CloseClient(slot);
            return;
        }
        int next = 0;
        while (pending.Count > 0)
        {
            var seg = pending.Peek();
            if (next + seg.Count > _writeBufSize) break;
            pending.Dequeue();
            Marshal.Copy(seg.Array!, seg.Offset, (nint)(wp + next), seg.Count);
            next += seg.Count;
            ArrayPool<byte>.Shared.Return(seg.Array!);
        }
        if (next > 0) SubmitSendBuffer(conn, slot, wi, next);
        else _writeBuffer.Release(wi);
    }

    private void ArmRecv(IocpConnection conn)
    {
        uint slot = conn.Slot;
        IocpOp* op = RecvOp(slot);
        op->Kind = OpKind.Recv;
        op->Slot = slot;
        op->Buf = conn.RecvBuf;

        Win32.WSABUF b; b.len = (uint)_recvBufSize; b.buf = _recvBuffer.Address(conn.RecvBuf);
        uint flags = 0, recvd = 0;
        conn.RecvArmed = true;
        int rc = Win32.WSARecv(conn.Socket, &b, 1, &recvd, &flags, &op->Overlapped, null);
        if (rc == 0)
        {
            // Synchronous success: with FILE_SKIP no packet posts, so defer the completion inline.
            // Without it (SkipOnSuccess false) a packet WILL post — do nothing and let it arrive.
            if (conn.SkipOnSuccess) QueueInline(OpKind.Recv, slot, recvd, failed: false);
            return;
        }
        if (Marshal.GetLastPInvokeError() != Win32.WSA_IO_PENDING)
            QueueInline(OpKind.Recv, slot, 0, failed: true); // synchronous failure never posts a packet
        // WSA_IO_PENDING → an async completion will arrive.
    }

    // Pin the loop thread to a core (best-effort). The base Run() pins on Linux; Windows is done here
    // since it needs SetThreadAffinityMask.
    private void PinLoopThread()
    {
        if (!Parent.Options.PinWorkerThreads || !OperatingSystem.IsWindows()) return;
        nuint mask = ChooseAffinityMask();
        if (mask != 0) Win32.SetThreadAffinityMask(Win32.GetCurrentThread(), mask);
    }

    // Pick the Shard-th CPU among those the PROCESS is allowed to run on — respecting a restriction
    // applied via a job object, `start /affinity`, or SetProcessAffinityMask — so pinning stays inside
    // the permitted set. This matches the Linux path (PinCurrentThreadToNthAllowedCpu), which pins to
    // the Nth CPU of the inherited cpuset rather than the Nth absolute core. Returns a single-bit mask,
    // or 0 if the process mask can't be read and no fallback applies (caller then leaves it unpinned).
    // NOTE: single processor group only (<= 64 CPUs); boxes with more use processor groups, which
    // SetThreadAffinityMask can't span — a later refinement if we ever run on such hardware.
    private nuint ChooseAffinityMask()
    {
        nuint proc, sys;
        if (!Win32.GetProcessAffinityMask(Win32.GetCurrentProcess(), &proc, &sys) || proc == 0)
            return (nuint)1 << (Shard % Environment.ProcessorCount); // can't read it → best-effort absolute

        int bits = sizeof(nuint) * 8;
        int allowed = 0;
        for (int b = 0; b < bits; b++)
            if ((proc & ((nuint)1 << b)) != 0) allowed++;
        if (allowed == 0) return 0;

        int target = Shard % allowed; // wrap when there are more shards than allowed CPUs
        int seen = 0;
        for (int b = 0; b < bits; b++)
        {
            if ((proc & ((nuint)1 << b)) == 0) continue;
            if (seen == target) return (nuint)1 << b;
            seen++;
        }
        return 0;
    }
}
#endif
