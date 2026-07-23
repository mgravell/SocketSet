#if NET // Windows RIO backend; compiled out of the netfx fallback build.
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SocketSets.Native;

namespace SocketSets.Windows;

/// <summary>
/// A single-threaded Registered-I/O (RIO) event loop — the TCP data-path accelerator layered on the
/// same IOCP foundation the <c>IocpShard</c> uses. Accept/connect stay pure IOCP (AcceptEx/ConnectEx →
/// completion port); the recv/send hot path rides RIO: pre-registered buffers, a per-connection request
/// queue, and a shard completion queue drained in USER MODE with <see cref="Win32.RIODequeueCompletion"/>
/// (no per-op syscall — the op-count win). RIONotify posts a packet to the IOCP port when the CQ needs
/// draining, so the whole thing runs off the one <see cref="Win32.GetQueuedCompletionStatusEx"/> loop.
///
/// TCP/UDP only — RIO can't do AF_UNIX (that stays on the IOCP backend). Parallel to <c>IocpShard</c>
/// rather than sharing a base: the control plane is similar but the data path + completion model differ
/// enough that duplication was the cheaper, lower-risk choice.
///
/// BLIND: written on Linux, validated on Windows. Compiles everywhere; only ever runs on Windows.
/// </summary>
internal sealed unsafe class WindowsRioShard : SocketSetShard
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
    private readonly RioConnection[] _conns;
    private uint _clientStart;

    // --- options snapshot ---
    private readonly int _socketsPerShard;
    private readonly int _writeCount;
    private readonly int _writeBufSize;
    private readonly int _recvCount;
    private readonly int _recvBufSize;
    private readonly int _listenBacklog;
    private readonly int _acceptConcurrency;

    // --- created on the loop thread in OnInitialize ---
    private nint _port;
    private PinnedWriteBufferPool _writeBuffer;    // registered send slab
    private PinnedWriteBufferPool _recvBuffer;     // registered recv slab (one buffer per connection)
    private Win32.OVERLAPPED_ENTRY* _entries;      // GQCSEx batch
    private CtlOp* _ops;                           // per-slot connect op context (indexed by slot-1)
    private byte* _connectAddrs;                   // per-slot sockaddr for ConnectEx
    private nint _cq;                              // RIO completion queue (shared across this shard's connections)
    private nint _recvBufferId;                    // RIORegisterBuffer(recv slab)
    private nint _writeBufferId;                   // RIORegisterBuffer(write slab)
    private Win32.OVERLAPPED* _rioNotifyOv;        // the OVERLAPPED RIONotify hands back on the port
    private Win32.RIORESULT* _rioResults;          // RIODequeueCompletion output
    private volatile bool _portReady;

    private readonly List<AcceptState> _acceptStates = [];
    private readonly object _acceptGate = new();

    private readonly ConcurrentQueue<(nint Socket, object? Token)> _incoming = [];
    private readonly ConcurrentQueue<(uint Slot, uint Generation)> _closes = [];

    // TEMP: stderr diagnostics for the conns<64 drop investigation. The real drop paths use
    // Debug.WriteLine, which is compiled OUT in Release — so drops were invisible. Remove once resolved.
    public WindowsRioShard(SocketSetOptions options)
    {
        _socketsPerShard = options.SocketsPerShard;
        _writeCount = options.WriteBuffersPerShard;
        _writeBufSize = options.BufferPageSize;
        _recvBufSize = options.BufferPageSize;
        _recvCount = _socketsPerShard; // one recv buffer per connection (recv is always armed)
        _listenBacklog = options.ListenBacklog;
        _acceptConcurrency = Math.Max(1, Math.Min(options.AcceptConcurrency, _socketsPerShard));
        _conns = new RioConnection[_socketsPerShard];
        for (int i = 0; i < _conns.Length; i++)
            _conns[i] = new RioConnection(this, (uint)i + 1);
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
            if (n == 0)
            {
                // CQ empty: re-arm race-free (arm, then one more drain to catch the gap).
                Win32.RIONotify(_cq);
                if (DrainRioOnce() == 0) return; // nothing slipped into the gap → done
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
        }

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
    internal void SubmitClose(uint slot, uint generation) { _closes.Enqueue((slot, generation)); Poke(); }

    private void DrainCrossThread()
    {
        while (_incoming.TryDequeue(out var inbound)) AdoptAccepted(inbound.Socket, inbound.Token);
        while (_closes.TryDequeue(out var c))
        {
            var conn = _conns[c.Slot - 1];
            if (conn.Generation == c.Generation && conn.Socket != 0) CloseClient(c.Slot);
        }
    }

    // =====================================================================
    // Slot table
    // =====================================================================

    private RioConnection? InitClient(nint socket, object? userToken, SocketSet.SocketFlags flags)
    {
        var conns = _conns;
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
                conn.Rq = 0;
                conn.RecvBuf = -1;
                conn.SendBuf = -1;
                conn.Pending?.Clear();
                Volatile.Write(ref conn.Generation, conn.Generation + 1);
                return conn;
            }
        }
        return null;
    }

    private void CloseClient(uint slot)
    {
        if (slot == 0) return;
        var conn = _conns[slot - 1];
        if (conn.Socket == 0 || conn.Closing) return;
        conn.Closing = true;

        if (conn.Opened)
        {
            conn.Opened = false;
            try { Parent.OnClosed(conn); }
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
        conn.Rq = 0; // torn down with the socket
        conn.UserToken = null;
        conn.Flags = 0;
        conn.Closing = false;
        Volatile.Write(ref conn.Socket, 0);
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
        if (endpoint is not IPEndPoint ip)
            throw new NotSupportedException("The RIO backend is TCP-only; use the IOCP backend for AF_UNIX.");

        nint s = Win32.WSASocketW(Win32.AF_INET, Win32.SOCK_STREAM, Win32.IPPROTO_TCP, null, 0,
            Win32.WSA_FLAG_OVERLAPPED | Win32.WSA_FLAG_REGISTERED_IO);
        if (s == Win32.INVALID_SOCKET) throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW failed");
        Win32.LoadExtensions(s);

        int one = 1;
        Win32.setsockopt(s, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int));
        Win32.SockAddrIn any = default;
        any.sin_family = (ushort)Win32.AF_INET;
        Win32.bind(s, &any, 16); // ConnectEx requires a bound socket

        var conn = InitClient(s, userToken, SocketSet.SocketFlags.None);
        if (conn is null) { Win32.closesocket(s); throw new InvalidOperationException("Shard socket table is full."); }
        uint slot = conn.Slot;

        if (Win32.CreateIoCompletionPort(s, _port, slot, 0) == 0)
        {
            Win32.closesocket(s);
            Volatile.Write(ref conn.Socket, 0);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort(connect) failed");
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
            Volatile.Write(ref conn.Socket, 0);
            throw new Win32Exception(Win32.WSAGetLastError(), "ConnectEx failed");
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

        var target = (WindowsRioShard)Parent.RoundRobin();
        target.EnqueueInbound(acc, st.Token);
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
        if (conn is null) { Win32.closesocket(socket); return; }
        uint slot = conn.Slot;

        if (!SetupConnection(conn)) { Win32.closesocket(socket); Volatile.Write(ref conn.Socket, 0); return; }

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
        if (Win32.RIOReceive(conn.Rq, &buf, 1, 0, (void*)(nuint)ReqRecv) == 0)
        {
            conn.RecvArmed = false;
            CloseClient(conn.Slot);
        }
        // else: the completion arrives on the CQ.
    }

    // Post a RIOSend of write-slab index wi, bytes [off, off+len). Tears the connection down on failure.
    private void IssueSend(RioConnection conn, uint slot, int wi, int off, int len)
    {
        Win32.RIO_BUF buf;
        buf.BufferId = _writeBufferId;
        buf.Offset = (uint)(wi * _writeBufSize + off);
        buf.Length = (uint)len;
        if (Win32.RIOSend(conn.Rq, &buf, 1, 0, (void*)(nuint)ReqSend) == 0) FailSend(conn, slot);
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
        byte* rp = _recvBuffer.Address(conn.RecvBuf);
        var ctx = new SocketSet.ReceiveContext(conn, rp, _recvBufSize, bytes);
        Parent.OnReceive(ref ctx);

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
            System.Diagnostics.Debug.WriteLine("Write buffer pool exhausted; closing connection.");
            CloseClient(slot);
            return false;
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
        var ctx = new SocketSet.WriteContext(conn, wp, _writeBufSize);
        Parent.OnWrite(ref ctx);

        int next = ctx.SendBytes;
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

    private void PinLoopThread()
    {
        if (!Parent.Options.PinWorkerThreads || !OperatingSystem.IsWindows()) return;
        nuint mask = (nuint)1 << (Shard % Environment.ProcessorCount);
        Win32.SetThreadAffinityMask(Win32.GetCurrentThread(), mask);
    }
}
#endif
