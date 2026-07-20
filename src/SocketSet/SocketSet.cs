using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SocketSets;

public abstract partial class SocketSet : IDisposable
{
    public SocketSetOptions Options { get; }
    private SocketSetShard[] _shards;
    private uint _next;

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
    }

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

    private SocketSetShard RoundRobin()
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
}