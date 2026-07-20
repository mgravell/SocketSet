#if NET // io_uring is a Linux + modern-.NET backend; compiled out of the netfx fallback build.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
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
        Send = 4,
    }

    private struct WriteState
    {
        public uint Slot;
        public int Fd;
        public int Sent;
        public int Total;
    }

    // --- slot table (1-based ids; id 0 == "none") ---
    private readonly int[] _fds;
    private readonly object?[] _userTokens;
    private readonly byte[] _slotFlags; // SocketSet.SocketFlags per slot
    private uint _clientStart;

    // --- options snapshot ---
    private readonly int _socketsPerShard;
    private readonly int _entriesPerShard;
    private readonly int _readPages;
    private readonly int _readPageSize;
    private readonly int _writeCount;
    private readonly int _writeBufSize;

    // --- created on the loop thread in Initialize() ---
    private RawIOUringRing _ring;
    private ManagedBufferPool _readBuffer;
    private WriteBufferPool _writeBuffer;
    private WriteState[] _writeState = [];
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

    public unsafe IoUringShard(SocketSetOptions options)
    {
        _socketsPerShard = options.SocketsPerShard;
        _entriesPerShard = options.EntriesPerShard;
        _readPages = options.BufferPagesPerShard;
        _readPageSize = options.BufferPageSize;
        _writeCount = options.WriteBuffersPerShard;
        _writeBufSize = options.BufferPageSize;

        _fds = new int[_socketsPerShard];
        _userTokens = new object?[_socketsPerShard];
        _slotFlags = new byte[_socketsPerShard];

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

    private uint InitClient(int fd, object? userToken, byte flags)
    {
        if (fd <= 0) Throw();
        var fds = _fds;
        var offset = Interlocked.Increment(ref _clientStart);
        for (int i = 0; i < fds.Length; i++)
        {
            var index = (uint)((i + offset) % fds.Length);
            if (Interlocked.CompareExchange(ref fds[index], fd, 0) is 0)
            {
                _slotFlags[index] = flags;
                Volatile.Write(ref _userTokens[index], userToken);
                return index + 1; // 1-based
            }
        }

        return 0; // table full

        static void Throw() => throw new ArgumentOutOfRangeException(nameof(fd), "Invalid socket handle");
    }

    private int GetFd(uint slot) => slot == 0 ? 0 : Volatile.Read(ref _fds[slot - 1]);

    private SocketSet.SocketFlags GetFlags(uint slot) => (SocketSet.SocketFlags)_slotFlags[slot - 1];

    private void CloseClient(uint slot)
    {
        if (slot == 0) return;
        uint idx = slot - 1;
        int fd = Interlocked.Exchange(ref _fds[idx], 0);
        Volatile.Write(ref _userTokens[idx], null);
        _slotFlags[idx] = 0;
        if (fd > 0) LibC.close(fd);
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

        uint slot = InitClient(fd, userToken, 0);
        if (slot == 0)
        {
            LibC.close(fd);
            throw new InvalidOperationException("Shard socket table is full.");
        }

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
        var sqe = new LibC.io_uring_sqe
        {
            opcode = LibC.IORING_OP_RECV,
            fd = fd,
            len = (uint)_readBuffer.BufferSize,
            flags = LibC.IOSQE_BUFFER_SELECT,
            buf_index = _readBuffer.GroupId, // buf_index aliases buf_group
            user_data = Pack(Op.Recv, slot),
        };
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

    /// <summary>Copy <paramref name="len"/> bytes from a read buffer into a leased write
    /// buffer and send it. Closes the connection if no write buffer is available.</summary>
    private unsafe void SendResponse(uint slot, int fd, byte* src, int len)
    {
        if ((GetFlags(slot) & SocketSet.SocketFlags.SendClosed) != 0) return;
        if (!_writeBuffer.TryLease(out int wi, out byte* wp))
        {
            // Pool exhausted: the safe thing is to drop the connection rather than stall it.
            System.Diagnostics.Debug.WriteLine("Write buffer pool exhausted; closing connection.");
            CloseClient(slot);
            return;
        }

        Buffer.MemoryCopy(src, wp, _writeBuffer.BufferSize, len);
        SubmitSend(slot, fd, wi, wp, len);
    }

    // =====================================================================
    // Initialization / teardown
    // =====================================================================

    private unsafe void Initialize()
    {
        _ring = new RawIOUringRing((uint)_entriesPerShard);
        _readBuffer = new ManagedBufferPool(_ring.RingFd, entries: _readPages, bufSize: _readPageSize);
        _writeBuffer = new WriteBufferPool(_writeCount, _writeBufSize);
        _writeState = new WriteState[_writeCount];
        _ringReady = true;
    }

    private unsafe void Cleanup()
    {
        _ringReady = false;
        foreach (var fd in _listeners.Keys) LibC.close(fd);
        _listeners.Clear();

        for (int i = 0; i < _fds.Length; i++)
        {
            int fd = Interlocked.Exchange(ref _fds[i], 0);
            if (fd > 0) LibC.close(fd);
        }

        _readBuffer.Dispose();
        _writeBuffer.Dispose();
        _ring.Dispose();

        if (_eventFd > 0) LibC.close(_eventFd);
        if (_connectAddrs != null) { NativeMemory.Free(_connectAddrs); _connectAddrs = null; }
        if (_wakeBuf != null) { NativeMemory.Free(_wakeBuf); _wakeBuf = null; }
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
        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);

        var ctx = new SocketSet.AcceptContext(
            SocketSet.SocketFlags.None, userToken, wp, leased ? _writeBuffer.BufferSize : 0);
        Parent.OnAccept(ref ctx);

        uint slot = InitClient(newFd, ctx.UserToken, (byte)ctx.Flags);
        if (slot == 0)
        {
            if (leased) _writeBuffer.Release(wi);
            LibC.close(newFd);
            return;
        }

        if ((ctx.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0)
            ArmRecv(slot, newFd);

        int sb = ctx.SendBytes;
        if (leased && sb > 0) SubmitSend(slot, newFd, wi, wp, sb);
        else if (leased) _writeBuffer.Release(wi);
    }

    private unsafe void HandleConnect(int res, uint slot)
    {
        int fd = GetFd(slot);
        if (res == 0 && fd != 0)
        {
            bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);

            object? token = Volatile.Read(ref _userTokens[slot - 1]);
            var ctx = new SocketSet.ConnectContext(
                SocketSet.SocketFlags.None, token, wp, leased ? _writeBuffer.BufferSize : 0);
            Parent.OnConnect(ref ctx);
            Volatile.Write(ref _userTokens[slot - 1], ctx.UserToken);
            _slotFlags[slot - 1] = (byte)ctx.Flags;

            if ((ctx.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0)
                ArmRecv(slot, fd);

            int sb = ctx.SendBytes;
            if (leased && sb > 0) SubmitSend(slot, fd, wi, wp, sb);
            else if (leased) _writeBuffer.Release(wi);
        }
        else
        {
            CloseClient(slot);
        }
    }

    private unsafe void HandleRecv(int res, uint flags, uint slot)
    {
        bool hasBuf = (flags & LibC.IORING_CQE_F_BUFFER) != 0;
        ushort bid = (ushort)(flags >> LibC.IORING_CQE_BUFFER_SHIFT);
        int fd = GetFd(slot);

        if (res > 0)
        {
            if (fd != 0)
            {
                byte* rp = _readBuffer.GetBufferAddress(bid);
                object? token = Volatile.Read(ref _userTokens[slot - 1]);
                var ctx = new SocketSet.ReceiveContext(
                    GetFlags(slot), token, rp, _readBuffer.BufferSize, res);
                Parent.OnReceive(ref ctx);
                Volatile.Write(ref _userTokens[slot - 1], ctx.UserToken);
                _slotFlags[slot - 1] = (byte)ctx.Flags;

                int rb = ctx.ResponseBytes;
                if (rb > 0) SendResponse(slot, fd, rp, rb); // copies out of rp before we release it
            }

            if (hasBuf) _readBuffer.ReleaseBuffer(bid);

            // Single-shot recv: re-arm unless input was closed or the slot went away.
            if (GetFd(slot) != 0 && (GetFlags(slot) & SocketSet.SocketFlags.ReceiveClosed) == 0)
                ArmRecv(slot, fd);
        }
        else if (res == -LibC.ENOBUFS)
        {
            // No buffer was available; nothing was consumed. Re-arm — buffers free up.
            if (fd != 0) ArmRecv(slot, fd);
        }
        else
        {
            // res == 0 (peer EOF) or a negative errno.
            if (hasBuf) _readBuffer.ReleaseBuffer(bid);
            CloseClient(slot);
        }
    }

    private unsafe void HandleSend(int res, uint slot, int writeIndex)
    {
        ref WriteState ws = ref _writeState[writeIndex];

        if (res < 0)
        {
            _writeBuffer.Release(writeIndex);
            CloseClient(slot);
            return;
        }

        ws.Sent += res;
        if (ws.Sent < ws.Total && res > 0)
        {
            // Partial write: resubmit the remainder from the same buffer.
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
            CloseClient(slot);
            return;
        }

        int fd = GetFd(slot);
        if (fd == 0) { _writeBuffer.Release(writeIndex); return; }

        // Full write completed. Offer the now-free buffer to OnWrite: a handler can pipeline
        // the next message straight back into it (no release/re-lease). Only if it declines
        // do we hand the buffer back to the pool.
        byte* wp = _writeBuffer.Address(writeIndex);
        object? token = Volatile.Read(ref _userTokens[slot - 1]);
        var ctx = new SocketSet.WriteContext(GetFlags(slot), token, wp, _writeBuffer.BufferSize);
        Parent.OnWrite(ref ctx);
        Volatile.Write(ref _userTokens[slot - 1], ctx.UserToken);
        _slotFlags[slot - 1] = (byte)ctx.Flags;

        int next = ctx.SendBytes;
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
            Submit(sqe);
        }
        else
        {
            _writeBuffer.Release(writeIndex);
        }
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
