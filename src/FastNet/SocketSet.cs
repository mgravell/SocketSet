using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using FastNet.Transport;

namespace FastNet;

public abstract partial class SocketSet : IDisposable
{
    public static SocketSet Create()
    {
        if (OperatingSystem.IsLinux())
        {
            return CreateIOUring();
        }

        return CreateManagedSockets();
    }

    private SocketSetShard[]? _shards;

    private ReadOnlySpan<SocketSetShard> Shards => _shards;

    public void Init(int shards = 4)
    {
        if (shards <= 0) ThrowArg();

        // empty place-holder while we init
        if (Interlocked.CompareExchange(ref _shards, [], null) is not null)
            ThrowInvalid();
        var arr = new SocketSetShard[shards];
        for (int i = 0; i < arr.Length; i++)
        {
            var obj =CreateShard();
            obj.Init(this, i);
            arr[i] = obj;
        }
        _shards = arr;
        
        static void ThrowArg() => throw new ArgumentOutOfRangeException(nameof(shards));
        static void ThrowInvalid() => throw new InvalidOperationException();
    }

    public void Run()
    {
        var arr = _shards;
        if (arr is null) Throw();
        switch (arr.Length)
        {
            case 0:
                Throw();
                break;
            case 1:
                arr[0].Run();
                break;
            default:
                var threads = new Thread[arr.Length - 1];
                for (int i = 0; i < threads.Length; i++)
                {
                    var runner = new Thread(static obj => Unsafe.As<SocketSetShard>(obj!).Run());
                    runner.IsBackground = false;
                    runner.Name = arr[i + 1].ToString();
                    threads[i] = runner;
                }
                // defer starts until all are init'd
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i].Start(arr[i + 1]);
                }
                arr[0].Run();
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i].Join();
                }
                break;
        }

        //Thread[] threads = 
        //for (int i = 0 ; )
        [DoesNotReturn]
        static void Throw() => throw new InvalidOperationException();
    }

    private uint _nextIndex;

    protected SocketSetShard NextShard()
    {
        var shards = Shards;
        return shards[(int)(Interlocked.Increment(ref _nextIndex) % shards.Length)];
        
    }

    protected virtual bool IsSingleListener => true;
    public void Listen(EndPoint endpoint)
    {
        ThrowIfDisposed();
        if (IsSingleListener) NextShard().Listen(endpoint);
        else
        {
            foreach (var shard in Shards)
            {
                shard.Listen(endpoint);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed) Throw(this);
        static void Throw(object @this) => throw new ObjectDisposedException(@this.GetType().Name);
    }

    protected abstract SocketSetShard CreateShard();

    public static SocketSet CreateRIO()
    {
        throw new NotImplementedException();
    }

    public static SocketSet CreateIOUring()
    {
        throw new NotImplementedException();
    }

    public static SocketSet CreateManagedSockets()
    {
        throw new NotImplementedException();
    }

    private bool _isDisposed;
    public void Dispose()
    {
        _isDisposed = true;
        GC.SuppressFinalize(this);
        OnDispose(true);
    }

    ~SocketSet() => OnDispose(false);

    protected virtual void OnDispose(bool disposing)
    {
        if (disposing)
        {
            if (Interlocked.Exchange(ref _shards, null) is { } shards)
            {
                foreach (var shard in shards)
                {
                    shard.Dispose();
                }
            }
        }
    }
}