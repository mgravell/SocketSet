using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Socketizer.Winsock;

namespace Socketizer;

sealed class SemiManagedSocketSet : SocketSet
{
    public sealed class SemiManagedSocket(SemiManagedSocketSet owner, Socket socket, object? userToken) : SocketBase(owner, userToken)
    {
        internal readonly Socket Socket = socket;
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
        var child = new SemiManagedSocket(this, socket, userToken);
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
        var s = ((SemiManagedSocket)socket).Socket.Handle;
        lock (_pendingRead)
        {
            if (!_pendingRead.Contains(s))
            {
                _pendingRead.Add(s);
                if (_pendingRead.Count == 1)
                {
                    Monitor.Pulse(_pendingRead);
                }
            }
        }
    }

    protected override void Write(SocketBase socket, ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        WindowsUnmanagedSocketSet.Write(((SemiManagedSocket)socket).Socket.Handle, value);
    }

    private readonly List<IntPtr> _pendingRead = [];

    public SemiManagedSocketSet(ReadCallback onRead) : base(onRead)
    {
        var thread = new Thread(DedicatedLoop)
        {
            Priority = ThreadPriority.AboveNormal,
            Name = "SemiManagedSocketPoll"
        };
        thread.Start();
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
                    SocketError error = Winsock.Read(socket, &readBuffer, out int bytes);
                    try
                    {
                        readAgain = OnRead(child, error, bytes > 0 ? pinnedBuffer.AsSpan(0, bytes) : default)
                            & error == SocketError.Success;
                    }
                    catch { }
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