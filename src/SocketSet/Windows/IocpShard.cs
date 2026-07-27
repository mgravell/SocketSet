#if NET // Windows IOCP backend; compiled out of the netfx fallback build.
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
/// A single-threaded IOCP event loop — the Windows analogue of <c>IoUringShard</c>. Exactly one
/// thread owns the completion port (created with concurrency 1); cross-thread work (accept hand-off,
/// Close) is marshaled in and the loop woken with <see cref="Win32.PostQueuedCompletionStatus"/> (the
/// eventfd analogue), and completions are drained in batches with
/// <see cref="Win32.GetQueuedCompletionStatusExBlocking"/>. <see cref="Listen"/>/<see cref="Connect"/> submit
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
internal sealed unsafe class IocpShard : SocketSetShard, IWindowsShard
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
    private SlotAllocator _slots; // loop-local free-slot allocator (claim/free, single-writer). NOT readonly:
                                  // it's a mutable struct, so a readonly field would mutate a throwaway copy.
    // Connect requests marshaled from the caller thread to the loop, which claims the slot + posts
    // ConnectEx — so the slot table stays single-writer. The socket is created caller-side (thread-agnostic
    // syscalls, sync failures stay synchronous); the port-assoc + ConnectEx run on the loop (their failures
    // become async, uniform with accept).
    private readonly ConcurrentQueue<(nint Socket, EndPoint Endpoint, object? Token)> _pendingConnects = [];

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

    // TLS scratch, shared by every connection on this shard (null unless Options.Tls is set). Safe to share
    // because a shard has ONE loop thread and a filter is only ever touched from it — the managed backend
    // needs a per-connection gate precisely because it has no such thread.
    private PooledBufferWriter? _tlsPlain;   // decrypt target
    private PooledBufferWriter? _tlsCipher;  // encrypt scratch
    private PooledBufferWriter? _tlsCtrl;    // handshake / control-record output

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
        // Receive buffers are per-socket and held for the connection lifetime, so their size multiplies by
        // SocketsPerShard; the send page does not. See SocketSetOptions.ReceiveBufferSize.
        _recvBufSize = options.ReceiveBufferSize > 0 ? options.ReceiveBufferSize : options.BufferPageSize;
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
        _slots = new SlotAllocator(_conns.Length);
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

        while (_pendingConnects.TryDequeue(out var pc))
            StartConnect(pc.Socket, pc.Endpoint, pc.Token);

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
            var tls = _conns[i].Tls;
            if (tls is not null) { _conns[i].Tls = null; tls.Dispose(); }
        }

        _tlsPlain?.Dispose(); _tlsPlain = null;
        _tlsCipher?.Dispose(); _tlsCipher = null;
        _tlsCtrl?.Dispose(); _tlsCtrl = null;

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

    /// <summary>Marshal a close request onto the loop thread (from <see cref="WindowsConnection.Close"/>).</summary>
    public void SubmitClose(uint slot, uint generation)
    {
        _closes.Enqueue((slot, generation));
        Poke();
    }

    /// <summary>Marshal an out-of-band flushed write onto the loop thread (from <see cref="OutboundConnection.Flush"/>).</summary>
    public void SubmitFlush(uint slot, uint generation, byte[] data, int length)
    {
        _flush.Enqueue((slot, generation, data, length));
        Poke();
    }

    // =====================================================================
    // Slot table
    // =====================================================================

    // Claim a free slot for a socket. Loop-thread only (accept adoption, or a connect marshaled via
    // StartConnect) — the single-writer model, so the claim is a plain free-list pop + plain stores, no
    // CAS. The caller reserved first, so a slot is guaranteed; Claim only fails on counter drift / an
    // unreserved caller (backstop → caller releases + drops). Returns null if the table is full.
    private IocpConnection? InitClient(nint socket, object? userToken, SocketSet.SocketFlags flags)
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
        conn.SkipOnSuccess = false;
        conn.RecvBuf = -1;
        conn.SendBuf = -1;
        conn.SendPageCount = 0;
        conn.Pending?.Clear();
        conn.Tls = null;      // disposed by TryFinalize; cleared here so a rolled-back claim starts clean
        conn.IsClient = false;
        // Bump the generation before publishing Socket: any out-of-band Close/flush captured against the
        // previous tenant now mismatches and is dropped rather than misapplied.
        Volatile.Write(ref conn.Generation, conn.Generation + 1);
        Volatile.Write(ref conn.Socket, socket); // publish live last (foreign readers gate on Socket != 0)
        return conn;
    }

    // Roll back a loop-side claim whose connect/adoption couldn't be armed: publish the slot free, return
    // it to the allocator, and release the reservation — the post-claim analogue of TryFinalize's tail
    // (minus buffer cleanup, since nothing was armed). Loop thread only.
    private void FreeSlot(IocpConnection conn)
    {
        Volatile.Write(ref conn.Socket, 0);
        _slots.Free((int)(conn.Slot - 1));
        ReleaseReservation();
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
        ReleaseSendPages(conn); // every page of any send that was still in flight
        // Return any queued (pooled) echo staging buffers before recycling the slot.
        if (conn.Pending is { } pending)
            while (pending.Count > 0) ArrayPool<byte>.Shared.Return(pending.Dequeue().Array!);
        // Release the TLS engine (SSPI context / SSL*) with the rest of the per-connection state.
        if (conn.Tls is { } tls) { conn.Tls = null; tls.Dispose(); }
        conn.UserToken = null;
        conn.Flags = 0;
        conn.Closing = false;
        Volatile.Write(ref conn.Socket, 0); // publish free last (socket already closed in CloseClient)
        _slots.Free((int)(slot - 1));       // return to the loop-local allocator (loop thread only)
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

        // This shard holds a reservation (TryPlace took it). Create + bind the socket HERE (thread-agnostic
        // syscalls, so their failures stay synchronous to the caller), then hand the claim + port-assoc +
        // ConnectEx to the loop, keeping the slot table single-writer. Release the reservation on any
        // synchronous failure so a rejected connect doesn't permanently consume capacity.
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

        _pendingConnects.Enqueue((s, endpoint, userToken));
        Poke();
    }

    // Loop thread: claim the reserved slot for a marshaled connect, associate the socket with the port,
    // build the target sockaddr into the slot's stable native storage, and post ConnectEx. The reservation
    // is consumed by the claim, or released here on any post-claim failure (which are now async, like
    // accept — the caller has already returned).
    private void StartConnect(nint s, EndPoint endpoint, object? userToken)
    {
        var conn = InitClient(s, userToken, SocketSet.SocketFlags.None);
        if (conn is null) { Win32.closesocket(s); ReleaseReservation(); return; }
        uint slot = conn.Slot;

        if (Win32.CreateIoCompletionPort(s, _port, slot, 0) == 0)
        {
            Win32.closesocket(s);
            FreeSlot(conn);
            return;
        }

        // Build the target sockaddr into this slot's stable native storage (the kernel dereferences it
        // asynchronously once ConnectEx is posted).
        byte* addrPtr = _connectAddrs + (nint)(slot - 1) * AddrStride;
        uint addrLen;
        if (endpoint is IPEndPoint ip)
        {
            var sa = (Win32.SockAddrIn*)addrPtr;
            *sa = default;
            sa->sin_family = (ushort)Win32.AF_INET;
            sa->sin_port = Win32.Htons((ushort)ip.Port);
            var b = ip.Address.GetAddressBytes(); // 4 bytes, network order
            byte* dst = (byte*)&sa->sin_addr;
            dst[0] = b[0]; dst[1] = b[1]; dst[2] = b[2]; dst[3] = b[3];
            addrLen = 16;
        }
        else // UnixDomainSocketEndPoint (caller validated the type before marshaling)
        {
            var uds = (UnixDomainSocketEndPoint)endpoint;
            addrLen = Win32.SockAddrUn.Init((Win32.SockAddrUn*)addrPtr, uds.ToString());
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
            FreeSlot(conn);
            return;
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
            FreeSlot(conn);
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
            FreeSlot(conn);
            return;
        }
        conn.RecvBuf = ri;

        // TLS: the app must not see this connection until the handshake completes, so OnAccept is deferred
        // to FireTlsOpen and everything below is skipped.
        if (Parent.Options.Tls is not null) { BeginTls(conn, slot, isClient: false); return; }

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

        // TLS: OnConnect is deferred to FireTlsOpen (the client speaks first — see BeginTls).
        if (Parent.Options.Tls is not null) { BeginTls(conn, slot, isClient: true); return; }

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
        if (conn.Tls is not null) return DeliverReceiveTls(conn, slot, bytes);

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

    // Send the whole of an already-filled write buffer (initial send / echo) as a one-page send.
    private void SubmitSendBuffer(IocpConnection conn, uint slot, int wi, int len)
    {
        conn.SendPages[0] = wi;
        conn.SendLens[0] = len;
        conn.SendPageCount = 1;
        conn.SendBuf = wi;
        conn.SendSent = 0;
        conn.SendTotal = len;
        conn.SendBusy = true;
        IssueSendPages(conn, slot);
    }

    /// <summary>
    /// Post the in-flight send as ONE WSASend over all its pages, resuming at <c>SendSent</c>. This is
    /// the whole point of the page array: a 256KB response is one call with 64 WSABUFs rather than 64
    /// sequential calls. On a synchronous outcome (success with FILE_SKIP, or any failure) no packet
    /// posts, so the completion is deferred inline - a synchronous failure flows through as
    /// HandleSend(failed) -> FailSend, exactly like an async error.
    /// </summary>
    private void IssueSendPages(IocpConnection conn, uint slot)
    {
        IocpOp* op = SendOp(slot);
        op->Kind = OpKind.Send;
        op->Slot = slot;
        op->Buf = conn.SendPages[0];

        // Skip whatever a previous partial send already delivered, then describe the remainder.
        Win32.WSABUF* bufs = stackalloc Win32.WSABUF[IocpConnection.MaxSendPages];
        int n = 0, skip = conn.SendSent;
        for (int i = 0; i < conn.SendPageCount; i++)
        {
            int len = conn.SendLens[i];
            if (skip >= len) { skip -= len; continue; } // this page is fully acknowledged
            bufs[n].buf = _writeBuffer.Address(conn.SendPages[i]) + skip;
            bufs[n].len = (uint)(len - skip);
            n++;
            skip = 0;
        }
        if (n == 0) { CompleteWrite(conn, slot); return; } // nothing left outstanding

        uint sent = 0;
        int rc = Win32.WSASend(conn.Socket, bufs, (uint)n, &sent, 0, &op->Overlapped, null);
        if (rc == 0)
        {
            if (conn.SkipOnSuccess) QueueInline(OpKind.Send, slot, sent, failed: false);
            return;
        }
        if (Marshal.GetLastPInvokeError() != Win32.WSA_IO_PENDING)
            QueueInline(OpKind.Send, slot, 0, failed: true);
        // WSA_IO_PENDING → an async completion will arrive.
    }

    /// <summary>Release every page of the in-flight send and mark the connection idle.</summary>
    private void ReleaseSendPages(IocpConnection conn)
    {
        for (int i = 0; i < conn.SendPageCount; i++) _writeBuffer.Release(conn.SendPages[i]);
        conn.SendPageCount = 0;
        conn.SendBuf = -1;
    }

    /// <summary>
    /// Pack queued responses into the send pages, spilling into freshly-leased pages as needed. Packing
    /// rather than one-segment-per-page is what keeps a pipelined echo cheap: several small responses
    /// still coalesce into a single page, and only a large run spills. Returns the bytes added.
    /// </summary>
    private int DrainPendingIntoPages(IocpConnection conn)
    {
        if (conn.Pending is not { Count: > 0 } pending) return 0;
        int added = 0;
        while (pending.Count > 0)
        {
            var seg = pending.Peek();
            int pi = conn.SendPageCount - 1;
            int used = pi >= 0 ? conn.SendLens[pi] : _writeBufSize; // no page yet -> force a lease
            if (pi < 0 || used + seg.Count > _writeBufSize)
            {
                if (conn.SendPageCount >= IocpConnection.MaxSendPages) break; // cap this send; rest follows
                if (!_writeBuffer.TryLease(out int wi, out _)) break;         // pool dry; send what we have
                pi = conn.SendPageCount++;
                conn.SendPages[pi] = wi;
                conn.SendLens[pi] = 0;
                used = 0;
            }
            // A staged segment never exceeds one page (StageOutbound chunks it), so it always fits here.
            pending.Dequeue();
            Marshal.Copy(seg.Array!, seg.Offset, (nint)(_writeBuffer.Address(conn.SendPages[pi]) + used), seg.Count);
            conn.SendLens[pi] = used + seg.Count;
            added += seg.Count;
            ArrayPool<byte>.Shared.Return(seg.Array!);
        }
        return added;
    }

    // A send failed synchronously: release its buffers, clear the send slot, tear the connection down.
    private void FailSend(IocpConnection conn, uint slot)
    {
        ReleaseSendPages(conn);
        conn.SendBusy = false;
        CloseClient(slot);
    }

    private void HandleSend(uint slot, uint bytes, bool failed)
    {
        var conn = _conns[slot - 1];

        if (conn.Closing)
        {
            ReleaseSendPages(conn);
            conn.SendBusy = false;
            TryFinalize(conn, slot);
            return;
        }

        if (failed) { FailSend(conn, slot); return; }

        conn.SendSent += (int)bytes;
        if (conn.SendSent < conn.SendTotal)
        {
            if (bytes == 0) { FailSend(conn, slot); return; } // no progress → dead peer
            // Partial send: re-post the remainder. IssueSendPages skips the acknowledged prefix across
            // pages, so a partial write that lands mid-page resumes at the right byte.
            IssueSendPages(conn, slot);
            return;
        }

        CompleteWrite(conn, slot);
    }

    // A send fully completed. Offer the freed buffer to OnWrite (pipeline the next message straight back
    // into it); failing that, drain a queued echo into it; failing both, release it and go idle.
    private void CompleteWrite(IocpConnection conn, uint slot)
    {
        // Keep page 0 (OnWrite needs somewhere to write); hand the rest back now that they are on the wire.
        for (int i = 1; i < conn.SendPageCount; i++) _writeBuffer.Release(conn.SendPages[i]);
        conn.SendPageCount = 1;
        conn.SendLens[0] = 0;
        int wi = conn.SendPages[0];
        conn.SendBuf = wi;
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

        if (next > 0)
        {
            // OnWrite filled page 0 directly; send it as a one-page send.
            conn.SendLens[0] = next;
            conn.SendSent = 0;
            conn.SendTotal = next; // reuse page 0; SendBusy stays set
            IssueSendPages(conn, slot);
            return;
        }

        // Coalesce queued responses. This is the batching lever under pipelining - it cuts send syscalls
        // N:1 and, because the peer then drains a bigger chunk per recv, its recv-op count too. It now
        // also SPILLS past one page, so a large queued run goes out as one multi-buffer WSASend instead
        // of ceil(size/page) sequential ones.
        int total = DrainPendingIntoPages(conn);
        if (total > 0)
        {
            conn.SendSent = 0;
            conn.SendTotal = total;
            IssueSendPages(conn, slot);
        }
        else
        {
            ReleaseSendPages(conn);
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

        if (conn.Tls is { } tls)
        {
            // Out-of-band writes are application plaintext; encrypt before staging. A flush cannot legally
            // arrive before the handshake completes (the app has no Connection reference until the deferred
            // open), so dropping one is strictly safer than letting plaintext reach the wire.
            if (!tls.HandshakeComplete)
            {
                System.Diagnostics.Debug.WriteLine("TLS flush before handshake completion; dropped.");
                return;
            }
            _tlsCipher!.Reset();
            tls.ProcessOutbound(new ReadOnlySpan<byte>(data, 0, len), _tlsCipher);
            StageOutbound(conn, _tlsCipher.WrittenSpan);
        }
        else
        {
            StageOutbound(conn, new ReadOnlySpan<byte>(data, 0, len));
        }
        if (!conn.SendBusy) StartPendingSend(conn, slot);
    }

    // Chunk bytes into write-page-sized pooled segments on Pending (the shape the echo path already
    // drains). Page-sized chunks preserve the drain loops' "the first item always fits" invariant.
    private void StageOutbound(IocpConnection conn, ReadOnlySpan<byte> data)
    {
        var pending = conn.Pending ??= new();
        for (int off = 0; off < data.Length;)
        {
            int n = Math.Min(_writeBufSize, data.Length - off);
            var buf = ArrayPool<byte>.Shared.Rent(n); // pooled staging (uniform with echo; returned on drain)
            data.Slice(off, n).CopyTo(buf);
            pending.Enqueue(new ArraySegment<byte>(buf, 0, n));
            off += n;
        }
    }

    // Start draining Pending into freshly-leased write pages (precondition: !SendBusy). Used to kick an
    // out-of-band flush when the connection is otherwise idle; spills across pages exactly as
    // CompleteWrite does, so a large flush is one multi-buffer WSASend.
    private void StartPendingSend(IocpConnection conn, uint slot)
    {
        if (conn.Pending is not { Count: > 0 }) return;
        conn.SendPageCount = 0;
        int total = DrainPendingIntoPages(conn);
        if (total == 0)
        {
            // DrainPendingIntoPages only fails to place the first segment if the pool is dry.
            System.Diagnostics.Debug.WriteLine("Write buffer pool exhausted; closing connection.");
            ReleaseSendPages(conn);
            CloseClient(slot);
            return;
        }
        conn.SendBuf = conn.SendPages[0];
        conn.SendSent = 0;
        conn.SendTotal = total;
        conn.SendBusy = true;
        IssueSendPages(conn, slot);
    }


    // =====================================================================
    // TLS interception (see TlsFilter)
    // -------------------------------------------------------------------------------------
    // This backend has ONE loop thread per shard and every filter call below runs on it, so — unlike the
    // managed fallback, which needs a per-connection gate — there is no locking here and the scratch
    // writers are shared shard-wide.
    //
    // The integration point is the existing Pending queue: ALL ciphertext (handshake flights, control
    // records, encrypted application data) is staged there and drained by the normal send machinery. That
    // is what keeps records in the order the engine produced them — TLS records are sequence-numbered, so
    // the direct SendResponse path, which would jump ahead of anything already queued, must never be used
    // on a TLS connection.
    // =====================================================================

    // Attach a fresh engine to a just-adopted connection and start the handshake. OnAccept/OnConnect are
    // NOT fired here — they fire from FireTlsOpen once the handshake completes.
    private void BeginTls(IocpConnection conn, uint slot, bool isClient)
    {
        var opts = Parent.Options;
        conn.IsClient = isClient;
        conn.Tls = isClient ? opts.Tls!.CreateClientFilter(opts.TlsClient) : opts.Tls!.CreateServerFilter(opts.TlsServer);

        // A client speaks first (ClientHello); a server emits nothing until it has seen one. Either way the
        // receive must be armed so the handshake can advance as bytes arrive.
        if (!DriveTlsHandshake(conn, slot, default)) return;
        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) ArmRecv(conn);
    }

    // Feed one chunk to the handshake and queue whatever it emits (already TLS records — staged raw, not
    // re-encrypted). Returns false if the connection was torn down.
    private bool DriveTlsHandshake(IocpConnection conn, uint slot, ReadOnlySpan<byte> input)
    {
        _tlsCtrl!.Reset();
        var status = conn.Tls!.DriveHandshake(input, conn.Socket, _tlsCtrl);
        QueueCipher(conn, slot, _tlsCtrl.WrittenSpan); // may carry a fatal alert on failure — send it first

        if (status == TlsHandshakeStatus.Faulted) { CloseClient(slot); return false; }
        if (conn.Closing || conn.Socket == 0) return false; // QueueCipher tore it down (pool exhausted)
        if (status == TlsHandshakeStatus.Completed) FireTlsOpen(conn, slot);
        return !conn.Closing && conn.Socket != 0;
    }

    // Handshake complete: fire the deferred open and encrypt any greeting it produced.
    private void FireTlsOpen(IocpConnection conn, uint slot)
    {
        // The write page here is only scratch for the greeting — the ciphertext goes out via Pending, so
        // the page is released again rather than sent.
        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        conn.Opened = true; // app now sees it open → pairs with OnClosed
        int sb;
        if (conn.IsClient)
        {
            var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _writeBufSize : 0);
            Parent.OnConnect(ref ctx);
            sb = ctx.SendBytes;
        }
        else
        {
            var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _writeBufSize : 0);
            Parent.OnAccept(ref ctx);
            sb = ctx.SendBytes;
        }

        if (!leased) return;
        if (sb > 0 && !conn.Closing && conn.Socket != 0 && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
            SendEncrypted(conn, slot, wp, sb);
        _writeBuffer.Release(wi);
    }

    // Data phase inbound: decrypt, hand the plaintext to OnReceive, encrypt any response.
    private bool DeliverReceiveTls(IocpConnection conn, uint slot, int bytes)
    {
        var tls = conn.Tls!;
        byte* rp = _recvBuffer.Address(conn.RecvBuf);
        var cipherIn = new ReadOnlySpan<byte>(rp, bytes);

        if (!tls.HandshakeComplete)
        {
            if (!DriveTlsHandshake(conn, slot, cipherIn)) return false;
            if (!tls.HandshakeComplete) return true; // still handshaking; keep receiving

            // Just completed. Application data coalesced into the same segment as the peer's final
            // handshake flight is already buffered INSIDE the engine — surface it now with an empty input,
            // or it strands until a next recv that may never come. See TlsFilter.DriveHandshake.
            cipherIn = default;
        }

        _tlsPlain!.Reset();
        _tlsCtrl!.Reset();
        var status = tls.ProcessInbound(cipherIn, TlsContentType.Ciphertext, _tlsPlain, _tlsCtrl);
        QueueCipher(conn, slot, _tlsCtrl.WrittenSpan); // protocol replies (e.g. a TLS 1.3 KeyUpdate ack)

        if (status == TlsInboundStatus.Faulted) { CloseClient(slot); return false; }
        if (conn.Closing || conn.Socket == 0) return false;

        int plainLen = _tlsPlain.WrittenCount;
        if (plainLen > 0)
        {
            byte[] plain = _tlsPlain.Array;
            fixed (byte* pp = plain)
            {
                var ctx = new SocketSet.ReceiveContext(conn, pp, plain.Length, plainLen);
                Parent.OnReceive(ref ctx);
                int rb = ctx.ResponseBytes;
                if (rb > 0 && !conn.Closing && conn.Socket != 0
                    && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
                {
                    SendEncrypted(conn, slot, pp, rb);
                }
            }
        }

        if (status == TlsInboundStatus.PeerClosed) { CloseClient(slot); return false; }
        return !conn.Closing;
    }

    // Encrypt application plaintext into the outbound stream.
    private void SendEncrypted(IocpConnection conn, uint slot, byte* plaintext, int len)
    {
        _tlsCipher!.Reset();
        conn.Tls!.ProcessOutbound(new ReadOnlySpan<byte>(plaintext, len), _tlsCipher);
        QueueCipher(conn, slot, _tlsCipher.WrittenSpan);
    }

    // Stage ciphertext on Pending and kick a send if the connection is idle.
    private void QueueCipher(IocpConnection conn, uint slot, ReadOnlySpan<byte> cipher)
    {
        if (cipher.IsEmpty) return;
        StageOutbound(conn, cipher);
        if (!conn.SendBusy && !conn.Closing && conn.Socket != 0) StartPendingSend(conn, slot);
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
