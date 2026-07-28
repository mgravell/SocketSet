#if NET // Windows RIO backend; compiled out of the netfx fallback build.
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SocketSets.Native;
using SocketSets.Tls;

namespace SocketSets.Windows;

/// <summary>
/// A single-threaded Registered-I/O (RIO) event loop — the TCP data-path accelerator layered on the
/// same IOCP foundation the <c>IocpShard</c> uses. Accept/connect stay pure IOCP (AcceptEx/ConnectEx →
/// completion port); the recv/send hot path rides RIO: pre-registered buffers, a per-connection request
/// queue, and a shard completion queue drained in USER MODE with <see cref="Win32.RIODequeueCompletion"/>
/// (no per-op syscall — the op-count win). RIONotify posts a packet to the IOCP port when the CQ needs
/// draining, so the whole thing runs off the one <see cref="Win32.GetQueuedCompletionStatusExBlocking"/> loop.
///
/// TCP/UDP only — RIO can't do AF_UNIX (that stays on the IOCP backend). Parallel to <c>IocpShard</c>
/// rather than sharing a base: the control plane is similar but the data path + completion model differ
/// enough that duplication was the cheaper, lower-risk choice.
///
/// BLIND: written on Linux, validated on Windows. Compiles everywhere; only ever runs on Windows.
/// </summary>
internal sealed unsafe class WindowsRioShard : WindowsShardBase<RioConnection>
{
    private const int EntryBatch = 128;               // IOCP completions per GQCSEx (accept/connect + RIO notify)
    private const int RioBatch = 256;                 // RIO completions per RIODequeueCompletion pass
    private const int RioDrainBudget = 4096;          // max RIO completions per DrainRio call before yielding to the port loop
    private const int AddrStride = 128;               // per-address storage for AcceptEx
    private const int AcceptBufSize = 2 * AddrStride;
    private static readonly nuint WakeKey = unchecked((nuint)(-1)); // PQCS wake
    private static readonly nuint RioKey = unchecked((nuint)(-2));  // RIONotify → "drain the CQ"

    private const uint ReqRecv = 1; // RIORESULT.RequestContext discriminator (both non-zero: rule out a
    private const uint ReqSend = 2; // NULL context being treated as "no completion")


    internal enum OpKind : int { Accept = 0, Connect = 1 }

    // Accept/connect op contexts (IOCP-style; recv/send do NOT use these — they're RIO). Both start with
    // {OVERLAPPED, Kind} so the loop reads Kind through a CtlOp* regardless of which it is.
    [StructLayout(LayoutKind.Sequential)]
    internal struct CtlOp
    {
        public Win32.OVERLAPPED Overlapped; // MUST be first
        public OpKind Kind;
        public uint Slot;                   // for Connect
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AcceptOp
    {
        public Win32.OVERLAPPED Overlapped; // MUST be first
        public OpKind Kind;
        public nint Handle;                 // GCHandle to AcceptState
    }

    private sealed class AcceptState
    {
        public nint Listener;
        public nint AcceptSocket;
        public nint Buf;      // (byte*) AcceptEx output buffer
        public nint Op;       // (AcceptOp*)
        public object? Token;
        public int Af;
        public int Proto;
        public GCHandle Gc;
    }

    // --- slot table ---
                                  // it's a mutable struct, so a readonly field would mutate a throwaway copy.
    // Connect requests marshaled from the caller thread to the loop, which claims the slot + posts
    // ConnectEx — so the slot table stays single-writer. The socket is created caller-side (thread-agnostic
    // syscalls, sync failures stay synchronous); the port-assoc + ConnectEx run on the loop (async failures).
    private readonly ConcurrentQueue<(nint Socket, EndPoint Endpoint, object? Token)> _pendingConnects = [];

    // --- options snapshot ---

    // --- created on the loop thread in OnInitialize ---
    private Win32.OVERLAPPED_ENTRY* _entries;      // GQCSEx batch
    private CtlOp* _ops;                           // per-slot connect op context (indexed by slot-1)
    private byte* _connectAddrs;                   // per-slot sockaddr for ConnectEx
    private nint _cq;                              // RIO completion queue (shared across this shard's connections)
    private nint _recvBufferId;                    // RIORegisterBuffer(recv slab)
    private nint _writeBufferId;                   // RIORegisterBuffer(write slab)
    private Win32.OVERLAPPED* _rioNotifyOv;        // the OVERLAPPED RIONotify hands back on the port
    private Win32.RIORESULT* _rioResults;          // RIODequeueCompletion output
    private volatile bool _portReady;

    // TLS scratch, shared by every connection on this shard (null unless Options.Tls is set). Safe to share
    // because a shard has ONE loop thread and a filter is only ever touched from it — the managed backend
    // needs a per-connection gate precisely because it has no such thread.

    private readonly List<AcceptState> _acceptStates = [];
    private readonly object _acceptGate = new();

    private readonly ConcurrentQueue<(nint Socket, object? Token)> _incoming = [];
    private readonly ConcurrentQueue<(uint Slot, uint Generation)> _closes = [];
    // Out-of-band flushed writes (Connection.Flush, any thread): sent on the loop via the Pending path.
    private readonly ConcurrentQueue<(uint Slot, uint Generation, byte[] Data, int Len)> _flush = [];

