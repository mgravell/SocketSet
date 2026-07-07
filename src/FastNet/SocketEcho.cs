using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastNet.Fallback;

/// <summary>
/// Plain async-<see cref="Socket"/> echo server — the portable baseline the
/// io_uring transport is measured against. Deliberately idiomatic .NET: one
/// accept loop, one fire-and-forget echo task per connection, a per-connection
/// heap buffer. No <see cref="SocketAsyncEventArgs"/> pooling and no pinned
/// native block (that is <see cref="SaeaEngine"/>'s job) — the gap between this
/// and <c>Transport.EchoServer</c> is exactly the transport cost we want to see.
///
/// Socket options mirror the io_uring listener (SO_REUSEADDR, backlog 512, and
/// notably <em>no</em> TCP_NODELAY) so the only variable is the I/O mechanism.
/// </summary>
internal sealed class SocketEchoServer : IDisposable
{
    private readonly int _port;
    private readonly int _bufferSize;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();

    public SocketEchoServer(int port, int bufferSize)
    {
        _port = port;
        _bufferSize = bufferSize;
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    public void Initialize()
    {
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(new IPEndPoint(IPAddress.Any, _port));
        _listener.Listen(512);
        Console.WriteLine($"[socket] listening on :{_port}, {_bufferSize}B/conn");
    }

    public void Run()
    {
        // Bridge the sync entry point to the async accept loop; blocks until Stop().
        try { AcceptLoop(_cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { /* Stop() */ }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket conn;
            try { conn = await _listener.AcceptAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; } // transient accept error; keep listening

            _ = EchoAsync(conn, ct); // one task per connection; recv -> echo -> recv ...
        }
    }

    private async Task EchoAsync(Socket conn, CancellationToken ct)
    {
        var buf = new byte[_bufferSize];
        try
        {
            while (true)
            {
                int n = await conn.ReceiveAsync(buf, SocketFlags.None, ct);
                if (n <= 0) break; // 0 == peer closed

                int sent = 0;
                while (sent < n) // loop on short writes until the payload is flushed
                    sent += await conn.SendAsync(buf.AsMemory(sent, n - sent), SocketFlags.None, ct);
            }
        }
        catch (SocketException) { }        // peer reset
        catch (OperationCanceledException) { } // Stop()
        catch (ObjectDisposedException) { }
        finally { conn.Dispose(); }
    }

    public void Stop() => _cts.Cancel();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
        _cts.Dispose();
    }
}
