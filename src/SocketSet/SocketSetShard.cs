using System.Diagnostics;
using System.Net;
using SocketSets.Native;

namespace SocketSets;

public abstract class SocketSetShard
{
    private volatile bool _isActive = true;
    private SocketSet _parent = null!; // set via init
    private int _shard;

    protected SocketSet Parent => _parent;
    
    protected bool IsActive => _isActive; 
    public void Stop()
    {
        _isActive = false;
        OnStop();
    }

    protected virtual void OnStop()
    {
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
        if (_parent.Options.PinWorkerThreads && OperatingSystem.IsLinux())
        {
            LibC.PinCurrentThreadToCpu(_shard % Environment.ProcessorCount);
        }

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

    /// <summary>Runs on the worker thread before the event loop. Throwing here fails
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
}
