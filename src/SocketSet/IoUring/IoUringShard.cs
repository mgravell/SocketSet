#if NET // io_uring is a Linux + modern-.NET backend; compiled out of the netfx fallback build.
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SocketSets.Native;
using SocketSets.Tls;
using SocketSets.Tls.OpenSsl;
using static SocketSets.Tls.OpenSsl.NativeOpenSsl;

namespace SocketSets.IoUring;

/// <summary>
/// A single-threaded io_uring event loop. Exactly one thread (the loop thread)
/// ever touches the ring, which is why we can run with SINGLE_ISSUER +
/// DEFER_TASKRUN. Work that originates on other threads (<see cref="Listen"/>,
/// <see cref="Connect"/>) is not submitted directly; it is enqueued and the loop
/// is woken via an eventfd that the ring itself is reading.
/// </summary>
internal sealed class IoUringShard : SocketSetShard
{
    private const int AddrStride = 128; // per-slot sockaddr storage (covers sockaddr_in and sockaddr_un)

    private enum Op : byte
    {
        Wake = 0,
        Accept = 1,
        Connect = 2,
        Recv = 3,
        Send = 4, // send from a write-pool buffer (aux = write index)
        SendBid = 5, // no-copy echo: send straight from a read (provided) buffer (aux = bid)
        WriteV = 6, // scatter-gather send of a large payload across N write-pool pages (aux = first page index)
        Cancel = 7, // IORING_OP_ASYNC_CANCEL(CANCEL_FD|ALL) issued during teardown to drain in-flight ops
        Poll = 8,   // IORING_OP_POLL_ADD readiness wait (kTLS: handshake step / RX SSL_read trigger)
    }

    private struct WriteState
    {
        public uint Slot;
        public int Fd;
        public int Sent;
        public int Total;
    }

    // --- slot table (1-based ids; id 0 == "none"). Connections are pooled: one instance per
    // slot, reused across connection lifetimes so accept/connect never allocates. ---
    private readonly IoUringConnection[] _conns;
    private SlotAllocator _slots; // loop-local free-slot allocator (claim/free, single-writer). NOT readonly:
                                  // it's a mutable struct, so a readonly field would mutate a throwaway copy.
    // Connect requests marshaled from the caller thread to the loop, which claims the slot + issues the
    // CONNECT SQE — so the slot table stays single-writer (only the loop claims). fd is created caller-side.
    private readonly ConcurrentQueue<(int Fd, EndPoint Endpoint, object? Token)> _pendingConnects = [];

    // --- options snapshot ---
    private readonly int _socketsPerShard;
    private readonly int _entriesPerShard;
    private readonly int _readPages;
    private readonly int _readPageSize;
    private readonly int _writeCount;
    private readonly int _writeBufSize;
    private readonly int _obWriteCount;

    // --- created on the loop thread in Initialize() ---
    private RawIOUringRing _ring;
    private ManagedBufferPool _readBuffer;

    private PinnedWriteBufferPool _writeBuffer;

    // Out-of-band write pool: leased from arbitrary threads (the IBufferWriter/Flush path), so guarded
    // by _obGate. Kept distinct from _writeBuffer so the IO-thread echo path never takes a lock.
    private PinnedWriteBufferPool _obWriteBuffer;
    private readonly object _obGate = new();
    private WriteState[] _writeState = []; // per write-pool index (Op.Send)
    private WriteState[] _bidState = []; // per bid, for no-copy echoes (Op.SendBid)
    private int _borrowedBids; // read buffers currently held by in-flight writes
    private int _maxBorrowedBids; // cap; above it echoes fall back to lease+copy
    private volatile bool _ringReady;

    // --- cross-thread wakeup ---
    private readonly int _eventFd;
    private unsafe ulong* _wakeBuf;

    // --- native, stable storage the kernel reads asynchronously ---
    private unsafe byte* _connectAddrs;

    private readonly ConcurrentQueue<LibC.io_uring_sqe> _pending = [];

    // Accepted fds handed to this shard by another shard's single listener (UDS /
    // any non-reuse-port listener). Drained on the loop thread, then adopted here.
    // The default accept token travels with the fd since the target shard has no
    // listener of its own to look it up on.
    private readonly ConcurrentQueue<(int Fd, object? Token)> _incoming = [];

    // Bound listener fd -> the default UserToken to seed into each AcceptContext.
    private readonly ConcurrentDictionary<int, object?> _listeners = new();

    // Flushed out-of-band writes marshaled from arbitrary threads onto the loop thread. The chain's
    // buffers were leased by the writing thread (OOB pool pages and/or ArrayPool overflow). The
    // generation is captured at enqueue and re-checked on drain so a flush for a since-closed (and
    // possibly reused) slot is dropped, not misdelivered.
    private readonly ConcurrentQueue<(uint Slot, uint Generation, OutChain Chain)> _flush = [];

    // Close requests marshaled from arbitrary threads onto the loop thread. Generation-guarded so a
    // request can't retract a slot that has since been closed and re-tenanted.
    private readonly ConcurrentQueue<(uint Slot, uint Generation)> _closes = [];

    /// <summary>SS_URING_TRACE=1 dumps every completion to stderr. Read once, so the hot loop pays only a
    /// never-taken branch. See the use site for why this is worth having.</summary>
    private static readonly bool TraceCompletions =
        Environment.GetEnvironmentVariable("SS_URING_TRACE") == "1";

    // TLS ciphertext is copied into WritePageSize (BufferPageSize, 4KB) chunks leased from the
    // out-of-band pool; when that pool is dry each chunk becomes a PINNED GC allocation instead. One
    // 256KB TLS response needs 64 chunks against a 256-chunk-per-shard pool, so the miss rate is a
    // function of payload size x concurrency. SS_URING_STATS=1 reports it at shutdown - a behavioural
    // fact, measurable even on a host too noisy to time.
    private static long s_oobHit, s_oobMiss;
    // Send accounting, to answer "how many completion round trips does ONE response cost?" - the quantity
    // behind the page-size cliff. s_sendSubmit counts every send SQE; the rest decompose it by cause.
    private static long s_opSend, s_opWriteV, s_pendEnqueue, s_pendDrain, s_partialResubmit;
    private static long s_segPooled, s_segManaged, s_segZeroCopy;
    private static long s_iovSegs; // total iovec segments across all writevs: segments/response is the
                                   // real independent variable, since sends/response is constant at 1.
    private static int s_statsReported;
    private static int s_ktlsReported; // kTLS TX/RX offload state is reported once per process

    private static void DumpStats(string tag)
    {
        long snd = Interlocked.Read(ref s_opSend), wv = Interlocked.Read(ref s_opWriteV);
        if (snd + wv == 0) return;
        Console.Error.WriteLine($"[uring-stats:{tag}] send SQEs={snd + wv:n0} " +
            $"(OP_SEND={snd:n0} OP_WRITEV={wv:n0} iov-segments={Interlocked.Read(ref s_iovSegs):n0} " +
            $"queued-behind-inflight={Interlocked.Read(ref s_pendEnqueue):n0} " +
            $"drained={Interlocked.Read(ref s_pendDrain):n0} " +
            $"partial-write-resubmits={Interlocked.Read(ref s_partialResubmit):n0}) " +
            $"segs: pooled-page={Interlocked.Read(ref s_segPooled):n0} pinned-managed={Interlocked.Read(ref s_segManaged):n0} " +
            $"zero-copy={Interlocked.Read(ref s_segZeroCopy):n0}");
    }
    private static readonly bool ReportStats =
        Environment.GetEnvironmentVariable("SS_URING_STATS") == "1";

    // Periodic dump, because the shutdown-only report is not dependable: after sustained load the process
    // does not always exit on SIGINT (suspected teardown stall - see TODO), and a measurement that needs a
    // clean shutdown to be readable is one that vanishes precisely under load.
    // MUST be declared after ReportStats: static field initializers run in declaration order, so reading
    // ReportStats from an earlier initializer sees false and silently disables the timer.
    private static readonly Timer? StatsTimer = ReportStats
        ? new Timer(static _ => DumpStats("periodic"), null, 2000, 2000)
        : null;

    public unsafe IoUringShard(SocketSetOptions options)
    {
        _socketsPerShard = options.SocketsPerShard;
        _entriesPerShard = options.EntriesPerShard;
        _readPages = options.BufferPagesPerShard;
        // Receive size is allowed to differ from the send page (SocketSetOptions.ReceiveBufferSize).
        // io_uring does not NEED the split the way epoll and the Windows backends do - its read pool is
        // per-SHARD (BufferPagesPerShard entries, 256) rather than per-socket, so a 64KB page is ~16MB per
        // shard here against 256MB there - but honouring it keeps the option meaning one thing on every
        // backend, and stops the harness banner reporting a recvbuf= the backend then ignores.
        _readPageSize = options.ReceiveBufferSize > 0 ? options.ReceiveBufferSize : options.BufferPageSize;
        _writeCount = options.WriteBuffersPerShard;
        _writeBufSize = options.BufferPageSize;
        _obWriteCount = options.OutOfBandWriteBuffersPerShard;

        _conns = new IoUringConnection[_socketsPerShard];
        for (int i = 0; i < _conns.Length; i++)
            _conns[i] = new IoUringConnection(this, (uint)i + 1);
        _slots = new SlotAllocator(_conns.Length);
        SetShardCapacity(_conns.Length); // reservation ceiling == slot-table size

        // Stable native storage the kernel dereferences after we return.
        _connectAddrs = (byte*)NativeMemory.AllocZeroed((nuint)_socketsPerShard * AddrStride);
        _wakeBuf = (ulong*)NativeMemory.AllocZeroed(sizeof(ulong));

        // Create the eventfd up-front (a plain syscall, thread-independent) so that
        // Listen/Connect can Poke() safely even before the loop has built the ring.
        // EFD_NONBLOCK makes io_uring arm a poll instead of blocking a worker.
        _eventFd = LibC.eventfd(0, 0x800 /* EFD_NONBLOCK */);
        if (_eventFd < 0) throw new InvalidOperationException("Failed to allocate shard eventfd");

        // _ring/_readBuffer/_writeBuffer are deferred; they must be created by the loop thread.
    }

    // =====================================================================
    // Slot table
    // =====================================================================

