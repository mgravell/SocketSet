using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Socketizer;

sealed class ManagedSocketSet : SocketSet
{
    public sealed class ManagedSocket(ManagedSocketSet owner, Socket socket, object? userToken) : SocketBase(owner, userToken)
    {
        internal readonly Socket Socket = socket;
    }

    private readonly ConcurrentDictionary<Socket, ManagedSocket> children = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            Socket[] arr = children.Keys.ToArray();
            children.Clear();
            foreach (Socket child in arr)
            {
                try { child.Dispose(); }
                catch { }
            }
        }
    }


    public override ManagedSocket Open(EndPoint endpoint, object? userToken = null, bool read = true)
    {
        ThrowIfDisposed();
        Socket socket = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(endpoint);
        var child = new ManagedSocket(this, socket, userToken);
        children.TryAdd(socket, child);
        if (read)
        {
            Read(child);
        }
        return child;
    }

    protected override void Read(SocketBase socket)
    {
        ThrowIfDisposed();
        var s = ((ManagedSocket)socket).Socket;
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
        do
        {
            var sent = ((ManagedSocket)socket).Socket.Send(value);
            value = value.Slice(sent);
        }
        while (!value.IsEmpty);
    }

    private readonly List<Socket> _pendingRead = [];

    public ManagedSocketSet(ReadCallback onRead) : base(onRead)
    {
        var thread = new Thread(DedicatedLoop);
        thread.Priority = ThreadPriority.AboveNormal;
        thread.Name = "SocketPoll";
        thread.Start();
    }

    private void DedicatedLoop()
    {
        List<Socket> read = [];
        var pulseTimeoutMilliseconds = 1000;
        var selectTimeoutMicroseconds = 50 * 1000;
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(8 * 1024);

        while (!IsDisposed)
        {
            lock (_pendingRead)
            {
                if (_pendingRead.Count == 0)
                {
                    Monitor.Wait(_pendingRead, pulseTimeoutMilliseconds);
                    continue;
                }
                CollectionsMarshal.SetCount(read, _pendingRead.Count);
                CollectionsMarshal.AsSpan(_pendingRead).CopyTo(CollectionsMarshal.AsSpan(read));
            }

            Socket.Select(read, null, null, microSeconds: selectTimeoutMicroseconds);
            if (read.Count != 0)
            {
                foreach (var socket in CollectionsMarshal.AsSpan(read))
                {
                    bool readAgain = false;
                    if (children.TryGetValue(socket, out var child))
                    {
                        int bytes;
                        SocketError error;
                        try
                        {
                            bytes = child.Socket.Receive(readBuffer, SocketFlags.None);
                            error = SocketError.Success;
                        }
                        catch (Exception e)
                        {
                            bytes = 0;
                            error = AsSocketError(e);
                        }
                        try
                        {
                            readAgain = OnRead(child, error, bytes > 0 ? readBuffer.AsSpan(0, bytes) : default)
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

        ArrayPool<byte>.Shared.Return(readBuffer, clearArray: true);

        static SocketError AsSocketError(Exception ex)
                => ex is SocketException s ? (SocketError)s.ErrorCode : SocketError.OperationAborted;
    }
}