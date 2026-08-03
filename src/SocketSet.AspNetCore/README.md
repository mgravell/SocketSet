# SocketSet.AspNetCore

A Kestrel **connection transport** backed by [SocketSet](../SocketSet) — run ASP.NET Core's HTTP stack over
`io_uring` / `epoll` (Linux), IOCP / RIO (Windows), or a portable managed fallback, with optional
**TLS terminated in the transport** (OpenSSL or SChannel, plus kernel-TLS offload on Linux).

> Pre-alpha. No packages are published; reference the project directly. API and defaults may change.

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.UseSocketSet(o =>
{
    // o.Factory = SocketSetFactory.IoUring;   // default: io_uring on Linux, IOCP on Windows
    // o.Shards  = Environment.ProcessorCount;
    // o.Mode    = SocketSetBridgeMode.Byo;     // outbound-leg strategy (see below)
});

var app = builder.Build();
app.MapGet("/", () => "hello from SocketSet");
app.Run();
```

`UseSocketSet` **replaces** Kestrel's built-in socket transport. That is all a consumer needs; everything
below is optional tuning.

## TLS in the transport

Set `o.Tls` to terminate TLS below Kestrel — Kestrel then sees plaintext and never constructs an
`SslStream`. On Linux this is OpenSSL (optionally with kTLS kernel offload); on Windows, SChannel.

```csharp
builder.UseSocketSet(o =>
{
    o.Tls = new OpenSslTlsProvider(certPem, keyPem, kernelOffload: true); // Linux
});
```

The negotiated ALPN id is exposed via `ITransportTlsFeature` (ASP.NET Core has no built-in feature for it):

```csharp
app.MapGet("/alpn", (HttpContext ctx) =>
    ctx.Features.Get<ITransportTlsFeature>()?.NegotiatedProtocol ?? "(none)");
```

## Bridge modes (`SocketSetBridgeMode`)

How the outbound (application → socket) leg is driven. Pick per workload:

| Mode | How | Best for |
|---|---|---|
| `Byo` (default) | Kestrel's pipes are handed to the transport (`ctx.UsePipe`); the backend sends straight from pipe memory (zero-copy `writev` where supported). | **Large responses.** |
| `HalfPipe` | Outbound is a `CycleBuffer`-backed `PipeWriter` that drains to `Connection.Send` on Kestrel's flush thread — no pump task, no thread hop. Copies on send. | **Small/mid responses** (roughly ≤ 16 KB); it wins there on cheaper machinery. |
| `Classic` | Copy inbound into a `Pipe`; an outbound pump task hands buffers to `Connection.Send`. | The universal fallback, and the only mode on backends without zero-copy send (RIO, managed). |

Measured tradeoffs (loopback, single box — see the repo's `RESULTS.md`): `HalfPipe` leads
`Byo`/`Classic` by ~3–8% for 256 B–16 KB but is ~35% behind `Byo` at 256 KB, and costs ~12–18% more p99.

## Options

| Option | Meaning |
|---|---|
| `Factory` | Which backend (`SocketSetFactory.IoUring` / `Epoll` / `WindowsIocp` / `WindowsRio` / `Managed` / `Default`). |
| `Shards` | Number of loop threads (0 = backend chooses). |
| `PinWorkerThreads` | Pin each shard's loop thread to a core. |
| `Tls` | Transport-terminated TLS provider; null = plaintext. |
| `Mode` | Bridge mode (above). |
| `PipePinned` | Back the bridge pipes with a pinned-block pool (matches Kestrel's default; helps `Byo` at large payloads). |
| `PipeSegment` | Pipe block size (0 = framework default). |
| `PageSize` / `ReceiveBufferSize` / `WriteBuffers` | Backend buffer geometry (0 = backend default). |

## Diagnostics

`UseSocketSet` registers a `SocketSetTransportMetrics` singleton — resolve it from DI to read accept/close
counts and the buffer geometry the backend actually resolved:

```csharp
var metrics = app.Services.GetRequiredService<SocketSetTransportMetrics>();
app.MapGet("/stats", () => new { metrics.Accepts, metrics.Closes, metrics.ResolvedGeometry });
```

## Platform notes

- **Linux:** `io_uring` (default) or `epoll`; kTLS is probed at runtime and degrades to userspace OpenSSL
  when unavailable.
- **Windows:** IOCP (default) or RIO. `HalfPipe` uses only cross-platform `Connection.Send`, so it should
  work on IOCP/RIO, but that is currently unverified.
- **Anywhere:** the managed backend runs where a native one is unavailable (e.g. Docker's default seccomp
  blocks `io_uring`).
