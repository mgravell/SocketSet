#if NET // Linux epoll backend; compiled out of the netfx fallback build.
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SocketSets.IoUring;
using SocketSets.Native;
using SocketSets.Tls;

namespace SocketSets.Epoll;

/// <summary>
/// Linux epoll backend: one loop thread per shard, a fixed slot table, and an eventfd for cross-thread
/// wake - structurally the same shard as io_uring and the Windows backends. The difference that shapes
/// everything below is the I/O model.
///
/// io_uring, IOCP and RIO are COMPLETION models: you submit a read and are later told how many bytes
/// arrived. epoll is a READINESS model: you are told an fd is readable, and must then call recv()
/// yourself. Three consequences run through this file:
///
///  1. Every wake is a hint, not a result. recv() can still return EAGAIN (a spurious or already-drained
///     wake), and can return fewer bytes than asked for. Nothing may assume a wake means data.
///  2. Writes can partially complete. send() on a non-blocking socket routinely accepts only part of a
///     buffer, so a per-connection queue plus a byte cursor into its head is mandatory, not an
///     optimisation for overlapping sends (which is why the Windows backends have one).
///  3. Write interest must be armed and disarmed. This uses LEVEL-triggered epoll, where EPOLLIN staying
///     set until drained is exactly what we want, but a level-triggered EPOLLOUT on an idle socket would
///     spin the loop continuously. So EPOLLOUT is registered only while a write is actually blocked.
///
/// Level- rather than edge-triggered is deliberate: edge-triggered demands that every fd be drained to
/// EAGAIN on every wake or events are lost forever, which turns any early return into a silent stall.
/// Level-triggered costs one extra syscall in some paths and is far harder to get subtly wrong.
/// </summary>
internal sealed unsafe class EpollShard : SocketSetShard
{
    // How many events one epoll_wait can return. Purely a batching knob.
    private const int MaxEvents = 256;

    // Bound the work one connection can do per wake, so a single hot socket cannot monopolise the loop
    // and starve accepts. Level-triggered epoll re-reports anything left over on the next pass.
    private const int ReadBurst = 8;
    private const int WriteBurst = 16;

    // epoll data-word tags. The data word is the ONLY context epoll hands back, so it carries a kind in
    // the high 32 bits and an index in the low 32 (slot for connections, fd for listeners).
    private const ulong KindWake = 0UL << 32;
    private const ulong KindListen = 1UL << 32;
    private const ulong KindConn = 2UL << 32;

    private readonly int _socketsPerShard;
    private readonly int _bufSize;      // SEND page: write scratch, and the OnWrite/OnAccept buffer
    // RECEIVE buffer, which is a DIFFERENT quantity and must be allowed to stay small. _recvBuffer is one
    // per LIVE CONNECTION (SocketsPerShard of them, 4096 by default), so this size multiplies by the slot
    // table, not by a pool depth: at a 64KB page that is 256MB per shard. It is the same per-socket
    // scaling that took a 12-shard RIO server from 283MB to 3,163MB on Windows, and it is why
    // SocketSetOptions.ReceiveBufferSize exists. epoll did not honour it until 2026-07-28; the reason the
    // RSS table in AspNetDemo/RESULTS.md showed epoll "flat" across page sizes is that the slab is
    // calloc'd (lazily faulted) and -c 64 touches 64 of the 4096 buffers.
    private readonly int _recvBufSize;
    private readonly int _listenBacklog;

    private readonly EpollConnection[] _conns;
    private SlotAllocator _slots;

    // --- created on the loop thread in OnInitialize ---
    private int _epfd = -1;
    private int _wakeFd = -1;
    private byte* _events;          // epoll_wait output; MaxEvents * LibC.EpollEventSize bytes
    private PinnedWriteBufferPool _recvBuffer;  // one per live connection
    private PinnedWriteBufferPool _writeBuffer; // scratch handed to OnAccept/OnConnect/OnWrite
    private volatile bool _ready;

    // TLS scratch, shared shard-wide (null unless Options.Tls is set). Safe to share because a shard has
    // ONE loop thread and a filter is only ever touched from it - the managed backend needs a
    // per-connection gate precisely because it has no such thread.
    private PooledBufferWriter? _tlsPlain;   // decrypt target
    private PooledBufferWriter? _tlsCipher;  // encrypt scratch
    private PooledBufferWriter? _tlsCtrl;    // handshake / control-record output

    // --- listeners (loop thread) ---
    private readonly Dictionary<int, object?> _listeners = [];

