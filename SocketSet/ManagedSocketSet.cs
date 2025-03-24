using System.Buffers;
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

    private readonly List<ManagedSocket> children = [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            ManagedSocket[] arr;
            lock (children)
            {
                arr = children.ToArray();
                children.Clear();
            }
            foreach (ManagedSocket child in arr)
            {
                try { child.Socket.Dispose(); }
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
        lock (children)
        {
            children.Add(child);
        }
        if (read)
        {
            Read(child);
        }
        return child;
    }

    protected override void Read(SocketBase socket)
    {
        ThrowIfDisposed();
        lock (_pendingRead)
        {
            _pendingRead.Add((ManagedSocket)socket);
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

    private readonly List<ManagedSocket> _pendingRead = [], _pendingWrite = [];

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
        var timeout = TimeSpan.FromMilliseconds(50);
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        Dictionary<Socket, ManagedSocket> lookup = new();
        while (!IsDisposed)
        {
            bool any = false;
            read.Clear();
            lookup.Clear();
            lock (_pendingRead)
            {
                var src = CollectionsMarshal.AsSpan(_pendingRead);
                if (src.Length != 0)
                {
                    read.EnsureCapacity(src.Length);
                    foreach (var socket in src)
                    {
                        read.Add(socket.Socket);
                        lookup[socket.Socket] = socket;
                    }
                    any = true;
                }
            }

            if (!any)
            {
                Thread.Sleep(timeout);
                continue;
            }

            Socket.Select(read, null, null, timeout);
            if (read.Count != 0)
            {
                foreach (var socket in CollectionsMarshal.AsSpan(read))
                {
                    var child = lookup[socket];
                    int bytes;
                    SocketError error;
                    bool readAgain;
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
                    catch
                    {
                        readAgain = false;
                    }
                    if (!readAgain)
                    {
                        lock (_pendingRead)
                        {
                            _pendingRead.Remove(child);
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