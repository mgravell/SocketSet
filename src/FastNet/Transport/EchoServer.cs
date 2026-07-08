using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FastNet.Buffers;
using FastNet.Native;
using static FastNet.Native.LibUring;
using static FastNet.Transport.IoUringCqeExtensions;

[module: SkipLocalsInit]

namespace FastNet.Transport;

/// <summary>
/// Reference io_uring transport, echo-only. This is the template the real
/// SE.Redis network layer grows from: a proactor loop that owns the ring,
/// drains completions, and drives a per-connection state machine.
///
/// Today reads and writes are submitted from the same loop thread (correct
/// and fast for a single ring — the SQ is not thread-safe without locking).
/// The submit paths are already distinct calls, so splitting into dedicated
/// read/write loops later is a mechanical change, not a redesign.
///
/// A connection <b>is</b> its buffer slot: each accepted socket rents exactly
/// one slot for its lifetime, so the slot index doubles as the connection id
/// and as the low bits of user_data. One in-flight op per connection at a
/// time (recv → echo send → recv …), which keeps routing unambiguous.
/// </summary>
internal sealed unsafe class EchoServer : IDisposable
{
    private const int MSG_NOSIGNAL = 0x4000; // don't raise SIGPIPE on send to a dead peer
    private const uint RingEntries = 4096;

    private readonly int _port;
    private readonly string? _udsName; // abstract UDS name; null => TCP on _port
    private readonly int _shardId;
    private readonly BufferPool _pool;
    private readonly Connection[] _conns;

    private IoUringOpaque* _ring;
    private int _listenFd = -1;
    private volatile bool _running;
    private uint _nextPeer; 
    private readonly EchoServer[]? _peers;

    public EchoServer(int port, int maxConnections, int bufferSize, int shardId = 0, string? udsName = null, EchoServer[]? peers = null)
    {
        _port = port;
        _udsName = udsName;
        _shardId = shardId;
        _pool = new BufferPool(maxConnections, bufferSize);
        _conns = new Connection[maxConnections];
        _peers = peers; // can include self when present
    }

    private struct Connection
    {
        public int Fd;
        public bool Active;
        public int SendOffset;    // bytes of the current payload already sent
        public int SendRemaining; // bytes still to send
    }

    public void Wake(ulong signalValue = 1)
    {
        if (_eventFd >= 0)
        {
            LibC.write(_eventFd, &signalValue, sizeof(ulong));
        }
    }

    private int _eventFd = -1;
    public void Initialize()
    {
        _eventFd = LibC.eventfd(0, LibC.EFD_NONBLOCK);
        if (_eventFd < 0)
        {
            throw new Exception($"Failed to allocate kernel eventfd resource. System error code: {Marshal.GetLastWin32Error()}");
        }

        if (_peers is null | _shardId is 0)
        {
            _listenFd = CreateListener(_port, _shardId, _udsName);
        }
    }

    public void Run()
    {
        // The ring must be created on the same thread that submits to it:
        // IORING_SETUP_SINGLE_ISSUER binds the ring to its creating task, so a
        // submit from any other thread returns -EEXIST. Initialize() runs on the
        // setup thread (shared by all shards), so allocate + queue_init here, on
        // the shard's own loop thread. The eventfd and listener stay in
        // Initialize(): shard 0 may Wake() a peer's eventfd before that peer's
        // Run() has started, so the eventfd must already exist process-wide.
        _ring = (IoUringOpaque*) NativeMemory.AlignedAlloc(RingStructSize, 64);
        NativeMemory.Clear(_ring, RingStructSize);

        int initRc = io_uring_queue_init(RingEntries, _ring, flags: IORING_SETUP_SINGLE_ISSUER | IORING_SETUP_DEFER_TASKRUN);
        if (initRc < 0) throw new InvalidOperationException($"io_uring_queue_init failed: {-initRc}");

        string where = _udsName != null ? $"abstract UDS @{_udsName}" : $":{_port}";
        Console.WriteLine($"[io_uring #{_shardId}] ring up ({RingEntries} entries), listening on {where}, " +
                          $"{_pool.SlotCount} slots x {_pool.SlotSize}B");

        _running = true;
        if (_listenFd >= 0) PostAccept();
        PostWake();

        while (_running)
        {
            // Block until at least one completion; flushes queued SQEs too.
            int rc = io_uring_submit_and_wait_blocking(_ring, 1);
            if (rc < 0)
            {
                switch (-rc)
                {
                    case LibC.EINTR: // SIGINT etc
                    case LibC.EBUSY: // couldn't submit, but we can mitigate that by doing a drain
                        break; // proceed to drain
                    default:
                        throw new InvalidOperationException($"io_uring_submit_and_wait failed: {-rc}, shard {_shardId}");
                }
            }

            if (!_running) break;
            Drain();
        }
    }