    // Connections with RIO_MSG_DEFER'd submissions awaiting a commit. Loop-thread-only. Flushed at each
    // drain-pass / loop-iteration boundary — so a connection's recv-rearm + echo-send (and any pipelined
    // extras in the same pass) collapse into ONE kernel kick, and the flush can't be forgotten.
    private readonly List<RioConnection> _toCommit = [];

    // TEMP: stderr diagnostics for the conns<64 drop investigation. The real drop paths use
    // Debug.WriteLine, which is compiled OUT in Release — so drops were invisible. Remove once resolved.
    public WindowsRioShard(SocketSetOptions options) : base(options)
    {
        _conns = new RioConnection[_socketsPerShard];
        for (int i = 0; i < _conns.Length; i++)
            _conns[i] = new RioConnection(this, (uint)i + 1);
        _slots = new SlotAllocator(_conns.Length);
        SetShardCapacity(_conns.Length); // reservation ceiling == slot-table size
    }

    // =====================================================================
    // Initialization / teardown
    // =====================================================================

    protected override void OnInitialize()
    {
        Win32.EnsureWinsock();

        _port = Win32.CreateIoCompletionPort(Win32.INVALID_HANDLE_VALUE, 0, 0, 1);
        if (_port == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort failed");

        // Load AcceptEx/ConnectEx + the RIO function table using a throwaway RIO-capable socket.
        nint probe = Win32.WSASocketW(Win32.AF_INET, Win32.SOCK_STREAM, Win32.IPPROTO_TCP, null, 0,
            Win32.WSA_FLAG_OVERLAPPED | Win32.WSA_FLAG_REGISTERED_IO);
        if (probe == Win32.INVALID_SOCKET)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW(probe) failed");
        try { Win32.LoadExtensions(probe); Win32.LoadRio(probe); }
        finally { Win32.closesocket(probe); }

        _writeBuffer = new PinnedWriteBufferPool(_writeCount, _writeBufSize);
        _recvBuffer = new PinnedWriteBufferPool(_recvCount, _recvBufSize);
        _entries = (Win32.OVERLAPPED_ENTRY*)NativeMemory.AllocZeroed(EntryBatch * (nuint)sizeof(Win32.OVERLAPPED_ENTRY));
        _ops = (CtlOp*)NativeMemory.AllocZeroed((nuint)_socketsPerShard * (nuint)sizeof(CtlOp));
        _connectAddrs = (byte*)NativeMemory.AllocZeroed((nuint)_socketsPerShard * AddrStride);
        _rioNotifyOv = (Win32.OVERLAPPED*)NativeMemory.AllocZeroed((nuint)sizeof(Win32.OVERLAPPED));
        _rioResults = (Win32.RIORESULT*)NativeMemory.AllocZeroed(RioBatch * (nuint)sizeof(Win32.RIORESULT));

        // Register the pinned slabs with RIO (whole slab; a pool index maps to an offset slice).
        _recvBufferId = Win32.RIORegisterBuffer(_recvBuffer.Address(0), (uint)(_recvCount * _recvBufSize));
        if (_recvBufferId == Win32.RIO_INVALID_BUFFERID)
            throw new Win32Exception(Win32.WSAGetLastError(), "RIORegisterBuffer(recv) failed");
        _writeBufferId = Win32.RIORegisterBuffer(_writeBuffer.Address(0), (uint)(_writeCount * _writeBufSize));
        if (_writeBufferId == Win32.RIO_INVALID_BUFFERID)
            throw new Win32Exception(Win32.WSAGetLastError(), "RIORegisterBuffer(write) failed");

        // One CQ per shard, sized for one recv + one send outstanding per connection, notifying via IOCP.
        Win32.RIO_NOTIFICATION_COMPLETION notify = default;
        notify.Type = Win32.RIO_IOCP_COMPLETION;
        notify.IocpHandle = _port;
        notify.CompletionKey = (nint)RioKey;
        notify.Overlapped = (nint)_rioNotifyOv;
        _cq = Win32.RIOCreateCompletionQueue((uint)(_socketsPerShard * 2), &notify);
        if (_cq == Win32.RIO_INVALID_CQ)
            throw new Win32Exception(Win32.WSAGetLastError(), "RIOCreateCompletionQueue failed");

        if (Parent.Options.Tls is not null)
        {
            _tlsPlain = new PooledBufferWriter(_recvBufSize);
            _tlsCipher = new PooledBufferWriter(_writeBufSize);
            _tlsCtrl = new PooledBufferWriter(1024);
        }

        _portReady = true;
    }

    protected override void OnRun()
    {
        PinLoopThread();

        // Arm the first RIO notification (must precede any RIO op so the CQ can wake us).
        Win32.RIONotify(_cq);

        while (IsActive)
        {
            DrainCrossThread();
            // Kick any deferred submissions (from AdoptAccepted this iteration, or a previous iteration's
            // HandleConnect) BEFORE blocking — else we'd wait on completions for ops never sent → deadlock.
            FlushCommits();

            uint removed = 0;
            bool ok = Win32.GetQueuedCompletionStatusExBlocking(_port, _entries, EntryBatch, &removed, Win32.INFINITE, alertable: false);
            if (!ok) continue; // port closed at shutdown → IsActive ends the loop

            for (uint i = 0; i < removed; i++)
            {
                ref Win32.OVERLAPPED_ENTRY e = ref _entries[i];
                if (e.lpCompletionKey == WakeKey || e.lpOverlapped == null) continue; // wake
                if (e.lpCompletionKey == RioKey) { DrainRio(); continue; }             // RIO CQ needs draining

                // Accept/connect completion (IOCP).
                CtlOp* op = (CtlOp*)e.lpOverlapped;
                bool failed = e.lpOverlapped->Internal != 0;
                switch (op->Kind)
                {
                    case OpKind.Accept: HandleAccept((AcceptOp*)e.lpOverlapped, failed); break;
                    case OpKind.Connect: HandleConnect(op->Slot, failed); break;
                    default: System.Diagnostics.Debug.WriteLine($"unexpected port completion kind={(int)op->Kind}"); break;
                }
            }
        }
    }

    // Drain the RIO completion queue in user mode, re-arming the notification race-free: dequeue to
    // empty, re-arm, then dequeue once more — if that finds work, the arm may have spent its trigger, so
    // loop and re-arm again.
    // BOUNDED so a busy echo can't monopolize the loop thread. Processing recv completions generates
    // more send completions on the same CQ, so an unbounded drain never returns to GetQueuedCompletion
    // StatusEx — which is where accept/CONNECT completions get serviced. Left unbounded, connect
    // completions arriving after the echo ramps up starve forever (the conns<64 bug). Drain up to a
    // budget, then re-arm and yield to the OnRun loop; active load re-fires the notify so we come back.
    private void DrainRio()
    {
        int budget = RioDrainBudget;
        while (true)
        {
            uint n = DrainRioOnce();
            FlushCommits(); // kick the recv-rearms + echo sends this chunk deferred — bounds their hold
                            // time to ~one RioBatch (keeps latency tight) while still batching per-RQ.
            if (n == 0)
            {
                // CQ empty: re-arm race-free (arm, then one more drain to catch the gap).
                Win32.RIONotify(_cq);
                if (DrainRioOnce() == 0) return; // nothing slipped into the gap → done
                FlushCommits();                   // flush anything the gap-drain deferred
                continue;                         // something arrived; keep going (subject to budget)
            }
            budget -= (int)n;
            if (budget <= 0)
            {
                // Busy: re-arm and hand the loop back so accept/connect (port) completions get serviced.
                Win32.RIONotify(_cq);
                return;
            }
        }
    }

    // Queue a connection whose RQ has RIO_MSG_DEFER'd submissions for a commit (dedup via CommitPending).
    private void QueueCommit(RioConnection conn)
    {
        if (conn.CommitPending) return;
        conn.CommitPending = true;
        _toCommit.Add(conn);
    }

    // Commit (kick) every queued RQ: one RIO_MSG_COMMIT_ONLY flushes all of that connection's deferred
    // recv+send requests to the kernel with a single call. MUST run before the loop blocks in GQCSEx, or
    // deferred ops (whose completions we'd then wait on) never get sent → deadlock.
    private void FlushCommits()
    {
        // AsSpan over the backing array: skips the List indexer's bounds/version checks on this hot
        // path. Safe — nothing structurally modifies _toCommit mid-loop (RIOSend can't re-enter
        // QueueCommit; the Clear is after).
        foreach (var conn in CollectionsMarshal.AsSpan(_toCommit))
        {
            conn.CommitPending = false;
            bool recv = conn.CommitRecv, send = conn.CommitSend;
            conn.CommitRecv = conn.CommitSend = false;
            if (conn.Socket != 0 && conn.Rq != 0)
            {
                // RIO_MSG_COMMIT_ONLY kicks ONLY the direction it's issued on, so commit each deferred
                // direction explicitly. A send commit APPEARS to also flush a co-pending recv (the
                // "piggyback"), and simple echo tolerates relying on that — but --verify-echo proved it
                // strands the recv under a deep pipeline (dropped message → hang). So never rely on it:
                // `if`/`if`, not `else if`. Cost is at most two commit calls per RQ per flush, still
                // amortized over a whole drain chunk (DEFER's batching win is intact).
                if (send) Win32.RIOSend(conn.Rq, null, 0, Win32.RIO_MSG_COMMIT_ONLY, null);
                if (recv) Win32.RIOReceive(conn.Rq, null, 0, Win32.RIO_MSG_COMMIT_ONLY, null);
            }
        }
        _toCommit.Clear();
    }

    private uint DrainRioOnce()
    {
        uint n = Win32.RIODequeueCompletion(_cq, _rioResults, RioBatch);
        if (n == Win32.RIO_CORRUPT_CQ)
            throw new InvalidOperationException("RIODequeueCompletion reported a corrupt completion queue.");
        for (uint i = 0; i < n; i++)
        {
            ref Win32.RIORESULT r = ref _rioResults[i];
            uint slot = (uint)r.SocketContext;
            bool failed = r.Status != 0;
            if (r.RequestContext == ReqRecv) HandleRecv(slot, r.BytesTransferred, failed);
            else HandleSend(slot, r.BytesTransferred, failed);
        }
        return n;
    }

    protected override void OnStop() => Poke();

    protected override void OnShutdown()
    {
        _portReady = false;

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

        for (int i = 0; i < _conns.Length; i++)
        {
            nint s = Interlocked.Exchange(ref _conns[i].Socket, 0);
            if (s != 0) Win32.closesocket(s); // also tears down the RIO request queue
            var tls = _conns[i].Tls;
            if (tls is not null) { _conns[i].Tls = null; tls.Dispose(); }
        }

        _tlsPlain?.Dispose(); _tlsPlain = null;
        _tlsCipher?.Dispose(); _tlsCipher = null;
        _tlsCtrl?.Dispose(); _tlsCtrl = null;

        if (_cq != Win32.RIO_INVALID_CQ && Win32.RIOCloseCompletionQueue != null) { Win32.RIOCloseCompletionQueue(_cq); _cq = 0; }
        if (_recvBufferId != Win32.RIO_INVALID_BUFFERID) { Win32.RIODeregisterBuffer(_recvBufferId); _recvBufferId = Win32.RIO_INVALID_BUFFERID; }
        if (_writeBufferId != Win32.RIO_INVALID_BUFFERID) { Win32.RIODeregisterBuffer(_writeBufferId); _writeBufferId = Win32.RIO_INVALID_BUFFERID; }

        if (_ops != null) { NativeMemory.Free(_ops); _ops = null; }
        if (_entries != null) { NativeMemory.Free(_entries); _entries = null; }
        if (_connectAddrs != null) { NativeMemory.Free(_connectAddrs); _connectAddrs = null; }
        if (_rioNotifyOv != null) { NativeMemory.Free(_rioNotifyOv); _rioNotifyOv = null; }
        if (_rioResults != null) { NativeMemory.Free(_rioResults); _rioResults = null; }
        _writeBuffer.Dispose();
        _recvBuffer.Dispose();
        if (_port != 0) { Win32.CloseHandle(_port); _port = 0; }
    }

    private void Poke()
    {
        if (!_portReady) return;
        Win32.PostQueuedCompletionStatus(_port, 0, WakeKey, null);
    }

    internal void EnqueueInbound(nint socket, object? token) { _incoming.Enqueue((socket, token)); Poke(); }
    public override void SubmitClose(uint slot, uint generation) { _closes.Enqueue((slot, generation)); Poke(); }
    public override void SubmitFlush(uint slot, uint generation, byte[] data, int length) { _flush.Enqueue((slot, generation, data, length)); Poke(); }

    private void DrainCrossThread()
    {
        DrainAwaitingPage(); // retry anyone who was waiting on a write page before taking on more work
        while (_incoming.TryDequeue(out var inbound)) AdoptAccepted(inbound.Socket, inbound.Token);
        while (_pendingConnects.TryDequeue(out var pc)) StartConnect(pc.Socket, pc.Endpoint, pc.Token);
        while (_closes.TryDequeue(out var c))
        {
            var conn = _conns[c.Slot - 1];
            if (conn.Generation == c.Generation && conn.Socket != 0) CloseClient(c.Slot);
        }
        // f.Data is rented (see OutboundConnection.Flush) and owned by this loop now: return it however
        // PumpFlush exits, including the drop paths where the slot was re-tenanted.
        while (_flush.TryDequeue(out var f))
        {
            try { PumpFlush(f.Slot, f.Generation, f.Data, f.Len); }
            finally { ArrayPool<byte>.Shared.Return(f.Data); }
        }
    }

    // =====================================================================
    // Slot table
    // =====================================================================

    // Loop-thread only (accept adoption, or a connect marshaled via StartConnect) — the single-writer
    // model, so the claim is a plain free-list pop + plain stores, no CAS. The caller reserved first, so a
    // slot is guaranteed; Claim only fails on counter drift / an unreserved caller (backstop → caller
    // releases + drops). Returns null if the table is full.
    private RioConnection? InitClient(nint socket, object? userToken, SocketSet.SocketFlags flags)
    {
        int idx = _slots.Claim();
        if (idx < 0) return null;
        var conn = _conns[idx];
        conn.UserToken = userToken;
        conn.Flags = flags;
        conn.Opened = false;
        conn.Closing = false;
        conn.RecvArmed = false;
        conn.SendBusy = false;
        conn.CommitPending = false;
        conn.CommitRecv = conn.CommitSend = false;
        conn.Rq = 0;
        conn.RecvBuf = -1;
        conn.SendBuf = -1;
        conn.Pending?.Clear();
        conn.Tls = null;      // disposed by TryFinalize; cleared here so a rolled-back claim starts clean
        conn.IsClient = false;
        // Bump the generation before publishing Socket: any out-of-band Close/flush captured against the
        // previous tenant now mismatches and is dropped rather than misapplied.
        Volatile.Write(ref conn.Generation, conn.Generation + 1);
        Volatile.Write(ref conn.Socket, socket); // publish live last (foreign readers gate on Socket != 0)
        return conn;
    }


    protected override void CloseClient(uint slot)
    {
        if (slot == 0) return;
        var conn = _conns[slot - 1];
        if (conn.Socket == 0 || conn.Closing) return;
        conn.Closing = true;

        if (conn.Opened)
        {
            conn.Opened = false;
            try { Parent.DispatchClosed(conn); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        // closesocket aborts the outstanding RIO recv/send (they complete on the CQ with an error) and
        // destroys the request queue. Socket stays non-zero as the claimed marker until TryFinalize.
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
        TryFinalize(conn, slot);
    }

    private void TryFinalize(RioConnection conn, uint slot)
    {
        if (!conn.Closing || conn.RecvArmed || conn.SendBusy) return;

        if (conn.RecvBuf >= 0) { _recvBuffer.Release(conn.RecvBuf); conn.RecvBuf = -1; }
        if (conn.SendBuf >= 0) { _writeBuffer.Release(conn.SendBuf); conn.SendBuf = -1; }
        if (conn.Pending is { } pending)
            while (pending.Count > 0) ArrayPool<byte>.Shared.Return(pending.Dequeue().Array!);
        // Release the TLS engine (SSPI context / SSL*) with the rest of the per-connection state.
        if (conn.Tls is { } tls) { conn.Tls = null; tls.Dispose(); }
        conn.Rq = 0; // torn down with the socket
        conn.UserToken = null;
        conn.Flags = 0;
        conn.Closing = false;
        Volatile.Write(ref conn.Socket, 0);
        _slots.Free((int)(slot - 1)); // return to the loop-local allocator (loop thread only)
        ReleaseReservation();         // paired with the TryReserve that placed this connection
    }

    // =====================================================================
    // Entry points
    // =====================================================================

    public override void Listen(EndPoint endpoint, object? userToken, bool local)
    {
        Win32.EnsureWinsock();
        if (endpoint is not IPEndPoint)
            throw new NotSupportedException("The RIO backend is TCP-only; use the IOCP backend for AF_UNIX.");

        var (listener, af, proto) = CreateListener((IPEndPoint)endpoint);
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
        Win32.EnsureWinsock();
        if (handle == 0 || handle == Win32.INVALID_SOCKET)
            throw new ArgumentOutOfRangeException(nameof(handle), "Invalid socket handle.");
        Win32.LoadExtensions(handle);
        if (Win32.CreateIoCompletionPort(handle, _port, 0, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort(listener) failed");
        StartAccept(handle, Win32.AF_INET, Win32.IPPROTO_TCP, userToken);
    }

    public override void Connect(EndPoint endpoint, object? userToken)
    {
        Win32.EnsureWinsock();
        if (endpoint is not IPEndPoint)
            throw new NotSupportedException("The RIO backend is TCP-only; use the IOCP backend for AF_UNIX.");

        // This shard holds a reservation (TryPlace took it). Create + bind the socket HERE (thread-agnostic
        // syscalls, so their failures stay synchronous to the caller), then hand the claim + port-assoc +
        // ConnectEx to the loop, keeping the slot table single-writer. Release the reservation on any
        // synchronous failure so a rejected connect doesn't permanently consume capacity.
        nint s = Win32.WSASocketW(Win32.AF_INET, Win32.SOCK_STREAM, Win32.IPPROTO_TCP, null, 0,
            Win32.WSA_FLAG_OVERLAPPED | Win32.WSA_FLAG_REGISTERED_IO);
        if (s == Win32.INVALID_SOCKET) { ReleaseReservation(); throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW failed"); }
        Win32.LoadExtensions(s);

        int one = 1;
        Win32.setsockopt(s, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int));
        Win32.SockAddrIn any = default;
        any.sin_family = (ushort)Win32.AF_INET;
        Win32.bind(s, &any, 16); // ConnectEx requires a bound socket

        _pendingConnects.Enqueue((s, endpoint, userToken));
        Poke();
    }

    // Loop thread: claim the reserved slot for a marshaled connect, associate the socket with the port,
    // build the target sockaddr into the slot's stable native storage, and post ConnectEx. The reservation
    // is consumed by the claim, or released here on any post-claim failure (now async, like accept).
    private void StartConnect(nint s, EndPoint endpoint, object? userToken)
    {
        var ip = (IPEndPoint)endpoint; // caller validated the type before marshaling
        var conn = InitClient(s, userToken, SocketSet.SocketFlags.None);
        if (conn is null) { Win32.closesocket(s); ReleaseReservation(); return; }
        uint slot = conn.Slot;

        if (Win32.CreateIoCompletionPort(s, _port, slot, 0) == 0)
        {
            Win32.closesocket(s);
            FreeSlot(conn);
            return;
        }

        byte* addrPtr = _connectAddrs + (nint)(slot - 1) * AddrStride;
        var sa = (Win32.SockAddrIn*)addrPtr;
        *sa = default;
        sa->sin_family = (ushort)Win32.AF_INET;
        sa->sin_port = Win32.Htons((ushort)ip.Port);
        var b = ip.Address.GetAddressBytes();
        byte* dst = (byte*)&sa->sin_addr;
        dst[0] = b[0]; dst[1] = b[1]; dst[2] = b[2]; dst[3] = b[3];

        CtlOp* op = &_ops[slot - 1];
        op->Kind = OpKind.Connect;
        op->Slot = slot;
        uint sent = 0;
        int okc = Win32.ConnectEx(s, addrPtr, 16, null, 0, &sent, &op->Overlapped);
        if (okc == 0 && Win32.WSAGetLastError() != Win32.WSA_IO_PENDING)
        {
            Win32.closesocket(s);
            FreeSlot(conn);
            return;
        }
        // okc != 0 (immediate) or WSA_IO_PENDING → completion arrives on the port → HandleConnect.
    }

    private (nint socket, int af, int proto) CreateListener(IPEndPoint ip)
    {
        // The listener must be REGISTERED_IO-capable too: accepted sockets inherit the listener's
        // provider characteristics via SO_UPDATE_ACCEPT_CONTEXT, and without it their RIO receives are
        // accepted at submission but silently never complete.
        nint s = Win32.WSASocketW(Win32.AF_INET, Win32.SOCK_STREAM, Win32.IPPROTO_TCP, null, 0,
            Win32.WSA_FLAG_OVERLAPPED | Win32.WSA_FLAG_REGISTERED_IO);
        if (s == Win32.INVALID_SOCKET) throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW failed");
        int one = 1;
        Win32.setsockopt(s, Win32.SOL_SOCKET, Win32.SO_REUSEADDR, &one, sizeof(int));
        Win32.setsockopt(s, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int));
        Win32.SockAddrIn addr = default;
        addr.sin_family = (ushort)Win32.AF_INET;
        addr.sin_port = Win32.Htons((ushort)ip.Port);
        addr.sin_addr = 0;
        if (Win32.bind(s, &addr, 16) == Win32.SOCKET_ERROR)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "bind() failed");
        if (Win32.listen(s, _listenBacklog) == Win32.SOCKET_ERROR)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "listen() failed");
        return (s, Win32.AF_INET, Win32.IPPROTO_TCP);
    }

    // Arm a pool of AcceptConcurrency outstanding AcceptEx on the listener (see the IocpShard note):
    // a backlog of accept consumers so connect bursts don't serialize, and a failed re-post on one
    // doesn't stall the listener. Each completion re-posts its own state (HandleAccept).
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

    // Accept sockets are created RIO-capable (they become RIO connections after adoption).
    private void PostAccept(AcceptState st)
    {
        nint acc = Win32.WSASocketW(st.Af, Win32.SOCK_STREAM, st.Proto, null, 0,
            Win32.WSA_FLAG_OVERLAPPED | Win32.WSA_FLAG_REGISTERED_IO);
        if (acc == Win32.INVALID_SOCKET)
        {
            System.Diagnostics.Debug.WriteLine($"WSASocketW(accept) failed: {Marshal.GetLastPInvokeError()}");
            st.AcceptSocket = 0;
            return;
        }

        st.AcceptSocket = acc;
        var op = (AcceptOp*)st.Op;
        op->Kind = OpKind.Accept;

        uint recvd = 0;
        int ok = Win32.AcceptEx(st.Listener, acc, (void*)st.Buf, 0, AddrStride, AddrStride, &recvd, &op->Overlapped);
        if (ok == 0 && Win32.WSAGetLastError() != Win32.WSA_IO_PENDING)
        {
            System.Diagnostics.Debug.WriteLine($"AcceptEx failed: {Win32.WSAGetLastError()}");
            Win32.closesocket(acc);
            st.AcceptSocket = 0;
        }
    }

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

        nint listener = st.Listener;
        Win32.setsockopt(acc, Win32.SOL_SOCKET, Win32.SO_UPDATE_ACCEPT_CONTEXT, &listener, sizeof(nint));

        // Single acceptor → place on the first shard with a free slot (capacity-aware; drops only if
        // every shard is full).
        var target = (WindowsRioShard?)Parent.TryPlace();
        if (target is not null) target.EnqueueInbound(acc, st.Token);
        else Win32.closesocket(acc); // every shard full → drop (runtime shard growth would expand here)
        PostAccept(st);
    }

    // =====================================================================
    // Adoption + data path (RIO)
    // =====================================================================

    private void AdoptAccepted(nint socket, object? token)
    {
        int one = 1;
        Win32.setsockopt(socket, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int));

        var conn = InitClient(socket, token, SocketSet.SocketFlags.None);
        if (conn is null) { Win32.closesocket(socket); ReleaseReservation(); return; }
        uint slot = conn.Slot;

        if (!SetupConnection(conn)) { Win32.closesocket(socket); FreeSlot(conn); return; }

        // TLS: the app must not see this connection until the handshake completes, so OnAccept is deferred
        // to FireTlsOpen and everything below is skipped.
        if (Parent.Options.Tls is not null) { BeginTls(conn, slot, isClient: false); return; }

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _writeBufSize : 0);
        conn.Opened = true;
        Parent.OnAccept(ref ctx);

        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) ArmReceive(conn);

