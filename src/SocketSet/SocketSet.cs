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
        if (endpoint is IPEndPoint)
        {
            // can multi-bind (reuse-port)
            foreach (var shard in _shards)
            {
                shard.Listen(endpoint, userToken, local: true);
            }
        }
        else
        {
            RoundRobin().Listen(endpoint, userToken, local: false);
        }
    }

    public void Connect(EndPoint endpoint, object? userToken = null)
        => RoundRobin().Connect(endpoint, userToken);

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

    // The contexts store the backing buffer as a raw pointer + length rather than a
    // Span<byte> field. Reasons: (1) the transports already own native/pinned memory
    // (io_uring provided buffers, the write pool), so a pointer is the natural currency;
    // (2) it keeps the token a plain by-value object? rather than a `ref` field, which
    // would require runtime byref-field support (.NET 7+) and rule out netfx. The
    // user-facing surface is still Span<byte>, materialized on demand. The token is
    // copied in by the transport and read back out after the callback returns.

    protected internal unsafe ref struct AcceptContext(SocketFlags flags, object? userToken, byte* buffer, int bufferLength)
    {
        private readonly byte* _buffer = buffer;
        private readonly int _bufferLength = bufferLength;
        private SocketFlags _flags = flags;

        /// <summary>The object associated with the socket.</summary>
        public object? UserToken { get; set; } = userToken;

        /// <summary>Disable writing, for one-way (client-to-server) transports.</summary>
        public void CloseOutput() => _flags |= SocketFlags.SendClosed;

        /// <summary>Disable reading, for one-way (server-to-client) transports.</summary>
        public void CloseInput() => _flags |= SocketFlags.ReceiveClosed;

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

        public readonly SocketFlags Flags => _flags;
    }

    protected internal unsafe ref struct ConnectContext(SocketFlags flags, object? userToken, byte* buffer, int bufferLength)
    {
        private readonly byte* _buffer = buffer;
        private readonly int _bufferLength = bufferLength;
        private SocketFlags _flags = flags;

        /// <summary>The object associated with the socket.</summary>
        public object? UserToken { get; set; } = userToken;

        /// <summary>Disable writing, for one-way (client-to-server) transports.</summary>
        public void CloseOutput() => _flags |= SocketFlags.SendClosed;

        /// <summary>Disable reading, for one-way (server-to-client) transports.</summary>
        public void CloseInput() => _flags |= SocketFlags.ReceiveClosed;

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

        public readonly SocketFlags Flags => _flags;
    }

    /// <summary>
    /// Represents a receive-buffer. The oversized backing buffer can also be used for inline
    /// replies by writing to <see cref="RawBuffer"/> and <see cref="ResponseBytes"/>, noting
    /// that reuses same buffer that underpins <see cref="Payload"/>, meaning that the
    /// received payload will be overwritten.
    /// </summary>
    protected internal unsafe ref struct ReceiveContext(SocketFlags flags, object? userToken, byte* buffer, int bufferLength, int bytes)
    {
        private readonly byte* _buffer = buffer;
        private readonly int _bufferLength = bufferLength;
        private SocketFlags _flags = flags;

        /// <summary>The object associated with the socket.</summary>
        public object? UserToken { get; set; } = userToken;

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

        public readonly SocketFlags Flags => _flags | (bytes is 0 ? SocketFlags.ReceiveClosed : 0);

        /// <summary>
        /// Disable further reads.
        /// </summary>
        public void CloseInput() => _flags |= SocketFlags.ReceiveClosed;
    }

    /// <summary>Indicates that a write has completed. The implementation will handle partial
    /// writes internally, so this indicates a complete write.</summary>
    protected internal ref struct WriteContext(SocketFlags flags, object? userToken)
    {
        private SocketFlags _flags = flags;

        /// <summary>The object associated with the socket.</summary>
        public object? UserToken { get; set; } = userToken;

        public readonly SocketFlags Flags => _flags;

        /// <summary>
        /// Disable further writes.
        /// </summary>
        public void CloseOutput() => _flags |= SocketFlags.SendClosed;
    }

    [Flags]
    public enum SocketFlags
    {
        None = 0,
        ReceiveClosed = 1 << 0,
        SendClosed = 1 << 1,
    }
}