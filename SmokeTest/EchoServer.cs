using SocketSets;

namespace SmokeTest;

public class EchoServer(SocketSetOptions options) : SocketSet(options)
{
    protected override void OnAccept(ref AcceptContext ctx)
    {
        ctx.UserToken = ServerSentinel;
    }

    protected override void OnConnect(ref ConnectContext ctx)
    {
        ctx.UserToken = new Client();
    }

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        if (ReferenceEquals(ctx.UserToken, ServerSentinel))
        {
            // tell the library to echo back directly
            ctx.ResponseBytes = ctx.PayloadBytes;
        }
        else if (ctx.UserToken is Client client)
        {
            client.OnReceived(ctx.PayloadBytes);
        }
    }

    public class Client
    {
        private int _sent, _received;
        public void OnSent(int count) => Interlocked.Add(ref _sent, count);
        public void OnReceived(int count) => Interlocked.Add(ref _received, count);
    }

    private static readonly object ServerSentinel = new();
}