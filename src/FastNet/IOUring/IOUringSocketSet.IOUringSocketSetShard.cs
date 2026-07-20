/*
using System.Runtime.InteropServices;using FastNet.IOUring;
using FastNet.Native;
using static FastNet.Native.LibUring;
namespace FastNet.IOUring;

internal sealed unsafe partial class IOUringSocketSet : SocketSet
{
    protected override SocketSetShard CreateShard() => new IOUringSocketSetShard();

    private sealed class IOUringSocketSetShard : SocketSetShard
    {
        protected override void OnInit()
        {
            _eventFd = LibC.eventfd(0, LibC.EFD_NONBLOCK);
            if (_eventFd < 0)
            {
                throw new Exception($"Failed to allocate kernel eventfd resource. System error code: {Marshal.GetLastWin32Error()}");
            }
            base.OnInit();
        }
        private int _eventFd = -1;
        private IoUringOpaque* _ring;

        protected internal override void OnRun()
        {
            _ring = (IoUringOpaque*) NativeMemory.AlignedAlloc(RingStructSize, 64);
            NativeMemory.Clear(_ring, RingStructSize);

            int initRc = io_uring_queue_init(RingEntries, _ring, flags: IORING_SETUP_SINGLE_ISSUER | IORING_SETUP_DEFER_TASKRUN);
            if (initRc < 0) throw new InvalidOperationException($"io_uring_queue_init failed: {-initRc}");

            ProcessInbound();
            PostWake();

            while (true)
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
                            throw new InvalidOperationException($"io_uring_submit_and_wait failed: {-rc}, shard {Index}");
                    }
                }

                if (IsStopped) break;
            }
        }

        static void CloseFD(ref int fd)
        {
            var val = fd;
            if (val >= 0 && Interlocked.CompareExchange(ref fd, -1, val) == val)
            {
                var err = LibC.close(val);
                if (err < 0) Throw();

                static void Throw() =>
                    throw new InvalidOperationException(
                        $"Unable to close file description; err {Marshal.GetLastWin32Error()}");
            }
        }
        protected override void OnDispose(bool disposing)
        {
            if (IsComplete) CloseFD(ref _eventFd);
            base.OnDispose(disposing);
        }
    }
}
*/