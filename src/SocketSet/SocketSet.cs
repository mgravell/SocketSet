using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SocketSets;

public abstract partial class SocketSet : IDisposable
{
    public SocketSetOptions Options { get; }
    private SocketSetShard[] _shards;
    private int _next; // int (not uint) so Interlocked.Increment works on netfx too

    // Startup handshake: each shard signals the gate once it has attempted its own
    // (worker-thread-bound) initialization, recording any failure. The constructor
    // blocks on the gate so it can fail fast rather than return a set with silently
    // dead shards that would swallow the work routed to them.
    private readonly CountdownEvent? _startupGate;
    private readonly ConcurrentBag<Exception> _startupFaults = [];

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    protected SocketSet(SocketSetOptions options)
    {
        Options = options;
        var factory = options.Factory;

        // Some backends cap the shard count (e.g. the managed fallback wants exactly 1).
        // (Math.Clamp isn't available on netfx; MaxShards is always >= 1 so this matches it.)
        int shardCount = Math.Min(Math.Max(options.Shards, 1), factory.MaxShards);

        // init all first
        var arr = new SocketSetShard[shardCount];
        for (int i = 0; i < arr.Length; i++)
        {
            var shard = factory.CreateShard(options);
            shard.Init(this, i);
            arr[i] = shard;
        }

        _shards = arr;

        // ReSharper disable once VirtualMemberCallInConstructor
        var name = Name;

        if (factory.UsesWorkerThreads)
        {
            // Thread-per-shard backend (io_uring): init runs on the pump thread because
            // the ring is single-issuer. A gate lets us block until all shards report.
            _startupGate = new CountdownEvent(arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                var thread = new Thread(static state => ((SocketSetShard)state!).Run());
                thread.IsBackground = true;
                thread.Priority = ThreadPriority.AboveNormal;
                thread.Name = $"{name} worker {i}";
                thread.Start(arr[i]);
            }

            if (!_startupGate.Wait(StartupTimeout))
            {
                Dispose();
                throw new TimeoutException($"{name}: shards did not finish initializing within {StartupTimeout.TotalSeconds:0}s.");
            }
        }
        else
        {
            // Callback-driven backend (managed SAEA): no pump threads. Initialize inline
            // so failures still surface as construction exceptions.
            foreach (var shard in arr)
            {
                try { shard.InitializeInline(); }
                catch (Exception ex) { _startupFaults.Add(ex); }
            }
        }

        if (!_startupFaults.IsEmpty)
        {
            Dispose();
            throw new AggregateException($"{name}: one or more shards failed to initialize.", _startupFaults);
        }
    }

    internal void SignalStartupComplete() => _startupGate!.Signal();

    internal void RecordStartupFault(Exception ex) => _startupFaults.Add(ex);

