using System.Runtime.InteropServices;
using FastNet.Buffers;
using FastNet.Native;
using static FastNet.Native.LibUring;
using static FastNet.Transport.IoUringCqeExtensions;

namespace FastNet.Transport;

/// <summary>
/// SKETCH — echo transport built on <b>multishot recv + a provided buffer
/// ring</b>, the counterpart to <see cref="EchoServer"/> (which uses classic
/// one-recv-per-completion into a fixed per-connection buffer). Here the kernel
/// keeps a single recv armed per connection and hands us a fresh buffer from a
/// registered ring on every arrival, so recv is decoupled from send: several
/// recv completions can land before their echo-sends drain.
///
/// This is also the shape RIO will want: a pre-registered buffer block, a
/// completion queue we drain, and buffers recycled back to the pool once the
/// kernel is done with them — so getting the ownership model right here pays
/// forward to the Windows backend.
///
/// Buffer lifecycle: a buffer is "owned by the kernel" from the moment it is in
/// the ring until a recv completion hands it to us; we then own it until the
/// echo-send for it completes, at which point we recycle it back into the ring.
/// A buffer id (bid) is therefore in flight for at most one send at a time.
///
/// KNOWN SIMPLIFICATIONS (call out before trusting the numbers):
///  * ENOBUFS re-arm is naive — if the pool is genuinely drained it can spin.
///  * No multishot-accept backpressure if connections outnumber conn slots.
///  * Single instance; sharding is the same <see cref="ShardedEchoServer"/>
///    wrapper once correctness is confirmed.
/// </summary>
internal sealed unsafe class RingEchoServer : IDisposable
{
    private const int MSG_NOSIGNAL = 0x4000;
    private const int ENOBUFS = 105;
    private const uint RingEntries = 4096;
    private const int BufGroupId = 0;

    private readonly int _port;
    private readonly string? _udsName; // abstract UDS name; null => TCP on _port
    private readonly int _shardId;
    private readonly BufferPool _pool;   // backing store for the ring buffers; bid == slot index
    private readonly int _bufMask;       // ring size - 1 (ring size is a power of two)

    // Per-connection state, indexed by a small connection id packed into user_data.
    private readonly int[] _connFd;
    private readonly bool[] _connActive;
    private readonly int[] _freeConn;
    private int _freeConnCount;

    // Per-buffer in-flight send bookkeeping (a bid is in exactly one send at a time).
    private readonly int[] _sendOffset;
    private readonly int[] _sendRemaining;

    private IoUringOpaque* _ring;
    private IoUringBufRing* _bufRing;
    private int _listenFd = -1;
    private int _eventFd = -1;
    private volatile bool _running;

    public RingEchoServer(int port, int maxConnections, int bufferSize, int shardId = 0, string? udsName = null)
    {
        _port = port;
        _udsName = udsName;
        _shardId = shardId;

        // Buffer-ring entry count must be a power of two. Size it to the max
        // connections rounded up — one buffer per connection is enough for the
        // strict request/response echo pattern, with slack from the rounding.
        int entries = 1;
        while (entries < maxConnections) entries <<= 1;
        _pool = new BufferPool(entries, bufferSize);
        _bufMask = entries - 1;

        _connFd = new int[maxConnections];
        _connActive = new bool[maxConnections];
        _freeConn = new int[maxConnections];
        for (int i = 0; i < maxConnections; i++) _freeConn[i] = maxConnections - 1 - i;
        _freeConnCount = maxConnections;

        _sendOffset = new int[entries];
        _sendRemaining = new int[entries];
    }

