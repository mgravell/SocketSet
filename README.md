# SocketSet

High-performance, low-allocation socket hosting for .NET.

`SocketSet` is a sharded socket engine that puts your callbacks as close to the kernel's completion
notification as the platform allows. It picks the best available backend for the host and presents a
single API over all of them:

| Backend | Platform | Notes |
| --- | --- | --- |
| `SocketSetFactory.IoUring` | Linux (5.x+, with the required features) | thread-per-shard, multishot accept, provided buffers, zero-copy send |
| `SocketSetFactory.Epoll` | Linux | readiness-driven fallback for kernels without the io_uring features; zero-copy `writev` send, full TLS/kTLS support |
| `SocketSetFactory.WindowsIocp` | Windows | raw Winsock + IOCP, bypassing managed sockets |
| `SocketSetFactory.WindowsRio` | Windows | Registered I/O; TCP-only, opt-in, **throughput**-focused — see the note below before choosing it |
| `SocketSetFactory.Managed` | anywhere | portable `SocketAsyncEventArgs` fallback |

`SocketSetFactory.Default` probes the host and chooses for you (IOCP on Windows; io_uring on a
capable Linux kernel, epoll otherwise; the managed fallback elsewhere), so the same binary runs
everywhere and simply goes faster where the platform lets it.

> **Choosing RIO on Windows: don't, unless you have measured a reason to.** RIO's request queues are
> drained in user mode with no per-op syscall, but every operation must still round-trip through a
> completion queue, and that carries a floor of roughly 3µs per completion which no amount of tuning
> removes — it is only ever amortised across a batch. IOCP avoids it structurally, completing a receive
> that already has buffered data *inside the syscall* with no completion at all. So RIO pays off with
> deep pipelines, many continuously-busy connections, or bulk transfer, and loses heavily on low-depth
> request/response — including any single multiplexed client connection, which is the normal shape for a
> .NET Redis client. Measured on one machine over loopback, RIO ran 13-25x behind IOCP at depth 1 and
> reached comparable throughput at depth 16.
> **[`IOCP-VS-RIO.md`](IOCP-VS-RIO.md)** has the measurements, the recommendation, and the seven things
> tried that did not change it.

Supported frameworks: `net10.0` and `net472`. (The native backends are .NET-only; .NET Framework
gets the managed fallback.)

## Working on SocketSet?

Start with [`AGENTS.md`](AGENTS.md) — it points at the backlog ([`TODO.md`](TODO.md)), the measurements of
record ([`RESULTS.md`](RESULTS.md)) and the benchmarking rules
([`bench/README.md`](bench/README.md)), and lists the house rules for producing a number anyone should
believe. **If you are picking this up on Windows, `TODO.md` opens with a section written for you.**

## Installation

```
dotnet add package SocketSet --prerelease
```

(`--prerelease` because current versions are `-alpha`; see below.)

Two companion packages host existing servers on the transport, and ship alongside it:

- **`SocketSet.AspNetCore`** — a Kestrel connection transport: `builder.UseSocketSet(...)`, with
  optional transport-terminated TLS (including kTLS on Linux).
- **`SocketSet.Garnet`** — hosts [Garnet](https://github.com/microsoft/garnet) on the SocketSet
  transport via its pluggable `IGarnetServer` seam.

All three are currently published as `-alpha`: this is pre-alpha code and every API is free to change.

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

        // the payload is already sitting in the buffer; reply in-place by saying how much
        // of it to send back
        ctx.ResponseBytes = ctx.PayloadBytes;

        // to reply with something DIFFERENT, ask for the room you need and write into it:
        //   var reply = ctx.GetWriteSpan(ctx.PayloadBytes + 5); // may return less - check .Length
        //   ...fill reply...
        //   ctx.ResponseBytes = reply.Length;
        // The buffer is shared and recycled across connections, so anything past PayloadBytes
        // that you have not asked for is zeroed before you or the peer can see it. Asking for
        // exactly what you need is what keeps that free: a reply no bigger than the request
        // clears nothing, and growing by 5 bytes clears 5 bytes, not the whole buffer.
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

## TLS

TLS is terminated **in the transport**, not in a stream wrapper above it: set a `TlsProvider` on the
options and the handshake, record framing and encrypt/decrypt happen on the engine's own buffers, with
your callbacks seeing plaintext. OpenSSL backs Linux (and can hand the record layer to the kernel —
**kTLS** — where the OpenSSL build and kernel support it, probed at runtime and reported rather than
assumed); SChannel backs Windows. TLS 1.3 is the default floor on both, and servers refuse
client-initiated renegotiation.

```csharp
var options = new SocketSetOptions
{
    Tls = new OpenSslTlsProvider(certPem, keyPem),   // or SChannelTlsProvider on Windows
    TlsMode = TlsMode.Both,                          // or Accept / Connect for proxy shapes
};
```

`TlsMode` makes the provider directional: `Accept` for a TLS-terminating proxy (TLS in, plaintext
out), `Connect` for a TLS-originating one (plaintext in, TLS out) — a direction that is off behaves
exactly as if no provider were configured.

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
