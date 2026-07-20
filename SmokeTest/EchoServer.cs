using SocketSets;

namespace SmokeTest;

public class EchoServer(SocketSetOptions options) : SocketSet(options)
{
    /// <summary>Size of the initial payload a client fires on connect to start the exchange.</summary>
    public int GreetingSize { get; set; } = 512;

    /// <summary>
    /// false (default): ping/pong — the client sends the next message only after receiving the
    /// echo (latency-bound). true: pipeline — the client sends the next as soon as the previous
    /// write completes, without waiting for the reply (throughput-bound; models a multiplexed
    /// client such as SE.Redis).
    /// </summary>
    public bool Pipeline { get; set; }

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
            // Client: count the round trip. In ping/pong, bounce it back to drive the next
            // exchange; in pipeline mode, sends are driven by OnWrite instead.
            client.OnReceived(ctx.PayloadBytes);
            Interlocked.Add(ref _roundTrip, ctx.PayloadBytes);
            if (!Pipeline) ctx.ResponseBytes = ctx.PayloadBytes;
        }
    }

    protected override void OnWrite(ref WriteContext ctx)
    {
        // Pipeline mode only: as soon as a client write completes, send the next message —
        // don't wait for the echo. The server never self-drives; it echoes on receive.
        if (Pipeline && ctx.UserToken is Client)
        {
            var buffer = ctx.SendBuffer;
            int n = Math.Min(GreetingSize, buffer.Length);
            buffer.Slice(0, n).Fill((byte)'x');
            ctx.SendBytes = n;
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
