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
    private List<(EndPoint Endpoint, object? Token, SocketSets.Tls.TlsProvider? Tls)>? _multiBindListens;

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

        StartSweepTimer(); // after the shards are live, so a tick can never race construction
    }

    // One timer per SET, not per shard. Interval is a quarter of the tightest deadline, clamped to
    // [250ms, 5s]: fine enough that a 10s handshake budget is enforced to within ~2.5s, coarse enough
    // that an idle 12-shard set is not being woken constantly.
    private Timer? _sweepTimer;

    private void StartSweepTimer()
    {
        if (!HasTimeouts) return; // no deadlines configured: no timer, no wakes, no cost
        long tightest = long.MaxValue;
        if (Options.HandshakeTimeout > TimeSpan.Zero) tightest = Math.Min(tightest, (long)Options.HandshakeTimeout.TotalMilliseconds);
        if (Options.IdleTimeout > TimeSpan.Zero) tightest = Math.Min(tightest, (long)Options.IdleTimeout.TotalMilliseconds);
        long step = tightest / 4;               // Math.Clamp is net5+; this multi-targets net472
        int interval = (int)(step < 250 ? 250 : step > 5000 ? 5000 : step);
        _sweepTimer = new Timer(static state =>
        {
            var set = (SocketSet)state!;
            foreach (var shard in set._shards) shard.RequestSweep();
        }, this, interval, interval);
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
            _sweepTimer?.Dispose();
            _sweepTimer = null;
            foreach (var shard in _shards)
            {
                shard.Stop();
            }
        }
    }

    // Cached so the sweep does not re-read TimeSpan properties per connection per tick.
    private long _handshakeMs = -1, _idleMs = -1;

    /// <summary>
    /// Has this connection outlived its deadline? THE single expiry decision, so all five backends share
    /// one policy and none can drift. Returns the reason for the log, or null to keep it.
    ///
    /// Two different clocks on purpose: a connection the app has not yet SEEN is on the handshake budget,
    /// and one it has is on the (opt-in) idle budget. See SocketSetOptions for why only the first is on
    /// by default.
    /// </summary>
    internal string? ExpiryReason(Connection conn, long nowTicks)
    {
        if (_handshakeMs < 0)
        {
            _handshakeMs = (long)Options.HandshakeTimeout.TotalMilliseconds;
            _idleMs = (long)Options.IdleTimeout.TotalMilliseconds;
        }

        if (!conn.Opened)
        {
            return _handshakeMs > 0 && nowTicks - conn.StartedTicks > _handshakeMs
                ? $"handshake did not complete within {_handshakeMs}ms" : null;
        }
        return _idleMs > 0 && nowTicks - conn.LastActivityTicks > _idleMs
            ? $"idle for more than {_idleMs}ms" : null;
    }

    /// <summary>True when any deadline is configured, so a set with both off starts no sweep timer and
    /// pays literally nothing.</summary>
    internal bool HasTimeouts => Options.HandshakeTimeout > TimeSpan.Zero || Options.IdleTimeout > TimeSpan.Zero;

    /// <summary>A connection was dropped for outliving a deadline. Default reports through
    /// <see cref="OnWorkerFaulted"/>; override to log or count. Runs on the owning loop thread.</summary>
    protected virtual void OnTimeout(Connection connection, string reason)
    {
        Interlocked.Increment(ref _timeouts);
        try { OnWorkerFaulted(new TimeoutException("connection dropped: " + reason)); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
    }

    internal void DispatchTimeout(Connection connection, string reason) => OnTimeout(connection, reason);

    private long _timeouts;

    /// <summary>Connections dropped for outliving <see cref="SocketSetOptions.HandshakeTimeout"/> or
    /// <see cref="SocketSetOptions.IdleTimeout"/>. Non-zero under attack, and non-zero also if a deadline
    /// is set too tight -- which is why it is a counter rather than only a log line.</summary>
    public long Timeouts => Interlocked.Read(ref _timeouts);

    protected internal virtual void OnWorkerFaulted(Exception exception)
    {
        Debug.WriteLine(exception.Message);
    }

    // =====================================================================
    // TLS negotiation: whether, and how (2026-08-04; see REVIEW.md D1)
    // =====================================================================

    /// <summary>
    /// Should this OUTBOUND connection use TLS, and with what? Runs on the owning loop thread once per
    /// connection, immediately before the handshake would begin, with the connection (and therefore its
    /// <see cref="Connection.UserToken"/>) already live.
    ///
    /// The default reproduces the engine-level configuration exactly: it returns whether
    /// <see cref="SocketSetOptions.TlsMode"/> enables the client direction, with the context pre-seeded
    /// from <see cref="SocketSetOptions.Tls"/> and <see cref="SocketSetOptions.TlsClient"/>. So an
    /// application that never overrides this behaves as it always did.
    ///
    /// Override it when one engine serves more than one TLS posture — which is the normal case for a
    /// client, since a single engine dials many endpoints and each needs its own
    /// <see cref="Tls.TlsClientAuthenticateContext.TargetHost"/>. Key off
    /// <c>ctx.Connection.UserToken</c>.
    /// </summary>
    protected internal virtual bool OnClientAuthenticate(ref Tls.TlsClientAuthenticateContext ctx)
        => Options.TlsEnabled(isClient: true);

    /// <summary>Inbound twin of <see cref="OnClientAuthenticate"/>. Default: the engine-level
    /// configuration, exactly as before.</summary>
    protected internal virtual bool OnServerAuthenticate(ref Tls.TlsServerAuthenticateContext ctx)
        => Options.TlsEnabled(isClient: false);

    /// <summary>
    /// Run <see cref="OnClientAuthenticate"/> and validate what it asked for. THE SINGLE ENFORCEMENT
    /// POINT for the client-side TLS posture: every backend routes through here, so the rules below hold
    /// on all five without any of them having to remember them.
    ///
    /// FAILS CLOSED, in all three ways it can fail. A callback that throws, one that returns true
    /// without a provider, and one that returns true without naming a host all yield
    /// <see cref="Tls.TlsResolution.Deny"/>, and the caller drops the connection. None of them fall back
    /// to plaintext: a silent downgrade is precisely the failure this whole item exists to remove.
    /// </summary>
    internal Tls.TlsResolution ResolveClientTls(Connection conn)
    {
        var ctx = new Tls.TlsClientAuthenticateContext(conn, conn.TlsOverride ?? Options.Tls, Options.TlsClient);
        bool wanted;
        try { wanted = OnClientAuthenticate(ref ctx); }
        catch (Exception ex) { return DenyTls(conn, nameof(OnClientAuthenticate), ex); }

        if (!wanted) return Tls.TlsResolution.None;
        if (ctx.Provider is not { } provider)
            return DenyTls(conn, nameof(OnClientAuthenticate), new InvalidOperationException(
                "returned true but left Provider null, so there is no engine to hand the connection to."));

        // THE HOST IS MANDATORY. Until 2026-08-04 a null host silently disabled hostname verification on
        // both providers while chain verification carried on, so the connection accepted any certificate
        // a trusted CA had ever issued, for any name. Refusing is the only safe reading of "unset".
        if (string.IsNullOrWhiteSpace(ctx.TargetHost))
            return DenyTls(conn, nameof(OnClientAuthenticate), new InvalidOperationException(
                "returned true without a TargetHost. Set the name being dialled so the certificate can be "
                + $"verified against it, or \"{Tls.TlsClientAuthenticateContext.AnyHost}\" to state "
                + "explicitly that no name check is wanted."));

        // Reuse the engine's options object unless the callback actually varied something, so the
        // common single-posture engine allocates nothing per connection.
        var opts = Options.TlsClient;
        if (!string.Equals(ctx.TargetHost, opts.TargetHost, StringComparison.Ordinal)
            || !ReferenceEquals(ctx.AlpnProtocols, opts.AlpnProtocols)
            || ctx.AllowKernelOffload != opts.AllowKernelOffload)
        {
            opts = new Tls.TlsClientOptions
            {
                TargetHost = ctx.TargetHost,
                AlpnProtocols = ctx.AlpnProtocols,
                AllowKernelOffload = ctx.AllowKernelOffload,
            };
        }
        return Tls.TlsResolution.ForClient(provider, opts, ctx.AllowKernelOffload);
    }

    /// <summary>Server-side twin of <see cref="ResolveClientTls"/>. No host requirement here: a server is
    /// told a name by SNI rather than choosing one.</summary>
    internal Tls.TlsResolution ResolveServerTls(Connection conn)
    {
        var ctx = new Tls.TlsServerAuthenticateContext(conn, conn.TlsOverride ?? Options.Tls, Options.TlsServer);
        bool wanted;
        try { wanted = OnServerAuthenticate(ref ctx); }
        catch (Exception ex) { return DenyTls(conn, nameof(OnServerAuthenticate), ex); }

        if (!wanted) return Tls.TlsResolution.None;
        if (ctx.Provider is not { } provider)
            return DenyTls(conn, nameof(OnServerAuthenticate), new InvalidOperationException(
                "returned true but left Provider null, so there is no engine to hand the connection to."));

        var opts = Options.TlsServer;
        if (!ReferenceEquals(ctx.AlpnProtocols, opts.AlpnProtocols)
            || ctx.AllowKernelOffload != opts.AllowKernelOffload)
        {
            opts = new Tls.TlsServerOptions
            {
                AlpnProtocols = ctx.AlpnProtocols,
                AllowKernelOffload = ctx.AllowKernelOffload,
            };
        }
        return Tls.TlsResolution.ForServer(provider, opts, ctx.AllowKernelOffload);
    }

    private Tls.TlsResolution DenyTls(Connection conn, string callback, Exception ex)
    {
        try { OnWorkerFaulted(new InvalidOperationException($"{callback}: connection refused.", ex)); }
        catch (Exception inner) { Debug.WriteLine(inner.Message); }
        return Tls.TlsResolution.Deny;
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
                foreach (var (endpoint, token, tls) in listens)
                {
                    shard.Listen(endpoint, token, local: true, tls);
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

    /// <summary>
    /// <paramref name="tls"/>: per-LISTENER TLS provider — connections accepted on THIS listener use it,
    /// overriding the engine-level options outright (an explicit provider IS the direction signal;
    /// <see cref="TlsMode"/> does not gate it). Null = engine options decide, exactly as before. With
    /// the per-connect twin on <see cref="Connect(EndPoint, object?, SocketSets.Tls.TlsProvider?)"/>,
    /// one engine can serve many TLS contexts in both directions.
    /// </summary>
    public void Listen(EndPoint endpoint, object? userToken = null, SocketSets.Tls.TlsProvider? tls = null)
    {
        ValidateEndpoint(endpoint);
        if (Options.Factory.CanMultiBind(endpoint, Options))
        {
            // Reuse-port: every shard binds its own listener and the kernel balances accepts (io_uring/IP).
            // Record it and bind the current shards under the growth lock so the two are atomic against a
            // concurrent grow — a shard added in between is bound by TryGrow's replay, never left unbound.
            lock (_growLock)
            {
                (_multiBindListens ??= []).Add((endpoint, userToken, tls));
                foreach (var shard in _shards)
                {
                    shard.Listen(endpoint, userToken, local: true, tls);
                }
            }
        }
        else
        {
            // Single listener on one shard, which bounces accepted connections round-robin. Because the
            // shard is chosen round-robin, distinct listen endpoints land on different shards rather than
            // all piling their accept load onto shard 0.
            RoundRobin().Listen(endpoint, userToken, local: false, tls);
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
    public void ListenHandle(nint handle, object? userToken = null, SocketSets.Tls.TlsProvider? tls = null)
        => RoundRobin().ListenHandle(handle, userToken, tls);

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
    /// Receive dispatch. A connection in pipe mode (<c>ctx.UsePipe(...)</c>) has its bytes written to the
    /// pipe; everything else goes to <see cref="OnReceive"/> exactly as before. Backends call this rather
    /// than OnReceive directly so the choice is made in one place instead of at every call site — and,
    /// since 2026-08-04, so an exception out of the handler cannot unwind into the shard loop (see the
    /// containment note below).
    /// </summary>
    internal void DispatchReceive(ref ReceiveContext ctx)
    {
        try
        {
#if NET
            var pipe = ctx.Connection.PipeIo;
            if (pipe is not null) { pipe.OnReceived(ctx.Payload); return; }
#endif
            OnReceive(ref ctx);
        }
        catch (Exception ex)
        {
            // Do not send whatever a half-completed handler left in the buffer.
            ctx.ResponseBytes = 0;
            OnCallbackFaulted(ctx.Connection, nameof(OnReceive), ex);
        }
    }

    // =====================================================================
    // Callback fault containment
    // ---------------------------------------------------------------------
    // WHY THESE WRAPPERS EXIST (2026-08-04 audit). Every loop-driven backend dispatched OnReceive/
    // OnAccept/OnConnect/OnWrite bare, so an exception out of APPLICATION code unwound the completion
    // loop, through OnRun, into SocketSetShard.Run's catch — which logs and falls into `finally
    // { OnShutdown(); }`. That runs Cleanup(): every fd on the shard closed, the loop never restarted.
    // One unhandled exception therefore killed up to SocketsPerShard (4096) live connections AND removed
    // 1/N of the set's capacity permanently, which for a server is a remote DoS with a one-request cost.
    //
    // DispatchClosed was already guarded, so the shape was established; it simply had not been applied to
    // the hot four. Containment is per-CONNECTION: the faulting connection is closed (its state is
    // unknowable after a partial handler) and every sibling on the shard is untouched.
    //
    // These deliberately return void and close via Connection.Close() rather than returning a
    // keep/drop bool. Close() is already the thread-safe, generation-guarded, marshal-onto-the-loop path
    // every backend implements and every application uses, so there is no new teardown route to get
    // wrong — and no call site has to change shape.
    // =====================================================================

    internal void DispatchAccept(ref AcceptContext ctx)
    {
        try { OnAccept(ref ctx); }
        catch (Exception ex)
        {
            ctx.SendBytes = 0;
            OnCallbackFaulted(ctx.Connection, nameof(OnAccept), ex);
        }
    }

    internal void DispatchConnect(ref ConnectContext ctx)
    {
        try { OnConnect(ref ctx); }
        catch (Exception ex)
        {
            ctx.SendBytes = 0;
            OnCallbackFaulted(ctx.Connection, nameof(OnConnect), ex);
        }
    }

    internal void DispatchWrite(ref WriteContext ctx)
    {
        try { OnWrite(ref ctx); }
        catch (Exception ex)
        {
            ctx.SendBytes = 0;
            OnCallbackFaulted(ctx.Connection, nameof(OnWrite), ex);
        }
    }

    /// <summary>
    /// An application callback threw. The default drops THAT connection (its protocol state is unknowable
    /// after a partially-run handler) and reports through <see cref="OnWorkerFaulted"/>; the shard and
    /// every other connection on it carry on. Override to add logging or metrics — but note that this
    /// runs ON the loop thread, so it must not block, and an exception thrown from an override is
    /// swallowed rather than allowed to defeat the containment it exists to provide.
    /// </summary>
    protected virtual void OnCallbackFaulted(Connection connection, string callback, Exception exception)
    {
        try { connection.Close(); } catch { /* teardown is best-effort; never re-throw into the loop */ }
        try { OnWorkerFaulted(new InvalidOperationException($"{callback} threw; connection closed.", exception)); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
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
    /// Report a TLS engine fault, once, during teardown. Called by every backend UNCONDITIONALLY, which
    /// is the whole point: <see cref="DispatchClosed"/> is gated on the app having seen the connection
    /// open, and a failed handshake means it never did. Hanging the report off DispatchClosed therefore
    /// reported nothing in exactly the case that matters -- caught by verify-tlsname asserting that a
    /// refusal carries a reason, which is why that assertion is in the gate.
    /// </summary>
    internal void DispatchTlsFault(Connection connection)
    {
        if (connection.Tls is not { FaultReason: { } reason }) return;
        connection.Tls.ClearFaultReason(); // fire once even if teardown is re-entered
        OnTlsFault(connection, reason);
    }

    /// <summary>
    /// A TLS connection was torn down because the engine faulted: a failed handshake, a rejected
    /// certificate, a decrypt error. Fires before <see cref="OnClosed"/>, on the owning IO thread.
    ///
    /// The default routes to <see cref="OnWorkerFaulted"/>, which is at least SOMETHING; before
    /// 2026-08-04 the reason went only to <c>Debug.WriteLine</c> and so did not exist in a Release
    /// build. Override to log properly. Note this covers the userspace-filter path only: the kTLS path
    /// has no filter to carry a reason, which is recorded as an inconsistency rather than fixed here.
    /// </summary>
    protected virtual void OnTlsFault(Connection connection, string reason)
    {
        try { OnWorkerFaulted(new InvalidOperationException("TLS: " + reason)); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
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
    //
    // =====================================================================================
    // THE TAIL WIPE, and what it is and is not for (added 2026-08-04; see REVIEW.md D4)
    // -------------------------------------------------------------------------------------
    // Those backend-owned buffers are SHARED AND RECYCLED — io_uring's provided-buffer ring is
    // per-SHARD and cycles across every connection on it, and the TLS scratch buffers are per-slot
    // and survive slot re-tenancy without being cleared. So the bytes past the current payload
    // belong to whoever used the buffer last, which on a TLS connection is another client's
    // DECRYPTED PLAINTEXT. The reply-in-place design is the whole point of this API, so the buffer
    // has to stay shared; what must not happen is those bytes reaching the wire.
    //
    // Each context therefore zeroes lazily, with TWO triggers, and the second is the one that
    // matters most because it is the one that is easy to miss:
    //   1. handing out the buffer span   (RawBuffer / SendBuffer)  -> wipe the whole tail, since
    //      once the span is out we cannot tell handler-written bytes from stale ones;
    //   2. setting the length to send    (ResponseBytes / SendBytes) -> wipe only up to that
    //      length. THIS is the vector a wipe-on-first-access alone would sail past: a handler can
    //      set ResponseBytes above PayloadBytes without ever touching RawBuffer, and a miscomputed
    //      frame length doing exactly that is the realistic accident.
    // Both are monotone through _wipedTo, so they compose in any order and nothing is cleared
    // twice, and a handler that reaches past nothing pays nothing — the instant-response idiom
    // (ctx.ResponseBytes = ctx.PayloadBytes) trips neither.
    //
    // SCOPE: this defends against ACCIDENT, and that is a deliberate limit, not an oversight. Any
    // handler that wants the stale bytes can have them — Unsafe.Add off a reference, its own
    // pointer arithmetic, or the explicitly-named *Unwiped accessors. Code running in this process
    // is already inside every boundary the library has, so there is nothing here to defend against
    // it and pretending otherwise would only buy false confidence. The thing worth stopping is an
    // ordinary length bug quietly putting another connection's data on the wire.
    // =====================================================================================

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
            PipeIoBridge.Attach(connection, pipe, pinned, connection.MaxInboundBufferBytes);
        }
#endif

        /// <summary>
        /// A library-owned outbound buffer. Write an initial payload here and set
        /// <see cref="SendBytes"/> to have it sent as soon as the socket is accepted.
        ///
        /// ZEROED on first access, for the same reason as <c>ReceiveContext.RawBuffer</c>: this is a
        /// recycled library buffer, so untouched bytes would otherwise be the previous tenant's. A
        /// handler that never touches it pays nothing.
        /// </summary>
        public Span<byte> SendBuffer
        {
            get
            {
                WipeTo(_bufferLength);
                return new(_buffer, _bufferLength);
            }
        }

        /// <summary>Write access to the first <paramref name="sizeHint"/> bytes, and THE CHEAP WAY to
        /// fill this buffer. Does not PROMISE that many (it is clamped to the buffer, so check
        /// <c>.Length</c>); granting is tracked as a high-water mark, so each call zeroes only the delta
        /// over what has already been granted, and asking twice never pays twice. Prefer this to
        /// <see cref="SendBuffer"/>, which has to zero the WHOLE buffer because handing out everything
        /// leaves us unable to tell written bytes from stale ones. See
        /// <c>ReceiveContext.GetWriteSpan</c>.</summary>
        public Span<byte> GetWriteSpan(int sizeHint)
        {
            int end = sizeHint <= 0 ? 0 : Math.Min(sizeHint, _bufferLength);
            WipeTo(end);
            return new(_buffer, end);
        }

        /// <summary><see cref="SendBuffer"/> without the wipe; also disclaims the <see cref="SendBytes"/>
        /// trigger, i.e. the handler takes responsibility for the whole buffer. Bytes are undefined and
        /// may belong to another connection. For measured use only.</summary>
        public Span<byte> SendBufferUnwiped
        {
            get { _wipedTo = _bufferLength; return new(_buffer, _bufferLength); }
        }

        // Offset up to which the buffer is known-zeroed (or explicitly disclaimed). Unlike a receive
        // there is no payload to preserve, so this starts at 0: the whole buffer is the previous
        // tenant's. Monotone, so the two triggers compose in any order and nothing is cleared twice.
        private int _wipedTo;

        private void WipeTo(int end)
        {
            if (end <= _wipedTo) return;
            new Span<byte>(_buffer + _wipedTo, end - _wipedTo).Clear();
            _wipedTo = end;
        }

        /// <summary>Number of leading bytes of <see cref="SendBuffer"/> to send. 0 = send nothing.
        /// Setting this WIPES up to <c>value</c>: it is reachable without ever touching
        /// <see cref="SendBuffer"/>, so it is the trigger a wipe-on-first-access alone would miss. See
        /// <c>ReceiveContext.ResponseBytes</c> for the full reasoning.</summary>
        public int SendBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _bufferLength) throw new ArgumentOutOfRangeException(nameof(SendBytes));
                WipeTo(value); // clear only what is about to go on the wire
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
            PipeIoBridge.Attach(connection, pipe, pinned, connection.MaxInboundBufferBytes);
        }
#endif

        /// <summary>
        /// A library-owned outbound buffer. Write an initial handshake/greeting here and set
        /// <see cref="SendBytes"/> to have it sent as soon as the connection completes.
        ///
        /// ZEROED on first access, for the same reason as <c>ReceiveContext.RawBuffer</c>: this is a
        /// recycled library buffer, so untouched bytes would otherwise be the previous tenant's. A
        /// handler that never touches it pays nothing.
        /// </summary>
        public Span<byte> SendBuffer
        {
            get
            {
                WipeTo(_bufferLength);
                return new(_buffer, _bufferLength);
            }
        }

        /// <summary>Write access to the first <paramref name="sizeHint"/> bytes, and THE CHEAP WAY to
        /// fill this buffer. Does not PROMISE that many (it is clamped to the buffer, so check
        /// <c>.Length</c>); granting is tracked as a high-water mark, so each call zeroes only the delta
        /// over what has already been granted, and asking twice never pays twice. Prefer this to
        /// <see cref="SendBuffer"/>, which has to zero the WHOLE buffer because handing out everything
        /// leaves us unable to tell written bytes from stale ones. See
        /// <c>ReceiveContext.GetWriteSpan</c>.</summary>
        public Span<byte> GetWriteSpan(int sizeHint)
        {
            int end = sizeHint <= 0 ? 0 : Math.Min(sizeHint, _bufferLength);
            WipeTo(end);
            return new(_buffer, end);
        }

        /// <summary><see cref="SendBuffer"/> without the wipe; also disclaims the <see cref="SendBytes"/>
        /// trigger, i.e. the handler takes responsibility for the whole buffer. Bytes are undefined and
        /// may belong to another connection. For measured use only.</summary>
        public Span<byte> SendBufferUnwiped
        {
            get { _wipedTo = _bufferLength; return new(_buffer, _bufferLength); }
        }

        // Offset up to which the buffer is known-zeroed (or explicitly disclaimed). Unlike a receive
        // there is no payload to preserve, so this starts at 0: the whole buffer is the previous
        // tenant's. Monotone, so the two triggers compose in any order and nothing is cleared twice.
        private int _wipedTo;

        private void WipeTo(int end)
        {
            if (end <= _wipedTo) return;
            new Span<byte>(_buffer + _wipedTo, end - _wipedTo).Clear();
            _wipedTo = end;
        }

        /// <summary>Number of leading bytes of <see cref="SendBuffer"/> to send. 0 = send nothing.
        /// Setting this WIPES up to <c>value</c>: it is reachable without ever touching
        /// <see cref="SendBuffer"/>, so it is the trigger a wipe-on-first-access alone would miss. See
        /// <c>ReceiveContext.ResponseBytes</c> for the full reasoning.</summary>
        public int SendBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _bufferLength) throw new ArgumentOutOfRangeException(nameof(SendBytes));
                WipeTo(value); // clear only what is about to go on the wire
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
        ///
        /// EVERYTHING PAST <see cref="PayloadBytes"/> IS ZEROED before you can see it or send it. The
        /// backing buffer is shared and recycled (io_uring's per-shard provided-buffer ring, or a
        /// per-slot TLS scratch that survives slot re-tenancy), so the tail would otherwise hold the
        /// PREVIOUS tenant's bytes, which on a TLS connection is another client's decrypted plaintext.
        ///
        /// TWO TRIGGERS, because there are two ways to reach those bytes and only one of them comes
        /// through this property (see <see cref="ResponseBytes"/> for the other, which is the one that is
        /// easy to miss). Reading here wipes the whole tail, because once the span is handed out we can
        /// no longer distinguish bytes the handler wrote from bytes it did not.
        ///
        /// The cost sits HERE rather than on buffer release so that a handler which never reaches past
        /// its payload pays nothing at all: the instant-response idiom
        /// (<c>ctx.ResponseBytes = ctx.PayloadBytes;</c>) trips neither trigger.
        ///
        /// SCOPE, deliberately: this defends against ACCIDENT, not against hostile in-process code. A
        /// handler that wants the stale bytes can trivially have them (<c>Unsafe.Add</c> off any
        /// reference, its own pointer arithmetic, <see cref="RawBufferUnwiped"/>). Anything running
        /// inside this process is already past every boundary the library has; the thing worth stopping
        /// is a length miscalculation quietly putting another connection's data on the wire.
        /// </summary>
        public Span<byte> RawBuffer
        {
            get
            {
                WipeTo(_bufferLength);
                return new(_buffer, _bufferLength);
            }
        }

        /// <summary>
        /// Write access to the first <paramref name="sizeHint"/> bytes of the same buffer, and THE CHEAP
        /// WAY TO REPLY. Prefer this to <see cref="RawBuffer"/>.
        ///
        /// It does not PROMISE <paramref name="sizeHint"/> bytes: the span is clamped to the buffer, so
        /// check <c>.Length</c> (the <see cref="System.Buffers.IBufferWriter{T}"/> convention, and the
        /// same shape as <c>Connection.GetSpan</c>). That freedom is exactly what makes it cheap — we
        /// hand out only what was asked for, so we only ever have to zero what was asked for.
        ///
        /// COST. Granting is tracked as a high-water mark, so each call clears only the DELTA over what
        /// has already been granted, and the two paths that matter cost nothing at all:
        ///   - a reply no larger than the request (<c>GetWriteSpan(ctx.PayloadBytes)</c>, ie. editing the
        ///     received bytes in place) clears NOTHING — the payload region is the peer's own data;
        ///   - growing in steps (<c>GetWriteSpan(600)</c> then <c>GetWriteSpan(900)</c>) clears
        ///     [payload,600) then [600,900), never the same byte twice.
        /// Contrast <see cref="RawBuffer"/>, which must clear the WHOLE tail: once the full span is out
        /// we can no longer tell bytes the handler wrote from bytes it did not, and we do not yet know
        /// how much will be sent. That pessimism is the price of asking for everything.
        /// </summary>
        public Span<byte> GetWriteSpan(int sizeHint)
        {
            int end = sizeHint <= 0 ? 0 : Math.Min(sizeHint, _bufferLength);
            WipeTo(end);
            return new(_buffer, end);
        }

        /// <summary>
        /// <see cref="RawBuffer"/> WITHOUT the wipe, and it also suppresses the
        /// <see cref="ResponseBytes"/> trigger for the rest of this call: taking this is a statement that
        /// the handler is accounting for the whole tail itself. Bytes past <see cref="PayloadBytes"/> are
        /// undefined and may belong to another connection. For a handler that has MEASURED the wipe to
        /// matter; named to be hard to reach for by accident.
        /// </summary>
        public Span<byte> RawBufferUnwiped
        {
            get
            {
                _wipedTo = _bufferLength; // claim the tail without clearing it
                return new(_buffer, _bufferLength);
            }
        }

        // Offset up to which the tail is known-zeroed (or explicitly disclaimed). Starts at the payload
        // end: everything below it is the peer's own bytes and is never wiped. Monotone, so the two
        // triggers compose in any order and nothing is cleared twice.
        private int _wipedTo = bytes;

        private void WipeTo(int end)
        {
            if (end <= _wipedTo) return;
            new Span<byte>(_buffer + _wipedTo, end - _wipedTo).Clear();
            _wipedTo = end;
        }

        /// <summary>
        /// Indicates the end of a receive stream.
        /// </summary>
        public readonly bool IsEof => bytes is 0;

        /// <remarks>
        /// It is the implementation's responsibility to handle <see cref="ResponseBytes"/>
        /// appropriately, whether that means reusing an existing socket buffer efficiently,
        /// or by copying the data into a new buffer.
        ///
        /// THIS IS THE EASY-TO-MISS DISCLOSURE VECTOR, and it is why the wipe cannot live on
        /// <see cref="RawBuffer"/> alone. Setting this ABOVE <see cref="PayloadBytes"/> transmits bytes
        /// the handler never wrote, and it can be done without ever touching <see cref="RawBuffer"/> —
        /// <c>ctx.ResponseBytes = frameLength;</c> on a miscomputed length is the whole bug, and a
        /// wipe-on-first-access would sail straight past it. So the setter wipes up to
        /// <paramref name="value"/> as well.
        ///
        /// Note this trigger is CHEAPER than the <see cref="RawBuffer"/> one, and exactly as cheap as it
        /// can be: it clears only what is about to go on the wire, not the whole buffer.
        /// </remarks>
        public int ResponseBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _bufferLength) Throw();
                // Zero anything between the payload and what we are about to send that the handler has
                // not already been handed (and therefore cannot have written).
                WipeTo(value);
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

        /// <summary>The buffer just written, now free; write the next payload here to pipeline.
        ///
        /// ZEROED on first access, for the same reason as <c>ReceiveContext.RawBuffer</c>. Note this one
        /// holds the bytes THIS connection just sent (and, on the TLS path, its just-decrypted request
        /// plaintext), so the wipe is as much about not re-sending our own stale tail as about the
        /// previous tenant. A handler that never touches it pays nothing.</summary>
        public Span<byte> SendBuffer
        {
            get
            {
                WipeTo(_bufferLength);
                return new(_buffer, _bufferLength);
            }
        }

        /// <summary>Write access to the first <paramref name="sizeHint"/> bytes, and THE CHEAP WAY to
        /// fill this buffer. Does not PROMISE that many (it is clamped to the buffer, so check
        /// <c>.Length</c>); granting is tracked as a high-water mark, so each call zeroes only the delta
        /// over what has already been granted, and asking twice never pays twice. Prefer this to
        /// <see cref="SendBuffer"/>, which has to zero the WHOLE buffer because handing out everything
        /// leaves us unable to tell written bytes from stale ones. See
        /// <c>ReceiveContext.GetWriteSpan</c>.</summary>
        public Span<byte> GetWriteSpan(int sizeHint)
        {
            int end = sizeHint <= 0 ? 0 : Math.Min(sizeHint, _bufferLength);
            WipeTo(end);
            return new(_buffer, end);
        }

        /// <summary><see cref="SendBuffer"/> without the wipe; also disclaims the <see cref="SendBytes"/>
        /// trigger, i.e. the handler takes responsibility for the whole buffer. Bytes are undefined and
        /// may belong to another connection. For measured use only.</summary>
        public Span<byte> SendBufferUnwiped
        {
            get { _wipedTo = _bufferLength; return new(_buffer, _bufferLength); }
        }

        // Offset up to which the buffer is known-zeroed (or explicitly disclaimed). Unlike a receive
        // there is no payload to preserve, so this starts at 0: the whole buffer is the previous
        // tenant's. Monotone, so the two triggers compose in any order and nothing is cleared twice.
        private int _wipedTo;

        private void WipeTo(int end)
        {
            if (end <= _wipedTo) return;
            new Span<byte>(_buffer + _wipedTo, end - _wipedTo).Clear();
            _wipedTo = end;
        }

        /// <summary>Number of leading bytes of <see cref="SendBuffer"/> to send next. 0 = send nothing.
        /// Setting this WIPES up to <c>value</c>: it is reachable without ever touching
        /// <see cref="SendBuffer"/>, so it is the trigger a wipe-on-first-access alone would miss. See
        /// <c>ReceiveContext.ResponseBytes</c> for the full reasoning.</summary>
        public int SendBytes
        {
            get => field;
            set
            {
                if (value < 0 | value > _bufferLength) throw new ArgumentOutOfRangeException(nameof(SendBytes));
                WipeTo(value); // clear only what is about to go on the wire
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