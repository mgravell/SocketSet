using Socketizer;
using System.Buffers;
using System.Net;
using System.Net.Sockets;

const int SOCKETS = 20;
var ep = new IPEndPoint(IPAddress.Loopback, 6380);
var timeout = TimeSpan.FromSeconds(10);
Socket[] socks = new Socket[SOCKETS];
Task[] tasks = new Task[SOCKETS];
int messages = 0;

for (int i = 0; i < SOCKETS; i++)
{
    Socket socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    socks[i] = socket;
    socket.Connect(ep);
    tasks[i] = Task.Run(async () =>
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            while (true)
            {
                socket.Send(Ping());
                int bytes = await socket.ReceiveAsync(buffer);
                Interlocked.Increment(ref messages);
            }
        }
        catch { }
    });
}
await Task.Delay(timeout);
foreach (var sock in socks)
{
    sock.Dispose();
}
await Task.WhenAll(tasks);
Console.WriteLine($"async read: {Volatile.Read(ref messages)} messages received in {timeout.TotalSeconds}s using {SOCKETS} connections, no pipelining");

using var set = new SemiManagedSocketSet(ReadSocket); // 2573906
//using var set = new ManagedSocketSet(ReadSocket); // 2382757
//using var set = new WindowsUnmanagedSocketSet(ReadSocket); // 404192
messages = 0;
for (int i = 0; i < SOCKETS; i++)
{
    set.Open(ep, $"connection {i}").Write(Ping());
}

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

