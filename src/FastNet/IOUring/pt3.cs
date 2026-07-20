using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FastNet.IOUring;

class IOUringShard(IOUringEngine engine, int id) : IDisposable
{
    public int Id => id;
    private readonly ConcurrentDictionary<ushort, IOUringSocket> _clients = [];
    private readonly ConcurrentQueue<io_uring_sqe> _pendingCqes = [];
    private readonly ConcurrentQueue<ushort> _availableIds = [];
    private ushort _lastId; 

    private RawIOUringRing _ring;
    private ManagedBufferPool _bufferPool;
    
    private int _eventFd;
    private volatile bool _isAlive = true;

    private static ulong PackUserData(ushort contextId, IOUringOperation op)
        => ((ulong)contextId << 16) | (byte)op;

    private static (ushort ContextId, IOUringOperation Op) UnpackUserData(ulong userData)
        => ((ushort)(userData >> 16), (IOUringOperation)(byte)userData);

    private unsafe void Initialize()
    {
        // 1. Fire up our memory-mapped ring structures
        _ring = new RawIOUringRing(256);
        _bufferPool = new ManagedBufferPool(_ring.RingFd, entries: 256, bufSize: 4096);

        // 2. Instantiate a UNIQUE non-blocking eventfd for this shard
        _eventFd = LinuxSyscall.eventfd(0, 0x800);
        if (_eventFd < 0) throw new InvalidOperationException("Failed to allocate shard eventfd");

        // 3. Register the eventfd directly with this specific ring.
        // The kernel now explicitly maps this file descriptor to this ring context!
        var pinned = _eventFd;
        int registrationResult = LinuxSyscall.io_uring_register(
            LinuxSyscall.SYS_io_uring_register,
            _ring.RingFd,
            LinuxSyscall.IORING_REGISTER_EVENTFD,
            &pinned, // Pointer to our eventfd integer handle (address only needed for this call)
            1 // Number of descriptors being registered
        );

        if (registrationResult < 0)
        {
            throw new InvalidOperationException($"Failed to register eventfd to ring: {Marshal.GetLastPInvokeError()}");
        }
    }