    /// <summary>Claim a free slot for <paramref name="fd"/> from the loop-local allocator. Loop-thread
    /// only (accept adoption, or a connect marshaled via <see cref="StartConnect"/>) — the single-writer
    /// model, so the claim is a plain free-list pop + plain store, no CAS. Returns null only if the table
    /// is full, which a prior reservation should have made impossible (backstop against counter drift).</summary>
    private IoUringConnection? InitClient(int fd, object? userToken, SocketSet.SocketFlags flags)
    {
        if (fd <= 0) Throw();
        // Loop-thread only (accept adoption, or a marshaled connect) → single writer of the slot table,
        // so the claim is a plain free-list pop + plain store, no CAS. The caller reserved first, so a
        // slot is guaranteed; Claim only fails on counter drift (backstop → caller releases + drops).
        int idx = _slots.Claim();
        if (idx < 0) return null;
        var conn = _conns[idx];
        conn.UserToken = userToken;
        conn.Flags = flags;
        conn.SendBusy = false;
        conn.TlsClient = false;  // Tls/KtlsSsl are nulled in TryFinalize; belt-and-suspenders for a fresh tenant
        conn.KtlsReady = false;
        conn.Pending?.Clear();
        // Bump the generation before publishing Fd: any out-of-band send/close captured against the
        // previous tenant now mismatches and is dropped rather than misdelivered.
        Volatile.Write(ref conn.Generation, conn.Generation + 1);
        Volatile.Write(ref conn.Fd, fd); // publish live last (foreign readers gate on Fd != 0)
        return conn;

        static void Throw() => throw new ArgumentOutOfRangeException(nameof(fd), "Invalid socket handle");
    }

    private SocketSet.SocketFlags GetFlags(uint slot) => _conns[slot - 1].Flags;

    /// <summary>Begin tearing a connection down (loop thread). Idempotent. Fires OnClosed now, but
    /// does NOT close the fd or free the slot yet — it forces the in-flight ops to drain (shutdown +
    /// ASYNC_CANCEL) and the slot is finalized only once they have (see <see cref="TryFinalize"/>), so
    /// no stale completion can ever land on a re-tenanted slot.</summary>
    private unsafe void CloseClient(uint slot)
    {
        if (slot == 0) return;
        var conn = _conns[slot - 1];
        if (conn.Fd == 0 || conn.Closing) return; // not open / already tearing down
        conn.Closing = true;

        // Notify the app once, while the identity is valid, if it saw the connection open.
        if (conn.Opened)
        {
            conn.Opened = false;
            try { Parent.DispatchClosed(conn); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        // Force the drain: shutdown(RDWR) sends the FIN and EOFs the armed recv; ASYNC_CANCEL(FD|ALL)
        // cancels the recv + any in-flight send so nothing lingers. The fd stays open (cancel matches
        // by fd) and the slot stays claimed until every op is reaped.
        if (Parent.Options.ResetOnClose)
        {
            // Abortive close: SO_LINGER{1,0} makes the eventual close() send RST (no FIN, no TIME_WAIT).
            // The ASYNC_CANCEL below still drains the in-flight recv/send so defer-recycle holds.
            var lg = new LibC.Linger { l_onoff = 1, l_linger = 0 };
            LibC.setsockopt(conn.Fd, LibC.SOL_SOCKET, LibC.SO_LINGER, &lg, (uint)sizeof(LibC.Linger));
        }
        else
        {
            // kTLS: emit a proper TLS close_notify (not just a bare FIN) so a strict peer sees a clean
            // shutdown rather than a truncation. Only when idle — the alert must not jump ahead of an
            // in-flight/queued application send on the wire; if busy we fall back to the FIN (still a
            // valid, if less polite, close). kTLS TX means the kernel encrypts the alert record.
            if (conn.KtlsSsl != 0 && !conn.SendBusy && (conn.Pending is null || conn.Pending.Count == 0))
                SSL_shutdown(conn.KtlsSsl);
            LibC.shutdown(conn.Fd, LibC.SHUT_RDWR);
        }
        if (conn.RecvArmed || conn.SendBusy)
        {
            conn.CancelPending = true;
            SubmitCancel(conn.Fd, slot);
        }
        TryFinalize(conn, slot); // nothing in flight → finalize immediately
    }

    // Finalize once all in-flight ops for a closing slot have drained: close the fd, release any
    // queued buffers, and publish the slot as free LAST (only now may a racing InitClient claim it).
    private void TryFinalize(IoUringConnection conn, uint slot)
    {
        if (!conn.Closing || conn.RecvArmed || conn.SendBusy || conn.CancelPending) return;

        if (conn.Pending is { } pending)
        {
            while (pending.Count > 0)
            {
                var job = pending.Dequeue();
                // A queued zero-copy send never reached the socket: fail it, so the pump stops waiting and
                // releases the caller's memory. Missing this deadlocks the pipe pump holding a ReadResult.
                if (job.Zc is { } zc) zc.Finish(false);
                else if (job.Chain.Count > 0) ReleaseChainBuffers(job.Chain);
                else if (job.Seg.Array is not null) ArrayPool<byte>.Shared.Return(job.Seg.Array); // pooled echo staging
            }
        }

        int fd = conn.Fd;
        if (conn.Tls is { } tlsf) { try { tlsf.Dispose(); } catch { /* SSL_free best-effort */ } conn.Tls = null; }
        if (conn.KtlsSsl != 0) { SSL_free(conn.KtlsSsl); conn.KtlsSsl = 0; conn.KtlsReady = false; }
        conn.UserToken = null;
        conn.Flags = 0;
        conn.SendBusy = false;
        conn.RecvArmed = false;
        conn.Closing = false;
        if (fd > 0) LibC.close(fd);
        Volatile.Write(ref conn.Fd, 0); // publish free (foreign readers see the slot as dead)
        _slots.Free((int)(slot - 1));   // return to the loop-local allocator (loop thread only)
        ReleaseReservation();           // paired with the TryReserve that placed this connection
    }

    // =====================================================================
    // Public entry points (called from arbitrary threads)
    // =====================================================================

    public override void Listen(EndPoint endpoint, object? userToken, bool local)
    {
        int fd = IoUringFactory.Bind(endpoint, Parent.Options.ListenBacklog);
        _listeners[fd] = userToken; // default token for connections accepted here
        EnqueueAccept(fd, local);
    }

    public override void ListenHandle(nint handle, object? userToken)
    {
        // Inherited/handed-over listener: it's a single fd (not reuse-port multi-bound), so this one
        // shard drives the accept and bounces each connection round-robin (local: false), exactly like
        // the UDS single-listener path. Assumed already bound + listen()ed by the caller.
        int fd = checked((int)handle);
        if (fd <= 0) throw new ArgumentOutOfRangeException(nameof(handle), "Invalid socket handle.");
        _listeners[fd] = userToken;
        EnqueueAccept(fd, local: false);
    }

    public override void Connect(EndPoint endpoint, object? userToken)
    {
        // This shard holds a reservation (TryPlace took it before dispatching here); release it on any
        // failure before the connect is handed to the loop, so a rejected connect doesn't leak capacity.
        if (endpoint is not (IPEndPoint or UnixDomainSocketEndPoint))
        {
            ReleaseReservation();
            throw new NotSupportedException($"{nameof(Connect)} on {endpoint.GetType().Name} is not supported.");
        }

        // Create the fd here (thread-agnostic syscall, so failures stay synchronous to the caller) but
        // hand the claim + sockaddr-build + CONNECT SQE to the loop, keeping the slot table single-writer.
        int fd = endpoint is IPEndPoint
            ? LibC.socket(LibC.AF_INET, LibC.SOCK_STREAM, LibC.IPPROTO_TCP)
            : LibC.socket(LibC.AF_UNIX, LibC.SOCK_STREAM, 0);
        if (fd < 0)
        {
            ReleaseReservation();
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "socket() failed");
        }

        _pendingConnects.Enqueue((fd, endpoint, userToken));
        Poke();
    }

    // Loop thread: claim the reserved slot for a marshaled connect, build the target sockaddr into its
    // stable native storage, and issue the CONNECT SQE. The reservation is consumed by the claim, or
    // released here on the (should-not-happen) claim-failure backstop.
    private unsafe void StartConnect(int fd, EndPoint endpoint, object? userToken)
    {
        var conn = InitClient(fd, userToken, SocketSet.SocketFlags.None);
        if (conn is null) { LibC.close(fd); ReleaseReservation(); return; }
        uint slot = conn.Slot;

        byte* addrPtr = _connectAddrs + (nint)(slot - 1) * AddrStride;
        uint addrLen;
        if (endpoint is IPEndPoint ip)
        {
            int one = 1;
            LibC.setsockopt(fd, LibC.IPPROTO_TCP, LibC.TCP_NODELAY, &one, sizeof(int));
            var sa = (LibC.SockAddrIn*)addrPtr;
            *sa = default; // clear any stale bytes from a prior tenant of this slot
            sa->sin_family = LibC.AF_INET;
            sa->sin_port = LibC.Htons((ushort)ip.Port);
            var bytes = ip.Address.GetAddressBytes(); // 4 bytes, already network order
            byte* dst = (byte*)&sa->sin_addr;
            dst[0] = bytes[0];
            dst[1] = bytes[1];
            dst[2] = bytes[2];
            dst[3] = bytes[3];
            addrLen = 16; // sizeof(sockaddr_in)
        }
        else // UnixDomainSocketEndPoint (caller validated the type before marshaling)
        {
            var uds = (UnixDomainSocketEndPoint)endpoint;
            // SockAddrUn.Init zeroes the struct and maps a leading '@' to the abstract namespace ('\0').
            addrLen = LibC.SockAddrUn.Init((LibC.SockAddrUn*)addrPtr, uds.ToString());
        }

        Submit(new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_CONNECT,
            fd = fd,
            addr = (ulong)addrPtr,
            off = addrLen,
            user_data = Pack(Op.Connect, slot),
        });
    }

