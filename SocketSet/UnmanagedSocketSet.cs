using System.Net;
using System.Net.Sockets;

namespace Socketizer;

internal class UnmanagedSocketSet(SocketSet.ReadCallback onRead) : SocketSet(onRead)
{
    public sealed class UnmanagedSocket(UnmanagedSocketSet owner, nint handle, object? userToken) : SocketBase(owner, userToken)
    {
        internal readonly nint Handle = handle;
    }

    public override SocketBase Open(EndPoint endpoint, object? userToken = null, bool read = true)
    {
        throw new NotImplementedException();
    }

    protected override void Read(SocketBase socket)
    {
        throw new NotImplementedException();
    }

    protected override void Write(SocketBase socket, ReadOnlySpan<byte> value)
    {
        throw new NotImplementedException();
    }
}
