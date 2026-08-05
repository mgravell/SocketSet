using System.Diagnostics;
using System.Net;
#if NET
using SocketSets.Native;
#endif

namespace SocketSets;

public abstract class SocketSetShard
{
    /// <summary>The shard whose loop thread we are currently on, or null off-loop. Set once per worker
    /// thread at <see cref="Run"/> entry. Exists so a callback (OnAccept/OnReceive fire ON the loop
    /// thread for the loop-driven backends) can discover its own shard and keep related work
    /// shard-AFFINE — e.g. a proxy routing a client to an upstream connection on the SAME loop, so
    /// forward and reply never cross threads. NOTE: every backend with UsesWorkerThreads (io_uring,
    /// epoll, IOCP, RIO) dispatches callbacks from its own <see cref="Run"/> loop thread — IOCP dequeues
    /// completions on that thread via GetQueuedCompletionStatusEx, it is NOT ThreadPool-dispatched (an
    /// earlier version of this comment claimed otherwise). Only the MANAGED fallback
    /// (UsesWorkerThreads=false; SAEA completion threads) reports -1 here, and affinity consumers must
    /// treat -1 as "no affinity available" rather than an error.</summary>
    [ThreadStatic]
    private static SocketSetShard? t_current;

    /// <summary>Index of the shard owning the current thread, or -1 when not on a shard loop thread.</summary>
    public static int CurrentShardIndex => t_current?._shard ?? -1;

    private volatile bool _isActive = true;
    private SocketSet _parent = null!; // set via init
    private int _shard;

    // Free-slot reservation counter (capacity-aware placement). A placing thread reserves a slot with a
    // single Interlocked.Decrement before routing a connection here; the owning loop releases it when the
    // slot is published free. `>= 0 after decrement` == a free slot is guaranteed to exist, so the walk
    // across shards fails a full shard in O(1) instead of scanning it. Default int.MaxValue == unbounded;
    // fixed-table backends call SetShardCapacity to cap it at their slot count. (Not cache-line padded yet
    // — a later micro-opt; today it neighbours rarely-written init fields, not another hot counter.)
    private int _available = int.MaxValue;

    private int _capacity; // 0 == untracked (unbounded backends never set it)

    /// <summary>Fixed-table backends set their reservation ceiling to the slot-table size (once, at init).</summary>
    protected void SetShardCapacity(int slots)
    {
        _capacity = slots;
        _available = slots;
    }

    /// <summary>Live connections on this shard (capacity minus free reservations), or -1 when the
    /// backend does not track a capacity (the unbounded managed backend). Approximate under
    /// concurrency — diagnostics, not accounting.</summary>
    internal int ActiveConnections
        => _capacity == 0 ? -1 : _capacity - Volatile.Read(ref _available);

    /// <summary>Reserve one slot on this shard. True == a slot is guaranteed available (go claim it);
    /// false == full (the decrement is undone). O(1). Callable from any thread. Overridden by unbounded
    /// backends (managed) to always succeed without touching the counter.</summary>
    internal virtual bool TryReserve()
    {
        if (Interlocked.Decrement(ref _available) >= 0) return true;
        Interlocked.Increment(ref _available); // undo the over-decrement; shard is full
        return false;
    }

    /// <summary>Release a reservation: paired 1:1 with <see cref="TryReserve"/>, called wherever a slot is
    /// published free (normal teardown, or a claim/connect rollback). Over/under-calling drifts the
    /// counter into phantom-full drops, so keep it colocated with the free-publish.</summary>
    internal void ReleaseReservation() => Interlocked.Increment(ref _available);

    protected SocketSet Parent => _parent;

    /// <summary>This shard's index within the set.</summary>
    protected int Shard => _shard;

    protected bool IsActive => _isActive;

