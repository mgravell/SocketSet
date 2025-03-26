using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Socketizer.Winsock;

namespace Socketizer;

sealed class SemiManagedSocketSet : SocketSet
{
    public static readonly bool IsWindows = OperatingSystem.IsWindows();
    public sealed class SemiManagedSocket : SocketBase
    {
        private readonly SemiManagedSocketPinned pinned;
        public unsafe SemiManagedSocket(SemiManagedSocketSet owner, Socket socket, object? userToken, IntPtr readEvent) : base(owner, userToken)
        {
            Socket = socket;
            readBuffer = GC.AllocateArray<byte>(1024, pinned: true);
            var arr = GC.AllocateArray<byte>(1024, pinned: true);
            fixed (byte* ptr = arr) // OK to escape: buffer is pinned
            {
                pinned = new(
                    new WSABuffer(arr.Length, new(ptr)),
                    new NativeOverlapped { EventHandle = readEvent });
            }
            socketsByOverlappedHandle.TryAdd(readEvent, this);
        }

        private ReadOnlySpan<byte> GetReadBuffer(int bytes) => bytes >= 0 ? readBuffer.AsSpan(0, bytes) : default;

        static readonly ConcurrentDictionary<IntPtr, SemiManagedSocket> socketsByOverlappedHandle = [];

        // we need to pin ourselves so we have somewhere for the chunks that we're handing to winsock
        // (we could potentially also self-alloc a read buffer at the same time, if we want)
        private sealed class SemiManagedSocketPinned
        {
            private GCHandle pin;
            public WSABuffer WsaBuffer;
            public NativeOverlapped Overlapped;

            public SemiManagedSocketPinned(WSABuffer buffer, NativeOverlapped overlapped)
            {
                pin = GCHandle.Alloc(this, GCHandleType.Pinned);
                this.WsaBuffer = buffer;
                this.Overlapped = overlapped;
            }

            public unsafe WSABuffer* WSABufferPtr => (WSABuffer*)Unsafe.AsPointer(ref WsaBuffer);
            public unsafe NativeOverlapped* OverlappedPtr => (NativeOverlapped*)Unsafe.AsPointer(ref Overlapped);

            ~SemiManagedSocketPinned()
            {
                var tmp = pin;
                pin = default;
                if (tmp.IsAllocated)
                {
                    tmp.Free();
                }
            }
        }

        private readonly byte[] readBuffer;
        internal readonly Socket Socket;

        internal unsafe void ReceiveOverlapped()
        {
            bool repeat;
            do
            {
                repeat = false;
                SocketFlags flags = SocketFlags.None;
                var status = WSARecv(Socket.Handle, pinned.WSABufferPtr, 1, out int bytes, ref flags, pinned.OverlappedPtr, &ReadCallbackUnmanaged);
                switch (status)
                {
                    case SocketError.Success:
                        // single-call sync
                        repeat = Owner.OnRead(this, status, GetReadBuffer(bytes)) & status == SocketError.Success;
                        break;
                    case SocketError.IOPending:
                        // single-call async
                        break;
                    case SocketError.SocketError:
                        status = GetLastSocketError();
                        if (status == SocketError.IOPending)
                        {
                            // callback deferred
                            break;
                        }
                        else
                        {
                            ThrowSocketError(status);
                        }
                        break;
                    default:
                        ThrowSocketError(status);
                        break;
                }
            } while (repeat);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static unsafe void ReadCallbackUnmanaged(SocketError status, int bytesTransferred, NativeOverlapped* overlapped, SocketFlags flags)
        {
            // we should be on an IOCP thread here; danger, will!
            if (socketsByOverlappedHandle.TryGetValue(overlapped->EventHandle, out var socket))
            {
                if (socket.Owner.OnRead(socket, status, socket.GetReadBuffer(bytesTransferred))
                    & status == SocketError.Success)
                {
                    socket.ReceiveOverlapped();
                }
            }
        }
    }