    public virtual string Name => GetType().Name;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Dispose(true);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var shard in _shards)
            {
                shard.Stop();
            }
        }
    }

    protected internal virtual void OnWorkerFaulted(Exception exception)
    {
        Debug.WriteLine(exception.Message);
    }

    internal SocketSetShard RoundRobin()
    {
        var arr = _shards;
        // Take the modulo unsigned so the index stays valid after the counter wraps
        // past int.MaxValue into negatives.
        uint next = (uint)Interlocked.Increment(ref _next);
        return arr[next % (uint)arr.Length];
    }

    public void Listen(EndPoint endpoint, object? userToken = null)
    {
        if (Options.Factory.CanMultiBind(endpoint))
        {
            // Reuse-port: every shard binds its own listener and the kernel balances accepts (io_uring/IP).
            foreach (var shard in _shards)
            {
                shard.Listen(endpoint, userToken, local: true);
            }
        }
        else
        {
            // Single listener on one shard, which bounces accepted connections round-robin. Because the
            // shard is chosen round-robin, distinct listen endpoints land on different shards rather than
            // all piling their accept load onto shard 0.
            RoundRobin().Listen(endpoint, userToken, local: false);
        }
    }

    public void Connect(EndPoint endpoint, object? userToken = null)
        => RoundRobin().Connect(endpoint, userToken);

    /// <summary>
    /// Start accepting on an already-bound-and-listening socket handle (an fd on Linux/io_uring, a
    /// <see cref="System.Net.Sockets.Socket"/> handle on the managed backend) instead of binding one
    /// — e.g. a socket-activation / systemd-inherited listener, or one handed over for a zero-downtime
    /// restart. (Mirrors Kestrel's <c>ListenHandle</c>.) Since it is a single handle (not reuse-port
    /// multi-bound), one shard drives the accept and spreads connections across shards.
    /// <paramref name="userToken"/> becomes the default <see cref="Connection.UserToken"/> for
    /// connections accepted on it. The set takes ownership of the handle and closes it on teardown.
    /// </summary>
    public void ListenHandle(nint handle, object? userToken = null)
        => RoundRobin().ListenHandle(handle, userToken);

    protected internal virtual void OnAccept(ref AcceptContext ctx)
    {
    }

    protected internal virtual void OnReceive(ref ReceiveContext ctx)
    {
    }

    protected internal virtual void OnWrite(ref WriteContext ctx)
    {
    }

    protected internal virtual void OnConnect(ref ConnectContext ctx)
    {
    }

    /// <summary>
    /// A connection has been torn down — peer close (EOF), a transport error, or a local
    /// <see cref="Connection.Close"/>. Fires exactly once, on the owning IO thread, and only for a
    /// connection the application actually saw open (i.e. paired with an <see cref="OnAccept"/> or
    /// <see cref="OnConnect"/>). The fd is already closed by the time this runs; it is a notification
    /// for bookkeeping (the <paramref name="connection"/>'s <see cref="Connection.UserToken"/> is
    /// still readable). After it returns the connection is recycled — do not retain it.
    /// </summary>
    protected internal virtual void OnClosed(Connection connection)
    {
    }

    // Each context carries the Connection (the per-connection identity, which owns UserToken and
    // the send/recv-closed Flags) plus, where relevant, a raw pointer + length into a
    // backend-owned buffer (io_uring provided/write buffers, the managed scratch). The buffer is
    // surfaced as Span<byte> on demand. Because UserToken and Flags live on the Connection, the
    // handler mutates it directly — nothing is copied in or out across the callback.

    protected internal unsafe ref struct AcceptContext(Connection connection, byte* buffer, int bufferLength)
    {
        private readonly byte* _buffer = buffer;
        private readonly int _bufferLength = bufferLength;

        /// <summary>The connection being accepted; carries <see cref="Connection.UserToken"/>.</summary>
        public readonly Connection Connection => connection;

        /// <summary>Disable writing, for one-way (client-to-server) transports.</summary>
        public readonly void CloseOutput() => connection.Flags |= SocketFlags.SendClosed;

        /// <summary>Disable reading, for one-way (server-to-client) transports.</summary>
        public readonly void CloseInput() => connection.Flags |= SocketFlags.ReceiveClosed;

        /// <summary>
        /// A library-owned outbound buffer. Write an initial payload here and set
        /// <see cref="SendBytes"/> to have it sent as soon as the socket is accepted.
        /// </summary>
        public readonly Span<byte> SendBuffer => new(_buffer, _bufferLength);

        /// <summary>Number of leading bytes of <see cref="SendBuffer"/> to send. 0 = send nothing.</summary>
        public int SendBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _bufferLength) throw new ArgumentOutOfRangeException(nameof(SendBytes));
                field = value;
            }
        }

        public readonly SocketFlags Flags => connection.Flags;
    }

    protected internal unsafe ref struct ConnectContext(Connection connection, byte* buffer, int bufferLength)
    {
        private readonly byte* _buffer = buffer;
        private readonly int _bufferLength = bufferLength;

        /// <summary>The connection just established; carries <see cref="Connection.UserToken"/>.</summary>
        public readonly Connection Connection => connection;

        /// <summary>Disable writing, for one-way (client-to-server) transports.</summary>
        public readonly void CloseOutput() => connection.Flags |= SocketFlags.SendClosed;

        /// <summary>Disable reading, for one-way (server-to-client) transports.</summary>
        public readonly void CloseInput() => connection.Flags |= SocketFlags.ReceiveClosed;

        /// <summary>
        /// A library-owned outbound buffer. Write an initial handshake/greeting here and set
        /// <see cref="SendBytes"/> to have it sent as soon as the connection completes.
        /// </summary>
        public readonly Span<byte> SendBuffer => new(_buffer, _bufferLength);

        /// <summary>Number of leading bytes of <see cref="SendBuffer"/> to send. 0 = send nothing.</summary>
        public int SendBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _bufferLength) throw new ArgumentOutOfRangeException(nameof(SendBytes));
                field = value;
            }
        }

        public readonly SocketFlags Flags => connection.Flags;
    }

    /// <summary>
    /// Represents a receive-buffer. The oversized backing buffer can also be used for inline
    /// replies by writing to <see cref="RawBuffer"/> and <see cref="ResponseBytes"/>, noting
    /// that reuses same buffer that underpins <see cref="Payload"/>, meaning that the
    /// received payload will be overwritten.
    /// </summary>
    protected internal unsafe ref struct ReceiveContext(Connection connection, byte* buffer, int bufferLength, int bytes)
    {
        private readonly byte* _buffer = buffer;
        private readonly int _bufferLength = bufferLength;

        /// <summary>The connection that received data; carries <see cref="Connection.UserToken"/>.</summary>
        public readonly Connection Connection => connection;

        public readonly int PayloadBytes => bytes;
        public readonly ReadOnlySpan<byte> Payload => new(_buffer, bytes);

        /// <summary>
        /// The buffer that holds the <see cref="PayloadBytes"/> of received payload, and can also be used
        /// to provide an immediate response by setting <see cref="ResponseBytes"/>.
        /// </summary>
        public readonly Span<byte> RawBuffer => new(_buffer, _bufferLength);

        /// <summary>
        /// Indicates the end of a receive stream.
        /// </summary>
        public readonly bool IsEof => bytes is 0;

        /// <remarks>It is the implementation's responsibility to handle <see cref="ResponseBytes"/>
        /// appropriately, whether that means reusing an existing socket buffer efficiently,
        /// or by copying the data into a new buffer.</remarks>
        public int ResponseBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _bufferLength) Throw();
                field = value;
                static void Throw() => throw new ArgumentOutOfRangeException(nameof(ResponseBytes));
            }
        }

        public readonly SocketFlags Flags => connection.Flags | (bytes is 0 ? SocketFlags.ReceiveClosed : 0);

        /// <summary>
        /// Disable further reads.
        /// </summary>
        public readonly void CloseInput() => connection.Flags |= SocketFlags.ReceiveClosed;
    }

    /// <summary>
    /// Indicates that a write has <em>fully</em> completed (implementations coalesce partial
    /// writes, so this never fires for a partial). To pipeline — keep the write pipe full
    /// without waiting for a reply — write the next payload into <see cref="SendBuffer"/> and
    /// set <see cref="SendBytes"/>; the implementation sends it immediately, reusing the just
    /// freed buffer. Still one write in flight per connection: the next is issued only once
    /// this one has completed.
    /// </summary>
    protected internal unsafe ref struct WriteContext(Connection connection, byte* buffer, int bufferLength)
    {
        private readonly byte* _buffer = buffer;
        private readonly int _bufferLength = bufferLength;

        /// <summary>The connection whose write completed; carries <see cref="Connection.UserToken"/>.</summary>
        public readonly Connection Connection => connection;

        /// <summary>The buffer just written, now free; write the next payload here to pipeline.</summary>
        public readonly Span<byte> SendBuffer => new(_buffer, _bufferLength);

        /// <summary>Number of leading bytes of <see cref="SendBuffer"/> to send next. 0 = send nothing.</summary>
        public int SendBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _bufferLength) throw new ArgumentOutOfRangeException(nameof(SendBytes));
                field = value;
            }
        }

        public readonly SocketFlags Flags => connection.Flags;

        /// <summary>
        /// Disable further writes.
        /// </summary>
        public readonly void CloseOutput() => connection.Flags |= SocketFlags.SendClosed;
    }

    [Flags]
    public enum SocketFlags
    {
        None = 0,
        ReceiveClosed = 1 << 0,
        SendClosed = 1 << 1,
    }
}