        int sb = ctx.SendBytes;
        if (leased && sb > 0 && conn.Socket != 0 && !conn.Closing) SubmitSendBuffer(conn, slot, wi, sb);
        else if (leased) _writeBuffer.Release(wi);
    }

    private void HandleConnect(uint slot, bool failed)
    {
        var conn = _conns[slot - 1];
        if (failed || conn.Socket == 0) { CloseClient(slot); return; }

        Win32.setsockopt(conn.Socket, Win32.SOL_SOCKET, Win32.SO_UPDATE_CONNECT_CONTEXT, null, 0);
        if (!SetupConnection(conn)) { CloseClient(slot); return; }

        // TLS: OnConnect is deferred to FireTlsOpen (the client speaks first — see BeginTls).
        if (Parent.Options.Tls is not null) { BeginTls(conn, slot, isClient: true); return; }

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _writeBufSize : 0);
        conn.Opened = true;
        Parent.OnConnect(ref ctx);

        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) ArmReceive(conn);

        int sb = ctx.SendBytes;
        if (leased && sb > 0 && conn.Socket != 0 && !conn.Closing) SubmitSendBuffer(conn, slot, wi, sb);
        else if (leased) _writeBuffer.Release(wi);
    }

    // Create the per-connection RIO request queue + lease its recv buffer. False on failure.
    private bool SetupConnection(RioConnection conn)
    {
        // (socket, maxOutstandingRecv, maxRecvDataBuffers, maxOutstandingSend, maxSendDataBuffers, recvCq, sendCq, sockCtx)
        nint rq = Win32.RIOCreateRequestQueue(conn.Socket, 1, 1, 1, 1, _cq, _cq, (void*)(nuint)conn.Slot);
        if (rq == Win32.RIO_INVALID_RQ)
        {
            System.Diagnostics.Debug.WriteLine($"RIOCreateRequestQueue failed: {Win32.WSAGetLastError()}");
            return false;
        }
        conn.Rq = rq;
        if (!_recvBuffer.TryLease(out int ri, out _)) { System.Diagnostics.Debug.WriteLine("recv-buffer pool exhausted"); return false; }
        conn.RecvBuf = ri;
        return true;
    }

    private void ArmReceive(RioConnection conn)
    {
        Win32.RIO_BUF buf;
        buf.BufferId = _recvBufferId;
        buf.Offset = (uint)(conn.RecvBuf * _recvBufSize);
        buf.Length = (uint)_recvBufSize;
        conn.RecvArmed = true;
        // DEFER: queue into the RQ ring without a kernel kick; committed in a batch (see FlushCommits).
        if (Win32.RIOReceive(conn.Rq, &buf, 1, Win32.RIO_MSG_DEFER, (void*)(nuint)ReqRecv) == 0)
        {
            conn.RecvArmed = false;
            CloseClient(conn.Slot);
            return;
        }
        conn.CommitRecv = true;
        QueueCommit(conn);
        // else: the completion arrives on the CQ (after the batched commit kicks the RQ).
    }

    // Post a RIOSend of write-slab index wi, bytes [off, off+len). Tears the connection down on failure.
    private void IssueSend(RioConnection conn, uint slot, int wi, int off, int len)
    {
        Win32.RIO_BUF buf;
        buf.BufferId = _writeBufferId;
        buf.Offset = (uint)(wi * _writeBufSize + off);
        buf.Length = (uint)len;
        // DEFER: queue into the RQ ring without a kernel kick; committed in a batch (see FlushCommits).
        if (Win32.RIOSend(conn.Rq, &buf, 1, Win32.RIO_MSG_DEFER, (void*)(nuint)ReqSend) == 0) { FailSend(conn, slot); return; }
        conn.CommitSend = true;
        QueueCommit(conn);
    }

    private void SubmitSendBuffer(RioConnection conn, uint slot, int wi, int len)
    {
        conn.SendBuf = wi;
        conn.SendSent = 0;
        conn.SendTotal = len;
        conn.SendBusy = true;
        IssueSend(conn, slot, wi, 0, len);
    }

    private void FailSend(RioConnection conn, uint slot)
    {
        if (conn.SendBuf >= 0) { _writeBuffer.Release(conn.SendBuf); conn.SendBuf = -1; }
        conn.SendBusy = false;
        CloseClient(slot);
    }

    private void HandleRecv(uint slot, uint bytes, bool failed)
    {
        var conn = _conns[slot - 1];

        if (conn.Closing) { conn.RecvArmed = false; TryFinalize(conn, slot); return; }
        if (failed || bytes == 0) { conn.RecvArmed = false; CloseClient(slot); return; }

        // RecvArmed stays true across DeliverReceive so a close it triggers can't finalize the slot here.
        bool keep = DeliverReceive(conn, slot, (int)bytes);

        if (keep && conn.Socket != 0 && !conn.Closing && (conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0)
            ArmReceive(conn);
        else { conn.RecvArmed = false; TryFinalize(conn, slot); }
    }

    private bool DeliverReceive(RioConnection conn, uint slot, int bytes)
    {
        if (conn.Tls is not null) return DeliverReceiveTls(conn, slot, bytes);

        byte* rp = _recvBuffer.Address(conn.RecvBuf);
        var ctx = new SocketSet.ReceiveContext(conn, rp, _recvBufSize, bytes);
        Parent.DispatchReceive(ref ctx);

        int rb = ctx.ResponseBytes;
        if (rb <= 0 || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0) return true;

        if (conn.SendBusy)
        {
            var copy = ArrayPool<byte>.Shared.Rent(rb);
            Marshal.Copy((nint)rp, copy, 0, rb);
            (conn.Pending ??= new()).Enqueue(new ArraySegment<byte>(copy, 0, rb));
            return true;
        }

        return SendResponse(conn, slot, rp, rb);
    }

    private bool SendResponse(RioConnection conn, uint slot, byte* src, int len)
    {
        if (!_writeBuffer.TryLease(out int wi, out byte* wp))
        {
            // Pool dry: stage the bytes and retry on a later pass instead of tearing down a healthy
            // connection. See WindowsShardBase._awaitingPage.
            StageOutbound(conn, new ReadOnlySpan<byte>(src, len));
            MarkAwaitingPage(conn);
            return true;
        }
        Buffer.MemoryCopy(src, wp, _writeBufSize, len);
        SubmitSendBuffer(conn, slot, wi, len);
        return !conn.Closing;
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
            IssueSend(conn, slot, wi, conn.SendSent, conn.SendTotal - conn.SendSent);
            return;
        }

        CompleteWrite(conn, slot);
    }

    private void CompleteWrite(RioConnection conn, uint slot)
    {
        int wi = conn.SendBuf;
        byte* wp = _writeBuffer.Address(wi);

        // On a TLS connection OnWrite is suppressed until the deferred open has fired: until then the app
        // has never seen this connection and must not be asked to fill a buffer for it.
        int next = 0;
        var tls = conn.Tls;
        if (tls is null || conn.Opened)
        {
            var ctx = new SocketSet.WriteContext(conn, wp, _writeBufSize);
            Parent.OnWrite(ref ctx);
            next = ctx.SendBytes;
        }

        if (tls is not null && next > 0)
        {
            // OnWrite produced PLAINTEXT in the write page. Encrypt it onto the TAIL of Pending rather than
            // sending the page as-is, so it stays ordered behind ciphertext already queued; the drain below
            // then refills this same page from the head. (Records are sequence-numbered — order is not
            // cosmetic here.)
            _tlsCipher!.Reset();
            tls.ProcessOutbound(new ReadOnlySpan<byte>(wp, next), _tlsCipher);
            StageOutbound(conn, _tlsCipher.WrittenSpan);
            next = 0;
        }

        if (next == 0 && conn.Pending is { Count: > 0 } pending)
        {
            // Coalesce as many queued responses as fit into the write page into one RIOSend.
            while (pending.Count > 0)
            {
                var seg = pending.Peek();
                if (next + seg.Count > _writeBufSize) break;
                pending.Dequeue();
                Marshal.Copy(seg.Array!, seg.Offset, (nint)(wp + next), seg.Count);
                next += seg.Count;
                ArrayPool<byte>.Shared.Return(seg.Array!);
            }
        }

        if (next > 0)
        {
            conn.SendSent = 0;
            conn.SendTotal = next; // reuse wi; SendBusy stays set
            IssueSend(conn, slot, wi, 0, next);
        }
        else
        {
            _writeBuffer.Release(wi);
            conn.SendBuf = -1;
            conn.SendBusy = false;
        }
    }



    // Start draining Pending into a freshly-leased write page (precondition: !SendBusy). Coalesces as in
    // CompleteWrite; used to kick an out-of-band flush when the connection is otherwise idle.
    protected override void StartPendingSend(RioConnection conn, uint slot)
    {
        if (conn.Pending is not { Count: > 0 } pending) return;
        if (!_writeBuffer.TryLease(out int wi, out byte* wp))
        {
            // Pool dry: leave the bytes staged in Pending and retry on a later pass. This used to close
            // the connection - see WindowsShardBase._awaitingPage for why that was wrong.
            MarkAwaitingPage(conn);
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

    // =====================================================================
    // TLS interception (see TlsFilter)
    // -------------------------------------------------------------------------------------
    // Mirrors the IOCP shard exactly; see the notes there. The short version: one loop thread per shard
    // means no locking and shard-wide scratch, and ALL ciphertext is staged on the existing Pending queue
    // so records reach the socket in the order the engine produced them (the direct SendResponse path
    // would jump the queue and is never used on a TLS connection).
    // =====================================================================

    // Attach a fresh engine to a just-adopted connection and start the handshake. OnAccept/OnConnect are
    // NOT fired here — they fire from FireTlsOpen once the handshake completes.
    private void BeginTls(RioConnection conn, uint slot, bool isClient)
    {
        var opts = Parent.Options;
        conn.IsClient = isClient;
        conn.Tls = isClient ? opts.Tls!.CreateClientFilter(opts.TlsClient) : opts.Tls!.CreateServerFilter(opts.TlsServer);

        // A client speaks first (ClientHello); a server emits nothing until it has seen one. Either way the
        // receive must be armed so the handshake can advance as bytes arrive.
        if (!DriveTlsHandshake(conn, slot, default)) return;
        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) ArmReceive(conn);
    }






    private void PinLoopThread()
    {
        if (!Parent.Options.PinWorkerThreads || !OperatingSystem.IsWindows()) return;
        nuint mask = (nuint)1 << (Shard % Environment.ProcessorCount);
        Win32.SetThreadAffinityMask(Win32.GetCurrentThread(), mask);
    }
}
#endif