    private void Drain()
    {
        const int MaxBatch = 32;
        IoUringCqe** cqePtrs = stackalloc IoUringCqe*[MaxBatch];
        // we will want to snapshot the incoming, so that we can release the CQE *before*
        // we start processing results, which may have side-effects that need SQEs -
        // and if the SQE queue is full, the way to release it is to call submit, which will
        // report EBUSY; make space *first*
        IoUringCqe* snapshot = stackalloc IoUringCqe[MaxBatch];
        while (true)
        {
            uint count = io_uring_peek_batch_cqe(_ring, cqePtrs, MaxBatch);
            if (count == 0) break; // drained

            for (uint i = 0; i < count; i++)
            {
                snapshot[i] = *cqePtrs[i];
            }
            io_uring_cq_advance(_ring, count);
            for (uint i = 0; i < count; i++)
            {
                IoUringCqe* cqe = snapshot + i;
                switch (cqe -> Op) // Op and Slot interpret -> userdata
                {
                    case OpType.Accept: OnAccept(cqe -> res, cqe -> flags); break;
                    case OpType.Recv: OnRecv(cqe -> Slot, cqe -> res); break;
                    case OpType.Send: OnSend(cqe -> Slot, cqe -> res); break;
                    case OpType.Wake: OnWake(); break;
                    case OpType.Close: OnClose(cqe -> Slot); break;
                }
            }

            if (count < MaxBatch) break; // incomplete read; avoid a pointless extra read
        }
    }

    private void AddLocal(int fd)
    {
        if (!_running)
        {
            // we're shutting down
            LibC.close(fd);
            return;
        }
        int slot = _pool.Rent();
        if (slot < 0)
        {
            // Out of slots — refuse the connection cleanly.
            LibC.close(fd);
            return;
        }

        _conns[slot] = new Connection { Fd = fd, Active = true };
        PostRecv(slot);
    }
    
    private void OnWake()
    {
        ulong signalValue;
        // ReSharper disable once AssignmentInConditionalExpression
        if (LibC.read(_eventFd, &signalValue, sizeof(ulong)) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err != LibC.EAGAIN) throw new Exception($"Fatal error reading eventfd file descriptor. Linux errno: {err}");
        }

        // since we're here, we can check the queue even if the wake was invalid
        ProcessInbound();

