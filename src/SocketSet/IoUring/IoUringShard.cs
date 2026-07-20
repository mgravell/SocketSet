using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using SocketSets.Native;

namespace SocketSets.IoUring;

internal sealed class IoUringShard : SocketSetShard
{
    private readonly object?[] _userTokens;
    private readonly int[] _fds;
    private uint _clientStart;
    private RawIOUringRing _ring;
    private int _eventFd;
    private ManagedBufferPool _readBuffer;
    private readonly ConcurrentQueue<LibC.io_uring_sqe> _pending = [];

    public IoUringShard(SocketSetOptions options)
    {
        _fds = new int[options.SocketsPerShard];
        _userTokens = new object?[options.SocketsPerShard];

        // defer _ring - it needs to be created by the IO thread
    }

    private uint InitClient(int fd, object? userToken)
    {
        if (fd is 0) Throw();
        var fds = _fds;
        var offset = Interlocked.Increment(ref _clientStart);
        for (int i = 0; i < fds.Length; i++)
        {
            var index = (uint)((i + offset) % fds.Length);
            if (index is uint.MaxValue) continue; // reserved
            if (Interlocked.CompareExchange(ref fds[index], fd, 0) is 0)
            {
                Volatile.Write(ref _userTokens[index], userToken);
                return index + 1; // we'll return 1 when we mean "the first", so: index 0
            }
        }

        return 0; // failure

        static void Throw() => throw new ArgumentOutOfRangeException(nameof(fd), "Invalid socket handle");
    }

    private int GetClient(uint id, out object? userToken)
    {
        var index = id - 1;
        userToken = Volatile.Read(ref _userTokens[index]);
        return Volatile.Read(ref _fds[index]);
    }

    private void ResetClient(uint id)
    {
        // reset the user-token first so we don't fight with InitClient
        var index = id - 1;
        Volatile.Write(ref _userTokens[index], null);
        Volatile.Write(ref _fds[index], 0);
    }

    public override void Listen(EndPoint endpoint, object? userToken, bool local)
    {
        int fd = IoUringFactory.Bind(endpoint);
        var id = InitClient(fd, userToken);
    }

    private unsafe void Initialize()
    {
        // 1. Fire up our memory-mapped ring structures
        var options = Parent.Options;
        _ring = new RawIOUringRing((uint)options.EntriesPerShard);
        _readBuffer = new ManagedBufferPool(_ring.RingFd, entries: options.BufferPagesPerShard,
            bufSize: options.BufferPageSize);

        // 2. Instantiate a UNIQUE non-blocking eventfd for this shard
        _eventFd = LibC.eventfd(0, 0x800);
        if (_eventFd < 0) throw new InvalidOperationException("Failed to allocate shard eventfd");

        // 3. Register the eventfd directly with this specific ring.
        // The kernel now explicitly maps this file descriptor to this ring context!
        var pinned = _eventFd;
        int registrationResult = LibC.io_uring_register(
            LibC.SYS_io_uring_register,
            _ring.RingFd,
            LibC.IORING_REGISTER_EVENTFD,
            &pinned, // Pointer to our eventfd integer handle (address only needed for this call)
            1 // Number of descriptors being registered
        );

        if (registrationResult < 0)
        {
            throw new InvalidOperationException($"Failed to register eventfd to ring: {Marshal.GetLastPInvokeError()}");
        }
    }

    private uint _unsubmittedCount;

    private unsafe uint ProcessAvailableCompletions()
    {
        uint head = *_ring.CqHead;
        uint tail = Volatile.Read(ref *_ring.CqTail);
        uint count = 0;
        while (head != tail)
        {
            LibC.io_uring_cqe* cqe = &_ring.Cqes[head & _ring.CqMask];
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
                        byte* nativeBufferPtr = _readBuffer.GetBufferAddress(assignedBid);
                        ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(nativeBufferPtr, result);

                        if (_clients.TryGetValue(contextId, out var client))
                        {
                            engine.OnRead(client, payload);
                        }

                        _readBuffer.ReleaseBuffer(assignedBid);
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

            count++;
            head++;
        }
        
        // Release semantics: updates the head so the kernel sees it safely
        Volatile.Write(ref *_ring.CqHead, head);

        return count;
    }

