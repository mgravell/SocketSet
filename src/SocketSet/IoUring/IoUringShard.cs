#if NET // io_uring is a Linux + modern-.NET backend; compiled out of the netfx fallback build.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SocketSets.Native;

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
    private uint _clientStart;

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

    private WriteBufferPool _writeBuffer;

    // Out-of-band write pool: leased from arbitrary threads (the IBufferWriter/Flush path), so guarded
    // by _obGate. Kept distinct from _writeBuffer so the IO-thread echo path never takes a lock.
    private WriteBufferPool _obWriteBuffer;
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

    public unsafe IoUringShard(SocketSetOptions options)
    {
        _socketsPerShard = options.SocketsPerShard;
        _entriesPerShard = options.EntriesPerShard;
        _readPages = options.BufferPagesPerShard;
        _readPageSize = options.BufferPageSize;
        _writeCount = options.WriteBuffersPerShard;
        _writeBufSize = options.BufferPageSize;
        _obWriteCount = options.OutOfBandWriteBuffersPerShard;

        _conns = new IoUringConnection[_socketsPerShard];
        for (int i = 0; i < _conns.Length; i++)
            _conns[i] = new IoUringConnection(this, (uint)i + 1);

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

    /// <summary>Claim a free slot for <paramref name="fd"/>. Lock-free (CAS on the pooled
    /// connection's Fd), so callable from the loop thread (accept) or an arbitrary thread
    /// (connect). Returns null if the table is full.</summary>
    private IoUringConnection? InitClient(int fd, object? userToken, SocketSet.SocketFlags flags)
    {
        if (fd <= 0) Throw();
        var conns = _conns;
        var offset = (uint)Interlocked.Increment(ref _clientStart);
        for (int i = 0; i < conns.Length; i++)
        {
            var conn = conns[(i + offset) % (uint)conns.Length];
            if (Interlocked.CompareExchange(ref conn.Fd, fd, 0) is 0)
            {
                conn.UserToken = userToken;
                conn.Flags = flags;
                conn.SendBusy = false;
                conn.Pending?.Clear();
                // Publish a fresh generation last: any out-of-band send captured against the
                // previous tenant now mismatches and is dropped rather than misdelivered.
                Volatile.Write(ref conn.Generation, conn.Generation + 1);
                return conn;
            }
        }

        return null; // table full

        static void Throw() => throw new ArgumentOutOfRangeException(nameof(fd), "Invalid socket handle");
    }

    private int GetFd(uint slot) => slot == 0 ? 0 : Volatile.Read(ref _conns[slot - 1].Fd);

    private SocketSet.SocketFlags GetFlags(uint slot) => _conns[slot - 1].Flags;

    /// <summary>Begin tearing a connection down (loop thread). Idempotent. Fires OnClosed now, but
    /// does NOT close the fd or free the slot yet — it forces the in-flight ops to drain (shutdown +
    /// ASYNC_CANCEL) and the slot is finalized only once they have (see <see cref="TryFinalize"/>), so
    /// no stale completion can ever land on a re-tenanted slot.</summary>
    private void CloseClient(uint slot)
    {
        if (slot == 0) return;
        var conn = _conns[slot - 1];
        if (conn.Fd == 0 || conn.Closing) return; // not open / already tearing down
        conn.Closing = true;

        // Notify the app once, while the identity is valid, if it saw the connection open.
        if (conn.Opened)
        {
            conn.Opened = false;
            try { Parent.OnClosed(conn); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        // Force the drain: shutdown(RDWR) sends the FIN and EOFs the armed recv; ASYNC_CANCEL(FD|ALL)
        // cancels the recv + any in-flight send so nothing lingers. The fd stays open (cancel matches
        // by fd) and the slot stays claimed until every op is reaped.
        LibC.shutdown(conn.Fd, LibC.SHUT_RDWR);
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
                if (job.Chain.Count > 0) ReleaseChainBuffers(job.Chain);
            }
        }

        int fd = conn.Fd;
        conn.UserToken = null;
        conn.Flags = 0;
        conn.SendBusy = false;
        conn.RecvArmed = false;
        conn.Closing = false;
        if (fd > 0) LibC.close(fd);
        Volatile.Write(ref conn.Fd, 0); // publish free last
    }

    // =====================================================================
    // Public entry points (called from arbitrary threads)
    // =====================================================================

    public override void Listen(EndPoint endpoint, object? userToken, bool local)
    {
        int fd = IoUringFactory.Bind(endpoint);
        _listeners[fd] = userToken; // default token for connections accepted here
        EnqueueAccept(fd, local);
    }

    public override unsafe void Connect(EndPoint endpoint, object? userToken)
    {
        int fd = endpoint switch
        {
            IPEndPoint => LibC.socket(LibC.AF_INET, LibC.SOCK_STREAM, LibC.IPPROTO_TCP),
            UnixDomainSocketEndPoint => LibC.socket(LibC.AF_UNIX, LibC.SOCK_STREAM, 0),
            _ => throw new NotSupportedException($"{nameof(Connect)} on {endpoint.GetType().Name} is not supported."),
        };
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "socket() failed");

        var conn = InitClient(fd, userToken, SocketSet.SocketFlags.None);
        if (conn is null)
        {
            LibC.close(fd);
            throw new InvalidOperationException("Shard socket table is full.");
        }

        uint slot = conn.Slot;

        // Build the target sockaddr into this slot's stable native storage; the
        // kernel dereferences it asynchronously once the CONNECT SQE is submitted.
        byte* addrPtr = _connectAddrs + (nint)(slot - 1) * AddrStride;
        uint addrLen;
        switch (endpoint)
        {
            case IPEndPoint ip:
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
                break;
            }
            case UnixDomainSocketEndPoint uds:
                // SockAddrUn.Init zeroes the struct and maps a leading '@' to the
                // abstract namespace ('\0'); addrLen bounds the abstract name.
                addrLen = LibC.SockAddrUn.Init((LibC.SockAddrUn*)addrPtr, uds.ToString());
                break;
            default:
                LibC.close(fd);
                throw new NotSupportedException(endpoint.GetType().Name);
        }

        var sqe = new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_CONNECT,
            fd = fd,
            addr = (ulong)addrPtr,
            off = addrLen,
            user_data = Pack(Op.Connect, slot),
        };
        Enqueue(sqe);
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
            len = (uint)_readBuffer.BufferSize,
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

        int fd = conn.Fd;

        // One writev is capped at IovMax iovecs; split a larger chain into ordered sub-chains that
        // serialize through the per-connection send queue (rare — needs >IovMax page-sized segments).
        if (chain.Count <= IovMax)
        {
            DispatchChain(conn, fd, chain);
            return;
        }

        for (int start = 0; start < chain.Count; start += IovMax)
        {
            int len = Math.Min(IovMax, chain.Count - start);
            var sub = new OutChain();
            sub.Add(in chain, start, len);
            DispatchChain(conn, fd, sub);
        }
    }

    private void DispatchChain(IoUringConnection conn, int fd, in OutChain chain)
    {
        // Ordering across a connection's byte stream is enforced by a single in-flight send per
        // connection (SendBusy) — response-writes and flushed writes share the gate, and the next is submitted
        // only when the current completes — rather than IO_LINK/IO_DRAIN. Separate connections still
        // submit concurrently/in bulk. Chosen for simplicity (esp. -ECANCELED handling on IO_LINK);
        // IO_LINK may earn its keep in massive-throughput scenarios later, but for now: KISS.
        if (conn.SendBusy)
        {
            (conn.Pending ??= new()).Enqueue(new PendingJob { Chain = chain });
            return;
        }

        conn.SendBusy = true;
        SubmitChain(conn, fd, chain);
    }

    /// <summary>Send a flushed chain as one IORING_OP_WRITEV: pool-page segments contribute their
    /// native address; POH overflow segments their (stable, pinned) array address. State lives on the
    /// connection (one send in flight at a time). Assumes SendBusy is already claimed.</summary>
    private unsafe void SubmitChain(IoUringConnection conn, int fd, in OutChain chain)
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
        }

        conn.WriteV = new WriteVState
        {
            Iov = iov, TotalIov = n, Cursor = 0, Sent = 0, Total = total, Chain = chain,
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
    private unsafe void ReleaseWriteV(IoUringConnection conn)
    {
        ref var ws = ref conn.WriteV;
        if (ws.Iov != null) NativeMemory.Free(ws.Iov);
        ReleaseChainBuffers(ws.Chain);
        conn.WriteV = default;
    }

    // Submit a queued job (leasing fresh IO-pool buffers for a segment). SendBusy stays set.
    private unsafe void SubmitPendingJob(IoUringConnection conn, uint slot, int fd, in PendingJob job)
    {
        if (job.Chain.Count > 0)
        {
            SubmitChain(conn, fd, job.Chain);
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
        SubmitSend(slot, fd, wi, wp, seg.Count);
    }

    // Pick the next queued job, or go idle. SendBusy stays set iff a job was dispatched.
    private void DrainNext(IoUringConnection conn, uint slot, int fd)
    {
        if (conn.Pending is { Count: > 0 } pending) SubmitPendingJob(conn, slot, fd, pending.Dequeue());
        else conn.SendBusy = false;
    }

    // =====================================================================
    // Initialization / teardown
    // =====================================================================

    private unsafe void Initialize()
    {
        _ring = new RawIOUringRing((uint)_entriesPerShard);
        _readBuffer = new ManagedBufferPool(_ring.RingFd, entries: _readPages, bufSize: _readPageSize);
        _writeBuffer = new WriteBufferPool(_writeCount, _writeBufSize);
        _obWriteBuffer = new WriteBufferPool(_obWriteCount, _writeBufSize);
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

            // Issue any out-of-band writes flushed in from other threads.
            while (_flush.TryDequeue(out var f))
                PumpFlush(f.Slot, f.Generation, f.Chain);

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

            ProcessAvailableCompletions();
        }
    }

    protected override void OnShutdown() => Cleanup();

    // =====================================================================
    // Completion processing
    // =====================================================================

    private unsafe void ProcessAvailableCompletions()
    {
        uint head = *_ring.CqHead;
        uint tail = Volatile.Read(ref *_ring.CqTail);

        while (head != tail)
        {
            LibC.io_uring_cqe* cqe = &_ring.Cqes[head & _ring.CqMask];
            var (op, id, aux) = Unpack(cqe->user_data);
            int res = cqe->res;
            uint flags = cqe->flags;
            head++; // advance our local view before dispatching (handlers may submit)

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
            }
        }

        // Release-store our consumed head so the kernel can reuse CQ slots.
        Volatile.Write(ref *_ring.CqHead, head);
    }

    private void HandleAccept(int res, uint flags, int listenerFd, bool local)
    {
        if (res >= 0)
        {
            int newFd = res;
            object? defaultToken = _listeners.TryGetValue(listenerFd, out var t) ? t : null;
            if (local)
            {
                // reuse-port: each shard has its own listener, so it is already balanced.
                AdoptAccepted(newFd, defaultToken);
            }
            else
            {
                // Single listener (e.g. UDS): all accepts land on this one shard, so
                // bounce each connection onto a round-robin shard to spread the load.
                var target = (IoUringShard)Parent.RoundRobin();
                target.EnqueueInbound(newFd, defaultToken);
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
            LibC.close(newFd);
            return;
        }

        uint slot = conn.Slot;

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
        byte* rp = _readBuffer.GetBufferAddress(bid);
        var ctx = new SocketSet.ReceiveContext(conn, rp, _readBuffer.BufferSize, res);
        Parent.OnReceive(ref ctx);

        int rb = ctx.ResponseBytes;
        if (rb <= 0 || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0) return false;

        if (conn.SendBusy)
        {
            // A send is already in flight (e.g. an out-of-band Send, or a still-draining pipeline):
            // serialize behind it by copying the response out and queueing. The read buffer is not
            // borrowed, so the caller releases it as usual.
            var copy = new byte[rb];
            Marshal.Copy((IntPtr)rp, copy, 0, rb);
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
                SubmitChain(conn, fd, job.Chain); // SendBusy stays set
                return;
            }

            // A queued echo segment: reuse this just-freed page for it.
            Marshal.Copy(job.Seg.Array!, job.Seg.Offset, (IntPtr)wp, job.Seg.Count);
            next = job.Seg.Count;
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

        // Full payload sent.
        ReleaseWriteV(conn);
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