    public unsafe void Run()
    {
        Initialize(); // this needs to happen from the same thread as our IO loop

        // Allocate our eventfd read target on the stack.
        // Stack addresses are implicitly pinned for the entire duration of this loop method.
        ulong localEventBuffer = 0;

        while (_isAlive)
        {
            // 1. Sleep cleanly here. 
            // Wakes up if a network operation completes OR if Poke() writes to our registered eventfd.
            int sleepResult = LinuxSyscall.io_uring_enter_blocking(
                LinuxSyscall.SYS_io_uring_enter, _ring.RingFd, 0, 1, LinuxSyscall.IORING_ENTER_GETEVENTS, null, 0);

            if (sleepResult < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == 4) continue; // 4 == EINTR (Interrupted system call), loop again safely
                break;
            }

            bool submitRequired = false;

            // 2. Check and drain the eventfd.
            // Because of EFD_NONBLOCK, if a standard network event woke us up instead of Poke(),
            // this synchronous read returns -1 with EAGAIN instantly, causing zero overhead.
            if (LinuxSyscall.read(_eventFd, &localEventBuffer, sizeof(ulong)) > 0)
            {
                // The wake was triggered by an external Poke() cross-thread action!
                while (_pendingCqes.TryDequeue(out var task))
                {
                    LocalPushSqe(task);
                }
            }

            // 3. Process the native memory-mapped completion ring entries
            uint head = *_ring.CqHead;
            uint tail = *_ring.CqTail;

            while (head != tail)
            {
                io_uring_cqe* cqe = &_ring.Cqes[head & _ring.CqMask];
                var (contextId, op) = UnpackUserData(cqe->user_data);
                int result = cqe->res;

                switch (op)
                {
                    case IOUringOperation.AcceptLocal:
                    case IOUringOperation.AcceptRoundRobin:
                        if (result >= 0)
                        {
                            ushort newClientId = (ushort)result;
                            var client = new IOUringSocket(newClientId);
                            _clients.TryAdd(newClientId, client);

                            engine.OnAccept(client);
                            PushAutoSelectReadSqe(result);
                            submitRequired = true;
                        }

                        if ((cqe->flags & LinuxSyscall.IORING_CQE_F_MORE) == 0)
                        {
                            PushMultishotAcceptSqe();
                            submitRequired = true;
                        }

                        break;

                    case IOUringOperation.Read:
                        if (result > 0)
                        {
                            ushort assignedBid = (ushort)(cqe->flags >> 16);
                            byte* nativeBufferPtr = _bufferPool.GetBufferAddress(assignedBid);
                            ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(nativeBufferPtr, result);

                            if (_clients.TryGetValue(contextId, out var client))
                            {
                                engine.OnRead(client, payload);
                            }

                            _bufferPool.ReleaseBuffer(assignedBid);
                            PushAutoSelectReadSqe(contextId);
                            submitRequired = true;
                        }
                        else
                        {
                            // Socket EOF closed or broken connection state
                            _clients.TryRemove(contextId, out _);
                            LinuxSyscall.close(contextId);
                        }

                        break;
                }

                head++;
            }

            // Update the consumer head tracking address pointer directly in memory
            *_ring.CqHead = head;

            // 4. Batch and flush any newly queued operations to the hardware in one non-blocking pass
            if (submitRequired)
            {
                LinuxSyscall.io_uring_enter_nonblocking(LinuxSyscall.SYS_io_uring_enter, _ring.RingFd, 1, 0, 0, null,
                    0);
            }
        }
    }

    public void AcceptMultishot(int fd, bool local)
    {
        io_uring_sqe sqe = default;
        sqe.opcode = LinuxSyscall.IORING_OP_ACCEPT;
        sqe.fd = fd;
        sqe.ioprio = 1; // Setting ioprio to 1 acts as IORING_ACCEPT_MULTISHOT
        sqe.user_data = PackUserData(0, local ? IOUringOperation.AcceptLocal : IOUringOperation.AcceptRoundRobin);
        Push(sqe);
    }

    private unsafe void LocalPushSqe(in io_uring_sqe sqe)
    {
        uint tail = *_ring.SqTail;
        uint index = tail & _ring.SqMask;
        _ring.Sqes[index] = sqe;
        _ring.SqArray[index] = index;
        *_ring.SqTail = tail + 1;
    }

    private unsafe void PushAutoSelectReadSqe(int clientFd)
    {
        uint tail = *_ring.SqTail;
        uint index = tail & _ring.SqMask;

        io_uring_sqe* sqe = &_ring.Sqes[index];
        *sqe = default;
        sqe->opcode = LinuxSyscall.IORING_OP_READ;
        sqe->fd = clientFd;

        // Let the kernel manage addresses and offsets dynamically
        sqe->addr = 0;
        sqe->len = 0;
        sqe->buf_index = _bufferPool.GroupId; // Tells the kernel which tracking buffer ring pool to select from
        sqe->flags |= LinuxSyscall.IOSQE_BUFFER_SELECT;

        sqe->user_data = PackUserData((ushort)clientFd, IOUringOperation.Read);

        _ring.SqArray[index] = index;
        *_ring.SqTail = tail + 1;
    }

    public void Stop()
    {
        _isAlive = false;
        ulong signal = 1;
        unsafe
        {
            LinuxSyscall.write(_eventFd, &signal, sizeof(ulong));
        }
    }

    public void Dispose()
    {
        _ring?.Dispose();
        _bufferPool?.Dispose();
        if (_eventFd > 0) LinuxSyscall.close(_eventFd);
    }

    private ushort NewClientId(bool demand = true)
    {
        while (true)
        {
            if (_availableIds.TryDequeue(out var clientId)) return clientId;

            var lastId = Volatile.Read(ref _lastId);
            if (lastId is ushort.MaxValue)
            {
                if (demand) Throw();
                return 0; // failure, no incr
            }
            clientId = (ushort)(lastId + 1);
            if (Interlocked.CompareExchange(ref _lastId, clientId, lastId) == lastId)
                return clientId;
        }
        static void Throw() => throw new InvalidOperationException("Clients saturated; unable to accept more load.");
    }

    public unsafe void Push(in io_uring_sqe sqe)
    {
        _pendingCqes.Enqueue(sqe);
        ulong signal = 1;
        LinuxSyscall.write(_eventFd, &signal, sizeof(ulong));
    }
}