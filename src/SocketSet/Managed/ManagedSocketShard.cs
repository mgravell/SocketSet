using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SocketSets.Managed;

/// <summary>
/// A callback-driven shard over .NET sockets + SocketAsyncEventArgs. There is no pump
/// thread: accept/receive/send completions run on thread-pool threads. Handlers may
/// therefore fire concurrently across connections — the same contract as the io_uring
/// backend (which fires concurrently across shards), so user callbacks must be thread-safe.
/// </summary>
internal sealed unsafe class ManagedSocketShard : SocketSetShard
{
    private int _bufferSize;
    private readonly List<Socket> _listeners = [];
    private readonly ConcurrentDictionary<Connection, byte> _connections = [];

    public ManagedSocketShard(SocketSetOptions options) => _bufferSize = options.BufferPageSize;

    protected override void OnInitialize() => _bufferSize = Parent.Options.BufferPageSize;

    // Never called: the managed backend reports UsesWorkerThreads = false, so the base
    // does not run a pump loop for it.
    protected override void OnRun()
    {
    }

    protected override void OnShutdown()
    {
        lock (_listeners)
        {
            foreach (var l in _listeners) SafeDispose(l);
            _listeners.Clear();
        }

        foreach (var conn in _connections.Keys)
            Close(conn);
    }

    // =====================================================================
    // Listen / accept
    // =====================================================================

    public override void Listen(EndPoint endpoint, object? defaultToken, bool local)
    {
        // MaxShards == 1, so there is only ever one shard here; a single listener binds
        // the endpoint directly — no reuse-port fan-out.
        var listener = Bind(endpoint);
        listener.Listen(512);
        lock (_listeners) _listeners.Add(listener);

        var acceptArgs = new SocketAsyncEventArgs { UserToken = new AcceptState(listener, defaultToken) };
        acceptArgs.Completed += OnAcceptCompleted;
        StartAccept(acceptArgs);
    }

    private sealed class AcceptState(Socket listener, object? defaultToken)
    {
        public Socket Listener { get; } = listener;
        public object? DefaultToken { get; } = defaultToken;
    }