    private readonly ConcurrentDictionary<IntPtr, SemiManagedSocket> children = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            SemiManagedSocket[] arr = [.. children.Values];
            children.Clear();
            foreach (var child in arr)
            {
                try { child.Socket.Dispose(); }
                catch { }
            }
        }
    }


    public override SemiManagedSocket Open(EndPoint endpoint, object? userToken = null, bool read = true)
    {
        ThrowIfDisposed();
        Socket socket = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(endpoint);


        var readEvent = IsWindows ? WSACreateEvent() : 0;
        if (readEvent == ~0) ThrowLastSocketError();
        var child = new SemiManagedSocket(this, socket, userToken, readEvent);
        children.TryAdd(socket.Handle, child);

        if (read)
        {
            Read(child);
        }
        return child;
    }

    protected override void Read(SocketBase socket)
    {
        ThrowIfDisposed();
        var s = ((SemiManagedSocket)socket);
        if (overlapped)
        {
            s.ReceiveOverlapped();
        }
        else
        {
            var handle = s.Socket.Handle;
            lock (_pendingRead)
            {
                if (!_pendingRead.Contains(handle))
                {
                    _pendingRead.Add(handle);
                    if (_pendingRead.Count == 1)
                    {
                        Monitor.Pulse(_pendingRead);
                    }
                }
            }
        }
    }

    protected override void Write(SocketBase socket, ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        if (IsWindows)
        {
            WindowsUnmanagedSocketSet.Write(((SemiManagedSocket)socket).Socket.Handle, value);
        }
        else
        {
            ((SemiManagedSocket)socket).Socket.Send(value);
        }
    }

    private readonly List<IntPtr> _pendingRead = [];
    private readonly bool overlapped;
    public SemiManagedSocketSet(ReadCallback onRead, bool overlapped = false) : base(onRead)
    {
        this.overlapped = overlapped;
        if (!overlapped)
        {
            var thread = new Thread(DedicatedLoop)
            {
                Priority = ThreadPriority.AboveNormal,
                Name = "SemiManagedSocketPoll"
            };
            thread.Start();
        }
    }

    private unsafe void DedicatedLoop()
    {
        List<IntPtr> read = [];
        var pulseTimeoutMilliseconds = 1000;
        TimeValue selectTimeout = new(seconds: 0, microseconds: 50 * 1000);
        byte[] pinnedBuffer = GC.AllocateArray<byte>(8 * 1024, pinned: true);
        WSABuffer readBuffer = new(pinnedBuffer.Length, new(Unsafe.AsPointer(ref pinnedBuffer[0])));

        while (!IsDisposed)
        {
            Span<IntPtr> readfds;
            lock (_pendingRead)
            {
                if (_pendingRead.Count == 0)
                {
                    Monitor.Wait(_pendingRead, pulseTimeoutMilliseconds);
                    continue;
                }
                CollectionsMarshal.SetCount(read, _pendingRead.Count + 1);
                readfds = CollectionsMarshal.AsSpan(read);
                readfds[0] = _pendingRead.Count;
                CollectionsMarshal.AsSpan(_pendingRead).CopyTo(readfds.Slice(1));
            }

            unsafe
            {
                fixed (IntPtr* ptr = readfds)
                {
                    var count = select(0, ptr, null, null, &selectTimeout);
                    if (count == 0) continue;
                    if (count < 0) ThrowLastSocketError();
                    readfds = readfds.Slice(0, count); // active handles
                }
            }

            foreach (var socket in readfds)
            {
                bool readAgain = false;
                if (children.TryGetValue(socket, out var child))
                {
                    if (IsWindows)
                    {
                        SocketError error = Winsock.Read(socket, &readBuffer, out int bytes);
                        try
                        {
                            readAgain = OnRead(child, error, bytes > 0 ? pinnedBuffer.AsSpan(0, bytes) : default)
                                & error == SocketError.Success;
                        }
                        catch { }
                    }
                    else
                    {
                        try
                        {
                            int bytes = ((SemiManagedSocket)child).Socket.Receive(pinnedBuffer);
                            readAgain = OnRead(child, SocketError.Success, bytes > 0 ? pinnedBuffer.AsSpan(0, bytes) : default);
                        } catch{}
                        
                    }
                }
                if (!readAgain)
                {
                    lock (_pendingRead)
                    {
                        _pendingRead.Remove(socket);
                    }
                }
            }
        }
    }
}