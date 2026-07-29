# SocketSet

High-performance, low-allocation socket hosting for .NET.

`SocketSet` is a sharded socket engine that puts your callbacks as close to the kernel's completion
notification as the platform allows. It picks the best available backend for the host and presents a
single API over all of them:

| Backend | Platform | Notes |
| --- | --- | --- |
| `SocketSetFactory.IoUring` | Linux (5.x+, with the required features) | thread-per-shard, multishot accept, provided buffers, zero-copy echo |
| `SocketSetFactory.WindowsIocp` | Windows | raw Winsock + IOCP, bypassing managed sockets |
| `SocketSetFactory.WindowsRio` | Windows | Registered I/O; TCP-only, opt-in, latency-focused |
| `SocketSetFactory.Managed` | anywhere | portable `SocketAsyncEventArgs` fallback |

`SocketSetFactory.Default` probes the host and chooses for you (IOCP on Windows, io_uring on a
capable Linux kernel, otherwise the managed fallback), so the same binary runs everywhere and simply
goes faster where the platform lets it.

Supported frameworks: `net10.0` and `net472`. (The native backends are .NET-only; .NET Framework
gets the managed fallback.)

## Working on SocketSet?

Start with [`AGENTS.md`](AGENTS.md) — it points at the backlog ([`TODO.md`](TODO.md)), the measurements of
record ([`AspNetDemo/RESULTS.md`](AspNetDemo/RESULTS.md)) and the benchmarking rules
([`bench/README.md`](bench/README.md)), and lists the house rules for producing a number anyone should
believe. **If you are picking this up on Windows, `TODO.md` opens with a section written for you.**

## Installation

```
dotnet add package SocketSet
```

## Usage

Derive from `SocketSet` and override the callbacks you care about. Buffers are handed to you as
`Span<byte>` over memory the engine owns — you never allocate on the IO path, and you reply by
writing into the response buffer rather than by handing back an array.

```csharp
using SocketSets;
using System.Net;

sealed class EchoServer(SocketSetOptions options) : SocketSet(options)
{
    protected override void OnReceive(ref ReceiveContext ctx)
    {
        if (ctx.IsEof) return; // peer closed

        // the payload is already sitting in RawBuffer; reply in-place by saying how much
        // of that buffer to send back (mutate RawBuffer first if the response differs)
        ctx.ResponseBytes = ctx.PayloadBytes;
    }
}

using var server = new EchoServer(new SocketSetOptions { Shards = 4 });
server.Listen(new IPEndPoint(IPAddress.Any, 10000));
Console.ReadLine();
```

Outbound connections use the same type; `OnConnect` fires when the handshake completes, and
`OnReceive` handles the replies:

```csharp
server.Connect(new IPEndPoint(IPAddress.Loopback, 10000));
```

To write from outside the IO callback (a background worker, a timer, a different connection's
callback), use the `Connection` handed to you — `Send` marshals onto the owning IO context and
serializes with the connection's other writes:

```csharp
connection.Send(payload); // safe from any thread
```

`Connection` also implements `IBufferWriter<byte>`, so `GetSpan`/`Advance`/`Flush` works for
incremental composition.

Unix domain sockets are supported on all backends (`UnixDomainSocketEndPoint`, including the Linux
abstract namespace via a leading `@`), and an already-bound listener can be adopted by handle with
`ListenHandle` for socket-activation scenarios.

## Configuration

`SocketSetOptions` controls sharding and the pre-allocated, pre-pinned buffer pools:

```csharp
var options = new SocketSetOptions
{
    Shards = Environment.ProcessorCount / 2,
    SocketsPerShard = 4096,
    PinWorkerThreads = true,
    Factory = SocketSetFactory.Default,
};
```

Everything is sized up front: read buffers, write buffers, and connection slots are allocated per
shard when the set is constructed, so steady-state operation does not allocate. See the XML docs on
`SocketSetOptions` for the individual knobs and which backends honour them.

## License

[MIT](LICENSE)
