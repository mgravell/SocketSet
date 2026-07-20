using SocketSets;

namespace SmokeTest;

public class EchoServer(SocketSetOptions options) : SocketSet(options)
{
    /// <summary>Size of the initial payload a client fires on connect to start the ping-pong.</summary>
    public int GreetingSize { get; init; } = 512;

    private long _echoed;      // bytes echoed by the server side
    private long _roundTrip;   // bytes received back by the client side (a completed round trip)
    private long _connected;

    public long Echoed => Interlocked.Read(ref _echoed);
    public long RoundTripBytes => Interlocked.Read(ref _roundTrip);
    public long Connected => Interlocked.Read(ref _connected);

    // No OnAccept override: accepted sockets inherit the default token passed to
    // Listen (ServerToken), which is all the server needs to recognise them.

    protected override void OnConnect(ref ConnectContext ctx)
    {
        Interlocked.Increment(ref _connected);
        ctx.UserToken = new Client();

        // Kick off the exchange: write a greeting into the library-owned buffer and
        // ask for it to be sent as soon as the connection is established.
        var buffer = ctx.SendBuffer;
        int n = Math.Min(GreetingSize, buffer.Length);
        buffer.Slice(0, n).Fill((byte)'x');
        ctx.SendBytes = n;
    }

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        if (ReferenceEquals(ctx.UserToken, ServerToken))
        {
            // Server: echo the payload straight back (data is already in the buffer).
            Interlocked.Add(ref _echoed, ctx.PayloadBytes);
            ctx.ResponseBytes = ctx.PayloadBytes;
        }
        else if (ctx.UserToken is Client client)
        {
            // Client: count the round trip, then bounce it back to sustain the ping-pong.
            client.OnReceived(ctx.PayloadBytes);
            Interlocked.Add(ref _roundTrip, ctx.PayloadBytes);
            ctx.ResponseBytes = ctx.PayloadBytes;
        }
    }

    public class Client
    {
        private int _sent, _received;
        public void OnSent(int count) => Interlocked.Add(ref _sent, count);
        public void OnReceived(int count) => Interlocked.Add(ref _received, count);
    }

    /// <summary>Default token for server-accepted sockets; passed to <see cref="SocketSet.Listen"/>.</summary>
    public static readonly object ServerToken = new();
}
