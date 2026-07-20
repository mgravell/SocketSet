using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace FastNet.IOUring;

// basic IOUring, using pit-packing to compose client-id and op into queue data
abstract class IOUringEngine
{
    private readonly IOUringShard[] _shards;
    private readonly string _name;
    protected IOUringEngine(string name, int shards)
    {
        _name = name;
        var arr = new IOUringShard[shards];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = new(this, i);
        }
        _shards = arr;
    }

    public override string ToString() => _name;

    // callback invoked from the IO thread(s) when work is available
    internal virtual void OnRead(IOUringSocket socket, ReadOnlySpan<byte> payload)
    {
    }

    internal virtual void OnAccept(IOUringSocket socket)
    {
        // ^^^ and possibly a result?
    }

    private int _state;
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) is 0)
        {
            foreach (var shard in _shards)
            {
                var thread = new Thread(static state => Unsafe.As<IOUringShard>(state!).Run())
                {
                    Name = $"{_name} shard {shard.Id} IO loop",
                    Priority = ThreadPriority.AboveNormal,
                    IsBackground = true,
                };
                thread.Start(shard);
            }
        }
        else
        {
            throw new InvalidOperationException();
        }
    }

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) is 1)
        {
            foreach (var shard in _shards)
            {
                shard.Stop();
            }
        }
        else
        {
            throw new InvalidOperationException();
        }
    }

    private uint _next;

    private IOUringShard RoundRobin()
    {
        var arr = _shards;
        return arr[Interlocked.Increment(ref _next) % arr.Length];
    }
    public void Listen(EndPoint endPoint, object? userToken = null)
    {
        switch (endPoint)
        {
            // IP can multi-bind using reuse-port, with each accept servicing their own load
            case IPEndPoint ip:
                foreach (var shard in _shards)
                {
                    shard.Push(Bind(ip), 0, IOUringOperation.AcceptLocal);
                }
                break;
            // UDS can only have single-await, which can then round-robin load
            case UnixDomainSocketEndPoint uds:
                RoundRobin().Push(Bind(uds), 0, IOUringOperation.AcceptRoundRobin);
                break;
            default:
                throw new NotSupportedException(endPoint.GetType().Name);
        }
    }

    private int Bind(IPEndPoint ip)
    {
        // create a socket and bind to the given IP with reuse-port enabled, returning the
        // socket handle / file descriptor
        throw new NotImplementedException();
    }
    
    private int Bind(UnixDomainSocketEndPoint uds)
    {
        // create a socket and bind to the given UDS, returning the
        // socket handle / file descriptor
        throw new NotImplementedException();
    }
}
/*
class IOUringShard(IOUringEngine engine)
{
    private readonly ConcurrentDictionary<ushort, IOUringClient> _clients = [];

    private uint _wakeHandle;

    private void Wake(int signal = 1)
    {
        // kernel: poke _wakeHandle    
    }

    private volatile bool _isAlive = true;

    public void Stop()
    {
        _isAlive = false;
        Wake();
    }

    private readonly struct PendingCqe(ushort clientId, IOUringOperation operation)
    {
        // TODO any more structure
    }

    private readonly ConcurrentQueue<PendingCqe> _pendingCqes = [];

    private void Poke(PendingCqe cqe)
    {
        _pendingCqes.Enqueue(cqe);
        Wake();
    }
    public void Run()
    {
        // init an IOUring, push _wakeHandle in as an event so we can be interrupted,
        // and being a work look
        while (_isAlive)
        {
            // dequeue items, calling
            ushort clientId = default; // from data
            IOUringOperation op = default; // from data
            switch (op)
            {
                case IOUringOperation.Wake:
                    // TODO: reset event handle *first* (avoid race) 
                    while (_pendingCqes.TryDequeue(out PendingCqe cqe))
                    {
                        // push to CQE queue
                    }
                    break;
                case IOUringOperation.Accept:
                    // invent (update) new clientid
                    // create new client, add, and push back new accept SQE
                    // ...
                    IOUringClient client = new IOUringClient(clientId); // newid etc
                    _clients.TryAdd(clientId, client); // maybe a loop/updatre
                    PushAcceptSqe(); // keep listening
                    engine.OnAccept(client);
                    break;
                case IOUringOperation.Read:
                    if (_clients.TryGetValue(clientId, out var client)) engine.OnRead(...);
            }
        }
        
        // cleanup
    }

    public void Listen(EndPoint endPoint)
    {
        // bind and push an accept SQE, then
        PushAcceptSqe();
    }

    private void PushAcceptSqe()
    {
        throw new NotImplementedException();
    }
}
*/
public enum IOUringOperation : byte
{
    None = 0,
    AcceptLocal, // handle locally when accepted
    AcceptRoundRobin, // push back to engine when accepted
    Read = 3,
}

class IOUringSocket(ushort id)
{
    public ushort Id => id; // id relative to a shard
}
