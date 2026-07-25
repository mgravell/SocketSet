using System.Text;
using SocketSets;

namespace SmokeTest;

/// <summary>
/// A deliberately dumb RESP client: connect, send one <c>PING</c> as a RESP array
/// (<c>*1\r\n$4\r\nPING\r\n</c>), read the reply, and check it is <c>+PONG\r\n</c>. Used to prove the
/// (optionally TLS-wrapped) client path talks to a real Redis-protocol server (Garnet / Redis). No
/// pipelining, no parser — just enough to round-trip one command over the transport.
/// </summary>
public sealed class RespPing(SocketSetOptions options) : SocketSet(options)
{
    private static readonly byte[] Ping = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    private readonly ManualResetEventSlim _done = new(false);
    private readonly StringBuilder _reply = new();

    public string Reply => _reply.ToString();
    public bool Ok { get; private set; }

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    protected override void OnConnect(ref ConnectContext ctx)
    {
        // Fires after the TLS handshake (if any) completes. Send the PING; it rides the encrypt path.
        Ping.CopyTo(ctx.SendBuffer);
        ctx.SendBytes = Ping.Length;
    }

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        _reply.Append(Encoding.ASCII.GetString(ctx.Payload));
        // A complete simple-string reply ends in CRLF; +PONG is all we expect here.
        if (_reply.ToString() is var s && s.EndsWith("\r\n"))
        {
            Ok = s.StartsWith("+PONG");
            _done.Set();
            ctx.Connection.Close();
        }
    }
}
