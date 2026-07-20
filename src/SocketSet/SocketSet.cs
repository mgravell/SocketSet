using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SocketSets;

public abstract partial class SocketSet : IDisposable
{
    public SocketSetOptions Options { get; }
    private SocketSetShard[] _shards;
    private uint _next;

    // Startup handshake: each shard signals the gate once it has attempted its own
    // (worker-thread-bound) initialization, recording any failure. The constructor
    // blocks on the gate so it can fail fast rather than return a set with silently
    // dead shards that would swallow the work routed to them.
    private readonly CountdownEvent _startupGate;
    private readonly ConcurrentBag<Exception> _startupFaults = [];

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    protected SocketSet(SocketSetOptions options)
    {
        Options = options;
        // init all first
        var arr = new SocketSetShard[options.Shards];
        for (int i = 0; i < arr.Length; i++)
        {
            var shard = options.Factory.CreateShard(options);
            shard.Init(this, i);
            arr[i] = shard;
        }

        _shards = arr;
        _startupGate = new CountdownEvent(arr.Length);

        // start once init'd
        // ReSharper disable once VirtualMemberCallInConstructor
        var name = Name;
        for (int i = 0; i < arr.Length; i++)
        {
            var thread = new Thread(static state => ((SocketSetShard)state!).Run());
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Name = $"{name} worker {i}";
            thread.Start(arr[i]);
        }

        // Block until every shard has reported (success or failure), then surface any
        // startup failures as a construction-time exception.
        if (!_startupGate.Wait(StartupTimeout))
        {
            Dispose();
            throw new TimeoutException($"{name}: shards did not finish initializing within {StartupTimeout.TotalSeconds:0}s.");
        }

        if (!_startupFaults.IsEmpty)
        {
            Dispose();
            throw new AggregateException($"{name}: one or more shards failed to initialize.", _startupFaults);
        }
    }

    internal void SignalStartupComplete() => _startupGate.Signal();

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
        return arr[Interlocked.Increment(ref _next) % arr.Length];
    }

    public void Listen(EndPoint endpoint, object? userToken = null)
    {
        if (endpoint is IPEndPoint ip)
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

    protected internal ref struct AcceptContext(SocketFlags flags, ref object? userToken, Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;

        /// <summary>
        /// Disable writing, for one-way (client-to-server) transports.
        /// </summary>
        public void CloseOutput() => _flags |= SocketFlags.SendClosed;

        /// <summary>
        /// Disable reading, for one-way (server-to-client) transports.
        /// </summary>
        public void CloseInput() => _flags |= SocketFlags.ReceiveClosed;

        /// <summary>
        /// A library-owned outbound buffer. Write an initial payload here and set
        /// <see cref="SendBytes"/> to have it sent as soon as the socket is accepted.
        /// </summary>
        public Span<byte> SendBuffer => _buffer;

        /// <summary>Number of leading bytes of <see cref="SendBuffer"/> to send. 0 = send nothing.</summary>
        public int SendBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _buffer.Length) throw new ArgumentOutOfRangeException(nameof(SendBytes));
                field = value;
            }
        }

        /// <summary>
        /// The object associated with the socket.
        /// </summary>
        public readonly ref object? UserToken => ref _userToken;

        public readonly SocketFlags Flags => _flags;

        private readonly ref object? _userToken = ref userToken;
        private SocketFlags _flags = flags;
    }

    protected internal ref struct ConnectContext(SocketFlags flags, ref object? userToken, Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;

        /// <summary>
        /// Disable writing, for one-way (client-to-server) transports.
        /// </summary>
        public void CloseOutput() => _flags |= SocketFlags.SendClosed;

        /// <summary>
        /// Disable reading, for one-way (server-to-client) transports.
        /// </summary>
        public void CloseInput() => _flags |= SocketFlags.ReceiveClosed;

        /// <summary>
        /// A library-owned outbound buffer. Write an initial handshake/greeting here and set
        /// <see cref="SendBytes"/> to have it sent as soon as the connection completes.
        /// </summary>
        public Span<byte> SendBuffer => _buffer;

        /// <summary>Number of leading bytes of <see cref="SendBuffer"/> to send. 0 = send nothing.</summary>
        public int SendBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _buffer.Length) throw new ArgumentOutOfRangeException(nameof(SendBytes));
                field = value;
            }
        }

        /// <summary>
        /// The object associated with the socket.
        /// </summary>
        public readonly ref object? UserToken => ref _userToken;

        public readonly SocketFlags Flags => _flags;

        private readonly ref object? _userToken = ref userToken;
        private SocketFlags _flags = flags;
    }

    /// <summary>
    /// Represents a receive-buffer. The oversized backing buffer can also be used for inline
    /// replies by writing to <see cref="RawBuffer"/> and <see cref="ResponseBytes"/>, noting
    /// that reuses same buffer that underpins <see cref="Payload"/>, meaning that the
    /// received payload will be overwritten. 
    /// </summary>
    protected internal ref struct ReceiveContext(SocketFlags flags, ref object? userToken, Span<byte> buffer, int bytes)
    {
        private readonly Span<byte> _buffer = buffer;
        public int PayloadBytes => bytes;
        public ReadOnlySpan<byte> Payload => _buffer.Slice(0, bytes);

        /// <summary>
        /// The buffer that holds the <see cref="PayloadBytes"/> of received payload, and can also be used
        /// to provide an immediate response by setting <see cref="ResponseBytes"/>.
        /// </summary>
        public Span<byte> RawBuffer => _buffer;

        /// <summary>
        /// Indicates the end of a receive stream.
        /// </summary>
        public bool IsEof => bytes is 0;

        /// <remarks>It is the implementation's responsibility to handle <see cref="ResponseBytes"/>
        /// appropriately, whether that means reusing an existing socket buffer efficiently,
        /// or by copying the data into a new buffer.</remarks>
        public int ResponseBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _buffer.Length) Throw();
                field = value;
                static void Throw() => throw new ArgumentOutOfRangeException(nameof(ResponseBytes));
            }
        }

        public readonly SocketFlags Flags => _flags | (bytes is 0 ? SocketFlags.ReceiveClosed : 0);

        /// <summary>
        /// The object associated with the socket. 
        /// </summary>
        public readonly ref object? UserToken => ref _userToken;

        private readonly ref object? _userToken = ref userToken;
        private SocketFlags _flags = flags;

        /// <summary>
        /// Disable further reads. 
        /// </summary>
        public void CloseInput() => _flags |= SocketFlags.ReceiveClosed;
    }

    /// <summary>Indicates that a write has completed. The implementation will handle partial
    /// writes internally, so this indicates a complete write.</summary>
    /// <param name="userToken"></param>
    protected internal ref struct WriteContext(SocketFlags flags, ref object? userToken)
    {
        public SocketFlags Flags => _flags;

        /// <summary>
        /// The object associated with the socket. 
        /// </summary>
        public readonly ref object? UserToken => ref _userToken;

        private readonly ref object? _userToken = ref userToken;
        private SocketFlags _flags = flags;

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