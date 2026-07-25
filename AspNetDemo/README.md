# AspNetDemo — ASP.NET Core (Kestrel) over a SocketSet io_uring transport

A working demo that replaces Kestrel's default socket transport with a SocketSet io_uring backend,
so ASP.NET Core runs its entire HTTP stack over io_uring.

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
dotnet run -c Release --project AspNetDemo      # listens on http://127.0.0.1:5080
curl http://127.0.0.1:5080/          # Hello ...
curl http://127.0.0.1:5080/ping      # {"ok":true,"transport":"socketset-io_uring"}
curl -X POST --data-binary @somefile http://127.0.0.1:5080/echo
curl "http://127.0.0.1:5080/big?n=100000" | wc -c
curl http://127.0.0.1:5080/stats     # transport counters (diagnostics)
```

Validated: single requests, HTTP/1.1 keep-alive (connection reused), POST request bodies, and large
(100 KB, multi-segment) responses all work.

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
