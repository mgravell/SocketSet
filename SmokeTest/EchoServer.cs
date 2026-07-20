using SocketSets;

namespace SmokeTest;

public class EchoServer(SocketSetOptions options) : SocketSet(options)
{
    /// <summary>Fixed message size the client sends.</summary>
    public int GreetingSize { get; set; } = 512;

    /// <summary>
    /// Client send window: how many messages may be outstanding (sent, not yet echoed back).
    /// 1 = ping/pong (latency-bound). N = bounded pipeline (keeps up to N in flight without
    /// waiting for each reply). int.MaxValue = unbounded pipeline (throughput, but a symmetric
    /// echo can saturate socket buffers and wedge — that's the point of bounding it).
    /// </summary>
    public int Window { get; set; } = 1;

    private long _echoed;      // bytes echoed by the server side
    private long _roundTrip;   // bytes received back by the client side
    private long _connected;
    private long _recvOps;     // number of OnReceive completions (either role)
    private long _recvBytes;   // total bytes delivered across those completions

    public long Echoed => Interlocked.Read(ref _echoed);
    public long RoundTripBytes => Interlocked.Read(ref _roundTrip);
    public long Connected => Interlocked.Read(ref _connected);
    public long RecvOps => Interlocked.Read(ref _recvOps);
    public long RecvBytes => Interlocked.Read(ref _recvBytes);
    /// <summary>Average bytes per receive completion — how much the stack coalesced per read.</summary>
    public double AvgRecvSize => RecvOps == 0 ? 0 : (double)RecvBytes / RecvOps;

    protected override void OnConnect(ref ConnectContext ctx)
    {
        Interlocked.Increment(ref _connected);
        var client = new Client(Window, GreetingSize);
        ctx.UserToken = client;

        // Prime the pipe with the first message.
        int n = client.Writable();
        if (n > 0)
        {
            ctx.SendBuffer.Slice(0, n).Fill((byte)'x');
            ctx.SendBytes = n;
        }
    }

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        Interlocked.Increment(ref _recvOps);
        Interlocked.Add(ref _recvBytes, ctx.PayloadBytes);

        if (ReferenceEquals(ctx.UserToken, ServerToken))
        {
            // Server: echo straight back (data already in the buffer).
            Interlocked.Add(ref _echoed, ctx.PayloadBytes);
            ctx.ResponseBytes = ctx.PayloadBytes;
        }
        else if (ctx.UserToken is Client client)
        {
            // Client: count the reply, then — if the write pipe went idle at the window
            // limit — restart it now that a slot has freed. (Racy with OnWrite; Client locks.)
            Interlocked.Add(ref _roundTrip, ctx.PayloadBytes);
            int n = client.Replied(ctx.PayloadBytes);
            if (n > 0)
            {
                ctx.RawBuffer.Slice(0, n).Fill((byte)'x');
                ctx.ResponseBytes = n;
            }
        }
    }

    protected override void OnWrite(ref WriteContext ctx)
    {
        // Client only: on write-complete, send the next message if the window has room.
        if (ctx.UserToken is Client client)
        {
            int n = client.Writable();
            if (n > 0)
            {
                ctx.SendBuffer.Slice(0, n).Fill((byte)'x');
                ctx.SendBytes = n;
            }
        }
    }

    /// <summary>
    /// Per-connection send-window bookkeeping. Exactly one write is ever in flight (matching
    /// the transport), and at most <c>window</c> messages are outstanding on the wire. The
    /// lock makes the "pipe idle + room → claim a send" decision atomic, so OnWrite (write
    /// completed) and OnReceive (reply freed a slot) can't both send, nor both stall.
    /// </summary>
    public sealed class Client(int window, int size)
    {
        private readonly long _windowBytes = Math.Max(1L, window) * size;
        private readonly int _size = size;
        private readonly object _gate = new();
        private long _outstanding; // bytes sent but not yet echoed back
        private bool _writeIdle = true;

        /// <summary>The write pipe is free (connect / write-complete). Returns the bytes to
        /// send next, or 0 to leave the pipe idle at the window limit.</summary>
        public int Writable()
        {
            lock (_gate)
            {
                if (_outstanding + _size <= _windowBytes)
                {
                    _outstanding += _size;
                    _writeIdle = false;
                    return _size;
                }
                _writeIdle = true;
                return 0;
            }
        }

        /// <summary>A reply arrived. Returns the bytes to send to refill the pipe if it had
        /// gone idle and a slot is now free, else 0.</summary>
        public int Replied(int bytes)
        {
            lock (_gate)
            {
                _outstanding -= bytes;
                if (_writeIdle && _outstanding + _size <= _windowBytes)
                {
                    _outstanding += _size;
                    _writeIdle = false;
                    return _size;
                }
                return 0;
            }
        }
    }

    /// <summary>Default token for server-accepted sockets; passed to <see cref="SocketSet.Listen"/>.</summary>
    public static readonly object ServerToken = new();
}