    public void Stop()
    {
        if (!_isActive) return;
        _isActive = false;
        OnStop();

        // Threaded backends run OnShutdown in Run()'s finally when the pump loop exits
        // (OnStop wakes it). Callback-driven backends have no such loop, so shut down here.
        if (!_parent.Options.Factory.UsesWorkerThreads)
        {
            try { OnShutdown(); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }
    }

    protected virtual void OnStop()
    {
    }

    // =====================================================================
    // Deadline sweep (REVIEW.md D2)
    // ---------------------------------------------------------------------
    // The loops BLOCK when idle -- io_uring in io_uring_enter, epoll in epoll_wait, IOCP in
    // GetQueuedCompletionStatusEx -- which is exactly the state a half-open handshake leaves them in, so
    // a deadline cannot be noticed by the loop unaided. One timer per SET therefore ticks and wakes each
    // shard through the doorbell it already has; the shard notices the flag on its next pass and sweeps
    // its own slot table on its own thread, so the single-writer discipline is untouched.
    //
    // A set with no deadline configured starts no timer and pays nothing.
    // =====================================================================

    private volatile bool _sweepDue;

    /// <summary>Ask this shard to sweep on its next pass. Any thread (the set's timer).</summary>
    internal void RequestSweep()
    {
        _sweepDue = true;
        Wake();
    }

    /// <summary>True once per request; the loop calls this and sweeps if so.</summary>
    protected bool TakeSweepRequest()
    {
        if (!_sweepDue) return false;
        _sweepDue = false;
        return true;
    }

    /// <summary>Wake a blocked loop. Backends already have a doorbell for Stop; this exposes it.</summary>
    protected virtual void Wake() { }

    /// <summary>Walk this shard's live connections and drop any past its deadline. Loop thread only.
    /// Backends override because only they know the shape of their slot table; the POLICY is shared
    /// (<c>SocketSet.ExpiryReason</c>) so the five cannot drift.</summary>
    protected virtual void SweepTimeouts(long nowTicks) { }

    /// <summary>Loop-side entry point: sweep iff one was requested.</summary>
    protected void MaybeSweep()
    {
        if (TakeSweepRequest()) SweepTimeouts(Clock.Millis);
    }

    internal void Init(SocketSet parent, int shard)
    {
        SocketSet? oldParent = Interlocked.CompareExchange(ref _parent, parent, null);
        if (oldParent is not null)
        {
            throw new InvalidOperationException($"Shard is already associated with {oldParent}.");
        }
        _shard = shard;
        
    }

    internal void Run()
    {
        t_current = this; // this thread IS the shard's loop from here on
#if NET
        // Thread pinning is Linux-only (sched_setaffinity) and lives in the io_uring
        // side of the build; the netfx fallback doesn't pin.
        if (_parent.Options.PinWorkerThreads && OperatingSystem.IsLinux())
        {
            LibC.PinCurrentThreadToNthAllowedCpu(_shard);
        }
#endif

        bool initialized = false;
        try
        {
            // Init must run on this worker thread (the ring is single-issuer). Signal
            // the parent's startup gate exactly once, whether we succeed or throw, so
            // construction can block until every shard has reported.
            try
            {
                OnInitialize();
                initialized = true;
            }
            finally
            {
                _parent.SignalStartupComplete();
            }

            OnRun(); // the event loop; reached only if init succeeded
        }
        catch (Exception ex)
        {
            if (!initialized)
            {
                // Handed to the constructor, which fails fast with an AggregateException.
                _parent.RecordStartupFault(ex);
            }
            else
            {
                // A shard dying mid-run must never be silent: it drops the work routed to it.
                Console.Error.WriteLine($"[shard {_shard} FAULTED] {ex.Message}");
                try { _parent.OnWorkerFaulted(ex); }
                catch (Exception etTu) { Debug.WriteLine(etTu.Message); }
            }
        }
        finally
        {
            try { OnShutdown(); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
            _isActive = false;
        }
    }

    /// <summary>Callback-driven backends initialize inline on the constructing thread
    /// (no pump thread). Exceptions propagate to the constructor, which fails fast.</summary>
    internal void InitializeInline() => OnInitialize();

    /// <summary>Runs before the event loop (on the pump thread for threaded backends, or
    /// on the constructing thread for callback-driven ones). Throwing here fails
    /// construction: the parent collects the exception and fails fast.</summary>
    protected virtual void OnInitialize()
    {
    }

    protected abstract void OnRun();

    /// <summary>Runs on the worker thread as it exits (after a clean stop, a mid-run
    /// fault, or a failed init). Must tolerate partially-initialized state.</summary>
    protected virtual void OnShutdown()
    {
    }

    public virtual void Listen(EndPoint endpoint, object? userToken, bool local) // local == keep on this shard
        => throw new NotSupportedException($"{nameof(Listen)} on {endpoint.GetType().Name} is not supported.");

    public virtual void Connect(EndPoint endpoint, object? userToken)
        => throw new NotSupportedException($"{nameof(Connect)} on {endpoint.GetType().Name} is not supported.");

    /// <summary>Accept on an already-listening socket handle on this shard (see
    /// <see cref="SocketSet.ListenHandle"/>). The set takes ownership of the handle.</summary>
    public virtual void ListenHandle(nint handle, object? userToken)
        => throw new NotSupportedException($"{nameof(ListenHandle)} is not supported by this backend.");
}