    public void Initialize()
    {
        // Wake channel: an eventfd the loop keeps a poll armed on, so Stop() can
        // unblock the drain thread parked in io_uring_submit_and_wait (which has
        // no other reason to return once the load stops).
        _eventFd = LibC.eventfd(0, LibC.EFD_NONBLOCK);
        if (_eventFd < 0)
            throw new InvalidOperationException($"eventfd failed: {Marshal.GetLastPInvokeError()}");

        _listenFd = EchoServer.CreateListener(_port, 0, _udsName);

        _ring = (IoUringOpaque*) NativeMemory.AlignedAlloc(RingStructSize, 64);
        NativeMemory.Clear(_ring, RingStructSize);
        int rc = io_uring_queue_init(RingEntries, _ring, IORING_SETUP_SINGLE_ISSUER | IORING_SETUP_DEFER_TASKRUN);
        if (rc < 0) throw new InvalidOperationException($"io_uring_queue_init failed: {-rc}");

        // Register the provided-buffer ring and publish every buffer into it.
        int ret;
        _bufRing = io_uring_setup_buf_ring(_ring, (uint)_pool.SlotCount, BufGroupId, 0, &ret);
        if (_bufRing == null) throw new InvalidOperationException($"io_uring_setup_buf_ring failed: {-ret}");

        for (int bid = 0; bid < _pool.SlotCount; bid++)
            io_uring_buf_ring_add(_bufRing, _pool.PointerFor(bid), (uint)_pool.SlotSize, (ushort)bid, _bufMask, bid);
        io_uring_buf_ring_advance(_bufRing, _pool.SlotCount);

        string where = _udsName != null ? $"abstract UDS @{_udsName}" : $":{_port}";
        Console.WriteLine($"[io_uring-ring #{_shardId}] ring up ({RingEntries} entries), listening on {where}, " +
                          $"{_pool.SlotCount} provided buffers x {_pool.SlotSize}B");
    }

    public void Run()
    {
        _running = true;
        PostAccept();
        PostWake();

        while (_running)
        {
            int rc = io_uring_submit_and_wait(_ring, 1);
            if (rc < 0)
            {
                if (-rc == 4 /* EINTR */) continue;
                throw new InvalidOperationException($"io_uring_submit_and_wait failed: {-rc}");
            }
            if (!_running) break; // Stop() woke us via the eventfd
            Drain();
        }
    }

    private void Drain()
    {
        while (true)
        {
            IoUringCqe* cqe;
            if (io_uring_peek_cqe(_ring, &cqe) != 0 || cqe == null) break;

            OpType op = cqe->Op;
            int packed = cqe->Slot;   // low 32: conn id (low 16) + bid (high 16) for sends
            int res = cqe->res;
            uint flags = cqe->flags;

            io_uring_cq_advance(_ring, 1);

            switch (op)
            {
                case OpType.Accept: OnAccept(res, flags); break;
                case OpType.Recv: OnRecv(packed & 0xFFFF, res, flags); break;
                case OpType.Send: OnSend(packed & 0xFFFF, packed >> 16, res); break;
                case OpType.Wake: OnWake(); break;
            }
        }
    }

    private void OnAccept(int res, uint flags)
    {
        if ((flags & IORING_CQE_F_MORE) == 0) PostAccept();
        if (res < 0) return;

        int fd = res;
        if (_freeConnCount == 0) { LibC.close(fd); return; }  // out of conn slots
        int conn = _freeConn[--_freeConnCount];
        _connFd[conn] = fd;
        _connActive[conn] = true;
        PostRecvMultishot(conn);
    }

    private void OnRecv(int conn, int res, uint flags)
    {
        if (!_connActive[conn]) return;

        // The multishot recv was terminated by the kernel (F_MORE clear); we
        // must re-arm it after handling whatever this completion carried.
        bool armed = (flags & IORING_CQE_F_MORE) != 0;

        if (res == 0) { CloseConn(conn); return; }            // EOF
        if (res < 0)
        {
            if (-res == ENOBUFS) { PostRecvMultishot(conn); return; } // no buffer free; retry (see note)
            CloseConn(conn);
            return;
        }
        if ((flags & IORING_CQE_F_BUFFER) == 0) { CloseConn(conn); return; } // unexpected: no buffer

        int bid = (int)(flags >> IORING_CQE_BUFFER_SHIFT);
        _sendOffset[bid] = 0;
        _sendRemaining[bid] = res;
        PostSend(conn, bid);

        if (!armed) PostRecvMultishot(conn); // re-arm the stream for more data
    }

    private void OnSend(int conn, int bid, int res)
    {
        if (res <= 0) { RecycleBuffer(bid); if (_connActive[conn]) CloseConn(conn); return; }

        _sendOffset[bid] += res;
        _sendRemaining[bid] -= res;

        if (_sendRemaining[bid] > 0)
            PostSend(conn, bid);      // short write: send the remainder from the same buffer
        else
            RecycleBuffer(bid);       // echoed in full: give the buffer back to the kernel
    }

