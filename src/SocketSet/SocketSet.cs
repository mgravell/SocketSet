using System.Collections.Concurrent;
using System.Diagnostics;
#if NET
using System.IO.Pipelines; // pipe mode is net-only: the netfx build does not reference System.IO.Pipelines
#endif
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
    // Not readonly: shard GROWTH reuses this to wait for the one new shard, under _growLock, which
    // serialises growth and cannot overlap construction (construction has returned before any connection
    // exists to trigger growth).
    private CountdownEvent? _startupGate;

    // Serialises growth. Readers never take it: TryPlace/RoundRobin snapshot _shards into a local, so a
    // longer array published with Volatile.Write is observed atomically or not at all.
    private readonly object _growLock = new();
    private readonly ConcurrentBag<Exception> _startupFaults = [];

    // Multi-bind (reuse-port) listens, recorded so a shard grown AFTER Listen can replay them. On that
    // path each shard binds its OWN listener and the kernel balances accepts across them (io_uring/epoll
    // over IP); a grown shard has no listener until we bind one too, so without this the kernel never
    // routes a single accept to it — the one genuinely unfinished piece of dynamic shard growth. Guarded
    // by _growLock (recorded there, replayed there), so recording the endpoint and binding the shards that
    // exist is atomic against a concurrent growth: a shard created in between is bound by the replay, one
    // created after sees the completed record. Stays null on the single-listener path (Windows/UDS/
    // ListenHandle), where a grown shard already receives bounced connections with no listener changes.
    private List<(EndPoint Endpoint, object? Token)>? _multiBindListens;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    protected SocketSet(SocketSetOptions options)
    {
        var factory = options.Factory;

        // Fill the "backend chooses" sentinels ONCE, before anything reads a size. Everything downstream -
        // shards, and Options itself - sees resolved values only, so no backend knows the sentinels exist.
        // Options exposes the RESOLVED copy deliberately: a banner that printed the caller's instance
        // would report 0, or worse a size the backend is not using, which is the exact failure
        // bench/README.md rule 1 exists to prevent.
        options = options.ResolvedFor(factory);
        Options = options;

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

    /// <summary>One diagnostic line: derived type, backend, shard count, live connections (when the
    /// backend tracks them), and the TLS posture. TLS is per-SET (options-level): one provider and one
    /// <see cref="TlsMode"/> for every connection this set makes or accepts.
    /// e.g. <c>EchoServer [io_uring] shards=6 connections=42 tls=openssl (Both, ktls-capable)</c>.</summary>
    public override string ToString()
    {
        var shards = _shards; // snapshot: growth publishes a longer array atomically
        var sb = new System.Text.StringBuilder(Name)
            .Append(" [").Append(BackendLabel(shards)).Append("] shards=").Append(shards.Length);

        int conns = 0;
        bool tracked = shards.Length > 0;
        foreach (var s in shards)
        {
            int c = s.ActiveConnections;
            if (c < 0) { tracked = false; break; }
            conns += c;
        }
        if (tracked) sb.Append(" connections=").Append(conns);

        if (Options.Tls is { } tls)
        {
            sb.Append(" tls=").Append(tls.Name).Append(" (").Append(Options.TlsMode);
            if (tls.SupportsKernelOffload) sb.Append(", ktls-capable");
            sb.Append(')');
        }
        else
        {
            sb.Append(" tls=off");
        }
        return sb.ToString();
    }

    private static string BackendLabel(SocketSetShard[] shards)
    {
        if (shards.Length == 0) return "none";
        var t = shards[0].GetType().Name;
        return t switch
        {
            "IoUringShard" => "io_uring",
            "EpollShard" => "epoll",
            "IocpShard" => "iocp",
            "WindowsRioShard" => "rio",
            "ManagedSocketShard" => "managed",
            _ => t,
        };
    }

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

    /// <summary>Capacity-aware placement: walk the shards from the round-robin cursor and reserve a slot
    /// on the first one with room. Returns that shard (the caller then routes the connection to it and
    /// claims the reserved slot), or <c>null</c> if every shard is full — the caller drops the connection.
    /// The reservation must be released (<see cref="SocketSetShard.ReleaseReservation"/>) if the claim
    /// never happens. Unlike <see cref="RoundRobin"/>, this never lands a connection on a full shard while
    /// a sibling has space, which is the spurious-drop the fixed round-robin could cause.</summary>
    internal SocketSetShard? TryPlace()
    {
        var arr = _shards;
        uint start = (uint)Interlocked.Increment(ref _next);
        for (uint k = 0; k < (uint)arr.Length; k++)
        {
            var shard = arr[(start + k) % (uint)arr.Length];
            if (shard.TryReserve()) return shard;
        }
        // Every shard is full. Grow if allowed, then re-walk ONLY the shards that did not exist a moment
        // ago - re-walking the old ones would just fail again.
        if (TryGrow(arr.Length))
        {
            var grown = _shards;
            for (uint k = (uint)arr.Length; k < (uint)grown.Length; k++)
            {
                if (grown[k].TryReserve()) return grown[k];
            }
        }

        Interlocked.Increment(ref _placementFailures);
        return null; // every shard full and growth is off or exhausted; the caller drops/throws
    }

    /// <summary>
    /// Add one shard, if <see cref="SocketSetOptions.MaxShards"/> allows. Returns true when the set is
    /// longer than <paramref name="observedLength"/> afterwards — including when another thread grew it
    /// first, since the caller only wants to know whether re-walking is worthwhile.
    ///
    /// Growth is deliberately ONE shard per failed placement rather than doubling: each shard
    /// pre-allocates and pins <c>SocketsPerShard</c> x the buffer sizes, so an over-eager step is
    /// expensive and irreversible (there is no shrink).
    /// </summary>
    private bool TryGrow(int observedLength)
    {
        var factory = Options.Factory;
        int ceiling = Math.Min(Options.MaxShards, factory.MaxShards);
        if (ceiling <= observedLength) return false; // growth off, or already at the cap

        lock (_growLock)
        {
            var current = _shards;
            if (current.Length > observedLength) return true;   // someone else grew; just re-walk
            if (current.Length >= ceiling) return false;

            SocketSetShard shard;
            try
            {
                shard = factory.CreateShard(Options);
                shard.Init(this, current.Length);
            }
            catch (Exception ex) { OnWorkerFaulted(ex); return false; }

            // Start it the same way the constructor does, but gated on this ONE shard. A grown shard that
            // never finishes initialising must not be published: it would silently swallow every
            // connection routed to it, which is worse than the drop that growth exists to prevent.
            if (factory.UsesWorkerThreads)
            {
                _startupGate = new CountdownEvent(1);
                var thread = new Thread(static state => ((SocketSetShard)state!).Run())
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = $"{Name} worker {current.Length}",
                };
                thread.Start(shard);
                bool ready = _startupGate.Wait(StartupTimeout);
                _startupGate = null;
                if (!ready) { shard.Stop(); return false; }
            }
            else
            {
                try { shard.InitializeInline(); }
                catch (Exception ex) { OnWorkerFaulted(ex); shard.Stop(); return false; }
            }

            // Reuse-port listeners are per-shard, so a shard grown after Listen has none and the kernel
            // would never balance an accept onto it. Replay every multi-bind listen the set was asked for
            // (recorded under this same lock). Done BEFORE publish so the shard is a full listening peer by
            // the time any reader can observe it. The single-listener path leaves _multiBindListens null and
            // needs nothing here — a grown shard there already takes bounced connections.
            if (_multiBindListens is { } listens)
            {
                foreach (var (endpoint, token) in listens)
                {
                    shard.Listen(endpoint, token, local: true);
                }
            }

            // Copy-on-write publish. Readers snapshot into a local, so they see either the old array or
            // the new one, never a torn view - and a reader holding the old snapshot simply misses the
            // new shard until its next call, which is harmless.
            var next = new SocketSetShard[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = shard;
            Volatile.Write(ref _shards, next);
            Interlocked.Increment(ref _shardsGrown);
            return true;
        }
    }

    private long _shardsGrown;

    /// <summary>Shards added on demand since construction. <see cref="Options"/>.<c>Shards</c> plus this
    /// is the current count.</summary>
    public long ShardsGrown => Interlocked.Read(ref _shardsGrown);

    private long _placementFailures;

    /// <summary>
    /// Connections that could not be placed because every shard's slot table was full.
    ///
    /// Counted because the two callers fail DIFFERENTLY and one of them failed silently: <c>Connect</c>
    /// throws <see cref="InvalidOperationException"/>, but an ACCEPT just closes the socket, so a server
    /// at capacity was indistinguishable from a healthy one — no callback, no log, no counter. Measured
    /// 2026-07-31: with the table full, accepts vanish and connects take down an unguarded caller.
    ///
    /// This is the quantity dynamic shard growth would drive: growth is worth building exactly when this
    /// is non-zero under a load you care about, and pointless while it stays at zero.
    /// </summary>
    public long PlacementFailures => Interlocked.Read(ref _placementFailures);

    /// <summary>
    /// Reject an endpoint this platform cannot express, BEFORE any socket is created. Validation lives
    /// here rather than in a backend so it happens once, on every backend, and - the point - before the
    /// socket layer is touched at all: a check further down has already created a socket (and, on the
    /// connect path, claimed a slot) that then has to be unwound.
    ///
    /// Today that means Linux abstract-namespace names (<c>'@'</c> prefix) off Linux, where AF_UNIX is
    /// filesystem-only and <c>@foo</c> would silently become a FILE called <c>@foo</c> in the working
    /// directory. Measured 2026-07-31: it did exactly that.
    /// </summary>
    private static void ValidateEndpoint(EndPoint endpoint)
    {
#if NET
        if (endpoint is UnixDomainSocketEndPoint uds) UnixSocketFile.ValidatePath(uds.ToString());
#endif
    }

    public void Listen(EndPoint endpoint, object? userToken = null)
    {
        ValidateEndpoint(endpoint);
        if (Options.Factory.CanMultiBind(endpoint))
        {
            // Reuse-port: every shard binds its own listener and the kernel balances accepts (io_uring/IP).
            // Record it and bind the current shards under the growth lock so the two are atomic against a
            // concurrent grow — a shard added in between is bound by TryGrow's replay, never left unbound.
            lock (_growLock)
            {
                (_multiBindListens ??= []).Add((endpoint, userToken));
                foreach (var shard in _shards)
                {
                    shard.Listen(endpoint, userToken, local: true);
                }
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

    /// <summary>
    /// <see cref="Connect"/>, but placed on a SPECIFIC shard instead of walking for a free slot. The use
    /// case is shard-affinity: callback code on a loop thread (see
    /// <see cref="SocketSetShard.CurrentShardIndex"/>) connecting an outbound peer onto its OWN loop, so
    /// traffic between the two never crosses threads. Throws if that shard is at capacity — the caller
    /// asked for a specific placement, so falling back to another shard would silently defeat the point;
    /// degrade explicitly at the call site instead.
    /// </summary>
    public void ConnectShard(int shardIndex, EndPoint endpoint, object? userToken = null, SocketSets.Tls.TlsProvider? tls = null)
    {
        ValidateEndpoint(endpoint);
        var arr = _shards;
        if ((uint)shardIndex >= (uint)arr.Length) throw new ArgumentOutOfRangeException(nameof(shardIndex));
        var shard = arr[shardIndex];
        if (!shard.TryReserve())
            throw new InvalidOperationException($"Shard {shardIndex} is at capacity; cannot place the connection.");
        shard.Connect(endpoint, userToken, tls);
    }

    /// <summary>
    /// <paramref name="tls"/>: per-connection TLS provider for THIS dial, overriding the engine-level
    /// options outright (an explicit provider IS the direction signal; <see cref="TlsMode"/> does not
    /// gate it). Null = the engine options decide, exactly as before. This is the "one engine, many TLS
    /// contexts" granularity: e.g. a proxy terminating with cert A downstream while originating with
    /// trust B upstream, on a single SocketSet. Client-side only today; per-Listen granularity is a
    /// recorded follow-up.
    /// </summary>
    public void Connect(EndPoint endpoint, object? userToken = null, SocketSets.Tls.TlsProvider? tls = null)
    {
        ValidateEndpoint(endpoint);
        // Walk for a shard with a free slot rather than committing to one round-robin pick that might be
        // full while siblings have room. The chosen shard holds a reservation; its Connect consumes it by
        // claiming the slot, or releases it on a pre-claim failure.
        var shard = TryPlace()
            ?? throw new InvalidOperationException("All shards are at capacity; cannot place the connection.");
        shard.Connect(endpoint, userToken, tls);
    }

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
    /// <summary>
    /// Receive dispatch. A connection in pipe mode (<c>ctx.UsePipe(...)</c>) has its bytes written to the
    /// pipe; everything else goes to <see cref="OnReceive"/> exactly as before. Backends call this rather
    /// than OnReceive directly so the choice is made in one place instead of at every call site.
    /// </summary>
    internal void DispatchReceive(ref ReceiveContext ctx)
    {
#if NET
        var pipe = ctx.Connection.PipeIo;
        if (pipe is not null) { pipe.OnReceived(ctx.Payload); return; }
#endif
        OnReceive(ref ctx);
    }

    /// <summary>Close dispatch: completes a pipe-mode connection's halves so whoever is awaiting either
    /// end unblocks, then runs the application's <see cref="OnClosed"/>.</summary>
    internal void DispatchClosed(Connection connection)
    {
#if NET
        connection.PipeIo?.OnConnectionClosed();
#endif
        OnClosed(connection);
    }

    /// <summary>
    /// Fires on a shard's loop thread after each BATCH of I/O events has been dispatched, before the
    /// loop waits again. The loop-thread backends (io_uring, epoll) call this when the batch contained
    /// at least one event; callback-driven dispatch (the managed fallback) never does.
    ///
    /// WHY IT EXISTS: per-callback output flushing amplifies peer segmentation. A peer that writes one
    /// logical burst as several segments produces several receive callbacks, and a consumer that
    /// flushes per callback faithfully turns each into its own send — measured on the RESP proxy as 3x
    /// the send submissions under a per-record-writing TLS peer. Deferring flushes to THIS hook batches
    /// at loop-iteration granularity instead (what event-loop servers do), with an unchanged latency
    /// envelope: a batch containing one event drains immediately after its callback.
    /// </summary>
    protected internal virtual void OnLoopDrain() { }

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
        /// Drive this connection's I/O through <paramref name="pipe"/> instead of the
        /// <see cref="OnReceive"/>/<see cref="OnWrite"/> callbacks. Entirely optional and per-connection:
        /// a connection that never calls this behaves exactly as before.
        ///
        /// <paramref name="pipe"/> is the TRANSPORT-side endpoint (Kestrel's <c>Application</c> half):
        /// received bytes are written to <c>pipe.Output</c>, and whatever the application writes to the
        /// other end is read from <c>pipe.Input</c> and sent. Handing us the caller's own pipe is the
        /// point - it removes the intermediate pipe-plus-copy that a transport bridge would otherwise need.
        ///
        /// Set <paramref name="pinned"/> when the pipe's memory is already pinned (Kestrel's
        /// <c>PinnedBlockMemoryPool</c>, or a pool over the pinned object heap) so a backend can skip
        /// per-operation pinning. It is advisory: backends that copy ignore it.
        ///
        /// Not compatible with the instant-response <c>RawBuffer</c> path or with multiple receives in
        /// flight - both hand out transport-owned memory whose lifetime does not match a pipe segment's.
        /// While in pipe mode, do not also write through <see cref="Connection.Send(System.ReadOnlySpan{byte})"/>
        /// or the IBufferWriter surface: the outbound pump owns ordering on that half.
        /// </summary>
#if NET
        public readonly void UsePipe(IDuplexPipe pipe, bool pinned = false)
        {
            ArgumentNullException.ThrowIfNull(pipe);
            PipeIoBridge.Attach(connection, pipe, pinned);
        }
#endif

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
        /// Drive this connection's I/O through <paramref name="pipe"/> instead of the
        /// <see cref="OnReceive"/>/<see cref="OnWrite"/> callbacks — the client-side counterpart of
        /// <c>AcceptContext.UsePipe</c>; see that overload for the full contract.
        /// </summary>
#if NET
        public readonly void UsePipe(IDuplexPipe pipe, bool pinned = false)
        {
            ArgumentNullException.ThrowIfNull(pipe);
            PipeIoBridge.Attach(connection, pipe, pinned);
        }
#endif

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