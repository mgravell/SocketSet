using System.Collections.Concurrent;
using SocketSets;

namespace SmokeTest;

public class EchoServer(SocketSetOptions options) : SocketSet(options)
{
    /// <summary>Fixed message size the client sends.</summary>
    public int GreetingSize { get; set; } = 512;

    /// <summary>
    /// When set, the server echoes out-of-band: instead of replying inline from OnReceive, it
    /// hands the payload + <see cref="Connection"/> to a background thread that calls
    /// <see cref="Connection.Send"/>. This exercises the cross-thread "poke a write" path (and its
    /// per-connection send serialization) rather than the inline response path.
    /// </summary>
    public bool PokeMode { get; set; }

    private readonly BlockingCollection<(Connection Conn, byte[] Data)> _pokes = new();
    private int _pokeStarted;

    private void EnsurePokeWorker()
    {
        if (Interlocked.CompareExchange(ref _pokeStarted, 1, 0) != 0) return;
        var t = new Thread(() =>
        {
            foreach (var (conn, data) in _pokes.GetConsumingEnumerable())
                conn.Send(data); // out-of-band: marshaled onto the owning IO context
        })
        { IsBackground = true, Name = "poke-writer" };
        t.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pokes.CompleteAdding();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Client send window: how many messages may be outstanding (sent, not yet echoed back).
    /// 1 = ping/pong (latency-bound). N = bounded pipeline (keeps up to N in flight without
    /// waiting for each reply). int.MaxValue = unbounded pipeline (throughput, but a symmetric
    /// echo can saturate socket buffers and wedge — that's the point of bounding it).
    /// </summary>
    public int Window { get; set; } = 1;

    /// <summary>If &gt; 0, each client stops after sending this many messages and closes its
    /// connection (a graceful-drain test); 0 = ping/pong forever.</summary>
    public long CloseAfterMessages { get; set; }

    private long _echoed;      // bytes echoed by the server side
    private long _roundTrip;   // bytes received back by the client side
    private long _connected;
    private long _live;        // currently-open connections (both roles): +OnAccept/+OnConnect, -OnClosed
    private long _liveClient;  // client-side live (OnConnect - OnClosed)
    private long _liveServer;  // server-side live (OnAccept - OnClosed)
    private long _closed;      // total OnClosed callbacks
    private long _recvOps;     // number of OnReceive completions (either role)
    private long _recvBytes;   // total bytes delivered across those completions

    public long Echoed => Interlocked.Read(ref _echoed);
    public long RoundTripBytes => Interlocked.Read(ref _roundTrip);
    public long Connected => Interlocked.Read(ref _connected);
    public long LiveConnections => Interlocked.Read(ref _live);
    public long LiveClient => Interlocked.Read(ref _liveClient);
    public long LiveServer => Interlocked.Read(ref _liveServer);
    public long Closed => Interlocked.Read(ref _closed);
    public long RecvOps => Interlocked.Read(ref _recvOps);
    public long RecvBytes => Interlocked.Read(ref _recvBytes);
    /// <summary>Average bytes per receive completion — how much the stack coalesced per read.</summary>
    public double AvgRecvSize => RecvOps == 0 ? 0 : (double)RecvBytes / RecvOps;

    protected override void OnAccept(ref AcceptContext ctx)
    {
        // Server-accepted connection; UserToken stays the default ServerToken from Listen().
        Interlocked.Increment(ref _live);
        Interlocked.Increment(ref _liveServer);
    }

    protected override void OnClosed(Connection connection)
    {
        Interlocked.Decrement(ref _live);
        if (ReferenceEquals(connection.UserToken, ServerToken)) Interlocked.Decrement(ref _liveServer);
        else Interlocked.Decrement(ref _liveClient);
        Interlocked.Increment(ref _closed);
    }

    protected override void OnConnect(ref ConnectContext ctx)
    {
        Interlocked.Increment(ref _connected);
        Interlocked.Increment(ref _live);
        Interlocked.Increment(ref _liveClient);
        var client = new Client(Window, GreetingSize, CloseAfterMessages);
        ctx.Connection.UserToken = client;

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

        if (ReferenceEquals(ctx.Connection.UserToken, ServerToken))
        {
            Interlocked.Add(ref _echoed, ctx.PayloadBytes);
            if (PokeMode)
            {
                // Out-of-band echo: copy the payload out and let a background thread Send it.
                EnsurePokeWorker();
                _pokes.Add((ctx.Connection, ctx.Payload.ToArray()));
            }
            else
            {
                // Inline echo (data already in the buffer).
                ctx.ResponseBytes = ctx.PayloadBytes;
            }
        }
        else if (ctx.Connection.UserToken is Client client)
        {
            // Client: count the reply, then — if the write pipe went idle at the window
            // limit — restart it now that a slot has freed. (Racy with OnWrite; Client locks.)
            Interlocked.Add(ref _roundTrip, ctx.PayloadBytes);
            int n = client.Replied(ctx.PayloadBytes, out bool done);
            if (done)
            {
                ctx.Connection.Close(); // sent our quota and it's all echoed back — retract the socket
            }
            else if (n > 0)
            {
                ctx.RawBuffer.Slice(0, n).Fill((byte)'x');
                ctx.ResponseBytes = n;
            }
        }
    }

    protected override void OnWrite(ref WriteContext ctx)
    {
        // Client only: on write-complete, send the next message if the window has room.
        if (ctx.Connection.UserToken is Client client)
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
    public sealed class Client(int window, int size, long limitMessages)
    {
        private readonly long _windowBytes = Math.Max(1L, window) * size;
        private readonly int _size = size;
        private readonly object _gate = new();
        private long _outstanding; // bytes sent but not yet echoed back
        private long _toSend = limitMessages <= 0 ? long.MaxValue : limitMessages; // messages left to send
        private bool _writeIdle = true;

        /// <summary>The write pipe is free (connect / write-complete). Returns the bytes to
        /// send next, or 0 to leave the pipe idle (window full, or the message quota is spent).</summary>
        public int Writable()
        {
            lock (_gate)
            {
                if (_toSend > 0 && _outstanding + _size <= _windowBytes)
                {
                    _toSend--;
                    _outstanding += _size;
                    _writeIdle = false;
                    return _size;
                }
                _writeIdle = true;
                return 0;
            }
        }

        /// <summary>A reply arrived. Returns the bytes to send to refill the pipe (0 if none), and
        /// sets <paramref name="done"/> when the quota is fully sent and echoed — the caller should
        /// then close the connection.</summary>
        public int Replied(int bytes, out bool done)
        {
            lock (_gate)
            {
                _outstanding -= bytes;
                done = _toSend == 0 && _outstanding <= 0;
                if (!done && _writeIdle && _toSend > 0 && _outstanding + _size <= _windowBytes)
                {
                    _toSend--;
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