    // --- cross-thread queues, drained on the loop thread ---
    private readonly ConcurrentQueue<(int Fd, object? Token)> _incoming = [];
    private readonly ConcurrentQueue<(int Slot, uint Generation)> _closes = [];
    private readonly ConcurrentQueue<(int Slot, uint Generation, byte[] Data, int Len)> _flush = [];
    private readonly ConcurrentQueue<(int Fd, EndPoint Endpoint, object? Token)> _connects = [];
    private readonly ConcurrentQueue<(int Fd, object? Token)> _newListeners = [];

    public EpollShard(SocketSetOptions options)
    {
        _socketsPerShard = options.SocketsPerShard;
        _bufSize = options.BufferPageSize;
        _recvBufSize = options.ReceiveBufferSize > 0 ? options.ReceiveBufferSize : options.BufferPageSize;
        _listenBacklog = options.ListenBacklog;

        // Pre-allocate the connection table: one pooled instance per slot, reused across lifetimes so
        // accept/connect never allocates. Every use is gated by the per-slot Generation token, so a stale
        // Close or flush against a re-tenanted slot is dropped rather than misdelivered.
        _conns = new EpollConnection[_socketsPerShard];
        for (int i = 0; i < _conns.Length; i++) _conns[i] = new EpollConnection(this, i);
        _slots = new SlotAllocator(_conns.Length);
        SetShardCapacity(_conns.Length);
    }

    // =====================================================================
    // Lifecycle
    // =====================================================================

    protected override void OnInitialize()
    {
        _epfd = LibC.epoll_create1(LibC.EPOLL_CLOEXEC);
        if (_epfd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "epoll_create1 failed");

        // EFD_NONBLOCK | EFD_CLOEXEC, so draining it never blocks the loop.
        _wakeFd = LibC.eventfd(0, 0x800 | 0x80000);
        if (_wakeFd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "eventfd failed");
        if (!Register(_wakeFd, LibC.EPOLLIN, KindWake))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "epoll_ctl(eventfd) failed");

