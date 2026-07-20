using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SocketSets.Managed;

/// <summary>
/// A callback-driven shard over .NET sockets + SocketAsyncEventArgs. There is no pump
/// thread: accept/receive/send completions run on thread-pool threads. Handlers may
/// therefore fire concurrently across connections — the same contract as the io_uring
/// backend (which fires concurrently across shards), so user callbacks must be thread-safe.
///
/// Each SocketAsyncEventArgs is a strongly-typed subclass that carries its own state as
/// fields and overrides OnCompleted to dispatch straight back into the shard — no Completed
/// event delegate, no boxing into UserToken, no base.OnCompleted. This is the shape an
/// optimized SAEA server uses, so the backend is a fair comparison rather than a naive one.
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

        StartAccept(new AcceptArgs(this, listener, defaultToken));
    }

    private void StartAccept(AcceptArgs args)
    {
        while (true)
        {
            args.AcceptSocket = null; // must be cleared before reuse
            bool pending;
            try { pending = args.Listener.AcceptAsync(args); }
            catch (ObjectDisposedException) { return; }
            if (pending) return;              // AcceptArgs.OnCompleted will fire
            if (!ProcessAccept(args)) return; // synchronous completion; loop unless told to stop
        }
    }

    /// <returns>true to keep accepting, false to stop (listener closed).</returns>
    private bool ProcessAccept(AcceptArgs args)
    {
        if (args.SocketError == SocketError.OperationAborted) return false;

        if (args.SocketError == SocketError.Success && args.AcceptSocket is { } sock)
        {
            MaybeNoDelay(sock);
            var conn = Register(sock, args.DefaultToken);

            int sendBytes;
            fixed (byte* buf = conn.SendBuffer)
            {
                var ctx = new SocketSet.AcceptContext(SocketSet.SocketFlags.None, args.DefaultToken, buf, _bufferSize);
                Parent.OnAccept(ref ctx);
                conn.UserToken = ctx.UserToken;
                conn.Flags = ctx.Flags;
                sendBytes = ctx.SendBytes;
            }

            if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0) PumpReceive(conn);
            if (sendBytes > 0) BeginSend(conn, conn.SendBuffer, sendBytes);
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
        var args = new ConnectArgs(this, userToken) { RemoteEndPoint = target };
        try
        {
            if (!socket.ConnectAsync(args)) CompleteConnect(args);
        }
        catch
        {
            SafeDispose(socket);
            args.Dispose();
        }
    }

    private void CompleteConnect(ConnectArgs args)
    {
        var socket = args.ConnectSocket;
        if (args.SocketError != SocketError.Success || socket is null)
        {
            if (socket is not null) SafeDispose(socket);
            args.Dispose();
            return;
        }

        MaybeNoDelay(socket);
        var token = args.Token;
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
        if (sendBytes > 0) BeginSend(conn, conn.SendBuffer, sendBytes);
    }

    // =====================================================================
    // Receive
    // =====================================================================

    private void PumpReceive(Connection conn)
    {
        while (true)
        {
            bool pending;
            try
            {
                // SetBuffer is inside the guard too: a concurrent Close can dispose the
                // socket between iterations, and touching it must fail gracefully, not throw.
                conn.RecvArgs.SetBuffer(conn.RecvBuffer, 0, _bufferSize);
                pending = conn.Socket.ReceiveAsync(conn.RecvArgs);
            }
            catch (ObjectDisposedException) { return; } // connection closed under us
            if (pending) return;               // ConnArgs.OnCompleted will fire
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

        // BeginSend copies the reply into the send buffer, so re-arming the receive
        // (which reuses RecvBuffer) afterwards is safe.
        if (response > 0)
            BeginSend(conn, conn.RecvBuffer, response);

        return (conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0;
    }

    // =====================================================================
    // Send (serialized per connection, with partial-write handling)
    // =====================================================================

    /// <summary>Begin (or queue) a send of <paramref name="length"/> bytes from
    /// <paramref name="source"/>. When the connection is idle we reuse its scratch buffer —
    /// no per-send allocation — copying only if the source isn't already that buffer. Only a
    /// genuine overlap (a new send requested while one is in flight) allocates, to preserve
    /// ordering. Sends stay serialized: one SAEA operation in flight at a time.</summary>
    private void BeginSend(Connection conn, byte[] source, int length)
    {
        lock (conn.SendGate)
        {
            if (conn.SendInFlight)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(source, 0, copy, 0, length);
                conn.Overflow.Enqueue(copy);
                return;
            }

            conn.SendInFlight = true;
            if (!ReferenceEquals(source, conn.SendBuffer))
                Buffer.BlockCopy(source, 0, conn.SendBuffer, 0, length);
            conn.CurrentBuffer = conn.SendBuffer;
            conn.CurrentLength = length;
            conn.SendOffset = 0;
        }
        PumpSend(conn);
    }

    // Async send completion (from ConnArgs.OnCompleted).
    private void AdvanceSend(Connection conn)
    {
        if (conn.SendArgs.SocketError != SocketError.Success) { Close(conn); return; }
        conn.SendOffset += conn.SendArgs.BytesTransferred;
        if (conn.SendOffset >= conn.CurrentLength && !CompleteAndAdvance(conn)) return;
        PumpSend(conn);
    }

    private void PumpSend(Connection conn)
    {
        while (true)
        {
            var data = conn.CurrentBuffer!;
            bool pending;
            try
            {
                conn.SendArgs.SetBuffer(data, conn.SendOffset, conn.CurrentLength - conn.SendOffset);
                pending = conn.Socket.SendAsync(conn.SendArgs);
            }
            catch (ObjectDisposedException) { return; } // connection closed under us
            if (pending) return; // ConnArgs.OnCompleted resumes the chain

            if (conn.SendArgs.SocketError != SocketError.Success) { Close(conn); return; }
            conn.SendOffset += conn.SendArgs.BytesTransferred;
            if (conn.SendOffset >= conn.CurrentLength && !CompleteAndAdvance(conn)) return;
        }
    }

    /// <summary>One app-requested write finished: fire OnWrite (which may pipeline the next
    /// straight into the scratch buffer), then pick what to send next.</summary>
    /// <returns>true if another buffer is queued (CurrentBuffer set), false if now idle.</returns>
    private bool CompleteAndAdvance(Connection conn)
    {
        int next;
        fixed (byte* buf = conn.SendBuffer)
        {
            var ctx = new SocketSet.WriteContext(conn.Flags, conn.UserToken, buf, conn.SendBuffer.Length);
            Parent.OnWrite(ref ctx);
            conn.UserToken = ctx.UserToken;
            conn.Flags = ctx.Flags;
            next = ctx.SendBytes;
        }

        lock (conn.SendGate)
        {
            if (conn.Overflow.Count > 0)
            {
                // Earlier-queued responses go first; a pipelined write joins the tail.
                if (next > 0)
                {
                    var copy = new byte[next];
                    Buffer.BlockCopy(conn.SendBuffer, 0, copy, 0, next);
                    conn.Overflow.Enqueue(copy);
                }
                conn.CurrentBuffer = conn.Overflow.Dequeue();
                conn.CurrentLength = conn.CurrentBuffer.Length;
                conn.SendOffset = 0;
                return true;
            }

            if (next > 0)
            {
                conn.CurrentBuffer = conn.SendBuffer; // reuse the scratch — no allocation
                conn.CurrentLength = next;
                conn.SendOffset = 0;
                return true;
            }

            conn.SendInFlight = false;
            conn.CurrentBuffer = null;
            return false;
        }
    }

    // =====================================================================
    // Connection lifecycle / helpers
    // =====================================================================

    private Connection Register(Socket socket, object? token)
    {
        var conn = new Connection(this, socket, _bufferSize) { UserToken = token };
        _connections[conn] = 0;
        return conn;
    }

    private void Close(Connection conn)
    {
        if (!_connections.TryRemove(conn, out _)) return; // already closed
        try { conn.Socket.Shutdown(SocketShutdown.Both); } catch { /* best effort */ }
        SafeDispose(conn.Socket);
        // Deliberately do NOT dispose RecvArgs/SendArgs here: a receive or send may still be
        // in flight, and disposing a SocketAsyncEventArgs with a pending operation makes that
        // operation's completion throw ObjectDisposedException on a thread-pool thread — an
        // unhandled crash. Disposing the socket aborts the pending ops; the SAEAs are then
        // unreferenced and reclaimed by GC (their finalizer frees the native resources).
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

    // ---------------------------------------------------------------------
    // Strongly-typed SocketAsyncEventArgs subclasses (state as fields; direct dispatch).
    // ---------------------------------------------------------------------

    private sealed class AcceptArgs(ManagedSocketShard shard, Socket listener, object? defaultToken) : SocketAsyncEventArgs
    {
        public readonly ManagedSocketShard Shard = shard;
        public readonly Socket Listener = listener;
        public readonly object? DefaultToken = defaultToken;

        protected override void OnCompleted(SocketAsyncEventArgs e)
        {
            if (Shard.ProcessAccept(this)) Shard.StartAccept(this);
        }
    }

    private sealed class ConnectArgs(ManagedSocketShard shard, object? token) : SocketAsyncEventArgs
    {
        public readonly ManagedSocketShard Shard = shard;
        public readonly object? Token = token;

        protected override void OnCompleted(SocketAsyncEventArgs e) => Shard.CompleteConnect(this);
    }

    // One instance per direction per connection (recv + send are full-duplex, so each
    // needs its own SAEA). LastOperation is stable per instance, so the dispatch is a
    // single check rather than a delegate call.
    private sealed class ConnArgs(ManagedSocketShard shard) : SocketAsyncEventArgs
    {
        public readonly ManagedSocketShard Shard = shard;
        public Connection Conn = null!; // set once, immediately after the owning Connection is built

        protected override void OnCompleted(SocketAsyncEventArgs e)
        {
            // Backstop: completions run on thread-pool threads, so any exception escaping
            // here is an unhandled process-level crash. A connection torn down mid-flight
            // can still surface a disposed-object error; swallow it and close cleanly.
            try
            {
                switch (LastOperation)
                {
                    case SocketAsyncOperation.Receive:
                        if (Shard.ProcessReceive(Conn)) Shard.PumpReceive(Conn);
                        break;
                    case SocketAsyncOperation.Send:
                        Shard.AdvanceSend(Conn);
                        break;
                }
            }
            catch (ObjectDisposedException)
            {
                Shard.Close(Conn);
            }
        }
    }

    private sealed class Connection
    {
        public readonly Socket Socket;
        public object? UserToken;
        public SocketSet.SocketFlags Flags;

        public readonly byte[] RecvBuffer;
        public readonly byte[] SendBuffer; // scratch the handler writes into
        public readonly ConnArgs RecvArgs;
        public readonly ConnArgs SendArgs;

        // Sends are serialized per connection: a SAEA can't be reused mid-flight, and
        // concurrent sends on one socket could reorder the byte stream. Only one SendAsync
        // is ever outstanding. The steady state reuses SendBuffer (CurrentBuffer points at
        // it); Overflow holds copies only for the rare case of a send requested while one is
        // already in flight. CurrentBuffer/CurrentLength/SendOffset belong to the active chain.
        public readonly object SendGate = new();
        public bool SendInFlight;
        public readonly Queue<byte[]> Overflow = new();
        public byte[]? CurrentBuffer;
        public int CurrentLength;
        public int SendOffset;

        public Connection(ManagedSocketShard shard, Socket socket, int bufferSize)
        {
            Socket = socket;
            RecvBuffer = new byte[bufferSize];
            SendBuffer = new byte[bufferSize];
            RecvArgs = new ConnArgs(shard) { Conn = this };
            SendArgs = new ConnArgs(shard) { Conn = this };
        }
    }
}
