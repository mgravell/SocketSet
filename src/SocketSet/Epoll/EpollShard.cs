#if NET // Linux epoll backend; compiled out of the netfx fallback build.
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SocketSets.IoUring;
using SocketSets.Native;
using SocketSets.Tls;
using SocketSets.Tls.OpenSsl;
using static SocketSets.Tls.OpenSsl.NativeOpenSsl;

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
    // RSS table in RESULTS.md showed epoll "flat" across page sizes is that the slab is
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
    private readonly Dictionary<int, (object? Token, SocketSets.Tls.TlsProvider? Tls)> _listeners = [];

    // --- cross-thread queues, drained on the loop thread ---
    private readonly ConcurrentQueue<(int Fd, object? Token, SocketSets.Tls.TlsProvider? Tls)> _incoming = [];
    private readonly ConcurrentQueue<(int Slot, uint Generation)> _closes = [];
    // Parked receives to re-arm (Connection.ResumeReceive, from the consumer's flush continuation).
    private readonly ConcurrentQueue<(int Slot, uint Generation)> _resumes = [];
    private readonly ConcurrentQueue<(int Slot, uint Generation, byte[] Data, int Len)> _flush = [];
    private readonly ConcurrentQueue<(int Fd, EndPoint Endpoint, object? Token, SocketSets.Tls.TlsProvider? Tls)> _connects = [];
    private readonly ConcurrentQueue<(int Fd, object? Token, SocketSets.Tls.TlsProvider? Tls)> _newListeners = [];
    private readonly ConcurrentQueue<(int Slot, uint Generation, EpollZcSend Zc)> _zeroCopy = [];

    private const int IovMax = 1024; // UIO_MAXIOV: writev rejects iovcnt above this with -EINVAL

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
            if (n > 0) Parent.OnLoopDrain();
            MaybeSweep();
        }
    }

    protected override void OnStop() => Poke();

    protected override void Wake() => Poke(); // the sweep timer's doorbell (see SocketSetShard)

    /// <summary>Drop connections past their deadline. Loop thread; see the io_uring twin for the cost.</summary>
    protected override void SweepTimeouts(long nowTicks)
    {
        for (int i = 0; i < _conns.Length; i++)
        {
            var conn = _conns[i];
            if (conn.Fd < 0 || conn.Closing) continue;
            if (Parent.ExpiryReason(conn, nowTicks) is not { } reason) continue;
            Parent.DispatchTimeout(conn, reason);
            CloseClient(conn.Slot);
        }
    }

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
            if (_conns[i].KtlsSsl != 0) { SSL_free(_conns[i].KtlsSsl); _conns[i].KtlsSsl = 0; }
            if (_conns[i].Zc is { } zc) { _conns[i].Zc = null; zc.Finish(false); }
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

    /// <summary>The interest mask this connection should currently carry: read unless the receive is
    /// parked, plus write when something is blocked. One place, because there are now two independent
    /// reasons to rewrite the mask and computing it at each call site is how they would drift apart.</summary>
    private static uint InterestFor(EpollConnection conn)
        => (conn.RecvParked ? 0u : ConnEventsRead) | (conn.WantWrite ? LibC.EPOLLOUT : 0u);

    private void ArmWrite(EpollConnection conn)
    {
        if (conn.WantWrite || conn.Fd < 0) return;
        if (Modify(conn.Fd, InterestFor(conn) | LibC.EPOLLOUT, KindConn | (uint)conn.Slot)) conn.WantWrite = true;
    }

    private void DisarmWrite(EpollConnection conn)
    {
        if (!conn.WantWrite || conn.Fd < 0) return;
        if (Modify(conn.Fd, InterestFor(conn) & ~LibC.EPOLLOUT, KindConn | (uint)conn.Slot)) conn.WantWrite = false;
    }

    /// <summary>
    /// PARKING (REVIEW.md D3), the readiness-model version: take read interest OFF the fd. The consumer is
    /// behind, so we stop reading, the socket's receive queue fills, the advertised window closes, and the
    /// PEER slows down instead of being dropped at a buffering cap.
    ///
    /// Dropping EPOLLRDHUP with EPOLLIN is not incidental. Both are level-triggered, so either one left
    /// registered over an unread condition returns from every epoll_wait immediately and spins the loop.
    /// EPOLLERR/EPOLLHUP cannot be masked off at all, which is why <see cref="HandleConnection"/> closes a
    /// PARKED connection on HUP rather than trying to ignore it.
    /// </summary>
    private void ParkReceive(EpollConnection conn)
    {
        if (conn.RecvParked || conn.Fd < 0) return;
        conn.RecvParked = true;
        // A failed epoll_ctl on a live fd leaves read interest ON while the loop believes it is off, which
        // is a level-triggered spin at 100% of a core. Drop the connection instead: it is the only outcome
        // here that is neither a hang nor a burnt core, and it should not be reachable.
        if (!Modify(conn.Fd, InterestFor(conn), KindConn | (uint)conn.Slot)) CloseClient(conn.Slot);
    }

    /// <summary>A parked receive was resumed from off-loop: put read interest back, then drain
    /// immediately rather than waiting for the next epoll_wait to re-report the level. (Level-triggering
    /// WOULD re-report it, so the pump is a latency saving rather than a correctness one — but it also
    /// means the resume path is exercised end-to-end even on an idle loop.)</summary>
    private void ResumeReceive(int slot, uint generation)
    {
        var conn = _conns[slot];
        if (conn.Generation != generation || conn.Fd < 0 || conn.Closing || !conn.RecvParked) return;
        conn.RecvParked = false;
        if (!Modify(conn.Fd, InterestFor(conn), KindConn | (uint)conn.Slot)) { CloseClient(slot); return; }
        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) PumpReceive(conn);
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

    internal void EnqueueInbound(int fd, object? token, SocketSets.Tls.TlsProvider? tls = null) { _incoming.Enqueue((fd, token, tls)); Poke(); }

    internal void SubmitClose(int slot, uint generation) { _closes.Enqueue((slot, generation)); Poke(); }

    /// <summary>Marshal a parked-receive resume onto the loop thread (from
    /// <see cref="Connection.ResumeReceive"/>, which runs on the consumer's flush continuation).</summary>
    internal void SubmitResumeReceive(int slot, uint generation) { _resumes.Enqueue((slot, generation)); Poke(); }

    internal void SubmitFlush(int slot, uint generation, byte[] data, int length)
    {
        _flush.Enqueue((slot, generation, data, length));
        Poke();
    }

    private void DrainCrossThread()
    {
        while (_newListeners.TryDequeue(out var l)) StartListen(l.Fd, l.Token, l.Tls);
        while (_connects.TryDequeue(out var c)) StartConnect(c.Fd, c.Endpoint, c.Token, c.Tls);
        while (_incoming.TryDequeue(out var a)) AdoptAccepted(a.Fd, a.Token, a.Tls);
        // f.Data is rented (see OutboundConnection.Flush) and owned by this loop now: return it however
        // PumpFlush exits, including the drop paths where the slot was re-tenanted.
        while (_flush.TryDequeue(out var f))
        {
            try { PumpFlush(f.Slot, f.Generation, f.Data, f.Len); }
            finally { ArrayPool<byte>.Shared.Return(f.Data); }
        }
        while (_zeroCopy.TryDequeue(out var z)) StartZc(z.Slot, z.Generation, z.Zc);
        while (_closes.TryDequeue(out var x)) RequestClose(x.Slot, x.Generation);
        // After the closes, so a close and a resume landing in the same pass resolve as "closed".
        while (_resumes.TryDequeue(out var r)) ResumeReceive(r.Slot, r.Generation);
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
        conn.TlsOverride = null; // recycled slot must not inherit the last tenant's provider
        conn.Flags = flags;
        conn.Opened = false;
        conn.Closing = false;
        conn.Connecting = false;
        conn.WantWrite = false;
        conn.RecvParked = false; // a recycled slot must not inherit the last tenant's parked interest mask
        conn.RecvBuf = -1;
        conn.SendOffset = 0;
        conn.Pending?.Clear();
        conn.StartedTicks = conn.LastActivityTicks = Clock.Millis;
        conn.MaxInboundBufferBytes = Parent.Options.MaxInboundBufferBytes; // deadline clock starts here
        conn.SkipBufferWipe = Parent.Options.DangerousDisableBufferWipe;
        conn.ResetReceiveParking();
        conn.Tls = null;
        conn.KtlsReady = false; // KtlsSsl is 0 by the time a slot is freed (CloseClient frees it); belt-and-braces
        conn.Zc = null;         // ditto — any in-flight zero-copy send was finished in CloseClient
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

        // Ungated: a failed handshake never set Opened, so this must not hang off DispatchClosed.

        Parent.DispatchTlsFault(conn);


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
        // Fail any in-flight zero-copy send: release its pins and unblock the pump (which then stops).
        if (conn.Zc is { } zc) { conn.Zc = null; zc.Finish(false); }
        if (conn.Tls is { } tls) { conn.Tls = null; tls.Dispose(); }
        // kTLS: free the SSL (the fd it was bound to is already closed above; BIO_NOCLOSE, so SSL_free does
        // not touch it). The reusable KtlsRecv buffer is kept for the next tenant. No SSL_shutdown: the fd
        // is gone, so a close_notify could not be written anyway, and the peer sees a TCP FIN.
        if (conn.KtlsSsl != 0) { SSL_free(conn.KtlsSsl); conn.KtlsSsl = 0; conn.KtlsReady = false; }
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

    public override void Listen(EndPoint endpoint, object? userToken, bool local, SocketSets.Tls.TlsProvider? tls = null)
    {
        int fd = IoUringFactory.Bind(endpoint, _listenBacklog, Parent.Options);
        LibC.SetNonBlocking(fd);
        _newListeners.Enqueue((fd, userToken, tls));
        Poke();
    }

    public override void ListenHandle(nint handle, object? userToken, SocketSets.Tls.TlsProvider? tls = null)
    {
        int fd = (int)handle;
        LibC.SetNonBlocking(fd);
        _newListeners.Enqueue((fd, userToken, tls));
        Poke();
    }

    private void StartListen(int fd, object? token, SocketSets.Tls.TlsProvider? tls = null)
    {
        if (!Register(fd, LibC.EPOLLIN, KindListen | (uint)fd))
        {
            System.Diagnostics.Debug.WriteLine($"epoll_ctl(listener) failed: {Marshal.GetLastPInvokeError()}");
            LibC.close(fd);
            return;
        }
        _listeners[fd] = (token, tls);
    }

    /// <summary>Drain the accept backlog. Level-triggered would re-notify, but accepting in a burst keeps
    /// the syscall count down under connection storms.</summary>
    private void AcceptBurst(int listenFd)
    {
        if (!_listeners.TryGetValue(listenFd, out var listener)) return;
        var (token, listenTls) = listener;
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
            if (ReferenceEquals(target, this)) AdoptAccepted(fd, token, listenTls);
            else target.EnqueueInbound(fd, token, listenTls);
        }
    }

    private void AdoptAccepted(int fd, object? token, SocketSets.Tls.TlsProvider? listenTls = null)
    {
        var conn = InitClient(fd, token, SocketSet.SocketFlags.None);
        if (conn is null) { LibC.close(fd); ReleaseReservation(); return; }
        conn.TlsOverride = listenTls; // InitClient nulled it; the LISTENER's provider re-seeds it

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

    public override void Connect(EndPoint endpoint, object? userToken, SocketSets.Tls.TlsProvider? tls = null)
    {
        // This shard already holds a reservation (TryPlace took it). Create the socket synchronously so
        // its failures stay on the caller's thread, then hand the claim + connect to the loop. Every
        // rejection below must release the reservation first or a refused dial leaks capacity.
        int domain, proto;
        try
        {
            switch (endpoint)
            {
                case IPEndPoint ip:
                    LibC.RequireIPv4(ip, nameof(Connect)); // IPv4-only sockaddr; never truncate an IPv6 address
                    (domain, proto) = (LibC.AF_INET, LibC.IPPROTO_TCP);
                    break;
                case UnixDomainSocketEndPoint:
                    (domain, proto) = (LibC.AF_UNIX, 0);
                    break;
                default:
                    throw new NotSupportedException($"{nameof(Connect)} on {endpoint.GetType().Name} is not supported.");
            }
        }
        catch { ReleaseReservation(); throw; }

        int fd = LibC.socket(domain, LibC.SOCK_STREAM, proto);
        if (fd < 0) { ReleaseReservation(); throw new Win32Exception(Marshal.GetLastPInvokeError(), "socket() failed"); }
        LibC.SetNonBlocking(fd);
        if (domain == LibC.AF_INET)
        {
            int one = 1;
            LibC.setsockopt(fd, LibC.IPPROTO_TCP, LibC.TCP_NODELAY, &one, sizeof(int));
        }

        _connects.Enqueue((fd, endpoint, userToken, tls));
        Poke();
    }

    private void StartConnect(int fd, EndPoint endpoint, object? token, SocketSets.Tls.TlsProvider? tls = null)
    {
        var conn = InitClient(fd, token, SocketSet.SocketFlags.None);
        if (conn is null) { LibC.close(fd); ReleaseReservation(); return; }
        conn.TlsOverride = tls; // InitClient nulled it (slot recycle); an explicit provider re-seeds it

        if (!_recvBuffer.TryLease(out int ri, out _)) { LibC.close(fd); FreeSlot(conn); return; }
        conn.RecvBuf = ri;
        conn.IsClient = true;

        int rc;
        if (endpoint is IPEndPoint ip)
        {
            var addr = new LibC.SockAddrIn
            {
                sin_family = LibC.AF_INET,
                sin_port = LibC.Htons(checked((ushort)ip.Port)),
                // ToSinAddr asserts four bytes; the old ToUInt32(GetAddressBytes(), 0) took the first four
                // of however many there were, which for IPv6 is a different host entirely (see RequireIPv4,
                // called on the caller's thread in Connect so the throw is synchronous).
                sin_addr = LibC.ToSinAddr(ip.Address),
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

        // TLS: the app must not see this connection until the handshake completes, so the open is deferred.
        // kTLS (kernel offload) and userspace TLS are the two shapes; pick per provider capability + option.
        // A kTLS connection's open is deferred to KtlsComplete, a userspace one to DriveTlsHandshake's
        // FireOpen re-entry — so both branches return here without firing OnAccept/OnConnect.
        var engagedTls = isClient ? Parent.ResolveClientTls(conn) : Parent.ResolveServerTls(conn);
        // Refused (callback threw, or asked for TLS it could not describe) drops the connection rather
        // than falling back to plaintext: a silent downgrade is the failure this whole path removes.
        if (engagedTls.Refused) { CloseClient(conn.Slot); return; }
        if (engagedTls.Enabled && conn.Tls is null && conn.KtlsSsl == 0)
        {
            if (engagedTls.AllowKernelOffload
                && engagedTls.Provider is OpenSslTlsProvider { SupportsKernelOffload: true } kop)
                StartKtls(conn, isClient, kop, engagedTls); // OpenSSL owns the fd; kernel does the crypto
            else
                BeginTls(conn, isClient, engagedTls);       // userspace TLS via memory BIOs
            return;
        }

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        conn.Opened = true;
        int sb;
        if (isClient)
        {
            var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _bufSize : 0);
            Parent.DispatchConnect(ref ctx);
            sb = ctx.SendBytes;
        }
        else
        {
            var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _bufSize : 0);
            Parent.DispatchAccept(ref ctx);
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

        // PARKED: read interest is off the fd, but EPOLLERR/EPOLLHUP are reported whether requested or
        // not — and level-triggered, so an ignored HUP would return from every epoll_wait and spin a core.
        // A HUP is a fully-dead socket (both directions down), so there is no buffered data worth waiting
        // to deliver and closing is both correct and the only non-spinning answer. Anything else while
        // parked is a write event, which is handled below as usual.
        if (conn.RecvParked && (events & LibC.EPOLLHUP) != 0) { CloseClient(slot); return; }

        // kTLS: a distinct data path (SSL_read for RX / plaintext write for TX, kernel does the crypto), so
        // route it before the userspace pumps. While handshaking, either readiness event just steps the
        // handshake; once ready, EPOLLOUT drains blocked plaintext sends and EPOLLIN drives SSL_read.
        if (conn.KtlsSsl != 0)
        {
            if (!conn.KtlsReady) { KtlsPump(conn); return; }
            if ((events & LibC.EPOLLOUT) != 0) { PumpSend(conn); if (conn.Fd < 0 || conn.Closing) return; }
            if (!conn.RecvParked && (events & (LibC.EPOLLIN | LibC.EPOLLRDHUP | LibC.EPOLLHUP)) != 0)
            {
                KtlsRead(conn);
                // Same park point as the userspace path: honour a request raised inside the callback once
                // the read side is quiet. Guarded on liveness because KtlsRead can tear the slot down.
                if (conn.Fd >= 0 && !conn.Closing && conn.TryParkReceive()) ParkReceive(conn);
            }
            return;
        }

        if ((events & LibC.EPOLLOUT) != 0)
        {
            // A zero-copy send drains via writev from its own pinned iovecs, not the pooled Pending queue.
            if (conn.Zc is not null) PumpZc(conn); else PumpSend(conn);
            if (conn.Fd < 0 || conn.Closing) return;
        }

        if (conn.RecvParked) return; // read interest is off; only the write half above still applies
        if ((events & (LibC.EPOLLIN | LibC.EPOLLRDHUP | LibC.EPOLLHUP)) != 0) PumpReceive(conn);
    }

    private void PumpReceive(EpollConnection conn)
    {
        conn.LastActivityTicks = Clock.Millis;
        // Zero-copy receive is available only for a NON-TLS pipe-mode connection: TLS inbound is ciphertext
        // that must land in the recv slab to be decrypted before any plaintext reaches the pipe, and the
        // callback path has no pipe to read into. (kTLS never reaches here — it routes to KtlsRead.)
        var bridge = conn.Tls is null ? conn.PipeIo : null;
        byte* buf = _recvBuffer.Address(conn.RecvBuf);
        for (int i = 0; i < ReadBurst; i++)
        {
            nint n;
            // Read straight into the pipe's own memory when the writer is free (no flush pending). GetMemory
            // hands out at least _recvBufSize; recv fills up to that. The pin is held only across the
            // syscall — pooled memory could otherwise move under GC.
            if (bridge is not null && bridge.TryBeginReceive(_recvBufSize, out var mem))
            {
                using (var h = mem.Pin()) n = LibC.recv(conn.Fd, (byte*)h.Pointer, (nuint)mem.Length, 0);
                if (n > 0)
                {
                    bridge.CommitReceive((int)n);           // advance + flush; no transport-side copy
                    if (conn.Fd < 0 || conn.Closing) return;
                    if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) != 0) return;
                    if (conn.ReceiveParkPending) break;      // consumer fell behind mid-burst
                    if (n < mem.Length) break;              // socket drained (short read)
                    continue;
                }
                // n <= 0: fall through to the shared error/EOF handling below. GetMemory without Advance is
                // harmless — the next receive reuses the same pipe memory.
            }
            else
            {
                n = LibC.recv(conn.Fd, buf, (nuint)_recvBufSize, 0);
                if (n > 0)
                {
                    if (!Deliver(conn, buf, (int)n)) return;
                    if (conn.Fd < 0 || conn.Closing) return;
                    if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) != 0) return;
                    if (conn.ReceiveParkPending) break;      // consumer fell behind mid-burst
                    // A short read means the socket buffer is drained; skip the extra syscall that would
                    // only return EAGAIN. Safe under level-triggering: if more arrives we get another wake.
                    if (n < _recvBufSize) break;
                    continue;
                }
            }
            if (n == 0) { CloseClient(conn.Slot); return; } // orderly EOF from the peer

            int err = Marshal.GetLastPInvokeError();
            if (err == LibC.EINTR) continue;
            if (err == LibC.EAGAIN) break; // spurious/already-drained wake - normal, not an error
            CloseClient(conn.Slot);
            return;
        }

        // Every exit that leaves the connection LIVE lands here (the burst ran out, the socket drained, or
        // the consumer asked us to stop mid-burst). A park request is honoured at exactly this point: the
        // socket is quiet right now, so taking read interest off it costs one epoll_ctl and nothing else.
        // Draining first and parking afterwards is deliberate — parking with data already sitting in the
        // socket buffer would be correct but pointlessly delays bytes the consumer could have had.
        if (conn.TryParkReceive()) ParkReceive(conn);
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
                Parent.DispatchWrite(ref ctx);
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

    private void BeginTls(EpollConnection conn, bool isClient, in SocketSets.Tls.TlsResolution tls)
    {
        conn.Tls = isClient ? tls.Provider!.CreateClientFilter(tls.Client!) : tls.Provider!.CreateServerFilter(tls.Server!);
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

    // =====================================================================
    // Zero-copy send (BYO pipe path). writev straight out of the caller's pinned pipe memory — no copy and
    // no buffer registration (unlike RIO, which is why RIO can't do this and epoll can). Mirrors io_uring's
    // TrySendZeroCopy, but the send is a synchronous writev with an EPOLLOUT drain, not a ring completion.
    // Measured 2026-07-31: without this, epoll's bridged 256KB goodput was ~41% below its bare transport,
    // because the pipe path fell back to Connection.Send and copied the whole response.
    // =====================================================================

    // Pump thread: pin the (prefix of the) sequence, build the iovec array, marshal the send to the loop.
    internal long TrySendZeroCopy(EpollConnection conn, in ReadOnlySequence<byte> data, bool pinned,
                                  out ValueTask<bool> completion)
    {
        completion = default;
        // TLS/kTLS: the wire bytes are not the caller's bytes, so decline and let the bridge copy via Send().
        if (conn.Tls is not null || conn.KtlsSsl != 0) return 0;
        if (Volatile.Read(ref conn.Fd) < 0) return 0;
        if (data.IsEmpty) return 0;

        // Count non-empty segments, capped at IovMax — beyond that we send a prefix and the caller
        // re-presents the remainder (PipeIoBridge handles a partial accept).
        int n = 0;
        foreach (var seg in data) { if (seg.IsEmpty) continue; if (++n == IovMax) break; }
        if (n == 0) return 0;

        var zc = new EpollZcSend { Handles = pinned ? null : new MemoryHandle[n] };
        var iov = (LibC.iovec*)NativeMemory.Alloc((nuint)n * (nuint)sizeof(LibC.iovec));
        zc.Iov = iov;
        int i = 0;
        long total = 0;
        foreach (var seg in data)
        {
            if (seg.IsEmpty) continue;
            if (i == n) break; // filled the prefix; the rest is the caller's to re-present
            byte* p;
            if (zc.Handles is null)
                p = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(seg.Span)); // caller asserts pinned
            else { var h = seg.Pin(); zc.Handles[i] = h; p = (byte*)h.Pointer; }
            iov[i].iov_base = p;
            iov[i].iov_len = (nuint)seg.Length;
            total += seg.Length;
            i++;
        }
        if (i == 0) { zc.Count = 0; zc.Finish(false); return 0; }

        zc.Count = i;
        zc.Total = total;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        zc.Completion = tcs;
        completion = new ValueTask<bool>(tcs.Task);
        _zeroCopy.Enqueue((conn.Slot, Volatile.Read(ref conn.Generation), zc));
        Poke();
        return total; // bytes accepted — the whole sequence, or an IovMax-segment prefix of it
    }

    // Loop thread: adopt a marshaled zero-copy send onto its connection, or fail it if the slot recycled.
    private void StartZc(int slot, uint generation, EpollZcSend zc)
    {
        var conn = _conns[slot];
        if (conn.Generation != generation || conn.Fd < 0 || conn.Closing
            || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0)
        {
            zc.Finish(false);
            return;
        }
        // In pipe mode the pump awaits each send before issuing the next, so there is never a second
        // zero-copy send in flight; fail a lingering one rather than leak its pins.
        if (conn.Zc is { } old) old.Finish(false);
        conn.Zc = zc;
        PumpZc(conn);
    }

    // Loop thread: writev from the cursor. Fully sent -> complete + release pins; EAGAIN/partial -> arm
    // EPOLLOUT and resume on the next writable wake; error -> close.
    private void PumpZc(EpollConnection conn)
    {
        var zc = conn.Zc!;
        while (zc.Sent < zc.Total)
        {
            nint w = LibC.writev(conn.Fd, zc.Iov + zc.Cursor, zc.Count - zc.Cursor);
            if (w > 0)
            {
                zc.Sent += w;
                // Skip iovecs this write fully drained and trim the one it stopped inside.
                long consume = w;
                while (consume > 0 && zc.Cursor < zc.Count)
                {
                    long il = (long)zc.Iov[zc.Cursor].iov_len;
                    if (il <= consume) { consume -= il; zc.Cursor++; }
                    else
                    {
                        zc.Iov[zc.Cursor].iov_base = (byte*)zc.Iov[zc.Cursor].iov_base + consume;
                        zc.Iov[zc.Cursor].iov_len = (nuint)(il - consume);
                        consume = 0;
                    }
                }
                continue;
            }
            int err = Marshal.GetLastPInvokeError();
            if (err == LibC.EINTR) continue;
            if (err == LibC.EAGAIN) { ArmWrite(conn); return; } // socket buffer full — resume on EPOLLOUT
            conn.Zc = null; zc.Finish(false); CloseClient(conn.Slot); return; // fatal
        }
        // Everything has gone out.
        if (conn.WantWrite) DisarmWrite(conn);
        conn.Zc = null;
        zc.Finish(true); // release the pins and unblock the pump to advance the pipe reader
    }

    // =====================================================================
    // kTLS (kernel TLS offload). OpenSSL is bound to the fd (socket BIO, SSL_OP_ENABLE_KTLS); once the
    // handshake completes the DATA path is plaintext: TX writes plaintext and the kernel encrypts (so the
    // normal SendBytes machinery is reused unchanged), RX is SSL_read (kernel decrypts when RX offload is
    // available, else OpenSSL decrypts in userspace — capability is discovered, never assumed). This is the
    // readiness-native analogue of io_uring's POLL-driven kTLS: epoll's EPOLLIN/EPOLLOUT ARE the readiness
    // io_uring has to synthesise, so there is no multishot receive to forfeit — the backend kTLS should
    // suit best (TODO item 3c).
    // =====================================================================

    private static int s_ktlsReported; // TX/RX offload state reported once per process (see io_uring's twin)

    // Stand up the kTLS engine at open. The fd is already non-blocking and already registered for EPOLLIN
    // (ConnEventsRead), so the handshake is driven purely by readiness — no extra arming for WANT_READ.
    private void StartKtls(EpollConnection conn, bool client, OpenSslTlsProvider prov,
                           in SocketSets.Tls.TlsResolution tls)
    {
        conn.IsClient = client;
        conn.KtlsRecv ??= new byte[_recvBufSize];
        // Client supplies TargetHost (SNI/verify); ALPN comes from whichever side's options apply.
        conn.KtlsSsl = prov.CreateKernelSsl(conn.Fd, client,
            client ? tls.Client!.TargetHost : null,
            client ? tls.Client!.AlpnProtocols : tls.Server!.AlpnProtocols);
        KtlsPump(conn);
    }

    // One handshake step. WANT_WRITE arms EPOLLOUT; WANT_READ needs nothing (EPOLLIN is always armed) beyond
    // dropping any stale write-interest. Runs on the loop thread against a non-blocking fd, so never blocks.
    private void KtlsPump(EpollConnection conn)
    {
        int ret = SSL_do_handshake(conn.KtlsSsl);
        if (ret == 1) { KtlsComplete(conn); return; }
        int err = SSL_get_error(conn.KtlsSsl, ret);
        if (err == SSL_ERROR_WANT_WRITE) ArmWrite(conn);
        else if (err == SSL_ERROR_WANT_READ) { if (conn.WantWrite) DisarmWrite(conn); }
        else CloseClient(conn.Slot); // bad cert, verify failure, protocol error, …
    }

    // Handshake done + keys in the kernel: fire the deferred open (greeting rides the NORMAL plaintext send
    // path — the kernel encrypts it), then let EPOLLIN drive reads. POLLIN is already armed, so app data
    // coalesced after the final handshake flight surfaces on the next wake.
    private void KtlsComplete(EpollConnection conn)
    {
        if (conn.WantWrite) DisarmWrite(conn); // no write-interest until an actual send blocks
        ReportKtlsOnce(conn.KtlsSsl);
        conn.KtlsReady = true;
        // No TlsFilter here, so publish the ALPN result straight off the SSL — NegotiatedProtocol reads it.
        conn.KernelAlpn = OpenSslTlsFilter.GetAlpnSelected(conn.KtlsSsl);
        if (!conn.IsClient) conn.KernelServerName = OpenSslTlsFilter.GetRequestedServerName(conn.KtlsSsl);

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        conn.Opened = true; // app now sees it open → pairs with OnClosed
        int sb;
        if (conn.IsClient)
        {
            var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _bufSize : 0);
            Parent.DispatchConnect(ref ctx);
            sb = ctx.SendBytes;
        }
        else
        {
            var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _bufSize : 0);
            Parent.DispatchAccept(ref ctx);
            sb = ctx.SendBytes;
        }
        if (leased)
        {
            // Greeting is plaintext in the write page; kTLS TX encrypts it in the kernel, so it goes out the
            // normal path (NOT SendEncrypted, which is the userspace-filter path).
            if (sb > 0 && !conn.Closing && conn.Fd >= 0 && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
                SendBytes(conn, wp, sb);
            _writeBuffer.Release(wi);
        }
    }

    // Socket readable: drain every whole record SSL_read can surface, delivering each to the app and sending
    // any inline response via the plaintext path (kernel encrypts). Level-triggered EPOLLIN re-fires when
    // more arrives, so WANT_READ just returns.
    private void KtlsRead(EpollConnection conn)
    {
        fixed (byte* p = conn.KtlsRecv)
        {
            while (true)
            {
                int n = SSL_read(conn.KtlsSsl, p, conn.KtlsRecv!.Length);
                if (n > 0)
                {
                    var ctx = new SocketSet.ReceiveContext(conn, p, conn.KtlsRecv.Length, n);
                    Parent.DispatchReceive(ref ctx);
                    int response = ctx.ResponseBytes;
                    if (response > 0 && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0
                        && conn.Fd >= 0 && !conn.Closing)
                        SendBytes(conn, p, response);
                    if (conn.Closing || conn.Fd < 0) return; // a callback / response tore it down
                    continue; // more records may be buffered in the engine
                }

                int err = SSL_get_error(conn.KtlsSsl, n);
                if (err == SSL_ERROR_WANT_READ) return;             // drained; EPOLLIN re-fires on more
                if (err == SSL_ERROR_WANT_WRITE) { ArmWrite(conn); return; } // e.g. a renegotiation write
                CloseClient(conn.Slot);                             // ZERO_RETURN (close_notify) / fatal / syscall
                return;
            }
        }
    }

    // Report what the kernel ACTUALLY took, once per process — a silent TX-only degradation (OpenSSL < 3.2
    // declines kTLS RX for TLS 1.3) is what made a year of io_uring kTLS numbers mean something other than
    // they appeared to. Capability is discovered (BIO_get_ktls_recv), so an old OpenSSL degrades to RX in
    // userspace rather than breaking — but it now says so.
    private static void ReportKtlsOnce(nint ssl)
    {
        if (Interlocked.Exchange(ref s_ktlsReported, 1) != 0) return;
        bool ktx = BIO_get_ktls_send(SSL_get_wbio(ssl));
        bool krx = BIO_get_ktls_recv(SSL_get_rbio(ssl));
        Console.Error.WriteLine(
            $"[ktls/epoll] openssl={OpenSslVersionString()} tx={ktx} rx={krx}" +
            (krx ? "" : " -- RX NOT offloaded: OpenSSL decrypts in userspace (SSL_read). Unlike io_uring this "
                      + "costs epoll nothing structural — it is readiness-driven anyway. OpenSSL 3.2+ is "
                      + "required for kTLS RX on TLS 1.3; see TODO item 4b."));
    }
}
#endif
