using System.Net;
using System.Net.Sockets;

namespace Socketizer;

abstract class SocketSet(SocketSet.ReadCallback onRead) : IDisposable
{
    private volatile bool _disposed;
    public abstract class SocketBase(SocketSet owner, object? userToken)
    {
        public SocketSet Owner { get; } = owner;
        public object? UserToken { get; } = userToken;

        public void Read() => Owner.Read(this);

        public void Write(ReadOnlySpan<byte> value) => Owner.Write(this, value);
    }

    protected readonly ReadCallback OnRead = onRead;

    public abstract SocketBase Open(EndPoint endpoint, object? userToken = null, bool read = true);

    protected abstract void Read(SocketBase socket);
    protected abstract void Write(SocketBase socket, ReadOnlySpan<byte> value);

    public delegate bool ReadCallback(SocketBase socket, SocketError error, ReadOnlySpan<byte> bytes);

    protected bool IsDisposed => _disposed;
    protected void ThrowIfDisposed()
    {
        if (_disposed) Throw(this);
        static void Throw(SocketSet obj) => throw new ObjectDisposedException(obj.ToString());
    }

    public void Dispose()
    {
        _disposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        _disposed = true;
    }
}