    // --- submission helpers ------------------------------------------------

    private IoUringSqe* Sqe()
    {
        var sqe = io_uring_get_sqe(_ring);
        if (sqe == null)
        {
            io_uring_submit(_ring);
            sqe = io_uring_get_sqe(_ring);
            if (sqe == null) throw new InvalidOperationException("SQ ring exhausted");
        }
        return sqe;
    }

    private void PostAccept()
    {
        var sqe = Sqe();
        io_uring_prep_multishot_accept(sqe, _listenFd, null, null, 0);
        io_uring_sqe_set_data64(sqe, Pack(OpType.Accept, 0));
    }

    // Arm a one-shot poll on the eventfd. Not multishot: a single wake is all we
    // need to break the submit_and_wait, and OnWake re-arms it.
    private void PostWake()
    {
        var sqe = Sqe();
        io_uring_prep_poll_add(sqe, _eventFd, LibC.POLLIN);
        io_uring_sqe_set_data64(sqe, Pack(OpType.Wake, 0));
    }

    private void OnWake()
    {
        ulong signalValue;
        if (LibC.read(_eventFd, &signalValue, sizeof(ulong)) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err != LibC.EAGAIN) throw new Exception($"eventfd read failed: {err}");
        }
        PostWake(); // re-arm; the _running re-check in Run() handles shutdown
    }

    private void PostRecvMultishot(int conn)
    {
        var sqe = Sqe();
        // buf/len ignored: the buffer is selected from BufGroupId at completion.
        io_uring_prep_recv_multishot(sqe, _connFd[conn], null, 0, 0);
        io_uring_sqe_set_flags(sqe, IOSQE_BUFFER_SELECT);
        io_uring_sqe_set_buf_group(sqe, BufGroupId);
        io_uring_sqe_set_data64(sqe, Pack(OpType.Recv, conn));
    }

    private void PostSend(int conn, int bid)
    {
        var sqe = Sqe();
        byte* p = _pool.PointerFor(bid) + _sendOffset[bid];
        io_uring_prep_send(sqe, _connFd[conn], p, (nuint)_sendRemaining[bid], MSG_NOSIGNAL);
        // Pack conn id (low 16) + bid (high 16) so the send completion can both
        // recycle the buffer and, on a short write, resubmit against the fd.
        io_uring_sqe_set_data64(sqe, Pack(OpType.Send, conn | (bid << 16)));
    }

    private void RecycleBuffer(int bid)
    {
        io_uring_buf_ring_add(_bufRing, _pool.PointerFor(bid), (uint)_pool.SlotSize, (ushort)bid, _bufMask, 0);
        io_uring_buf_ring_advance(_bufRing, 1);
    }

    private void CloseConn(int conn)
    {
        if (!_connActive[conn]) return;
        _connActive[conn] = false;
        LibC.close(_connFd[conn]);   // sketch: synchronous close (EchoServer defers via the ring)
        _freeConn[_freeConnCount++] = conn;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        Wake(); // unblock the drain thread parked in submit_and_wait
    }

    // Write to the eventfd so the armed poll completes and the loop wakes. Safe
    // to call from another thread (e.g. the Ctrl+C handler): it only touches the
    // eventfd, never the ring's SQ.
    private void Wake(ulong signalValue = 1)
    {
        if (_eventFd >= 0) LibC.write(_eventFd, &signalValue, sizeof(ulong));
    }

    public void Dispose()
    {
        if (_bufRing != null && _ring != null)
        {
            io_uring_free_buf_ring(_ring, _bufRing, (uint)_pool.SlotCount, BufGroupId);
            _bufRing = null;
        }
        if (_ring != null)
        {
            io_uring_queue_exit(_ring);
            NativeMemory.AlignedFree(_ring);
            _ring = null;
        }
        if (_listenFd >= 0) { LibC.close(_listenFd); _listenFd = -1; }
        if (_eventFd >= 0) { LibC.close(_eventFd); _eventFd = -1; }
        _pool.Dispose();
    }
}