    private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs args)
    {
        if (ProcessAccept(args)) StartAccept(args);
    }

    private void StartAccept(SocketAsyncEventArgs args)
    {
        var state = (AcceptState)args.UserToken!;
        while (true)
        {
            args.AcceptSocket = null; // must be cleared before reuse
            bool pending;
            try { pending = state.Listener.AcceptAsync(args); }
            catch (ObjectDisposedException) { return; }
            if (pending) return;             // OnAcceptCompleted will fire
            if (!ProcessAccept(args)) return; // synchronous completion; loop unless told to stop
        }
    }

    /// <returns>true to keep accepting, false to stop (listener closed).</returns>
    private bool ProcessAccept(SocketAsyncEventArgs args)
    {
        if (args.SocketError == SocketError.OperationAborted) return false;

        if (args.SocketError == SocketError.Success && args.AcceptSocket is { } sock)
        {
            var state = (AcceptState)args.UserToken!;
            MaybeNoDelay(sock);
            var conn = Register(sock, state.DefaultToken);

            int sendBytes;
            fixed (byte* buf = conn.SendBuffer)
            {
                var ctx = new SocketSet.AcceptContext(SocketSet.SocketFlags.None, state.DefaultToken, buf, _bufferSize);
                Parent.OnAccept(ref ctx);
                conn.UserToken = ctx.UserToken;
                conn.Flags = ctx.Flags;
                sendBytes = ctx.SendBytes;
            }

            if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) PumpReceive(conn);
            if (sendBytes > 0) QueueSend(conn, conn.SendBuffer, sendBytes);
        }

        return true;
    }

    // =====================================================================
    // Connect
    // =====================================================================

    public override void Connect(EndPoint endpoint, object? userToken)
    {
        var target = Normalize(endpoint);
        var socket = NewSocket(target);
        var args = new SocketAsyncEventArgs { RemoteEndPoint = target, UserToken = userToken };
        args.Completed += OnConnectCompleted;
        try
        {
            if (!socket.ConnectAsync(args)) OnConnectCompleted(socket, args);
        }
        catch
        {
            SafeDispose(socket);
            args.Dispose();
        }
    }

    private void OnConnectCompleted(object? sender, SocketAsyncEventArgs args)
    {
        var socket = args.ConnectSocket ?? sender as Socket;
        object? token = args.UserToken;

        if (args.SocketError != SocketError.Success || socket is null)
        {
            if (socket is not null) SafeDispose(socket);
            args.Dispose();
            return;
        }

        MaybeNoDelay(socket);
        var conn = Register(socket, token);

        int sendBytes;
        fixed (byte* buf = conn.SendBuffer)
        {
            var ctx = new SocketSet.ConnectContext(SocketSet.SocketFlags.None, token, buf, _bufferSize);
            Parent.OnConnect(ref ctx);
            conn.UserToken = ctx.UserToken;
            conn.Flags = ctx.Flags;
            sendBytes = ctx.SendBytes;
        }

        args.Dispose(); // connect SAEA no longer needed

        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) PumpReceive(conn);
        if (sendBytes > 0) QueueSend(conn, conn.SendBuffer, sendBytes);
    }

    // =====================================================================
    // Receive
    // =====================================================================

    private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs args)
    {
        var conn = (Connection)args.UserToken!;
        if (ProcessReceive(conn)) PumpReceive(conn);
    }

    private void PumpReceive(Connection conn)
    {
        while (true)
        {
            conn.RecvArgs.SetBuffer(conn.RecvBuffer, 0, _bufferSize);
            bool pending;
            try { pending = conn.Socket.ReceiveAsync(conn.RecvArgs); }
            catch (ObjectDisposedException) { return; }
            if (pending) return;             // OnReceiveCompleted will fire
            if (!ProcessReceive(conn)) return; // synchronous completion; loop unless stopping
        }
    }

    /// <returns>true to keep receiving, false to stop (closed / input shut).</returns>
    private bool ProcessReceive(Connection conn)
    {
        var args = conn.RecvArgs;
        int n = args.BytesTransferred;
        if (args.SocketError != SocketError.Success || n == 0)
        {
            Close(conn);
            return false;
        }

        int response;
        fixed (byte* buf = conn.RecvBuffer)
        {
            var ctx = new SocketSet.ReceiveContext(conn.Flags, conn.UserToken, buf, _bufferSize, n);
            Parent.OnReceive(ref ctx);
            conn.UserToken = ctx.UserToken;
            conn.Flags = ctx.Flags;
            response = ctx.ResponseBytes;
        }

        // QueueSend copies the reply out, so it's safe to re-arm the receive afterwards.
        if (response > 0)
            QueueSend(conn, conn.RecvBuffer, response);

        return (conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0;
    }

    // =====================================================================
    // Send (with partial-write handling)
    // =====================================================================

    /// <summary>Copy <paramref name="length"/> bytes from <paramref name="source"/> and
    /// enqueue them for sending. Safe to call from any completion thread; the send is
    /// serialized per connection.</summary>
    private void QueueSend(Connection conn, byte[] source, int length)
    {
        var data = new byte[length];
        Buffer.BlockCopy(source, 0, data, 0, length);
        lock (conn.SendGate)
        {
            conn.SendQueue.Enqueue(data);
            if (conn.SendInFlight) return; // the active chain will drain it
            conn.SendInFlight = true;
        }
        PumpSend(conn);
    }

    private void OnSendCompleted(object? sender, SocketAsyncEventArgs args)
    {
        var conn = (Connection)args.UserToken!;
        if (args.SocketError != SocketError.Success) { Close(conn); return; }
        conn.SendOffset += args.BytesTransferred;
        if (conn.CurrentSend is { } data && conn.SendOffset >= data.Length) CompleteCurrentSend(conn);
        PumpSend(conn);
    }

    private void PumpSend(Connection conn)
    {
        while (true)
        {
            if (conn.CurrentSend is null)
            {
                lock (conn.SendGate)
                {
                    if (conn.SendQueue.Count == 0) { conn.SendInFlight = false; return; }
                    conn.CurrentSend = conn.SendQueue.Dequeue();
                    conn.SendOffset = 0;
                }
            }

            var data = conn.CurrentSend;
            conn.SendArgs.SetBuffer(data, conn.SendOffset, data.Length - conn.SendOffset);
            bool pending;
            try { pending = conn.Socket.SendAsync(conn.SendArgs); }
            catch (ObjectDisposedException) { return; }
            if (pending) return; // OnSendCompleted resumes the chain

            if (conn.SendArgs.SocketError != SocketError.Success) { Close(conn); return; }
            conn.SendOffset += conn.SendArgs.BytesTransferred;
            if (conn.SendOffset >= data.Length) CompleteCurrentSend(conn);
        }
    }

    private void CompleteCurrentSend(Connection conn)
    {
        conn.CurrentSend = null;
        var ctx = new SocketSet.WriteContext(conn.Flags, conn.UserToken);
        Parent.OnWrite(ref ctx);
        conn.UserToken = ctx.UserToken;
        conn.Flags = ctx.Flags;
    }

    // =====================================================================
    // Connection lifecycle / helpers
    // =====================================================================

    private Connection Register(Socket socket, object? token)
    {
        var conn = new Connection(socket, _bufferSize) { UserToken = token };
        conn.RecvArgs.UserToken = conn;
        conn.RecvArgs.Completed += OnReceiveCompleted;
        conn.SendArgs.UserToken = conn;
        conn.SendArgs.Completed += OnSendCompleted;
        _connections[conn] = 0;
        return conn;
    }

    private void Close(Connection conn)
    {
        if (!_connections.TryRemove(conn, out _)) return; // already closed
        try { conn.Socket.Shutdown(SocketShutdown.Both); } catch { /* best effort */ }
        SafeDispose(conn.Socket);
        conn.RecvArgs.Dispose();
        conn.SendArgs.Dispose();
    }

    private static void MaybeNoDelay(Socket s)
    {
        if (s.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
        {
            try { s.NoDelay = true; } catch { /* not fatal */ }
        }
    }

    private static Socket NewSocket(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp),
#if NET
        UnixDomainSocketEndPoint => new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
#endif
        _ => throw new NotSupportedException(endpoint.GetType().Name),
    };

    private static Socket Bind(EndPoint endpoint)
    {
        var target = Normalize(endpoint);
        var s = NewSocket(target);
        if (target is IPEndPoint)
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            MaybeNoDelay(s);
        }
        s.Bind(target);
        return s;
    }

    /// <summary>Map our "@name" abstract-socket convention onto the leading-NUL form
    /// that .NET's <see cref="UnixDomainSocketEndPoint"/> uses for the abstract namespace.</summary>
    private static EndPoint Normalize(EndPoint endpoint)
    {
#if NET
        if (endpoint is UnixDomainSocketEndPoint uds)
        {
            var path = uds.ToString()!;
            if (path.StartsWith('@'))
                return new UnixDomainSocketEndPoint("\0" + path[1..]);
        }
#endif
        return endpoint;
    }

    private static void SafeDispose(Socket s)
    {
        try { s.Dispose(); } catch { /* best effort */ }
    }

    private sealed class Connection(Socket socket, int bufferSize)
    {
        public readonly Socket Socket = socket;
        public object? UserToken;
        public SocketSet.SocketFlags Flags;

        public readonly byte[] RecvBuffer = new byte[bufferSize];
        public readonly byte[] SendBuffer = new byte[bufferSize]; // scratch the handler writes into
        public readonly SocketAsyncEventArgs RecvArgs = new();
        public readonly SocketAsyncEventArgs SendArgs = new();

        // A SocketAsyncEventArgs cannot be reused while its operation is in flight, and
        // concurrent sends on one socket could reorder the byte stream — so sends are
        // serialized per connection through this queue. Only one SendAsync is ever
        // outstanding; CurrentSend/SendOffset belong to that single active chain.
        public readonly object SendGate = new();
        public bool SendInFlight;
        public readonly Queue<byte[]> SendQueue = new();
        public byte[]? CurrentSend;
        public int SendOffset;
    }
}
