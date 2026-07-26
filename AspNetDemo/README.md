# AspNetDemo — ASP.NET Core (Kestrel) over a SocketSet transport

Replaces Kestrel's default socket transport with a SocketSet backend, so ASP.NET Core runs its entire
HTTP stack over io_uring / IOCP / RIO / managed sockets — optionally with TLS terminated *in the
transport* rather than by Kestrel's `SslStream`.

Every axis is a command-line flag so configurations can be A/B'd without a rebuild; see `--help`.

## How it works

- `SocketSetTransportFactory : IConnectionListenerFactory` — Kestrel asks it to bind an endpoint.
- `SocketSetConnectionListener : IConnectionListener` — wraps a SocketSet io_uring `SocketSet` instance. It
  *pushes* accepts (`OnAccept` on the loop thread); Kestrel *pulls* (`AcceptAsync`), so accepts cross an
  unbounded channel.
- `SocketSetConnection : ConnectionContext, IDuplexPipe` — bridges SocketSet's push model to Kestrel's
  `IDuplexPipe` with two `System.IO.Pipelines.Pipe`s:
  - **inbound**: `OnReceive` copies bytes into the inbound pipe *on the loop thread* (frees SocketSet's
    recv buffer immediately — "unload ASAP") → Kestrel reads them.
  - **outbound**: Kestrel writes responses → a per-connection pump reads them → `Connection.Send`
    (SocketSet's thread-safe out-of-band path).
  Pipe schedulers are `ThreadPool`, so no Kestrel/pump work runs on the io_uring loop thread.

## Run

```
dotnet run -c Release --project AspNetDemo -- --help
curl  http://127.0.0.1:5080/config           # what is actually running — check this first
curl -k https://127.0.0.1:5080/plaintext     # -k: the certificate is self-signed
curl -X POST --data-binary @somefile http://127.0.0.1:5080/echo
curl "http://127.0.0.1:5080/big?n=100000" | wc -c
curl  http://127.0.0.1:5080/stats            # transport counters (diagnostics)
```

## The A/B matrix

The comparisons this exists to answer — "how does vanilla Kestrel with TLS compare to X":

| Question | Command |
|---|---|
| control: Kestrel sockets + SslStream | `--kestrel --tls` |
| IOCP + SChannel/SSPI | `--iocp --tls` |
| RIO + SChannel/SSPI | `--rio --tls` |
| io_uring + OpenSSL (userspace) | `--io-uring --tls` |
| io_uring + kernel TLS offload | `--ktls` |
| any of the above, plaintext | drop the TLS flag |

`--managed` selects the portable managed-socket backend; with no backend flag the transport
auto-detects (IOCP on Windows, io_uring on Linux) and `/config` reports what it actually resolved to.

**Fairness.** All TLS legs — including Kestrel's own — present the *same* certificate: one RSA-2048 /
SHA-256 self-signed key generated once per process (`DemoCertificate`). Certificate choice moves TLS
numbers a lot, so this is deliberately not a per-leg variable. Every leg is also pinned to HTTP/1.1,
so a TLS leg cannot quietly negotiate HTTP/2 via ALPN while a plaintext leg stays on 1.1.

`/config` reports the resolved backend, the certificate, `Request.IsHttps` and the negotiated ALPN id —
worth checking before trusting any number.

Measured results, and the confounders that invalidated several attempts at measuring them, are in
[`RESULTS.md`](RESULTS.md). Read it before running your own — a single-box loopback benchmark has more
ways to lie than to tell the truth.

Validated: single requests, HTTP/1.1 keep-alive, POST bodies, and large (250 KB, multi-segment)
responses, over every Windows leg and over Kestrel/managed/kTLS on Linux.

> **Measuring io_uring needs a real Linux host.** Under Docker Desktop the io_uring *data* path does not
> work: the default seccomp profile blocks the syscalls outright (the backend silently falls back to
> managed sockets — check `/config`), and with `--privileged` io_uring is selected but multishot receive
> yields no completions on the WSL2 kernel. The kTLS leg happens to survive because it drives the socket
> through `POLL` + `SSL_read`/`SSL_write` rather than io_uring's receive path.

> **Note (2026-07-26):** everything below was investigated while this demo was inadvertently capped at
> ~2,000 rps by per-request `Information` logging (no `appsettings.json`, so
> `Hosting.Diagnostics` logged every request behind a console lock). That is fixed in `Program.cs`; the
> app now reaches ~250k rps. The findings below are about *correctness* and still stand, but the
> concurrency they were reproduced at was far lower than the same `xargs -P 8` command produces today —
> worth re-running if you want confidence at current rates. See [`RESULTS.md`](RESULTS.md).

## RESOLVED — the ~4–8% RST truncation under concurrency (2026-07-25)

Concurrent load (`xargs -P 8`) used to fail ~4–8% of requests with **curl exit 52** (reset / empty reply).
Now **0 failures** (P8×600 and P16×400 → all 200s), matching Kestrel's default transport.

**Root cause (fully isolated, not guessed):**
- **Not** OOB send data-loss — instrumenting `SubmitFlush`→`PumpFlush`→`DispatchChain`→`WriteV` showed
  `enq==deq`, 0 drops, 0 send errors, every `WriteV` completing (`wvDone`). SocketSet sends every byte.
  (This also cleared the Redis-client OOB concern — same path.)
- **Not** unread-data-RST (`FIONREAD`-at-close probe: ~0), not latency (30 s timeout didn't help), not a
  multi-shard race (reproduced at 1 shard).
- A bare SocketSet HTTP responder with **no Kestrel/pipes** (see `SmokeTest --http`) passed **400/400**,
  and disabling *all* bridge-initiated `_conn.Close()` made this demo pass **400/400** — pinpointing the
  bug to **this bridge eagerly calling `Connection.Close()` from `DisposeAsync`/the pump**. SocketSet's
  `Close()` is *abortive* (it cancels a queued/in-flight send → RST), so calling it while a just-written
  response is still queued truncated the response.

**Fix (bridge-side, no change to SocketSet's teardown):** don't abortive-close from `DisposeAsync`/the
pump. Let the connection close the graceful way — the client closes → SocketSet's recv sees EOF → its own
teardown runs *after* the response has gone out (proven graceful). A genuine `Abort()` still force-closes
(a RST is correct there). See `SocketSetConnection.PumpOutboundAsync`/`DisposeAsync`/`Abort`.

**Residual (SocketSet core, noted not fixed):** `Connection.Close()` is abortive and truncates a queued
send. Harmless for the client-close and abort paths, and this demo no longer hits it. But a *server*-side
graceful close with a pending write (or the Redis client closing with an unsent command) would still want a
"flush-then-close" primitive in SocketSet — a small, well-understood core addition, deferred as a design call.

The `/stats` endpoint (`Accepts`/`Closes`/`ClosedEmpty`/`WriteFail`/`SendFalse`) and `--default-transport`
A/B toggle remain as diagnostics.