    private void EnqueueAccept(int listenerFd, bool local)
    {
        var sqe = new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_ACCEPT,
            fd = listenerFd,
            ioprio = LibC.IORING_ACCEPT_MULTISHOT,
            user_data = Pack(Op.Accept, (uint)listenerFd, local ? 1u : 0u),
        };
        Enqueue(sqe);
    }

    private void Enqueue(in LibC.io_uring_sqe sqe)
    {
        _pending.Enqueue(sqe);
        Poke();
    }

    /// <summary>Adopt an fd accepted on another shard's single listener. Cross-thread;
    /// the fd is process-global so any shard's ring can drive it. The default accept
    /// token is carried across since the target shard has no listener to look it up.</summary>
    internal void EnqueueInbound(int fd, object? defaultToken)
    {
        _incoming.Enqueue((fd, defaultToken));
        Poke();
    }

    /// <summary>Marshal a flushed out-of-band write chain onto the loop thread (called from
    /// <see cref="IoUringConnection.Flush"/>, i.e. any thread). The chain's buffers are already
    /// filled and owned by the loop from here on.</summary>
    internal void SubmitFlush(uint slot, uint generation, in OutChain chain)
    {
        _flush.Enqueue((slot, generation, chain));
        Poke();
    }

    /// <summary>Marshal a close request onto the loop thread (called from
    /// <see cref="IoUringConnection.Close"/>, i.e. any thread).</summary>
    internal void SubmitClose(uint slot, uint generation)
    {
        _closes.Enqueue((slot, generation));
        Poke();
    }

    // --- out-of-band write pool (thread-safe; used by the IBufferWriter path) ---

    /// <summary>Page size of the write pools (bytes).</summary>
    internal int WritePageSize => _writeBufSize;

    /// <summary>Lease a pinned out-of-band write page. Thread-safe; callable from any thread.</summary>
    internal unsafe bool LeaseOutOfBand(out int index, out byte* ptr)
    {
        lock (_obGate) return _obWriteBuffer.TryLease(out index, out ptr);
    }

    /// <summary>Return a pinned out-of-band write page. Thread-safe.</summary>
    internal void ReleaseOutOfBand(int index)
    {
        lock (_obGate) _obWriteBuffer.Release(index);
    }

    /// <summary>Native address of an out-of-band write page (pure arithmetic on a stable slab).</summary>
    internal unsafe byte* OutOfBandAddress(int index) => _obWriteBuffer.Address(index);

    private unsafe void Poke()
    {
        if (!_ringReady) return; // loop drains _pending on its first pass anyway
        ulong one = 1;
        LibC.write(_eventFd, &one, sizeof(ulong));
    }

    protected override void OnStop() => Poke(); // wake the loop so it observes !IsActive

    // =====================================================================
    // Loop-thread submission helpers
    // =====================================================================

    private void Submit(in LibC.io_uring_sqe sqe)
    {
        // Send accounting (SS_URING_STATS=1). Every send SQE is one completion round trip, so
        // sends-per-response is the quantity the page-size cliff is really about: a response that fits one
        // page should cost exactly one. Counted at the single funnel so no send path can be missed.
        if (ReportStats)
        {
            // Split by opcode: SEND is the single-buffer path, WRITEV the scatter-gather chain path.
            // Which one a response takes is the thing page size actually selects.
            if (sqe.opcode == LibC.IORING_OP_SEND) Interlocked.Increment(ref s_opSend);
            else if (sqe.opcode == LibC.IORING_OP_WRITEV) Interlocked.Increment(ref s_opWriteV);
        }

        // Fast path: place directly in the SQ. If the ring is momentarily full
        // (a completion batch can generate more SQEs than capacity), defer to the
        // pending queue rather than fault — it is drained at the top of every loop
        // iteration, once io_uring_enter has freed SQ slots. Loop-thread only, but
        // _pending is concurrent so the enqueue is safe regardless.
        if (!_ring.TryPush(sqe)) _pending.Enqueue(sqe);
    }

    private unsafe void ArmWake()
    {
        Submit(new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_READ,
            fd = _eventFd,
            addr = (ulong)_wakeBuf,
            len = sizeof(ulong),
            user_data = Pack(Op.Wake, 0),
        });
    }

    private void ArmRecv(uint slot, int fd)
    {
        // Multishot: one SQE yields many recv completions (each selecting a provided buffer),
        // so we don't re-submit per message. It stays armed while IORING_CQE_F_MORE is set and
        // must be re-armed only when that clears (error / buffer exhaustion).
        _conns[slot - 1].RecvArmed = true; // an in-flight recv op now exists for this slot
        var sqe = new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_RECV,
            fd = fd,
            ioprio = LibC.IORING_RECV_MULTISHOT,
            // len MUST be 0 for a MULTISHOT recv: the size comes from the provided-buffer ring, and the
            // kernel rejects a non-zero len outright (io_recv_prep: "opcode == IORING_OP_RECV && sr->len"
            // -> -EINVAL). Passing the buffer size here - which reads naturally, and which a SINGLE-shot
            // buffer-select recv would want as its maximum - made every recv fail with -22 and the
            // connection close with no data ever delivered.
            len = 0,
            flags = LibC.IOSQE_BUFFER_SELECT,
            buf_index = _readBuffer.GroupId, // buf_index aliases buf_group
            user_data = Pack(Op.Recv, slot),
        };
        Submit(sqe);
    }

    // Cancel every in-flight op on a fd (teardown). CANCEL_FD matches by fd; CANCEL_ALL gets them all.
    private void SubmitCancel(int fd, uint slot)
    {
        var sqe = new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_ASYNC_CANCEL,
            fd = fd,
            user_data = Pack(Op.Cancel, slot),
        };
        sqe.rw_flags.rw_flags = (int)(LibC.IORING_ASYNC_CANCEL_FD | LibC.IORING_ASYNC_CANCEL_ALL);
        Submit(sqe);
    }

    private unsafe void SubmitSend(uint slot, int fd, int writeIndex, byte* data, int len)
    {
        _writeState[writeIndex] = new WriteState { Slot = slot, Fd = fd, Sent = 0, Total = len };
        var sqe = new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_SEND,
            fd = fd,
            addr = (ulong)data,
            len = (uint)len,
            user_data = Pack(Op.Send, slot, (uint)writeIndex),
        };
        sqe.rw_flags.rw_flags = LibC.MSG_NOSIGNAL;
        Submit(sqe);
    }

    /// <summary>No-copy echo: send <paramref name="len"/> bytes straight from read buffer
    /// <paramref name="bid"/>. The buffer stays out of the provided-buffer ring until the send
    /// completes (see <see cref="HandleSendBid"/>), so the caller must not release it.</summary>
    private unsafe void SubmitSendBid(uint slot, int fd, ushort bid, int len)
    {
        _bidState[bid] = new WriteState { Slot = slot, Fd = fd, Sent = 0, Total = len };
        var sqe = new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_SEND,
            fd = fd,
            addr = (ulong)_readBuffer.GetBufferAddress(bid),
            len = (uint)len,
            user_data = Pack(Op.SendBid, slot, bid),
        };
        sqe.rw_flags.rw_flags = LibC.MSG_NOSIGNAL;
        Submit(sqe);
    }

    /// <summary>Copy <paramref name="len"/> bytes from a read buffer into a leased write
    /// buffer and send it. Closes the connection if no write buffer is available.</summary>
    private unsafe void SendResponse(uint slot, int fd, byte* src, int len)
    {
        // The caller claimed the send slot (SendBusy) before deciding borrow-vs-copy; if we bail here
        // without actually submitting a send, release it so teardown doesn't wait on a phantom send.
        if ((GetFlags(slot) & SocketSet.SocketFlags.SendClosed) != 0) { _conns[slot - 1].SendBusy = false; return; }
        if (!_writeBuffer.TryLease(out int wi, out byte* wp))
        {
            // Pool exhausted: the safe thing is to drop the connection rather than stall it.
            System.Diagnostics.Debug.WriteLine("Write buffer pool exhausted; closing connection.");
            _conns[slot - 1].SendBusy = false;
            CloseClient(slot);
            return;
        }

        Buffer.MemoryCopy(src, wp, _writeBuffer.BufferSize, len);
        SubmitSend(slot, fd, wi, wp, len);
    }

    // =====================================================================
    // Out-of-band writes (loop thread; flushed chains drained from _flush each iteration)
    // =====================================================================

    private const int IovMax = 1024; // UIO_MAXIOV: writev rejects iovcnt above this with -EINVAL

    private void PumpFlush(uint slot, uint generation, in OutChain chain)
    {
        var conn = _conns[slot - 1];
        // Slot reused (or closing) since the flush was queued: drop it rather than misdeliver.
        if (conn.Generation != generation || conn.Fd == 0 || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0)
        {
            ReleaseChainBuffers(chain);
            return;
        }

        if (conn.Tls is not null)
        {
            // Out-of-band write on a TLS connection: the chain holds PLAINTEXT — encrypt it, send the
            // ciphertext, and release the plaintext buffers. (Ordered behind any in-flight send by TlsSend.)
            TlsPumpFlush(conn, chain);
            return;
        }

        DispatchChainSplit(conn, conn.Fd, chain);
    }

    /// <summary>Dispatch a chain as one or more writevs, each capped at <see cref="IovMax"/> iovecs (writev
    /// rejects more with -EINVAL). A larger chain is split into ordered sub-chains that serialize through
    /// the per-connection send queue, preserving wire order. <paramref name="appData"/> (fire OnWrite on
    /// completion) rides ONLY the last sub-chain, so a split encrypted response still fires OnWrite exactly
    /// once — when the whole thing has gone out. Every producer of a chain that can exceed IovMax must go
    /// through here: the plaintext OOB flush (large Connection.Send) and the TLS ciphertext send (TlsSend)
    /// both can — a ~4MB response at a 4KB page is ~1024 page segments — and routing only one of them
    /// through the cap is exactly the bug this centralises out (TLS verify-oob-4m stalled on an oversized
    /// writev while the plaintext leg passed).</summary>
    private void DispatchChainSplit(IoUringConnection conn, int fd, in OutChain chain, bool appData = false)
    {
        if (chain.Count <= IovMax)
        {
            DispatchChain(conn, fd, chain, appData);
            return;
        }

        for (int start = 0; start < chain.Count; start += IovMax)
        {
            int len = Math.Min(IovMax, chain.Count - start);
            var sub = new OutChain();
            sub.Add(in chain, start, len);
            DispatchChain(conn, fd, sub, appData && start + len >= chain.Count);
        }
    }

    private void DispatchChain(IoUringConnection conn, int fd, in OutChain chain, bool appData = false)
    {
        // Ordering across a connection's byte stream is enforced by a single in-flight send per
        // connection (SendBusy) — response-writes and flushed writes share the gate, and the next is submitted
        // only when the current completes — rather than IO_LINK/IO_DRAIN. Separate connections still
        // submit concurrently/in bulk. Chosen for simplicity (esp. -ECANCELED handling on IO_LINK);
        // IO_LINK may earn its keep in massive-throughput scenarios later, but for now: KISS.
        if (conn.SendBusy)
        {
            if (ReportStats) Interlocked.Increment(ref s_pendEnqueue);
            (conn.Pending ??= new()).Enqueue(new PendingJob { Chain = chain, AppData = appData });
            return;
        }

        conn.SendBusy = true;
        SubmitChain(conn, fd, chain, appData);
    }

    /// <summary>Send a flushed chain as one IORING_OP_WRITEV: pool-page segments contribute their
    /// native address; POH overflow segments their (stable, pinned) array address. State lives on the
    /// connection (one send in flight at a time). Assumes SendBusy is already claimed.</summary>
    private unsafe void SubmitChain(IoUringConnection conn, int fd, in OutChain chain, bool appData = false)
    {
        int n = chain.Count;
        var iov = (LibC.iovec*)NativeMemory.Alloc((nuint)n * (nuint)sizeof(LibC.iovec));
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            var seg = chain[i];
            iov[i].iov_base = seg.Page >= 0 ? OutOfBandAddress(seg.Page) : PinnedAddress(seg.Managed!);
            iov[i].iov_len = (nuint)seg.Length;
            total += seg.Length;
            // Which memory does a segment actually come from? A pooled page is free; a managed segment is
            // a pinned GC allocation made per response. This is the branch the page size selects.
            if (ReportStats)
            {
                if (seg.Page >= 0) Interlocked.Increment(ref s_segPooled);
                else Interlocked.Increment(ref s_segManaged);
            }
        }

        if (ReportStats) Interlocked.Add(ref s_iovSegs, n);
        conn.WriteV = new WriteVState
        {
            Iov = iov, TotalIov = n, Cursor = 0, Sent = 0, Total = total, Chain = chain, AppData = appData,
        };
        SubmitWriteV(conn.Slot, fd, iov, n);
    }

    // Address of a pinned-heap (POH) array's data — stable because POH arrays are never moved.
    private static unsafe byte* PinnedAddress(byte[] arr) =>
        (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(arr));

    private unsafe void SubmitWriteV(uint slot, int fd, LibC.iovec* iov, int count)
    {
        Submit(new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_WRITEV,
            fd = fd,
            addr = (ulong)iov,
            len = (uint)count,
            off = ulong.MaxValue, // -1: not a positioned write (plain writev, as for a socket)
            user_data = Pack(Op.WriteV, slot),
        });
    }

    // Release a flushed chain's buffers without having submitted it (dropped before send).
    private void ReleaseChainBuffers(in OutChain chain)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            var seg = chain[i];
            if (seg.Page >= 0) ReleaseOutOfBand(seg.Page); // POH overflow buffers just drop
        }
    }

    // Release an in-flight writev's resources on completion: free the iovec array and return the pool
    // pages (POH overflow segments are just dropped for GC).
    private unsafe void ReleaseWriteV(IoUringConnection conn) => ReleaseWriteV(conn, ok: false);

    /// <summary>Release the in-flight send's resources. <paramref name="ok"/> is the result reported to a
    /// zero-copy caller: true only when the send actually completed, since the pump uses it to decide
    /// whether to advance its reader.</summary>
    private unsafe void ReleaseWriteV(IoUringConnection conn, bool ok)
    {
        ref var ws = ref conn.WriteV;
        if (ws.Zc is { } zc)
        {
            // Zero-copy: the iovecs point at the CALLER's memory, so there is no chain to release and the
            // iovec array belongs to the job. Finish() frees it, drops the pins, and releases the pump.
            zc.Finish(ok);
            conn.WriteV = default;
            return;
        }
        if (ws.Iov != null) NativeMemory.Free(ws.Iov);
        ReleaseChainBuffers(ws.Chain);
        conn.WriteV = default;
    }

    // =====================================================================
    // Zero-copy send (BYO buffers; see Connection.TrySendZeroCopy)
    // =====================================================================

    /// <summary>Zero-copy requests marshaled in from the pipe pump. The job is fully built by the caller
    /// (pinning and iovec construction are thread-agnostic); the loop thread only issues it.</summary>
    private readonly ConcurrentQueue<(uint Slot, uint Generation, ZcJob Job)> _zeroCopy = [];

    // Returns the number of BYTES accepted for zero-copy send, which may be a PREFIX of `data`: the caller
    // (PipeIoBridge) then re-presents the remainder, so a sequence longer than IovMax is streamed as
    // several zero-copy sends rather than falling off a cliff into a full copy. Returns 0 when zero-copy is
    // not possible at all (TLS, closed, empty), and the caller takes the copy path. Measured 2026-07-31:
    // the >IovMax decline is far (a 4MB response at Kestrel's 4KB blocks = 1024 segments) but a hard cliff
    // when hit - an 8MB response went 100% copy (zero-copy=2 of ~61k segments) before this.
    internal unsafe long TrySendZeroCopy(IoUringConnection conn, in ReadOnlySequence<byte> data, bool pinned,
                                         out ValueTask<bool> completion)
    {
        completion = default;
        // TLS must encrypt, so the bytes on the wire are never the caller's bytes. Refusing (rather than
        // silently copying) keeps the fallback the obvious path - the caller then uses Send().
        if (conn.Tls is not null || conn.KtlsSsl != 0) return 0;
        if (Volatile.Read(ref conn.Fd) == 0) return 0;
        if (data.IsEmpty) return 0;

        // Count the non-empty segments, CAPPED at IovMax: a single writev takes at most IovMax iovecs, so
        // beyond that we send the first IovMax as a prefix. Counting first (rather than pinning as we go)
        // avoids unwinding pins if we stop early. Empty segments carry no iovec, so they don't count.
        int n = 0;
        foreach (var seg in data)
        {
            if (seg.IsEmpty) continue;
            if (++n == IovMax) break; // cap: the rest becomes the caller's remainder
        }
        if (n == 0) return 0; // all-empty sequence: nothing to send zero-copy

        // THE PIN COST IS THE THING TO WATCH, and it is why `pinned` is not a micro-optimisation. With an
        // ordinary MemoryPool this takes one GCHandle pin per SEGMENT, and Kestrel's default 4KB blocks
        // make a 256KB response ~64 pins + 64 disposes - plausibly dearer than the single copy being
        // removed. A null or negative result here is therefore ambiguous until the pool is pinned
        // (`ctx.UsePipe(pipe, pinned: true)` over a pinned-block pool), at which point this whole branch
        // disappears. See TODO 2d.
        var job = new ZcJob { Handles = pinned ? null : new MemoryHandle[n] };
        var iov = (LibC.iovec*)NativeMemory.Alloc((nuint)n * (nuint)sizeof(LibC.iovec));
        job.Iov = iov;
        int i = 0;
        long total = 0;
        foreach (var seg in data)
        {
            if (seg.IsEmpty) continue;
            if (i == n) break; // filled the prefix; the remainder is the caller's to re-present
            byte* p;
            if (job.Handles is null)
            {
                // Caller asserts the memory is already pinned (a pinned-block pool), so its address is
                // stable without a handle and we skip the per-segment pin entirely.
                p = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(seg.Span));
            }
            else
            {
                var h = seg.Pin();
                job.Handles[i] = h;
                p = (byte*)h.Pointer;
            }
            iov[i].iov_base = p;
            iov[i].iov_len = (nuint)seg.Length;
            total += seg.Length;
            i++;
        }
        if (i == 0) { job.Count = 0; job.Finish(false); return 0; }

        job.Count = i;
        job.Total = total;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Completion = tcs;
        completion = new ValueTask<bool>(tcs.Task);

        _zeroCopy.Enqueue((conn.Slot, Volatile.Read(ref conn.Generation), job));
        Poke();
        return total; // bytes accepted — the full sequence, or an IovMax-segment prefix of it
    }

    /// <summary>Loop thread: issue a zero-copy job, or queue it behind an in-flight send.</summary>
    private unsafe void PumpZeroCopy(uint slot, uint generation, ZcJob job)
    {
        var conn = _conns[slot - 1];
        // Slot reused, closing, or send-closed since the request was queued: fail it rather than write
        // into a re-tenanted socket. Finish(false) is what stops the pump waiting forever.
        if (conn.Generation != generation || conn.Fd == 0 || conn.Closing
            || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0)
        {
            job.Finish(false);
            return;
        }

        if (conn.SendBusy)
        {
            if (ReportStats) Interlocked.Increment(ref s_pendEnqueue);
            (conn.Pending ??= new()).Enqueue(new PendingJob { Zc = job });
            return;
        }

        conn.SendBusy = true;
        SubmitZc(conn, conn.Fd, job);
    }

    /// <summary>Issue a zero-copy job as one writev over the caller's segments. Assumes SendBusy is held.
    /// Partial writes need no special handling: HandleWriteV advances the same cursor it does for our own
    /// chains, and the caller's memory stays put until Finish().</summary>
    private unsafe void SubmitZc(IoUringConnection conn, int fd, ZcJob job)
    {
        // NB: s_opWriteV is counted centrally in Submit() by opcode, so it must NOT be incremented here -
        // that funnel exists precisely so no send path can be missed or double-counted.
        if (ReportStats)
        {
            Interlocked.Add(ref s_iovSegs, job.Count);
            Interlocked.Add(ref s_segZeroCopy, job.Count);
        }
        conn.WriteV = new WriteVState
        {
            Iov = job.Iov, TotalIov = job.Count, Cursor = 0, Sent = 0, Total = job.Total, Zc = job,
        };
        SubmitWriteV(conn.Slot, fd, job.Iov, job.Count);
    }

    // Submit a queued job (leasing fresh IO-pool buffers for a segment). SendBusy stays set.
    private unsafe void SubmitPendingJob(IoUringConnection conn, uint slot, int fd, in PendingJob job)
    {
        if (job.Zc is { } zc)
        {
            SubmitZc(conn, fd, zc);
            return;
        }

        if (job.Chain.Count > 0)
        {
            SubmitChain(conn, fd, job.Chain, job.AppData);
            return;
        }

        if (!_writeBuffer.TryLease(out int wi, out byte* wp))
        {
            conn.SendBusy = false; // no send actually issued — don't strand teardown on it
            CloseClient(slot);
            return;
        }

        var seg = job.Seg;
        Marshal.Copy(seg.Array!, seg.Offset, (IntPtr)wp, seg.Count);
        ArrayPool<byte>.Shared.Return(seg.Array!); // copied into the write page; done with the pooled buffer
        SubmitSend(slot, fd, wi, wp, seg.Count);
    }

    // Pick the next queued job, or go idle. SendBusy stays set iff a job was dispatched.
    private void DrainNext(IoUringConnection conn, uint slot, int fd)
    {
        if (conn.Pending is { Count: > 0 } pending)
        {
            if (ReportStats) Interlocked.Increment(ref s_pendDrain);
            SubmitPendingJob(conn, slot, fd, pending.Dequeue());
        }
        else conn.SendBusy = false;
    }

    // =====================================================================
    // TLS (loop thread; see TlsFilter). No lock: the loop is the single owner, and OOB flushes are
    // already marshaled here — so the filter's not-thread-safe contract holds natively (unlike managed).
    // All TLS output rides the OOB chain path (TlsSend -> DispatchChainSplit -> WriteV), reusing the
    // validated scatter-gather send + teardown machinery. Arbitrary-size ciphertext works because the
    // chain is split at IovMax per writev — for a while it was NOT (TlsSend called DispatchChain directly),
    // so a ~4MB response at a 4KB page overran the 1024-iovec limit and stalled; see DispatchChainSplit.
    // =====================================================================

    // Stand up the per-connection engine at open, arm recv, and take the first handshake step (a client
    // emits its ClientHello; a server produces nothing until it sees one). OnConnect/OnAccept are deferred
    // to TlsFireOpen at handshake completion.
    private unsafe void StartTls(IoUringConnection conn, uint slot, int fd, bool client)
    {
        var opts = Parent.Options;
        conn.TlsClient = client;
        conn.Tls = client ? opts.Tls!.CreateClientFilter(opts.TlsClient) : opts.Tls!.CreateServerFilter(opts.TlsServer);
        conn.TlsPlain ??= new PooledBufferWriter(_readPageSize);
        conn.TlsOut ??= new PooledBufferWriter(_readPageSize);

        ArmRecv(slot, fd);                          // multishot recv: handshake records then app data land here
        TlsDriveHandshake(conn, slot, fd, default); // client: sends ClientHello; server: no-op (awaits peer)
    }

    // Ciphertext arrived in a read (provided) buffer. Feed the engine; the bytes are copied into the
    // engine's read BIO synchronously, so the caller may release the buffer as soon as this returns.
    private unsafe void DeliverReceiveTls(IoUringConnection conn, uint slot, int fd, byte* rp, int res)
    {
        var cipher = new ReadOnlySpan<byte>(rp, res);
        if (!conn.Tls!.HandshakeComplete) TlsDriveHandshake(conn, slot, fd, cipher);
        else TlsData(conn, slot, fd, cipher);
    }

    // One handshake step: feed input, send any handshake records produced (RAW — they are already TLS
    // records, not app data), and on completion fire the deferred open + drain any coalesced app data.
    private unsafe void TlsDriveHandshake(IoUringConnection conn, uint slot, int fd, ReadOnlySpan<byte> input)
    {
        conn.TlsOut!.Reset();
        var status = conn.Tls!.DriveHandshake(input, fd, conn.TlsOut);
        if (conn.TlsOut.WrittenCount > 0) TlsSend(conn, fd, conn.TlsOut.WrittenSpan);

        if (status == TlsHandshakeStatus.Faulted) { CloseClient(slot); return; }
        if (status != TlsHandshakeStatus.Completed) return; // NeedMoreData: await the next recv

        TlsFireOpen(conn, slot, fd);
        // Application data coalesced into the same segment as the peer's final handshake flight is already
        // buffered in the engine — surface it now with an empty inbound step (see TlsFilter.DriveHandshake).
        if (conn.Fd != 0 && !conn.Closing) TlsData(conn, slot, fd, default);
    }

    // Handshake done: fire the deferred OnConnect/OnAccept and send any greeting (encrypted).
    private unsafe void TlsFireOpen(IoUringConnection conn, uint slot, int fd)
    {
        byte[] gbuf = conn.TlsPlain!.Array; // scratch for greeting plaintext (>= a page)
        int sb;
        fixed (byte* gp = gbuf)
        {
            conn.Opened = true; // app now sees it open → pairs with OnClosed
            if (conn.TlsClient)
            {
                var ctx = new SocketSet.ConnectContext(conn, gp, gbuf.Length);
                Parent.OnConnect(ref ctx);
                sb = ctx.SendBytes;
            }
            else
            {
                var ctx = new SocketSet.AcceptContext(conn, gp, gbuf.Length);
                Parent.OnAccept(ref ctx);
                sb = ctx.SendBytes;
            }
        }
        if (sb > 0) TlsEncryptSend(conn, fd, new ReadOnlySpan<byte>(gbuf, 0, sb));
    }

    // Data phase inbound: decrypt, deliver plaintext to OnReceive, encrypt any response.
    private unsafe void TlsData(IoUringConnection conn, uint slot, int fd, ReadOnlySpan<byte> cipher)
    {
        conn.TlsPlain!.Reset();
        conn.TlsOut!.Reset();
        var status = conn.Tls!.ProcessInbound(cipher, TlsContentType.Ciphertext, conn.TlsPlain, conn.TlsOut);
        if (conn.TlsOut.WrittenCount > 0) TlsSend(conn, fd, conn.TlsOut.WrittenSpan); // control replies (raw)

        if (status == TlsInboundStatus.Faulted) { CloseClient(slot); return; }

        int plen = conn.TlsPlain.WrittenCount;
        if (plen > 0)
        {
            byte[] pbuf = conn.TlsPlain.Array;
            int response;
            fixed (byte* pp = pbuf)
            {
                var ctx = new SocketSet.ReceiveContext(conn, pp, pbuf.Length, plen);
                Parent.DispatchReceive(ref ctx);
                response = ctx.ResponseBytes;
            }
            if (response > 0 && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
                TlsEncryptSend(conn, fd, new ReadOnlySpan<byte>(pbuf, 0, response));
        }

        if (status == TlsInboundStatus.PeerClosed) CloseClient(slot);
    }

    // Out-of-band flushed plaintext (Connection.Flush) on a TLS connection: encrypt the whole chain, send
    // the ciphertext, release the plaintext buffers.
    private unsafe void TlsPumpFlush(IoUringConnection conn, in OutChain chain)
    {
        conn.TlsOut!.Reset();
        for (int i = 0; i < chain.Count; i++)
        {
            var seg = chain[i];
            byte* p = seg.Page >= 0 ? OutOfBandAddress(seg.Page) : PinnedAddress(seg.Managed!);
            conn.Tls!.ProcessOutbound(new ReadOnlySpan<byte>(p, seg.Length), conn.TlsOut);
        }
        if (conn.TlsOut.WrittenCount > 0) TlsSend(conn, conn.Fd, conn.TlsOut.WrittenSpan);
        ReleaseChainBuffers(chain);
    }

    // A prior encrypted-application send completed: offer OnWrite a plaintext scratch so the app can
    // pipeline the next message (drives the echo/verify window state machine), and send it if it does.
    private unsafe void TlsOnWrite(IoUringConnection conn, uint slot, int fd)
    {
        byte[] wbuf = conn.TlsPlain!.Array; // plaintext scratch for the next write
        int sb;
        fixed (byte* wp = wbuf)
        {
            var ctx = new SocketSet.WriteContext(conn, wp, wbuf.Length);
            Parent.OnWrite(ref ctx);
            sb = ctx.SendBytes;
        }
        if (sb > 0 && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
            TlsEncryptSend(conn, fd, new ReadOnlySpan<byte>(wbuf, 0, sb));
    }

    // Encrypt application plaintext and send the resulting ciphertext (appData → fires OnWrite on completion).
    private unsafe void TlsEncryptSend(IoUringConnection conn, int fd, ReadOnlySpan<byte> plaintext)
    {
        conn.TlsOut!.Reset();
        conn.Tls!.ProcessOutbound(plaintext, conn.TlsOut);
        TlsSend(conn, fd, conn.TlsOut.WrittenSpan, appData: true);
    }

    // Send raw ciphertext: chunk it into OOB pool pages (POH overflow if the pool is dry) and dispatch as
    // a scatter-gather chain — reusing the connection's single-in-flight-send + Pending serialization, so
    // handshake records, control replies and encrypted responses all stay in wire order. appData marks an
    // encrypted-application send (→ OnWrite on completion); handshake/control sends leave it false.
    private unsafe void TlsSend(IoUringConnection conn, int fd, ReadOnlySpan<byte> cipher, bool appData = false)
    {
        if (cipher.IsEmpty) return;
        var chain = new OutChain();
        int off = 0, page = WritePageSize;
        while (off < cipher.Length)
        {
            int take = Math.Min(page, cipher.Length - off);
            if (LeaseOutOfBand(out int idx, out byte* p))
            {
                if (ReportStats) Interlocked.Increment(ref s_oobHit);
                cipher.Slice(off, take).CopyTo(new Span<byte>(p, take));
                chain.Add(new OutSeg { Page = idx, Managed = null, Length = take });
            }
            else
            {
                // Pool dry: fall back to a PINNED per-segment allocation. Counted because the rate of this
                // is a property of the workload, not of timing, and so can be measured even where
                // throughput cannot (see SS_URING_STATS).
                if (ReportStats) Interlocked.Increment(ref s_oobMiss);
                var arr = GC.AllocateUninitializedArray<byte>(take, pinned: true);
                cipher.Slice(off, take).CopyTo(arr);
                chain.Add(new OutSeg { Page = -1, Managed = arr, Length = take });
            }
            off += take;
        }
        // Split at IovMax: a large record (or a coalesced flush) chunked by a small page can exceed the
        // per-writev iovec limit — a ~4MB ciphertext at a 4KB page is ~1024 segments.
        DispatchChainSplit(conn, fd, chain, appData);
    }

    // =====================================================================
    // kTLS (io_uring only). OpenSSL owns the fd (socket BIO, SSL_OP_ENABLE_KTLS) and pushes the keys into
    // the kernel at handshake completion — so the DATA path is plaintext: TX = the NORMAL SubmitSend
    // machinery (kernel encrypts), RX = SSL_read (kernel decrypts, OpenSSL services control records)
    // driven by an io_uring POLL. The handshake runs non-blocking, stepped by POLL readiness. Distinct
    // from the userspace TlsFilter path (no memory BIOs, no per-record userspace crypto).
    // =====================================================================

    // Stand up the kTLS engine at open: fd non-blocking (so OpenSSL doesn't block the loop), SSL bound to
    // the fd, then drive the handshake (client sends ClientHello; server awaits). OnConnect/OnAccept fire
    // from KtlsComplete once keys are in the kernel.
    private unsafe void StartKtls(IoUringConnection conn, uint slot, int fd, bool client, OpenSslTlsProvider prov)
    {
        LibC.SetNonBlocking(fd);
        conn.TlsClient = client;
        conn.KtlsRecv ??= new byte[_readPageSize];
        conn.KtlsSsl = prov.CreateKernelSsl(
            fd, client,
            client ? Parent.Options.TlsClient.TargetHost : null,
            client ? Parent.Options.TlsClient.AlpnProtocols : Parent.Options.TlsServer.AlpnProtocols);
        KtlsPump(conn, slot, fd);
    }

    // One handshake step: SSL_do_handshake, then either complete or arm a POLL for the readiness OpenSSL
    // is waiting on. Runs on the loop thread; the fd is non-blocking so this never blocks.
    private unsafe void KtlsPump(IoUringConnection conn, uint slot, int fd)
    {
        int ret = SSL_do_handshake(conn.KtlsSsl);
        if (ret == 1) { KtlsComplete(conn, slot, fd); return; }
        int err = SSL_get_error(conn.KtlsSsl, ret);
        if (err == SSL_ERROR_WANT_READ) SubmitPoll(conn, slot, fd, LibC.POLLIN);
        else if (err == SSL_ERROR_WANT_WRITE) SubmitPoll(conn, slot, fd, LibC.POLLOUT);
        else CloseClient(slot); // handshake failed (bad cert, verify failure, protocol error, …)
    }

    // Arm a one-shot readiness wait. RecvArmed doubles as "a poll is in flight" so teardown (which cancels
    // by fd) waits for it to drain, exactly as it does for a multishot recv.
    private void SubmitPoll(IoUringConnection conn, uint slot, int fd, uint events)
    {
        conn.RecvArmed = true;
        var sqe = new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_POLL_ADD,
            fd = fd,
            user_data = Pack(Op.Poll, slot),
        };
        sqe.rw_flags.rw_flags = events; // poll32_events aliases the rw_flags union
        Submit(sqe);
    }

    private unsafe void HandlePoll(int res, uint slot)
    {
        var conn = _conns[slot - 1];
        conn.RecvArmed = false; // the poll op has completed (incl. -ECANCELED during teardown)
        if (conn.Closing) { TryFinalize(conn, slot); return; }
        if (res < 0) { CloseClient(slot); return; }
        int fd = conn.Fd;
        if (!conn.KtlsReady) KtlsPump(conn, slot, fd); // still handshaking
        else KtlsRead(conn, slot, fd);                 // data phase: socket readable → SSL_read
    }

    // Handshake done + keys in the kernel: fire the deferred open (greeting rides the NORMAL plaintext send
    // path — kernel encrypts), then arm RX readiness (also surfaces any app data coalesced after the
    // handshake, since POLL fires immediately if the socket already has bytes).
    private unsafe void KtlsComplete(IoUringConnection conn, uint slot, int fd)
    {
        // Report what the kernel ACTUALLY took, once per process. Until 2026-07-29 every kTLS number on
        // file measured a HALF-offloaded path - TX in the kernel, RX still decrypted by OpenSSL - and
        // nothing said so: `TlsRxSw` sat at 0 in /proc/net/tls_stat and had to be noticed by hand months
        // later. The cause is not ours: OpenSSL < 3.2 declines kTLS RX for TLS 1.3. Measured on this box -
        // 3.0.13 gives RX=False, a self-built 3.5.7 gives RX=True, same probe, TLS 1.3 both times.
        //
        // Capability is discovered rather than assumed (BIO_get_ktls_recv), so an old OpenSSL degrades to
        // TX-only instead of breaking - but a silent degradation is exactly what made a year of kTLS
        // measurements mean something other than what they appeared to. So it now says so out loud.
        if (Interlocked.Exchange(ref s_ktlsReported, 1) == 0)
        {
            bool ktx = BIO_get_ktls_send(SSL_get_wbio(conn.KtlsSsl));
            bool krx = BIO_get_ktls_recv(SSL_get_rbio(conn.KtlsSsl));
            Console.Error.WriteLine(
                $"[ktls] openssl={OpenSslVersionString()} tx={ktx} rx={krx}" +
                (krx ? "" : " -- RX NOT offloaded: OpenSSL is decrypting in userspace, so receive runs " +
                            "POLL+SSL_read (one syscall per message) and cannot use multishot. OpenSSL " +
                            "3.2+ is required for kTLS RX on TLS 1.3; see TODO item 4b."));
        }
        conn.KtlsReady = true;
        // No TlsFilter on this path, so publish the ALPN result straight from the SSL before the app sees
        // the connection open — Connection.NegotiatedProtocol falls back to this.
        conn.KernelAlpn = OpenSslTlsFilter.GetAlpnSelected(conn.KtlsSsl);

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        conn.Opened = true; // app now sees it open → pairs with OnClosed
        int sb;
        if (conn.TlsClient)
        {
            var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _writeBuffer.BufferSize : 0);
            Parent.OnConnect(ref ctx);
            sb = ctx.SendBytes;
        }
        else
        {
            var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _writeBuffer.BufferSize : 0);
            Parent.OnAccept(ref ctx);
            sb = ctx.SendBytes;
        }
        if (leased && sb > 0) { conn.SendBusy = true; SubmitSend(slot, fd, wi, wp, sb); }
        else if (leased) _writeBuffer.Release(wi);

        SubmitPoll(conn, slot, fd, LibC.POLLIN);
    }

    // Socket readable: drain every whole record SSL_read can decrypt, delivering each to OnReceive and
    // sending any response via the normal (plaintext) send path. Re-arm POLL when the kernel has no more.
    private unsafe void KtlsRead(IoUringConnection conn, uint slot, int fd)
    {
        while (true)
        {
            int n;
            fixed (byte* p = conn.KtlsRecv) n = SSL_read(conn.KtlsSsl, p, conn.KtlsRecv!.Length);
            if (n > 0)
            {
                int response;
                fixed (byte* p = conn.KtlsRecv)
                {
                    var ctx = new SocketSet.ReceiveContext(conn, p, conn.KtlsRecv.Length, n);
                    Parent.DispatchReceive(ref ctx);
                    response = ctx.ResponseBytes;
                }
                if (response > 0 && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
                    KtlsRespond(conn, slot, fd, response);
                if (conn.Closing || conn.Fd == 0) return; // OnReceive/response tore it down
                continue; // more records may be buffered
            }

            int err = SSL_get_error(conn.KtlsSsl, n);
            if (err == SSL_ERROR_WANT_READ) { SubmitPoll(conn, slot, fd, LibC.POLLIN); return; }
            if (err == SSL_ERROR_ZERO_RETURN) { CloseClient(slot); return; } // peer close_notify
            CloseClient(slot); // fatal alert / decrypt error / syscall error
            return;
        }
    }

    // Send a plaintext response (kernel encrypts). Mirrors the non-TLS echo serialization: if a send is in
    // flight, stage the bytes (pooled) behind it; otherwise claim the send and copy into a write page.
    private unsafe void KtlsRespond(IoUringConnection conn, uint slot, int fd, int len)
    {
        fixed (byte* rp = conn.KtlsRecv)
        {
            if (conn.SendBusy)
            {
                var copy = ArrayPool<byte>.Shared.Rent(len);
                Marshal.Copy((IntPtr)rp, copy, 0, len);
                if (ReportStats) Interlocked.Increment(ref s_pendEnqueue);
                (conn.Pending ??= new()).Enqueue(new PendingJob { Seg = new ArraySegment<byte>(copy, 0, len) });
                return;
            }
            conn.SendBusy = true;
            SendResponse(slot, fd, rp, len);
        }
    }

    // =====================================================================
    // Initialization / teardown
    // =====================================================================

    private unsafe void Initialize()
    {
        _ring = new RawIOUringRing((uint)_entriesPerShard);
        _readBuffer = new ManagedBufferPool(_ring.RingFd, entries: _readPages, bufSize: _readPageSize);
        _writeBuffer = new PinnedWriteBufferPool(_writeCount, _writeBufSize);
        _obWriteBuffer = new PinnedWriteBufferPool(_obWriteCount, _writeBufSize);
        _writeState = new WriteState[_writeCount];
        _bidState = new WriteState[_readPages];
        // How many read buffers the write path may hold at once. Default (0) = half the pool,
        // scaling with it. Hard-cap at three-quarters so at least a quarter of the ring always
        // stays free for receives — starving it triggers -ENOBUFS and multishot re-arm thrash.
        int cap = Parent.Options.MaxBorrowedReadBuffers;
        _maxBorrowedBids = Math.Min(cap > 0 ? cap : _readPages / 2, _readPages * 3 / 4);
        _ringReady = true;
    }

    private unsafe void Cleanup()
    {
        _ringReady = false;
        // Any zero-copy request that arrived but never reached a connection must still be completed, or
        // whichever pump is awaiting it never returns.
        while (_zeroCopy.TryDequeue(out var z)) z.Job.Finish(false);
        foreach (var fd in _listeners.Keys) LibC.close(fd);
        _listeners.Clear();

        for (int i = 0; i < _conns.Length; i++)
        {
            int fd = Interlocked.Exchange(ref _conns[i].Fd, 0);
            if (fd > 0) LibC.close(fd);
        }

        _readBuffer.Dispose();
        _writeBuffer.Dispose();
        lock (_obGate) _obWriteBuffer.Dispose();
        _ring.Dispose();

        if (_eventFd > 0) LibC.close(_eventFd);
        if (_connectAddrs != null)
        {
            NativeMemory.Free(_connectAddrs);
            _connectAddrs = null;
        }

        if (_wakeBuf != null)
        {
            NativeMemory.Free(_wakeBuf);
            _wakeBuf = null;
        }
    }

    // Allocation only (io_uring_setup, mmap, buffer-pool registration). Runs on the
    // worker thread, before the parent's startup gate is signalled, so an ENOMEM here
    // (e.g. RLIMIT_MEMLOCK) fails construction rather than silently killing the shard.
    protected override void OnInitialize() => Initialize();

    protected override unsafe void OnRun()
    {
        // Arm the wake read here rather than in OnInitialize: it writes an SQE, which is
        // submission-side work that belongs to the issuer (this) thread.
        ArmWake();

        while (IsActive)
        {
            // Adopt any connections handed off from another shard's listener.
            while (_incoming.TryDequeue(out var inbound))
                AdoptAccepted(inbound.Fd, inbound.Token);

            // Start any connects marshaled in from caller threads (claim + CONNECT SQE on the loop).
            while (_pendingConnects.TryDequeue(out var pc))
                StartConnect(pc.Fd, pc.Endpoint, pc.Token);

            // Issue any out-of-band writes flushed in from other threads.
            while (_flush.TryDequeue(out var f))
                PumpFlush(f.Slot, f.Generation, f.Chain);

            // Issue any zero-copy sends handed in by a pipe pump.
            while (_zeroCopy.TryDequeue(out var z))
                PumpZeroCopy(z.Slot, z.Generation, z.Job);

            // Honour any close requests marshaled in from other threads.
            while (_closes.TryDequeue(out var c))
            {
                var conn = _conns[c.Slot - 1];
                if (conn.Generation == c.Generation && conn.Fd != 0) CloseClient(c.Slot);
            }

            // Fold any cross-thread submissions into the ring.
            if (!_pending.IsEmpty)
                _ring.Push(_pending);

            // Submit everything currently in the SQ and block until at least one
            // completion. to_submit is read live from the ring (SqTail - SqHead) so
            // it always matches exactly what is unconsumed — no hand-tracked counter
            // to drift and strand SQEs unsubmitted.
            int res = LibC.io_uring_enter_blocking(
                LibC.SYS_io_uring_enter, _ring.RingFd,
                to_submit: _ring.SqReady, min_complete: 1,
                flags: LibC.IORING_ENTER_GETEVENTS, sig: null, sigsz: 0);

            if (res < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                switch (err)
                {
                    case LibC.EINTR:
                    case LibC.EAGAIN:
                    case LibC.EBUSY:
                        break; // transient; drain whatever is ready and retry
                    default:
                        throw new InvalidOperationException($"io_uring_enter failed: errno {err}");
                }
            }

            if (ProcessAvailableCompletions() > 0) Parent.OnLoopDrain();
        }
    }

    protected override void OnShutdown()
    {
        // The counters are process-wide, but OnShutdown runs per shard - so let exactly one shard report,
        // or an 8-shard run prints the same global totals eight times and reads like eight measurements.
        if (ReportStats && Interlocked.Exchange(ref s_statsReported, 1) == 0)
        {
            long hit = Interlocked.Read(ref s_oobHit), miss = Interlocked.Read(ref s_oobMiss);
            if (hit + miss > 0)
                Console.Error.WriteLine($"[uring-stats] tls out-of-band chunks: pooled={hit:n0} " +
                    $"pinned-alloc={miss:n0} ({100.0 * miss / (hit + miss):n1}% miss)");

            DumpStats("shutdown");
        }
        Cleanup();
    }

    // =====================================================================
    // Completion processing
    // =====================================================================

    private unsafe int ProcessAvailableCompletions()
    {
        int processed = 0;
        uint head = *_ring.CqHead;
        uint tail = Volatile.Read(ref *_ring.CqTail);

        while (head != tail)
        {
            LibC.io_uring_cqe* cqe = &_ring.Cqes[head & _ring.CqMask];
            var (op, id, aux) = Unpack(cqe->user_data);
            int res = cqe->res;
            uint flags = cqe->flags;
            head++; // advance our local view before dispatching (handlers may submit)
            processed++;

            // Opt-in CQE trace (SS_URING_TRACE=1). io_uring reports operation failures as a NEGATIVE
            // cqe.res, not as a syscall error, so a mis-built SQE looks like a healthy submit followed by
            // silence - every handler below just closes the connection. Without this the only symptom is
            // "no data ever arrives", which is exactly how the -EINVAL on multishot recv above hid.
            // Read once into a static: a predictable never-taken branch, not an env lookup per completion.
            if (TraceCompletions)
                Console.Error.WriteLine($"[cqe] shard={Shard} op={op} id={id} aux={aux} res={res} flags=0x{flags:x}");

            switch (op)
            {
                case Op.Wake:
                    ArmWake(); // re-arm; _pending is drained at the top of the loop
                    break;

                case Op.Accept:
                    HandleAccept(res, flags, listenerFd: (int)id, local: aux != 0);
                    break;

                case Op.Connect:
                    HandleConnect(res, slot: id);
                    break;

                case Op.Recv:
                    HandleRecv(res, flags, slot: id);
                    break;

                case Op.Send:
                    HandleSend(res, slot: id, writeIndex: (int)aux);
                    break;

                case Op.SendBid:
                    HandleSendBid(res, slot: id, bid: (ushort)aux);
                    break;

                case Op.WriteV:
                    HandleWriteV(res, slot: id);
                    break;

                case Op.Cancel:
                    HandleCancel(slot: id);
                    break;

                case Op.Poll:
                    HandlePoll(res, slot: id);
                    break;
            }
        }

        // Release-store our consumed head so the kernel can reuse CQ slots.
        Volatile.Write(ref *_ring.CqHead, head);
        return processed;
    }

    private void HandleAccept(int res, uint flags, int listenerFd, bool local)
    {
        if (res >= 0)
        {
            int newFd = res;
            object? defaultToken = _listeners.TryGetValue(listenerFd, out var t) ? t : null;
            if (local)
            {
                // reuse-port: each shard has its own listener, so it is already balanced — adopt here.
                // Reserve our own slot first; if we're full, fall back to capacity-aware placement rather
                // than dropping. That fallback is what lets a reuse-port SERVER grow: TryPlace adds a shard
                // when every table is full, and a grown shard is given its own reuse-port listener (see
                // SocketSet.Listen's replay in TryGrow) so the kernel balances subsequent accepts onto it
                // directly — this bounce is only for the one connection that triggered the growth. Without
                // it a full shard closed the accepted fd silently and the set never grew here (epoll's
                // AcceptBurst already routes every accept through TryPlace, so only io_uring needed this).
                if (TryReserve())
                {
                    AdoptAccepted(newFd, defaultToken);
                }
                else
                {
                    var target = (IoUringShard?)Parent.TryPlace();
                    if (target is not null) target.EnqueueInbound(newFd, defaultToken);
                    else LibC.close(newFd); // genuinely full and growth off/exhausted (TryPlace counted it)
                }
            }
            else
            {
                // Single listener (e.g. UDS): all accepts land on this one shard, so place each connection
                // on the first shard with a free slot (capacity-aware; drops only if every shard is full).
                var target = (IoUringShard?)Parent.TryPlace();
                if (target is not null) target.EnqueueInbound(newFd, defaultToken);
                else LibC.close(newFd); // every shard full → drop (runtime shard growth would expand here)
            }
        }

        // Multishot accept re-arms itself while IORING_CQE_F_MORE is set; re-issue when it clears.
        if ((flags & LibC.IORING_CQE_F_MORE) == 0)
            EnqueueAccept(listenerFd, local);
    }

    /// <summary>Run OnAccept, allocate a slot, arm the first receive and fire any
    /// initial send — all on the loop thread that will own this connection. The
    /// handler sees <paramref name="userToken"/> pre-seeded and may replace it.</summary>
    private unsafe void AdoptAccepted(int newFd, object? userToken)
    {
        // Set TCP_NODELAY explicitly rather than relying on the accepted socket inheriting
        // it from the listener. Harmless on AF_UNIX (setsockopt just fails, ignored).
        int one = 1;
        LibC.setsockopt(newFd, LibC.IPPROTO_TCP, LibC.TCP_NODELAY, &one, sizeof(int));

        // Claim the slot first so the connection identity exists before OnAccept sees it; the
        // handler mutates UserToken/Flags on it directly (no copy-out).
        var conn = InitClient(newFd, userToken, SocketSet.SocketFlags.None);
        if (conn is null)
        {
            // Reserved (in HandleAccept) but couldn't claim — counter drift; release and drop.
            LibC.close(newFd);
            ReleaseReservation();
            return;
        }

        uint slot = conn.Slot;

        if (Parent.Options.TlsEnabled(isClient: false)
            && Parent.Options.Tls is { SupportsKernelOffload: true } and OpenSslTlsProvider kop
            && Parent.Options.TlsServer.AllowKernelOffload)
        {
            StartKtls(conn, slot, newFd, client: false, kop); // kernel TLS
            return;
        }
        if (Parent.Options.TlsEnabled(isClient: false))
        {
            // TLS: don't fire OnAccept yet — stand up the server engine and arm recv to await the
            // ClientHello. OnAccept fires from TlsFireOpen once the handshake completes.
            StartTls(conn, slot, newFd, client: false);
            return;
        }

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _writeBuffer.BufferSize : 0);
        conn.Opened = true; // app now sees it open → pairs with OnClosed
        Parent.OnAccept(ref ctx);

        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0)
            ArmRecv(slot, newFd);

        int sb = ctx.SendBytes;
        if (leased && sb > 0)
        {
            conn.SendBusy = true;
            SubmitSend(slot, newFd, wi, wp, sb);
        }
        else if (leased) _writeBuffer.Release(wi);
    }

    private unsafe void HandleConnect(int res, uint slot)
    {
        var conn = _conns[slot - 1];
        int fd = conn.Fd;
        if (res == 0 && fd != 0)
        {
            if (Parent.Options.TlsEnabled(isClient: true)
                && Parent.Options.Tls is { SupportsKernelOffload: true } kp and OpenSslTlsProvider kop
                && Parent.Options.TlsClient.AllowKernelOffload)
            {
                StartKtls(conn, slot, fd, client: true, kop); // kernel TLS: OpenSSL owns the fd, POLL-driven
                return;
            }
            if (Parent.Options.TlsEnabled(isClient: true))
            {
                // TLS: don't fire OnConnect yet — stand up the client engine, arm recv, send ClientHello.
                // OnConnect fires from TlsFireOpen once the handshake completes.
                StartTls(conn, slot, fd, client: true);
                return;
            }

            // UserToken was seeded by Connect()'s InitClient; the handler may replace it in-place.
            bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
            var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _writeBuffer.BufferSize : 0);
            conn.Opened = true; // app now sees it open → pairs with OnClosed
            Parent.OnConnect(ref ctx);

            if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0)
                ArmRecv(slot, fd);

            int sb = ctx.SendBytes;
            if (leased && sb > 0)
            {
                conn.SendBusy = true;
                SubmitSend(slot, fd, wi, wp, sb);
            }
            else if (leased) _writeBuffer.Release(wi);
        }
        else
        {
            CloseClient(slot);
        }
    }

    private unsafe void HandleRecv(int res, uint flags, uint slot)
    {
        var conn = _conns[slot - 1];
        bool hasBuf = (flags & LibC.IORING_CQE_F_BUFFER) != 0;
        ushort bid = (ushort)(flags >> LibC.IORING_CQE_BUFFER_SHIFT);
        bool more = (flags & LibC.IORING_CQE_F_MORE) != 0;

        // Whenever the multishot doesn't continue, its in-flight op has ended (a re-arm below starts a
        // fresh one). This is what TryFinalize waits on during teardown.
        if (!more) conn.RecvArmed = false;

        if (conn.Closing)
        {
            // Draining: release any buffer, don't deliver or re-arm; finalize once fully drained.
            if (hasBuf) _readBuffer.ReleaseBuffer(bid);
            TryFinalize(conn, slot);
            return;
        }

        int fd = conn.Fd; // live (not closing) → valid; defer-recycle means no stale completions
        if (res > 0)
        {
            // DeliverReceive may borrow the buffer for a no-copy echo; if so it owns releasing it.
            bool borrowed = DeliverReceive(slot, fd, bid, res);
            if (hasBuf && !borrowed) _readBuffer.ReleaseBuffer(bid);

            // Multishot stays armed while F_MORE is set; re-arm only when it clears.
            if (!more && !conn.Closing && (conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0)
                ArmRecv(slot, fd);
        }
        else if (res == -LibC.ENOBUFS)
        {
            // Provided-buffer ring was empty, so the multishot ended; re-arm — buffers free as
            // borrowed ones are returned on send completion.
            ArmRecv(slot, fd);
        }
        else
        {
            // res == 0 (peer EOF) or a negative errno → begin teardown.
            if (hasBuf) _readBuffer.ReleaseBuffer(bid);
            CloseClient(slot);
        }
    }

    /// <summary>Dispatch OnReceive and, if it set a response, send it. Returns true iff the read
    /// buffer <paramref name="bid"/> was borrowed for an in-flight send (caller must not release it).</summary>
    private unsafe bool DeliverReceive(uint slot, int fd, ushort bid, int res)
    {
        var conn = _conns[slot - 1];
        if (conn.Tls is not null)
        {
            // Ciphertext in the read buffer: decrypt/handshake, then the caller releases the bid (we never
            // borrow it — TLS breaks the no-copy echo path). The engine copies the bytes it needs first.
            DeliverReceiveTls(conn, slot, fd, _readBuffer.GetBufferAddress(bid), res);
            return false;
        }

        byte* rp = _readBuffer.GetBufferAddress(bid);
        var ctx = new SocketSet.ReceiveContext(conn, rp, _readBuffer.BufferSize, res);
        Parent.DispatchReceive(ref ctx);

        int rb = ctx.ResponseBytes;
        if (rb <= 0 || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0) return false;

        if (conn.SendBusy)
        {
            // A send is already in flight (e.g. an out-of-band Send, or a still-draining pipeline):
            // serialize behind it by copying the response out and queueing. The read buffer is not
            // borrowed, so the caller releases it as usual.
            // Pooled staging buffer (loop-thread rent/return → per-thread cache): a pipelined echo
            // doesn't allocate per message. Returned when the job is drained (SubmitPendingJob /
            // CompleteWrite) or dropped (TryFinalize). Rent may over-size, so Seg carries the true length.
            var copy = ArrayPool<byte>.Shared.Rent(rb);
            Marshal.Copy((IntPtr)rp, copy, 0, rb);
            if (ReportStats) Interlocked.Increment(ref s_pendEnqueue);
            (conn.Pending ??= new()).Enqueue(new PendingJob { Seg = new ArraySegment<byte>(copy, 0, rb) });
            return false;
        }

        conn.SendBusy = true;
        if (_borrowedBids < _maxBorrowedBids)
        {
            // No-copy echo: send straight from the read buffer, holding it until the send done.
            _borrowedBids++;
            SubmitSendBid(slot, fd, bid, rb);
            return true;
        }

        // Over the borrow cap: lease+copy so the read-buffer ring keeps draining.
        SendResponse(slot, fd, rp, rb);
        return false;
    }

    private unsafe void HandleSend(int res, uint slot, int writeIndex)
    {
        var conn = _conns[slot - 1];
        ref WriteState ws = ref _writeState[writeIndex];

        if (conn.Closing)
        {
            // Draining (this send was cancelled/errored/completed): free its buffer, clear the send
            // slot, finalize once fully drained.
            _writeBuffer.Release(writeIndex);
            conn.SendBusy = false;
            TryFinalize(conn, slot);
            return;
        }

        if (res < 0)
        {
            _writeBuffer.Release(writeIndex);
            conn.SendBusy = false;
            CloseClient(slot);
            return;
        }

        ws.Sent += res;
        if (ws.Sent < ws.Total && res > 0)
        {
            // Partial write: resubmit the remainder from the same buffer (still one send in flight).
            byte* p = _writeBuffer.Address(writeIndex) + ws.Sent;
            int remaining = ws.Total - ws.Sent;
            var sqe = new LibC.io_uring_sqe
            {
                opcode = LibC.IORING_OP_SEND,
                fd = ws.Fd,
                addr = (ulong)p,
                len = (uint)remaining,
                user_data = Pack(Op.Send, slot, (uint)writeIndex),
            };
            sqe.rw_flags.rw_flags = LibC.MSG_NOSIGNAL;
            Submit(sqe);
            return;
        }

        if (ws.Sent < ws.Total)
        {
            // res == 0 with bytes still outstanding: treat as a dead peer.
            _writeBuffer.Release(writeIndex);
            conn.SendBusy = false;
            CloseClient(slot);
            return;
        }

        CompleteWrite(conn, slot, conn.Fd, writeIndex);
    }

    /// <summary>A send from write page <paramref name="writeIndex"/> fully completed. Offer the
    /// now-free page to OnWrite (a handler can pipeline the next message straight back into it, no
    /// release/re-lease); failing that, drain a queued out-of-band send into it; failing that, hand
    /// the page back and go idle. The page stays leased across any follow-up.</summary>
    private unsafe void CompleteWrite(IoUringConnection conn, uint slot, int fd, int writeIndex)
    {
        byte* wp = _writeBuffer.Address(writeIndex);
        var ctx = new SocketSet.WriteContext(conn, wp, _writeBuffer.BufferSize);
        Parent.OnWrite(ref ctx);

        int next = ctx.SendBytes;
        if (next == 0 && conn.Pending is { Count: > 0 } pending)
        {
            var job = pending.Dequeue();
            if (job.Chain.Count > 0)
            {
                // A flushed out-of-band chain is next: it has its own buffers, so free this page.
                _writeBuffer.Release(writeIndex);
                SubmitChain(conn, fd, job.Chain, job.AppData); // SendBusy stays set
                return;
            }

            // A queued echo segment: reuse this just-freed page for it.
            Marshal.Copy(job.Seg.Array!, job.Seg.Offset, (IntPtr)wp, job.Seg.Count);
            next = job.Seg.Count;
            ArrayPool<byte>.Shared.Return(job.Seg.Array!); // done with the pooled staging buffer
        }

        if (next > 0)
        {
            _writeState[writeIndex] = new WriteState { Slot = slot, Fd = fd, Sent = 0, Total = next };
            var sqe = new LibC.io_uring_sqe
            {
                opcode = LibC.IORING_OP_SEND,
                fd = fd,
                addr = (ulong)wp,
                len = (uint)next,
                user_data = Pack(Op.Send, slot, (uint)writeIndex),
            };
            sqe.rw_flags.rw_flags = LibC.MSG_NOSIGNAL;
            Submit(sqe); // SendBusy stays set
        }
        else
        {
            _writeBuffer.Release(writeIndex);
            conn.SendBusy = false;
        }
    }

    // Completion of a no-copy echo (Op.SendBid). Mirrors HandleSend but the "buffer" is a read
    // (provided) buffer held out of the ring; on final completion we return it and drop the borrow.
    private unsafe void HandleSendBid(int res, uint slot, ushort bid)
    {
        var conn = _conns[slot - 1];
        ref WriteState ws = ref _bidState[bid];

        if (conn.Closing)
        {
            // Draining: return the borrowed read buffer, clear the send slot, finalize when drained.
            _readBuffer.ReleaseBuffer(bid);
            _borrowedBids--;
            conn.SendBusy = false;
            TryFinalize(conn, slot);
            return;
        }

        if (res < 0)
        {
            _readBuffer.ReleaseBuffer(bid);
            _borrowedBids--;
            conn.SendBusy = false;
            CloseClient(slot);
            return;
        }

        ws.Sent += res;
        if (ws.Sent < ws.Total && res > 0)
        {
            // Partial: resubmit the remainder from the same read buffer (still borrowed).
            byte* p = _readBuffer.GetBufferAddress(bid) + ws.Sent;
            var sqe = new LibC.io_uring_sqe
            {
                opcode = LibC.IORING_OP_SEND,
                fd = ws.Fd,
                addr = (ulong)p,
                len = (uint)(ws.Total - ws.Sent),
                user_data = Pack(Op.SendBid, slot, bid),
            };
            sqe.rw_flags.rw_flags = LibC.MSG_NOSIGNAL;
            Submit(sqe);
            return;
        }

        if (ws.Sent < ws.Total)
        {
            // res == 0 with bytes outstanding: dead peer.
            _readBuffer.ReleaseBuffer(bid);
            _borrowedBids--;
            conn.SendBusy = false;
            CloseClient(slot);
            return;
        }

        int fd = conn.Fd;

        // Full echo sent. Fire OnWrite offering the read buffer for a pipelined follow-up (drives
        // the window state machine); if the handler declines, return the buffer to the ring.
        byte* rp = _readBuffer.GetBufferAddress(bid);
        var ctx = new SocketSet.WriteContext(conn, rp, _readBuffer.BufferSize);
        Parent.OnWrite(ref ctx);

        int next = ctx.SendBytes;
        if (next > 0)
        {
            // Keep reusing the same read buffer — still borrowed, no new copy. SendBusy stays set.
            SubmitSendBid(slot, fd, bid, next);
            return;
        }

        // No pipelined follow-up: return the borrowed read buffer to the ring, then drain the next.
        _readBuffer.ReleaseBuffer(bid);
        _borrowedBids--;
        DrainNext(conn, slot, fd);
    }

    // Completion of a scatter-gather out-of-band send (Op.WriteV). Handles partial writev by advancing
    // through the iovec array and resubmitting the remainder; on full completion releases the chain
    // and drains the next queued job. Out-of-band flushes deliberately do not fire OnWrite.
    private unsafe void HandleWriteV(int res, uint slot)
    {
        var conn = _conns[slot - 1];
        ref var ws = ref conn.WriteV;

        if (conn.Closing)
        {
            // Draining: release the chain, clear the send slot, finalize once fully drained.
            ReleaseWriteV(conn);
            conn.SendBusy = false;
            TryFinalize(conn, slot);
            return;
        }

        if (res < 0)
        {
            ReleaseWriteV(conn);
            conn.SendBusy = false;
            CloseClient(slot);
            return;
        }

        int fd = conn.Fd;
        ws.Sent += res;
        if (ws.Sent < ws.Total && res > 0)
        {
            // Partial writev: skip the iovecs fully drained by this completion and trim the one it
            // stopped inside, then resubmit from there (state unchanged).
            long consume = res;
            int c = ws.Cursor;
            while (consume > 0)
            {
                long il = (long)ws.Iov[c].iov_len;
                if (il <= consume)
                {
                    consume -= il;
                    c++;
                }
                else
                {
                    ws.Iov[c].iov_base = (byte*)ws.Iov[c].iov_base + consume;
                    ws.Iov[c].iov_len = (nuint)(il - consume);
                    consume = 0;
                }
            }

            ws.Cursor = c;
            if (ReportStats) Interlocked.Increment(ref s_partialResubmit);
            SubmitWriteV(slot, fd, ws.Iov + c, ws.TotalIov - c);
            return;
        }

        if (ws.Sent < ws.Total)
        {
            // res == 0 with bytes outstanding: dead peer.
            ReleaseWriteV(conn);
            conn.SendBusy = false;
            CloseClient(slot);
            return;
        }

        // Full payload sent. ok: true is what releases a zero-copy caller to AdvanceTo its reader - the
        // socket has finished reading its memory only now.
        bool appData = conn.WriteV.AppData;
        ReleaseWriteV(conn, ok: true);
        // TLS: an encrypted application send just completed → fire OnWrite so the app can pipeline the
        // next message (handshake records / control replies / plaintext OOB flushes leave AppData false).
        if (appData && conn.Tls is not null && !conn.Closing && conn.Fd != 0)
            TlsOnWrite(conn, slot, fd);
        DrainNext(conn, slot, fd);
    }

    // Completion of the teardown ASYNC_CANCEL. The cancelled ops post their own -ECANCELED
    // completions (which clear RecvArmed/SendBusy); this just clears the cancel itself.
    private void HandleCancel(uint slot)
    {
        var conn = _conns[slot - 1];
        conn.CancelPending = false;
        TryFinalize(conn, slot);
    }

    // =====================================================================
    // user_data packing: [op:8][aux:24][id:32]
    // =====================================================================

    private static ulong Pack(Op op, uint id, uint aux = 0)
        => ((ulong)(byte)op << 56) | ((ulong)(aux & 0xFFFFFF) << 32) | id;

    private static (Op op, uint id, uint aux) Unpack(ulong ud)
        => ((Op)(byte)(ud >> 56), (uint)ud, (uint)((ud >> 32) & 0xFFFFFF));
}
#endif