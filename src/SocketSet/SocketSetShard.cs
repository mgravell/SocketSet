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
        try
        {
            if (_parent.Options.PinWorkerThreads)
            {
                if (OperatingSystem.IsLinux())
                {
                    LibC.PinCurrentThreadToCpu(_shard % Environment.ProcessorCount);
                }
            }
            OnRun();
        }
        catch (Exception ex)
        {
            // A shard dying (e.g. io_uring_setup ENOMEM under RLIMIT_MEMLOCK) must never
            // be silent: a dead shard silently drops any work routed to it.
            Console.Error.WriteLine($"[shard {_shard} FAULTED] {ex.Message}");
            try
            {
                _parent?.OnWorkerFaulted(ex);
            }
            catch (Exception etTu)
            {
                Debug.WriteLine(etTu.Message);
            }
        }
        _isActive = false;
    }

    protected abstract void OnRun();

    public virtual void Listen(EndPoint endpoint, object? userToken, bool local) // local == keep on this shard
        => throw new NotSupportedException($"{nameof(Listen)} on {endpoint.GetType().Name} is not supported.");

    public virtual void Connect(EndPoint endpoint, object? userToken)
        => throw new NotSupportedException($"{nameof(Connect)} on {endpoint.GetType().Name} is not supported.");
}