        PostWake(); // re-arm: we don't multi-shot this to avoid floods
    }

    private void ProcessInbound()
    {
        while (_incoming.TryDequeue(out int fd))
        {
            AddLocal(fd);
        }
    }

    
    private void AddRemote(int fd)
    {
        _incoming.Enqueue(fd);
        Wake();
    }

    private readonly ConcurrentQueue<int> _incoming = new();

    private void OnAccept(int res, uint flags)
    {
        // Multishot accept yields one completion per new connection. If the
        // kernel dropped the arm (F_MORE clear), re-post it.
        if ((flags & IORING_CQE_F_MORE) == 0) PostAccept();

        if (res < 0) return; // transient accept error; loop stays armed

        EchoServer peer;
        if (_peers is null || ReferenceEquals(this, peer = _peers[_nextPeer++ % _peers.Length]))
        {
            AddLocal(res);
        }
        else
        {
            peer.AddRemote(res);
        }
    }

    private void OnRecv(int slot, int res)
    {
        ref Connection c = ref _conns[slot];
        if (!c.Active) return;

        if (res <= 0) { CloseConn(slot); return; } // 0 == EOF, <0 == -errno

        c.SendOffset = 0;
        c.SendRemaining = res;
        PostSend(slot);
    }

    private void OnSend(int slot, int res)
    {
        ref Connection c = ref _conns[slot];
        if (!c.Active) return;

        if (res <= 0) { CloseConn(slot); return; }

        c.SendOffset += res;
        c.SendRemaining -= res;

        if (c.SendRemaining > 0)
            PostSend(slot);       // short write: send the remainder
        else
            PostRecv(slot);       // payload flushed: back to reading
    }

    private void OnClose(int slot)
    {
        _pool.Return(slot);
    }

    // --- submission helpers ------------------------------------------------

    private IoUringSqe* Sqe()
    {
        var sqe = io_uring_get_sqe(_ring);
        if (sqe == null)
        {
            io_uring_submit(_ring); // flush to make room, then retry once
            sqe = io_uring_get_sqe(_ring);
            if (sqe == null) throw new InvalidOperationException("SQ ring exhausted");
        }
        return sqe;
    }

    private void PostWake()
    {
        var sqe = Sqe();
        io_uring_prep_poll_add(sqe, _eventFd, LibC.POLLIN);
        io_uring_sqe_set_data64(sqe, Pack(OpType.Wake, 0));
    }
    private void PostAccept()
    {
        var sqe = Sqe();
        io_uring_prep_multishot_accept(sqe, _listenFd, null, null, 0);
        io_uring_sqe_set_data64(sqe, Pack(OpType.Accept, 0));
    }

    private void PostRecv(int slot)
    {
        ref Connection c = ref _conns[slot];
        var sqe = Sqe();
        io_uring_prep_recv(sqe, c.Fd, _pool.PointerFor(slot), (nuint)_pool.SlotSize, 0);
        io_uring_sqe_set_data64(sqe, Pack(OpType.Recv, slot));
    }

    private void PostSend(int slot)
    {
        ref Connection c = ref _conns[slot];
        var sqe = Sqe();
        byte* p = _pool.PointerFor(slot) + c.SendOffset;
        io_uring_prep_send(sqe, c.Fd, p, (nuint)c.SendRemaining, MSG_NOSIGNAL);
        io_uring_sqe_set_data64(sqe, Pack(OpType.Send, slot));
    }

    private void CloseConn(int slot)
    {
        ref Connection c = ref _conns[slot];
        c.Active = false;
        var sqe = Sqe();
        io_uring_prep_close(sqe, c.Fd);
        io_uring_sqe_set_data64(sqe, Pack(OpType.Close, slot));
    }

    // --- listener setup ----------------------------------------------------

    /// <summary>
    /// Create, bind and listen. When <paramref name="udsName"/> is non-null the
    /// listener is an abstract-namespace AF_UNIX socket (loopback proxy front
    /// end: no port churn, no TIME_WAIT, no socket file); otherwise it is a TCP
    /// socket on <paramref name="port"/> with Nagle disabled. TCP_NODELAY is set
    /// on the listener because Linux propagates it to accepted sockets, which
    /// keeps it off the accept hot path; UDS has no Nagle so the option is N/A.
    /// </summary>
    internal static int CreateListener(int port, int shard, string? udsName = null)
    {
        if (udsName != null) return CreateUnixListener(shard, udsName);

        int fd = LibC.socket(LibC.AF_INET, LibC.SOCK_STREAM, LibC.IPPROTO_TCP);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "socket() failed");

        int one = 1;
        LibC.setsockopt(fd, LibC.SOL_SOCKET, LibC.SO_REUSEADDR, &one, sizeof(int));
        LibC.setsockopt(fd, LibC.SOL_SOCKET, LibC.SO_REUSEPORT, &one, sizeof(int));
        // Disable Nagle so request/response echoes are not held for coalescing
        // (Nagle + delayed-ACK is the classic ~40ms ping-pong stall). Inherited
        // by every socket accept() returns from this listener.
        LibC.setsockopt(fd, LibC.IPPROTO_TCP, LibC.TCP_NODELAY, &one, sizeof(int));

        var addr = new SockAddrIn
        {
            sin_family = LibC.AF_INET,
            sin_port = LibC.Htons((ushort)port),
            sin_addr = 0, // INADDR_ANY
        };
        if (LibC.bind(fd, &addr, 16) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "bind() failed");
        if (LibC.listen(fd, 512) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "listen() failed");

        return fd;
    }

    private static int CreateUnixListener(int shard, string name)
    {
        int fd = LibC.socket(LibC.AF_UNIX, LibC.SOCK_STREAM, 0);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"socket(AF_UNIX) failed for shard {shard}");

        // No SO_REUSEPORT/REUSEADDR: an abstract address is freed as soon as the
        // last holder closes, so there is nothing to reuse or clean up. No
        // TCP_NODELAY either — AF_UNIX has no Nagle.
        SockAddrUn addr;
        uint len = SockAddrUn.InitAbstract(&addr, name);
        if (LibC.bind(fd, &addr, len) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"bind(AF_UNIX) failed for shard {shard}");
        if (LibC.listen(fd, 512) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"listen() failed for shard {shard}");

        return fd;
    }

    public void Stop()
    {
        if (_running)
        {
            _running = false;
            Wake();
        }
    }

    public void Dispose()
    {
        if (_ring != null)
        {
            io_uring_queue_exit(_ring);
            NativeMemory.AlignedFree(_ring);
            _ring = null;
        }
        if (_listenFd >= 0) { LibC.close(_listenFd); _listenFd = -1; }
        _pool.Dispose();
    }
}