        _events = (byte*)NativeMemory.AllocZeroed((nuint)(MaxEvents * LibC.EpollEventSize));
        _recvBuffer = new PinnedWriteBufferPool(_socketsPerShard, _recvBufSize);
        _writeBuffer = new PinnedWriteBufferPool(Math.Max(8, Parent.Options.WriteBuffersPerShard), _bufSize);
        if (Parent.Options.Tls is not null)
        {
            _tlsPlain = new PooledBufferWriter(_bufSize);
            _tlsCipher = new PooledBufferWriter(_bufSize);
            _tlsCtrl = new PooledBufferWriter(1024);
        }
        _ready = true;
    }

    protected override void OnRun()
    {
        while (IsActive)
        {
            // Honour marshaled work before blocking. Anything enqueued after this point writes the
            // eventfd, which is what unblocks epoll_wait below - so nothing can be missed.
            DrainCrossThread();

            int n = LibC.epoll_wait(_epfd, _events, MaxEvents, -1);
            if (n < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err is LibC.EINTR) continue; // signal (e.g. the GC's suspend) - not an error
                break;
            }

            for (int i = 0; i < n; i++)
            {
                uint events = LibC.ReadEpollEvents(_events, i);
                ulong data = LibC.ReadEpollData(_events, i);
                switch (data & 0xFFFF_FFFF_0000_0000UL)
                {
                    case KindWake: DrainWake(); break;
                    case KindListen: AcceptBurst((int)(uint)data); break;
                    case KindConn: HandleConnection((int)(uint)data, events); break;
                }
            }
        }
    }

    protected override void OnStop() => Poke();

    protected override void OnShutdown()
    {
        _ready = false;

        foreach (int fd in _listeners.Keys) LibC.close(fd);
        _listeners.Clear();

        for (int i = 0; i < _conns.Length; i++)
        {
            int fd = Interlocked.Exchange(ref _conns[i].Fd, -1);
            if (fd >= 0) LibC.close(fd);
            var tls = _conns[i].Tls;
            if (tls is not null) { _conns[i].Tls = null; tls.Dispose(); }
        }

        if (_events != null) { NativeMemory.Free(_events); _events = null; }
        _recvBuffer.Dispose();
        _writeBuffer.Dispose();
        _tlsPlain?.Dispose(); _tlsPlain = null;
        _tlsCipher?.Dispose(); _tlsCipher = null;
        _tlsCtrl?.Dispose(); _tlsCtrl = null;
        if (_wakeFd >= 0) { LibC.close(_wakeFd); _wakeFd = -1; }
        if (_epfd >= 0) { LibC.close(_epfd); _epfd = -1; }
    }

    // =====================================================================
    // epoll registration
    // =====================================================================

    private bool Register(int fd, uint events, ulong data)
    {
        byte* ev = stackalloc byte[LibC.EpollEventSize];
        LibC.WriteEpollEvent(ev, events, data);
        return LibC.epoll_ctl(_epfd, LibC.EPOLL_CTL_ADD, fd, ev) == 0;
    }

    private bool Modify(int fd, uint events, ulong data)
    {
        byte* ev = stackalloc byte[LibC.EpollEventSize];
        LibC.WriteEpollEvent(ev, events, data);
        return LibC.epoll_ctl(_epfd, LibC.EPOLL_CTL_MOD, fd, ev) == 0;
    }

    // Baseline interest for a live connection. EPOLLRDHUP surfaces a peer half-close as an event rather
    // than leaving us to discover it only via a zero-length recv.
    private const uint ConnEventsRead = LibC.EPOLLIN | LibC.EPOLLRDHUP;

    private void ArmWrite(EpollConnection conn)
    {
        if (conn.WantWrite || conn.Fd < 0) return;
        if (Modify(conn.Fd, ConnEventsRead | LibC.EPOLLOUT, KindConn | (uint)conn.Slot)) conn.WantWrite = true;
    }

    private void DisarmWrite(EpollConnection conn)
    {
        if (!conn.WantWrite || conn.Fd < 0) return;
        if (Modify(conn.Fd, ConnEventsRead, KindConn | (uint)conn.Slot)) conn.WantWrite = false;
    }

    // =====================================================================
    // Cross-thread plumbing
    // =====================================================================

    /// <summary>Wake the loop. One 8-byte eventfd write; the counter is drained in <see cref="DrainWake"/>.</summary>
    private void Poke()
    {
        if (!_ready || _wakeFd < 0) return;
        ulong one = 1;
        LibC.write(_wakeFd, &one, 8);
    }

    private void DrainWake()
    {
        ulong sink;
        while (LibC.read(_wakeFd, &sink, 8) == 8) { } // EFD_NONBLOCK: returns EAGAIN when empty
    }

    internal void EnqueueInbound(int fd, object? token) { _incoming.Enqueue((fd, token)); Poke(); }

    internal void SubmitClose(int slot, uint generation) { _closes.Enqueue((slot, generation)); Poke(); }

    internal void SubmitFlush(int slot, uint generation, byte[] data, int length)
    {
        _flush.Enqueue((slot, generation, data, length));
        Poke();
    }

    private void DrainCrossThread()
    {
        while (_newListeners.TryDequeue(out var l)) StartListen(l.Fd, l.Token);
        while (_connects.TryDequeue(out var c)) StartConnect(c.Fd, c.Endpoint, c.Token);
        while (_incoming.TryDequeue(out var a)) AdoptAccepted(a.Fd, a.Token);
        // f.Data is rented (see OutboundConnection.Flush) and owned by this loop now: return it however
        // PumpFlush exits, including the drop paths where the slot was re-tenanted.
        while (_flush.TryDequeue(out var f))
        {
            try { PumpFlush(f.Slot, f.Generation, f.Data, f.Len); }
            finally { ArrayPool<byte>.Shared.Return(f.Data); }
        }
        while (_closes.TryDequeue(out var x)) RequestClose(x.Slot, x.Generation);
    }

    // =====================================================================
    // Slot table
    // =====================================================================

    private EpollConnection? InitClient(int fd, object? userToken, SocketSet.SocketFlags flags)
    {
        int idx = _slots.Claim();
        if (idx < 0) return null;
        var conn = _conns[idx];
        conn.UserToken = userToken;
        conn.Flags = flags;
        conn.Opened = false;
        conn.Closing = false;
        conn.Connecting = false;
        conn.WantWrite = false;
        conn.RecvBuf = -1;
        conn.SendOffset = 0;
        conn.Pending?.Clear();
        conn.Tls = null;
        conn.IsClient = false;
        // Bump the generation before publishing Fd: any out-of-band Close/flush captured against the
        // previous tenant now mismatches and is dropped rather than misapplied.
        Volatile.Write(ref conn.Generation, conn.Generation + 1);
        Volatile.Write(ref conn.Fd, fd); // publish live last (foreign readers gate on Fd >= 0)
        return conn;
    }

    /// <summary>Roll back a claim whose setup failed, before anything was registered with epoll.</summary>
    private void FreeSlot(EpollConnection conn)
    {
        Volatile.Write(ref conn.Fd, -1);
        _slots.Free(conn.Slot);
        ReleaseReservation();
    }

    private void RequestClose(int slot, uint generation)
    {
        var conn = _conns[slot];
        if (conn.Generation != generation) return; // stale: the slot was re-tenanted
        CloseClient(slot);
    }

    /// <summary>
    /// Tear a connection down. Unlike the completion backends there are no in-flight kernel operations to
    /// drain: epoll never owns a buffer, so once the fd is removed from the interest list and closed, no
    /// further event for it can arrive and the slot is immediately safe to recycle.
    /// </summary>
    private void CloseClient(int slot)
    {
        var conn = _conns[slot];
        int fd = conn.Fd;
        if (fd < 0 || conn.Closing) return;
        conn.Closing = true;

        if (conn.Opened)
        {
            conn.Opened = false;
            try { Parent.DispatchClosed(conn); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        // EPOLL_CTL_DEL before close: closing an fd removes it implicitly, but only if no dup survives.
        // Being explicit costs one syscall and removes the whole class of doubt.
        LibC.epoll_ctl(_epfd, LibC.EPOLL_CTL_DEL, fd, null);

        if (Parent.Options.ResetOnClose)
        {
            var lg = new LibC.Linger { l_onoff = 1, l_linger = 0 }; // RST, no FIN, no TIME_WAIT here
            LibC.setsockopt(fd, LibC.SOL_SOCKET, LibC.SO_LINGER, &lg, (uint)sizeof(LibC.Linger));
        }
        LibC.close(fd);

        if (conn.RecvBuf >= 0) { _recvBuffer.Release(conn.RecvBuf); conn.RecvBuf = -1; }
        if (conn.Pending is { } pending)
            while (pending.Count > 0) ArrayPool<byte>.Shared.Return(pending.Dequeue().Array!);
        conn.SendOffset = 0;
        conn.WantWrite = false;
        if (conn.Tls is { } tls) { conn.Tls = null; tls.Dispose(); }
        conn.UserToken = null;
        conn.Flags = 0;
        conn.Closing = false;
        Volatile.Write(ref conn.Fd, -1); // publish free last
        _slots.Free(slot);
        ReleaseReservation();
    }

    // =====================================================================
    // Listen / accept
    // =====================================================================

    public override void Listen(EndPoint endpoint, object? userToken, bool local)
    {
        int fd = IoUringFactory.Bind(endpoint, _listenBacklog);
        LibC.SetNonBlocking(fd);
        _newListeners.Enqueue((fd, userToken));
        Poke();
    }

    public override void ListenHandle(nint handle, object? userToken)
    {
        int fd = (int)handle;
        LibC.SetNonBlocking(fd);
        _newListeners.Enqueue((fd, userToken));
        Poke();
    }

    private void StartListen(int fd, object? token)
    {
        if (!Register(fd, LibC.EPOLLIN, KindListen | (uint)fd))
        {
            System.Diagnostics.Debug.WriteLine($"epoll_ctl(listener) failed: {Marshal.GetLastPInvokeError()}");
            LibC.close(fd);
            return;
        }
        _listeners[fd] = token;
    }

    /// <summary>Drain the accept backlog. Level-triggered would re-notify, but accepting in a burst keeps
    /// the syscall count down under connection storms.</summary>
    private void AcceptBurst(int listenFd)
    {
        if (!_listeners.TryGetValue(listenFd, out object? token)) return;
        while (true)
        {
            int fd = LibC.accept4(listenFd, null, null, LibC.SOCK_NONBLOCK | LibC.SOCK_CLOEXEC);
            if (fd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == LibC.EINTR) continue;
                return; // EAGAIN: backlog drained
            }

            // Capacity-aware placement across shards; drops only if every shard is full.
            var target = (EpollShard?)Parent.TryPlace();
            if (target is null) { LibC.close(fd); continue; }
            if (ReferenceEquals(target, this)) AdoptAccepted(fd, token);
            else target.EnqueueInbound(fd, token);
        }
    }

    private void AdoptAccepted(int fd, object? token)
    {
        var conn = InitClient(fd, token, SocketSet.SocketFlags.None);
        if (conn is null) { LibC.close(fd); ReleaseReservation(); return; }

        if (!_recvBuffer.TryLease(out int ri, out _)) { LibC.close(fd); FreeSlot(conn); return; }
        conn.RecvBuf = ri;

        if (!Register(fd, ConnEventsRead, KindConn | (uint)conn.Slot))
        {
            _recvBuffer.Release(ri);
            conn.RecvBuf = -1;
            LibC.close(fd);
            FreeSlot(conn);
            return;
        }

        FireOpen(conn, isClient: false);
    }

    // =====================================================================
    // Connect
    // =====================================================================

    public override void Connect(EndPoint endpoint, object? userToken)
    {
        // This shard already holds a reservation (TryPlace took it). Create the socket synchronously so
        // its failures stay on the caller's thread, then hand the claim + connect to the loop.
        (int domain, int proto) = endpoint switch
        {
            IPEndPoint => (LibC.AF_INET, LibC.IPPROTO_TCP),
            UnixDomainSocketEndPoint => (LibC.AF_UNIX, 0),
            _ => throw new NotSupportedException($"{nameof(Connect)} on {endpoint.GetType().Name} is not supported."),
        };

        int fd = LibC.socket(domain, LibC.SOCK_STREAM, proto);
        if (fd < 0) { ReleaseReservation(); throw new Win32Exception(Marshal.GetLastPInvokeError(), "socket() failed"); }
        LibC.SetNonBlocking(fd);
        if (domain == LibC.AF_INET)
        {
            int one = 1;
            LibC.setsockopt(fd, LibC.IPPROTO_TCP, LibC.TCP_NODELAY, &one, sizeof(int));
        }

        _connects.Enqueue((fd, endpoint, userToken));
        Poke();
    }

    private void StartConnect(int fd, EndPoint endpoint, object? token)
    {
        var conn = InitClient(fd, token, SocketSet.SocketFlags.None);
        if (conn is null) { LibC.close(fd); ReleaseReservation(); return; }

        if (!_recvBuffer.TryLease(out int ri, out _)) { LibC.close(fd); FreeSlot(conn); return; }
        conn.RecvBuf = ri;
        conn.IsClient = true;

        int rc;
        if (endpoint is IPEndPoint ip)
        {
            var addr = new LibC.SockAddrIn
            {
                sin_family = checked((ushort)ip.AddressFamily),
                sin_port = LibC.Htons(checked((ushort)ip.Port)),
                sin_addr = BitConverter.ToUInt32(ip.Address.GetAddressBytes(), 0),
            };
            rc = LibC.connect(fd, &addr, 16);
        }
        else
        {
            LibC.SockAddrUn addr;
            uint len = LibC.SockAddrUn.Init(&addr, endpoint.ToString()!);
            rc = LibC.connect(fd, &addr, len);
        }

        // A non-blocking connect almost always returns EINPROGRESS; completion arrives as EPOLLOUT, and
        // whether it SUCCEEDED is then read from SO_ERROR (the wake alone does not tell you).
        int err = rc < 0 ? Marshal.GetLastPInvokeError() : 0;
        if (rc < 0 && err != LibC.EINPROGRESS)
        {
            _recvBuffer.Release(ri);
            conn.RecvBuf = -1;
            LibC.close(fd);
            FreeSlot(conn);
            return;
        }

        conn.Connecting = rc < 0;
        uint want = conn.Connecting ? ConnEventsRead | LibC.EPOLLOUT : ConnEventsRead;
        if (!Register(fd, want, KindConn | (uint)conn.Slot))
        {
            _recvBuffer.Release(ri);
            conn.RecvBuf = -1;
            LibC.close(fd);
            FreeSlot(conn);
            return;
        }
        conn.WantWrite = conn.Connecting;

        if (!conn.Connecting) FireOpen(conn, isClient: true); // connected immediately (loopback/UDS)
    }

    private void CompleteConnect(EpollConnection conn)
    {
        int err = 0;
        uint len = sizeof(int);
        if (LibC.getsockopt(conn.Fd, LibC.SOL_SOCKET, LibC.SO_ERROR, &err, &len) < 0 || err != 0)
        {
            CloseClient(conn.Slot);
            return;
        }
        conn.Connecting = false;
        DisarmWrite(conn);
        FireOpen(conn, isClient: true);
    }

    // =====================================================================
    // Open / receive / send
    // =====================================================================

    private void FireOpen(EpollConnection conn, bool isClient)
    {
        conn.IsClient = isClient;

        // TLS: the app must not see this connection until the handshake completes, so the open is
        // deferred to the DriveTlsHandshake -> FireOpen call that follows completion.
        if (Parent.Options.Tls is not null && conn.Tls is null) { BeginTls(conn, isClient); return; }

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        conn.Opened = true;
        int sb;
        if (isClient)
        {
            var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _bufSize : 0);
            Parent.OnConnect(ref ctx);
            sb = ctx.SendBytes;
        }
        else
        {
            var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _bufSize : 0);
            Parent.OnAccept(ref ctx);
            sb = ctx.SendBytes;
        }

        if (leased)
        {
            if (sb > 0 && !conn.Closing && conn.Fd >= 0 && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
            {
                // The greeting is application PLAINTEXT in the write page. On a TLS connection it must be
                // encrypted, not sent raw - otherwise the handshake completes, the very first app write
                // goes out in the clear, and the peer's engine rejects it as a malformed record.
                if (conn.Tls is { } tls) SendEncrypted(conn, tls, wp, sb); else SendBytes(conn, wp, sb);
            }
            _writeBuffer.Release(wi);
        }
    }

    private void HandleConnection(int slot, uint events)
    {
        var conn = _conns[slot];
        if (conn.Fd < 0 || conn.Closing) return;

        if (conn.Connecting)
        {
            if ((events & (LibC.EPOLLOUT | LibC.EPOLLERR | LibC.EPOLLHUP)) != 0) CompleteConnect(conn);
            return;
        }

        // EPOLLERR alone is fatal. EPOLLHUP/EPOLLRDHUP are NOT: there may still be buffered inbound data
        // to read, and discarding it on the hangup event would truncate the last response.
        if ((events & LibC.EPOLLERR) != 0) { CloseClient(slot); return; }

        if ((events & LibC.EPOLLOUT) != 0)
        {
            PumpSend(conn);
            if (conn.Fd < 0 || conn.Closing) return;
        }

        if ((events & (LibC.EPOLLIN | LibC.EPOLLRDHUP | LibC.EPOLLHUP)) != 0) PumpReceive(conn);
    }

    private void PumpReceive(EpollConnection conn)
    {
        byte* buf = _recvBuffer.Address(conn.RecvBuf);
        for (int i = 0; i < ReadBurst; i++)
        {
            nint n = LibC.recv(conn.Fd, buf, (nuint)_recvBufSize, 0);
            if (n > 0)
            {
                if (!Deliver(conn, buf, (int)n)) return;
                if (conn.Fd < 0 || conn.Closing) return;
                if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) != 0) return;
                // A short read means the socket buffer is drained; skip the extra syscall that would
                // only return EAGAIN. Safe under level-triggering: if more arrives we get another wake.
                if (n < _recvBufSize) return;
                continue;
            }
            if (n == 0) { CloseClient(conn.Slot); return; } // orderly EOF from the peer

            int err = Marshal.GetLastPInvokeError();
            if (err == LibC.EINTR) continue;
            if (err == LibC.EAGAIN) return; // spurious/already-drained wake - normal, not an error
            CloseClient(conn.Slot);
            return;
        }
    }

    /// <summary>Hand received bytes to the app and send any inline response. False if the connection died.</summary>
    private bool Deliver(EpollConnection conn, byte* data, int bytes)
    {
        if (conn.Tls is not null) return DeliverTls(conn, data, bytes);

        // Capacity is the RECEIVE buffer's - an in-place response is written back into the buffer the
        // bytes arrived in, so it is bounded by that, not by the send page.
        var ctx = new SocketSet.ReceiveContext(conn, data, _recvBufSize, bytes);
        Parent.DispatchReceive(ref ctx);
        int rb = ctx.ResponseBytes;
        if (rb <= 0 || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0) return true;
        if (conn.Fd < 0 || conn.Closing) return false;
        SendBytes(conn, data, rb);
        return conn.Fd >= 0 && !conn.Closing;
    }

    /// <summary>
    /// Write bytes to the socket, queueing whatever the kernel would not take. Everything outbound goes
    /// through here so ordering is preserved: if anything is already queued, this appends rather than
    /// writing, or a later buffer would overtake an earlier partially-written one.
    /// </summary>
    private void SendBytes(EpollConnection conn, byte* data, int length)
    {
        if (length <= 0) return;

        int off = 0;
        if (conn.Pending is not { Count: > 0 })
        {
            off = TryWrite(conn, data, length);
            if (off < 0) return;             // fatal; connection closed
            if (off == length)
            {
                // Fully accepted by the kernel. On a COMPLETION backend this would raise a send
                // completion, which is what drives OnWrite and keeps a pipelined client moving. epoll
                // raises nothing for a write that never blocked, so the completion has to be synthesised
                // here or the connection wedges after one exchange.
                CompleteSend(conn);
                return;
            }
        }

        Stage(conn, data + off, length - off);
        ArmWrite(conn);
    }

    // Re-entrancy guard: PumpWriteCallback calls SendBytes, which would otherwise call back into it.
    // A single flag suffices - one loop thread, one connection in flight at a time.
    private bool _inWriteCallback;

    private void CompleteSend(EpollConnection conn)
    {
        if (_inWriteCallback) return;
        _inWriteCallback = true;
        try { PumpWriteCallback(conn); }
        finally { _inWriteCallback = false; }
    }

    /// <summary>Raw non-blocking write loop. Returns bytes accepted, or -1 if the connection was torn
    /// down. A short count means EAGAIN - the caller must queue the remainder.</summary>
    private int TryWrite(EpollConnection conn, byte* data, int length)
    {
        int off = 0;
        while (off < length)
        {
            // MSG_NOSIGNAL: a write to a peer that has gone away must return EPIPE, not raise SIGPIPE.
            nint n = LibC.send(conn.Fd, data + off, (nuint)(length - off), LibC.MSG_NOSIGNAL);
            if (n > 0) { off += (int)n; continue; }

            int err = Marshal.GetLastPInvokeError();
            if (err == LibC.EINTR) continue;
            if (err == LibC.EAGAIN) break; // socket buffer full - queue the rest and wait for EPOLLOUT
            CloseClient(conn.Slot);
            return -1;
        }
        return off;
    }

    private void Stage(EpollConnection conn, byte* data, int length)
    {
        var pending = conn.Pending ??= new();
        var buf = ArrayPool<byte>.Shared.Rent(length);
        new ReadOnlySpan<byte>(data, length).CopyTo(buf);
        pending.Enqueue(new ArraySegment<byte>(buf, 0, length));
    }

    /// <summary>EPOLLOUT: resume the queue from the partial-write cursor.</summary>
    private void PumpSend(EpollConnection conn)
    {
        while (conn.Pending is { Count: > 0 } pending)
        {
            var seg = pending.Peek();
            int remaining = seg.Count - conn.SendOffset;
            int wrote;
            fixed (byte* p = seg.Array!) wrote = TryWrite(conn, p + seg.Offset + conn.SendOffset, remaining);
            if (wrote < 0) return; // closed

            conn.SendOffset += wrote;
            if (conn.SendOffset < seg.Count) return; // still blocked; EPOLLOUT stays armed

            pending.Dequeue();
            ArrayPool<byte>.Shared.Return(seg.Array!);
            conn.SendOffset = 0;
        }

        DisarmWrite(conn);
        CompleteSend(conn);
    }

    /// <summary>
    /// The queue drained, so offer the app the chance to pipeline more (the OnWrite contract the other
    /// backends give on send completion). Bounded: on a completion backend each iteration costs a kernel
    /// round-trip, but here a fully-accepted write returns synchronously, so an app that always has more
    /// to say would otherwise spin the loop forever. Hitting the cap re-arms EPOLLOUT, which fires
    /// immediately on a writable socket and resumes on the next pass - progress without monopolising.
    /// </summary>
    private void PumpWriteCallback(EpollConnection conn)
    {
        if (!conn.Opened) return; // pre-handshake/pre-open: the app has never seen this connection
        if (!_writeBuffer.TryLease(out int wi, out byte* wp)) return;
        try
        {
            for (int i = 0; i < WriteBurst; i++)
            {
                if (conn.Fd < 0 || conn.Closing) return;
                if ((conn.Flags & SocketSet.SocketFlags.SendClosed) != 0) return;
                if (conn.Pending is { Count: > 0 }) return; // blocked again; EPOLLOUT will resume us

                var ctx = new SocketSet.WriteContext(conn, wp, _bufSize);
                Parent.OnWrite(ref ctx);
                int n = ctx.SendBytes;
                if (n <= 0) return;
                if (conn.Tls is { } tls) SendEncrypted(conn, tls, wp, n); else SendBytes(conn, wp, n);
            }
            // Still producing after the burst: let epoll pace us instead of looping here.
            ArmWrite(conn);
        }
        finally
        {
            _writeBuffer.Release(wi);
        }
    }

    /// <summary>Out-of-band flushed write (Connection.Flush from any thread), now on the loop.</summary>
    private void PumpFlush(int slot, uint generation, byte[] data, int len)
    {
        var conn = _conns[slot];
        if (conn.Generation != generation || conn.Fd < 0 || conn.Closing
            || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0)
        {
            return; // slot re-tenanted or send half closed - drop rather than misdeliver
        }
        if (conn.Tls is { } tls)
        {
            // Out-of-band writes are application plaintext; encrypt before sending. A flush cannot legally
            // arrive before the handshake completes (the app has no Connection reference until the
            // deferred open), so dropping one is safer than letting plaintext reach the wire.
            if (!tls.HandshakeComplete)
            {
                System.Diagnostics.Debug.WriteLine("TLS flush before handshake completion; dropped.");
                return;
            }
            fixed (byte* p = data) SendEncrypted(conn, tls, p, len);
            return;
        }
        fixed (byte* p = data) SendBytes(conn, p, len);
    }

    // =====================================================================
    // TLS interception (see TlsFilter)
    // -------------------------------------------------------------------------------------
    // Same shape as the IOCP/RIO shards: one loop thread, so no locking and shard-wide scratch, and ALL
    // ciphertext goes out through the ordinary send path so records reach the socket in the order the
    // engine produced them (they are sequence-numbered - order is not cosmetic).
    //
    // It falls out more simply here than on the completion backends. There, ciphertext had to be staged
    // on the Pending queue explicitly to avoid overtaking an in-flight send. SendBytes already imposes
    // that ordering for every caller: it appends to Pending whenever anything is queued ahead of it.
    // =====================================================================

    private void BeginTls(EpollConnection conn, bool isClient)
    {
        var opts = Parent.Options;
        conn.Tls = isClient ? opts.Tls!.CreateClientFilter(opts.TlsClient) : opts.Tls!.CreateServerFilter(opts.TlsServer);
        // A client speaks first (ClientHello); a server emits nothing until it has seen one. The receive
        // is already armed by the caller, so the handshake advances as bytes arrive.
        DriveTlsHandshake(conn, default);
    }

    /// <summary>Feed one chunk to the handshake and send whatever it emits (already TLS records, so sent
    /// raw rather than re-encrypted). False if the connection was torn down.</summary>
    private bool DriveTlsHandshake(EpollConnection conn, ReadOnlySpan<byte> input)
    {
        _tlsCtrl!.Reset();
        var status = conn.Tls!.DriveHandshake(input, conn.Fd, _tlsCtrl);
        QueueCipher(conn, _tlsCtrl.WrittenSpan); // may carry a fatal alert on failure - send it first

        if (status == TlsHandshakeStatus.Faulted) { CloseClient(conn.Slot); return false; }
        if (conn.Fd < 0 || conn.Closing) return false;
        if (status == TlsHandshakeStatus.Completed) FireOpen(conn, conn.IsClient);
        return conn.Fd >= 0 && !conn.Closing;
    }

    /// <summary>Data phase inbound: decrypt, hand the plaintext to OnReceive, encrypt any response.</summary>
    private bool DeliverTls(EpollConnection conn, byte* data, int bytes)
    {
        var tls = conn.Tls!;
        var cipherIn = new ReadOnlySpan<byte>(data, bytes);

        if (!tls.HandshakeComplete)
        {
            if (!DriveTlsHandshake(conn, cipherIn)) return false;
            if (!tls.HandshakeComplete) return true; // still handshaking; keep receiving

            // Just completed. Application data coalesced into the same segment as the peer's final
            // handshake flight is already buffered INSIDE the engine - surface it now with an empty
            // input, or it strands until a next read that may never come.
            cipherIn = default;
        }

        _tlsPlain!.Reset();
        _tlsCtrl!.Reset();
        var status = tls.ProcessInbound(cipherIn, TlsContentType.Ciphertext, _tlsPlain, _tlsCtrl);
        QueueCipher(conn, _tlsCtrl.WrittenSpan); // protocol replies (e.g. a TLS 1.3 KeyUpdate ack)

        if (status == TlsInboundStatus.Faulted) { CloseClient(conn.Slot); return false; }
        if (conn.Fd < 0 || conn.Closing) return false;

        int plainLen = _tlsPlain.WrittenCount;
        if (plainLen > 0)
        {
            byte[] plain = _tlsPlain.Array;
            fixed (byte* pp = plain)
            {
                var ctx = new SocketSet.ReceiveContext(conn, pp, plain.Length, plainLen);
                Parent.DispatchReceive(ref ctx);
                int rb = ctx.ResponseBytes;
                if (rb > 0 && conn.Fd >= 0 && !conn.Closing
                    && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
                {
                    SendEncrypted(conn, tls, pp, rb);
                }
            }
        }

        if (status == TlsInboundStatus.PeerClosed) { CloseClient(conn.Slot); return false; }
        return conn.Fd >= 0 && !conn.Closing;
    }

    /// <summary>Encrypt application plaintext and send it.</summary>
    private void SendEncrypted(EpollConnection conn, TlsFilter tls, byte* plaintext, int len)
    {
        _tlsCipher!.Reset();
        tls.ProcessOutbound(new ReadOnlySpan<byte>(plaintext, len), _tlsCipher);
        QueueCipher(conn, _tlsCipher.WrittenSpan);
    }

    private void QueueCipher(EpollConnection conn, ReadOnlySpan<byte> cipher)
    {
        if (cipher.IsEmpty || conn.Fd < 0 || conn.Closing) return;
        fixed (byte* p = cipher) SendBytes(conn, p, cipher.Length);
    }
}
#endif
