using System.Collections.Concurrent;
using System.Text;
using SocketSets;

namespace SmokeTest;

/// <summary>
/// Isolation harness for the "RST truncation under concurrency" hunt: a bare FastNet/SocketSet HTTP
/// responder — NO Kestrel, NO pipes, NO transport bridge. On each request it sends a canned HTTP/1.1
/// keep-alive response via the out-of-band <c>Connection.Send</c> path FROM A BACKGROUND THREAD (mirroring
/// the AspNet transport's pump). If this reproduces the ~7% curl-52 failures, the bug is in SocketSet
/// itself; if it's clean, the bug is in the Kestrel bridge.
/// </summary>
public sealed class HttpBench(SocketSetOptions options, int bodySize = 2) : SocketSet(options)
{
    // Pre-rendered once. This exists so the bare responder can be compared against AspNetDemo at the SAME
    // payload under the SAME client: the difference between them is then the Kestrel bridge (two Pipes,
    // an ArrayBufferWriter, a per-flush ToArray, and a thread hop) and nothing else.
    private readonly byte[] Response = BuildResponse(bodySize);

    private static byte[] BuildResponse(int bodySize)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {bodySize}\r\nConnection: keep-alive\r\n\r\n");
        var buf = new byte[head.Length + bodySize];
        head.CopyTo(buf, 0);
        buf.AsSpan(head.Length).Fill((byte)'x');
        return buf;
    }

    private readonly BlockingCollection<Connection> _work = new();
    private int _started;

    private void EnsureWorker()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        // A couple of background senders, so responses go out cross-thread like the transport pump.
        for (int i = 0; i < 2; i++)
            new Thread(() => { foreach (var c in _work.GetConsumingEnumerable()) c.Send(Response); })
            { IsBackground = true, Name = $"http-responder-{i}" }.Start();
    }

    protected override void OnAccept(ref AcceptContext ctx) => EnsureWorker();

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        // Crude: respond once per OnReceive that carries an end-of-headers (fine for single-shot curl GETs).
        if (ctx.Payload.IndexOf("\r\n\r\n"u8) >= 0)
            _work.Add(ctx.Connection);
    }
}
