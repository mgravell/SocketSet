using System.Net;
using System.Net.Sockets;

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
/// TCP_NODELAY on TCP connections) so the only variable is the I/O mechanism.
/// With a UDS name it binds an abstract-namespace Unix socket instead of TCP;
/// UDS has no Nagle, so NoDelay is skipped there.
/// </summary>
internal sealed class SocketEchoServer : IDisposable
{
    private readonly int _port;
    private readonly int _bufferSize;
    private readonly string? _udsName; // abstract UDS name; null => TCP on _port
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();

    public SocketEchoServer(int port, int bufferSize, string? udsName = null)
    {
        _port = port;
        _bufferSize = bufferSize;
        _udsName = udsName;
        _listener = udsName != null
            ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    public void Initialize()
    {
        if (_udsName != null)
        {
            // A leading NUL in the path selects Linux's abstract namespace:
            // no socket file to create or unlink, gone when the last ref closes.
            _listener.Bind(new UnixDomainSocketEndPoint("\0" + _udsName));
            _listener.Listen(512);
            Console.WriteLine($"[socket] listening on abstract UDS @{_udsName}, {_bufferSize}B/conn");
            return;
        }

        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(new IPEndPoint(IPAddress.Any, _port));
        _listener.Listen(512);
        Console.WriteLine($"[socket] listening on :{_port}, {_bufferSize}B/conn (TCP_NODELAY)");
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

            // Kill Nagle on TCP so echoes aren't held for coalescing; N/A on UDS.
            if (_udsName == null) conn.NoDelay = true;

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