    private unsafe void Flush() // flush at least some of the queue
    {
        if (_unsubmittedCount is 0) return;

        bool waitForCompletion = false;
        while (true)
        {
            Thread.MemoryBarrier();

            int result;
            if (waitForCompletion)
            {
                result = LibC.io_uring_enter_nonblocking(
                    LibC.SYS_io_uring_enter, _ring.RingFd, _unsubmittedCount,
                    min_complete: 0, flags: LibC.IORING_ENTER_GETEVENTS,
                    sig: null, sigsz: 0);
                waitForCompletion = false;
            }
            else
            {
                result = LibC.io_uring_enter_blocking(
                    LibC.SYS_io_uring_enter, _ring.RingFd, _unsubmittedCount,
                    min_complete: 1, flags: LibC.IORING_ENTER_GETEVENTS,
                    sig: null, sigsz: 0);
            }

            if (result <= 0)
            {
                switch (result)
                {
                    case 0: // bypassed completions (race)
                    case -LibC.EINTR: // interrupt signal, might not need to block
                        ProcessAvailableCompletions();
                        break;
                    case -LibC.EBUSY:
                    case -LibC.EAGAIN:
                        // kernel couldn't make progress; try to make space before retrying
                        waitForCompletion = ProcessAvailableCompletions() is 0;
                        goto case 0;
                    default:
                        Throw(result);

                        static void Throw(int err) =>
                            throw new InvalidOperationException($"Error code {err} when submitting queue");

                        break;
                }
            }
            else
            {
                Debug.Assert(result <= _unsubmittedCount, $"Unexpected result in {nameof(Flush)}");
                _unsubmittedCount -= (uint)result;
                return; // we don't guarantee to flush everything, just something
            }
        }
    }

    protected override unsafe void OnRun()
    {
        Initialize(); // this needs to happen from the same thread as our IO loop

        // Allocate our eventfd read target on the stack.
        // Stack addresses are implicitly pinned for the entire duration of this loop method.
        ulong localEventBuffer = 0;

        while (IsActive)
        {
            // clear the wave event
            LibC.read(_eventFd, &localEventBuffer, sizeof(ulong));
            
            // check for anything pending
            if (!_pending.IsEmpty)
            {
                _unsubmittedCount += _ring.Push(_pending);
            }

            // 1. Sleep cleanly here. 
            // Wakes up if a network operation completes OR if Poke() writes to our registered eventfd.
            int sleepResult = LibC.io_uring_enter_blocking(
                LibC.SYS_io_uring_enter, _ring.RingFd,
                to_submit: _unsubmittedCount,
                min_complete: 1,
                flags: LibC.IORING_ENTER_GETEVENTS,
                sig: null, sigsz: 0);
            
            if (sleepResult < 0)
            {
                switch (sleepResult)
                {
                    case -LibC.EINTR:
                        continue; // (Interrupted system call), loop again safely
                    case -LibC.EBUSY:
                    case -LibC.EAGAIN:
                        break; // fine, we're about to process pending anyway
                    default:
                        Throw(sleepResult);
                        
                        static void Throw(int err) =>
                            throw new InvalidOperationException($"Error code {err} when submitting queue");
                        break;
                }
            }
            else
            {
                _unsubmittedCount -= (uint)sleepResult;
            }
            ProcessAvailableCompletions();
        }
    }

    public void AcceptMultishot(int fd, bool local)
    {
        LibC.io_uring_sqe sqe = default;
        sqe.opcode = LinuxSyscall.IORING_OP_ACCEPT;
        sqe.fd = fd;
        sqe.ioprio = 1; // Setting ioprio to 1 acts as IORING_ACCEPT_MULTISHOT
        sqe.user_data = PackUserData(0, local ? IOUringOperation.AcceptLocal : IOUringOperation.AcceptRoundRobin);
        Push(sqe);
    }
}