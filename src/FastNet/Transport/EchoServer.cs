using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FastNet.Buffers;
using FastNet.Native;
using static FastNet.Native.LibUring;
using static FastNet.Transport.IoUringCqeExtensions;

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
    private readonly int _shardId;
    private readonly BufferPool _pool;
    private readonly Connection[] _conns;

    private void* _ring;
    private int _listenFd = -1;
    private volatile bool _running;

    public EchoServer(int port, int maxConnections, int bufferSize, int shardId = 0)
    {
        _port = port;
        _shardId = shardId;
        _pool = new BufferPool(maxConnections, bufferSize);
        _conns = new Connection[maxConnections];
    }

    private struct Connection
    {
        public int Fd;
        public bool Active;
        public int SendOffset;    // bytes of the current payload already sent
        public int SendRemaining; // bytes still to send
    }

    public void Initialize()
    {
        _listenFd = CreateListener(_port);

        _ring = NativeMemory.AlignedAlloc(RingStructSize, 64);
        NativeMemory.Clear(_ring, RingStructSize);

        int rc = io_uring_queue_init(RingEntries, _ring, 0);
        if (rc < 0) throw new InvalidOperationException($"io_uring_queue_init failed: {-rc}");

        Console.WriteLine($"[io_uring #{_shardId}] ring up ({RingEntries} entries), listening on :{_port}, " +
                          $"{_pool.SlotCount} slots x {_pool.SlotSize}B");
    }

    public void Run()
    {
        _running = true;
        PostAccept();

        while (_running)
        {
            // Block until at least one completion; flushes queued SQEs too.
            int rc = io_uring_submit_and_wait(_ring, 1);
            if (rc < 0)
            {
                if (-rc == 4 /* EINTR */) continue;
                throw new InvalidOperationException($"io_uring_submit_and_wait failed: {-rc}");
            }
            Drain();
        }
    }

    private void Drain()
    {
        while (true)
        {
            IoUringCqe* cqe;
            if (io_uring_peek_cqe(_ring, &cqe) != 0 || cqe == null) break;

            // Snapshot the fields before advancing: cq_advance frees the slot
            // back to the kernel, which may overwrite this CQE.
            OpType op = cqe->Op;
            int slot = cqe->Slot;
            int res = cqe->res;
            uint flags = cqe->flags;

            // Consume this CQE before we post follow-up SQEs.
            io_uring_cq_advance(_ring, 1);

            switch (op)
            {
                case OpType.Accept: OnAccept(res, flags); break;
                case OpType.Recv: OnRecv(slot, res); break;
                case OpType.Send: OnSend(slot, res); break;
                case OpType.Close: OnClose(slot); break;
            }
        }
    }

    private void OnAccept(int res, uint flags)
    {
        // Multishot accept yields one completion per new connection. If the
        // kernel dropped the arm (F_MORE clear), re-post it.
        if ((flags & IORING_CQE_F_MORE) == 0) PostAccept();

        if (res < 0) return; // transient accept error; loop stays armed

        int newFd = res;
        int slot = _pool.Rent();
        if (slot < 0)
        {
            // Out of slots — refuse the connection cleanly.
            LibC.close(newFd);
            return;
        }

        _conns[slot] = new Connection { Fd = newFd, Active = true };
        PostRecv(slot);
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

    internal static int CreateListener(int port)
    {
        int fd = LibC.socket(LibC.AF_INET, LibC.SOCK_STREAM, LibC.IPPROTO_TCP);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "socket() failed");

        int one = 1;
        LibC.setsockopt(fd, LibC.SOL_SOCKET, LibC.SO_REUSEADDR, &one, sizeof(int));
        LibC.setsockopt(fd, LibC.SOL_SOCKET, LibC.SO_REUSEPORT, &one, sizeof(int));

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

    public void Stop() => _running = false;

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
