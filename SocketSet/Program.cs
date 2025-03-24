using Socketizer;
using System.Net;
using System.Net.Sockets;

int messages = 0;
using var set = new ManagedSocketSet(ReadSocket);
var ep = new IPEndPoint(IPAddress.Loopback, 6379);
const int SOCKETS = 20;
for (int i = 0; i < SOCKETS; i++)
{
    set.Open(ep, $"connection {i}").Write(Ping());
}
var timeout = TimeSpan.FromSeconds(10); 
Thread.Sleep(timeout);
Console.WriteLine($"{set.GetType().Name}: {Volatile.Read(ref messages)} messages received in {timeout.TotalSeconds}s using {SOCKETS} connections, no pipelining");

bool ReadSocket(SocketSet.SocketBase socket, SocketError error, ReadOnlySpan<byte> bytes)
{
    Interlocked.Increment(ref messages);
    //Console.WriteLine($"received {bytes.Length} bytes from {(string)socket.UserToken!}");
    ThreadPool.QueueUserWorkItem(static s =>
    {
        s.Write(Ping());
    }, socket, false);
    return true;
}

static ReadOnlySpan<byte> Ping() => "*1\r\n$4\r\nPING\r\n"u8;
// static ReadOnlySpan<byte> Pong() => "+PONG\r\n"u8;

