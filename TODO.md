# TODO

Engineering backlog — design calls and deferred work. Not user-facing (see `README.md` for that).

---

## START HERE (state as of 2026-07-27)

Orientation for picking this up cold.

**Backends.** io_uring + epoll (Linux), IOCP + RIO (Windows), managed (portable fallback). All except
managed own one loop thread per shard; managed is callback-driven, which is why it alone needs a
per-connection TLS gate. TLS is wired into every backend: SChannel via raw SSPI on Windows, OpenSSL
(optionally kTLS) on Linux. ALPN works on both providers.

**Recently landed, in order:** SChannel TLS without SslStream; ALPN; TLS on IOCP/RIO; the epoll backend
(+ TLS); a fix that made io_uring work at all (multishot recv was submitted with a non-zero `len`, which
the kernel rejects with `-EINVAL`, so *every* recv failed silently); scatter-gather sends on IOCP.

**The one measured performance win:** IOCP sends were quantised to a single 4KB write page, so a 256KB
response left as 64 sequential `WSASend`s. Issuing one call with up to 64 `WSABUF`s gave **+133% at 16KB
and +162% at 256KB**, validated with `bench/Compare-Commits.ps1`. See `AspNetDemo/RESULTS.md`.

**In flight and UNMEASURED:** `fa97dd4` makes the out-of-band flush snapshot rent from `ArrayPool`
instead of allocating. Its A/B was contaminated by a power loss mid-run and is void. Re-run:

```
.\bench\Compare-Commits.ps1 -Before HEAD~1 -After HEAD     # (while fa97dd4 is HEAD)
```

Uncontaminated passes leaned flat-to-slightly-positive, which if confirmed is the *informative* answer:
removing the allocation alone changes little, so **copies dominate** — go to item 2b below.

**Before trusting any measurement, read `bench/README.md`.** It documents the eight confounders that each
produced clean-looking wrong numbers, and the noise floor (~6% between identical builds on this host).

**Direction of travel:** BYO-buffer, in measured steps — see 2b. The end goal is that a caller supplies
the memory (ideally pinned/registered) and we stop copying into it. Accepting single-shot reads, or
bypassing provided buffers, is an acceptable price for minimal copy. A robust fallback is required for
backends that cannot take foreign memory at all — RIO takes only registered `BufferId`s, never addresses.

---

---

## Factor the shared IOCP/RIO data path

**Status:** proposed, not started.

`IocpShard` and `WindowsRioShard` have evolved in parallel and are now largely the same code. Measured
2026-07-26: normalise the two type names and **646 lines are byte-identical** (~57% of the RIO file,
which is 1126 lines to IOCP's 1263); 38 methods share a name across both. Some are already
character-for-character identical (`QueueCipher`, `SendEncrypted`, `FireTlsOpen`, `DriveTlsHandshake`,
`StartPendingSend`).

The genuine differences cluster in only two places:

- the I/O primitive — `WSASend`/`WSARecv` + per-op `OVERLAPPED` vs `RIOSend`/`RIOReceive` + request
  queue, completion queue, and deferred-commit batching;
- teardown bookkeeping — RIO additionally carries `Rq`, `CommitPending`, `CommitRecv`, `CommitSend`.

**Why it matters:** every feature is currently written twice. TLS interception was (2026-07-25), and an
epoll backend (below) would make it a third copy of the send machinery. That is the argument — not
tidiness.

**Shape:** a shared `WindowsShardBase<TConn>` owning slot lifecycle (`InitClient`/`FreeSlot`/
`CloseClient`/`TryFinalize`), cross-thread marshalling (`EnqueueInbound`/`SubmitClose`/`SubmitFlush`/
`DrainCrossThread`/`Poke`), listen + connect (`Listen`/`ListenHandle`/`Connect`/`StartConnect`/
`CreateListener`/`StartAccept`/`PostAccept`/`HandleAccept`), the send state machine (`Pending`,
`StageOutbound`, `StartPendingSend`, `CompleteWrite`, `SubmitSendBuffer`, `FailSend`, `HandleSend`), the
whole TLS block, and adoption (`AdoptAccepted`/`HandleConnect`). Each backend keeps only: arm-a-receive,
issue-a-send, the completion pump (`OnRun`), its setup/teardown, and RIO's commit-batching hook.

Half the pattern already exists: `WindowsOutboundConnection` is the shared base for the connection
types, and `IocpConnection`/`RioConnection` already declare identical `Socket`/`Generation`/`Closing`/
`RecvArmed`/`RecvBuf`/`SendBusy`/`SendBuf`/`SendSent`/`SendTotal`/`Pending`/`IsClient` fields. Those move
down; `SkipOnSuccess` stays on IOCP, `Rq`/`Commit*` on RIO.

**Decide up front:**

1. *Dispatch cost.* A plain abstract base puts a virtual call on `IssueSend`/`ArmRecv` — per operation,
   on the hottest path. Prefer a generic-struct ops parameter (`where TOps : struct, IShardOps`) so the
   JIT devirtualises.
2. *Test story.* No unit tests; this is the hottest code in the repo. The smoke matrix is now good enough
   to be a before/after gate — `--verify-echo`, `--verify`, `--churn`, `--poke`, plaintext and TLS — but
   it must be run across all three Windows backends on both sides of the change.

---

## epoll backend for Linux

**Status:** proposed, not started. Sequence **after** the IOCP/RIO factoring, so it lands on a shared
base rather than adding a fourth copy of the send machinery.

**Not for throughput.** Linux already falls back to `SocketSetFactory.Managed`, and .NET's managed socket
async path *is* epoll (`SocketAsyncEngine`). So this is not "add epoll", it is "replace .NET's epoll
engine with our own" — unlikely to win much on syscall efficiency, and it will never beat io_uring where
io_uring is available.

**The actual argument — threading-model uniformity.** `ManagedSocketShard` reports
`UsesWorkerThreads = false`: no loop thread, completions land on arbitrary thread-pool threads. That is
why it alone needs `conn.TlsGate`, a coarse per-connection lock held across encrypt→enqueue, and why it
cannot share shard-wide scratch the way io_uring/IOCP/RIO do. A native epoll backend with the
single-owner loop-thread model collapses that special case: one shape for all backends, no gate, features
written once.

**And the fallback matters more than it looks.** Discovered 2026-07-25: Docker's default seccomp profile
blocks io_uring outright, so a containerised Linux deployment silently lands on the managed backend
(confirmed — the container reports `ManagedSocketFactory` unless `--privileged`). GKE and others disable
it too. "Linux means io_uring" is not safe to assume, which makes the weakest backend the one that often
runs in production.

**Cost, honestly:** epoll is *readiness*, not completion — a different model from the other three. Needs a
readiness→completion adapter (on `EPOLLIN`, non-blocking `recv` into a pooled buffer and synthesise the
completion the shard API expects; on `EPOLLOUT`, drain the write queue), with `EAGAIN`/partial-write
handling moving into the loop and per-connection writable-interest registration (arm/disarm `EPOLLOUT`)
that no other backend needs. That last part is the usually-underestimated bit.

**Do first:** measure io_uring vs epoll vs managed with `--latency` and `--bandwidth`. If they land within
a few percent, the performance case is dead and the decision is purely architectural. This IS measurable
under Docker with `--security-opt seccomp=unconfined` (see below), though a loopback container shares all
the caveats in `AspNetDemo/RESULTS.md`.

**Status: DONE (2026-07-26).** `src/SocketSet/Epoll`, with TLS. Passes the smoke matrix - echo, byte-exact
verify, out-of-band send, poke, churn, AF_UNIX, ALPN. Measured at parity with io_uring and stock Kestrel
on small-message plaintext (102.5k vs 105.2k vs 102.8k rps, ~3% spread). What remains is the measurement
below, on real hardware.

---

## Performance follow-ups (from the 2026-07-26 Linux size sweep)

Goodput MiB/s, median of 3 scored passes, Docker/loopback (`bench/run-tls-sizes.sh`):

| payload | iouring | epoll+tls | iouring+tls | iouring+ktls | kestrel+tls |
|---|---:|---:|---:|---:|---:|
| 512 B | 94.9 | 70.3 | 69.4 | 74.8 | 65.2 |
| 16 KB | 2,401 | 1,602 | 1,395 | 1,350 | 1,673 |
| 256 KB | 4,379 | 2,814 | **1,183** | 1,951 | **8,383** |

Unlike the small-message numbers these repeat tightly (~1-10% between passes), so the large-payload
shape is worth acting on.

### 1. io_uring+TLS large-payload behaviour — INVESTIGATED 2026-07-26, NOT CONFIRMED

**Status: could not reproduce in a container. Do not treat the regression below as established.**

What was checked, so it is not repeated:

- *Pool-exhaustion hypothesis: REFUTED.* `TlsSend` copies ciphertext into `WritePageSize` (4KB) chunks
  leased from the out-of-band pool, falling back to a PINNED GC allocation per chunk when dry. One 256KB
  response needs 64 chunks against a 256-chunk-per-shard pool, so exhaustion looked likely. Measured with
  `SS_URING_STATS=1` across 512B-256KB x 8-64 connections: **0.0% miss in every combination.** Pages are
  released as sends complete; the pool is never the constraint. A tidy story, and wrong.
- *Throughput: did not reproduce.* A pure-transport comparison (SmokeTest, no Kestrel bridge) gave
  contradictory results run to run at 8 connections, and at 64 connections every backend converged on
  ~6.5 GB/s - a shared ceiling, so the test was bounded by the host rather than the transport. The
  original 16KB->256KB decline came from the ASP.NET sweep, where the bridge is also in the path (item 3).

**What remains, and is real regardless of timing:** io_uring copies EVERY ciphertext byte into 4KB chunks
and dispatches a writev of N segments (~1.6M chunk-copies/sec at load), where epoll sends directly from
the TLS output buffer with zero copies. That is a genuine structural difference in the send path. Whether
it costs measurable throughput needs a host that is not saturating elsewhere - i.e. real hardware.

Worth knowing before optimising it: the copy exists for a reason. `conn.TlsOut` is a reusable
`PooledBufferWriter`, so its buffer cannot be handed to an in-flight io_uring send. `PooledBufferWriter`
already has `TakeArray()` for exactly this hand-off pattern; the obstacle is that io_uring needs a stable
(pinned) address for the duration, and ArrayPool arrays are not pinned.

### ~~1b. Original observation (superseded by the above)~~

1,395 -> 1,183 MiB/s while every other leg rises. Goodput falling as payload grows is a defect signature,
not a tuning problem. Prime suspect is the outbound path: `OutChain`/writev plus write-page chunking
interacting badly with 16KB TLS records. Note io_uring+TLS is also the *slowest* leg at 256KB, below both
epoll+TLS (2.4x) and kTLS (1.6x) - it should not be.

Method that worked for the last io_uring bug: `SS_URING_TRACE=1` to see completions (failures are a
negative `cqe.res`, never a syscall error), and bisect against a minimal raw probe before blaming the
kernel or the environment.

**Highest expected value of anything on this list.**

### 2. Re-run the size sweep now the plaintext controls are in

The first sweep had one plaintext control and it was the wrong one, so nothing at >=16KB is fully
interpretable yet. Plaintext `kestrel` and `epoll` legs added 2026-07-26; just needs a run.

### 0. IOCP/RIO sends are page-quantised - 4x on the table at large payloads (Windows, 2026-07-26)

**The single most actionable finding so far.** `BufferPageSize` defaults to 4096 and the Windows send path
puts at most ONE page in flight per connection, so a 256KB response goes out as **64 sequential 4KB
WSASends**, each with its own completion-port round trip. Kestrel's transport issues one `SendAsync` over
the whole buffer.

Measured on the BARE SocketSet HTTP responder (no Kestrel, no bridge), 256KB payload, `-c 64`, 16 shards,
median of 2 scored passes:

| page size | goodput | vs 4KB |
|---|---:|---:|
| 4KB (default) | 885 MiB/s | - |
| 16KB | 2,503 MiB/s | 2.8x |
| 64KB | 3,556 MiB/s | **4.0x** |

Passes were tight (878/885, 2474/2503, 3257/3556), so this is not noise.

**Do not just raise the default.** Page size is a trade-off, not a dial: at a 16KB payload the best page is
16KB (2,103 MiB/s) and a 64KB page is WORSE (1,273 MiB/s) because most of the page is wasted. A fixed page
is wrong at both ends.

The real fix is to stop quantising sends by page at all:

- `IocpShard.IssueSend` posts a single WSABUF (`WSASend(sock, &b, 1, ...)`), and `CompleteWrite` coalesces
  the pending queue into one write page. Build a WSABUF **array** from the pending chain and issue one
  WSASend with count > 1 - scatter/gather is exactly what the API is for.
- RIO has the same shape and `RIOSend` likewise takes a buffer array.
- **io_uring already does this**: `TlsSend` builds an `OutChain` of segments and dispatches one writev.
  So this is an IOCP/RIO gap, not a design gap - the shape to copy already exists in-tree.

This also reframes the shard-count result: throughput scaled near-linearly with shards (2->4->8->16, then
flat at 32 = the pinned core count) precisely BECAUSE each connection's sends are serialised. Parallelism
across connections was the only lever available.

### 2b. BYO-buffer: caller-supplied (pinned) pipes, and killing the copies below them

Proposal: let a connection optionally accept a caller-supplied `IDuplexPipe` plus a "this memory is
pinned" flag, and drive I/O straight off it - so a caller who hands us a pipe backed by a pinned-heap
pool (as Kestrel's own `PinnedBlockMemoryPool` does) pays no pinning and no copy.

Right target. Today the out-of-band path copies **three times** for one response:

1. `OutboundConnection.Flush` -> `w.WrittenSpan.ToArray()` - an UNPOOLED full-size allocation
2. `PumpFlush` -> `StageOutbound` copies that array again into pooled `Pending` segments
3. the drain copies those segments into write pages

Split it into two tiers, because only one of them is cheap.

**Tier 1 - no API change, do this first.** Rent in `Flush` instead of `ToArray`, and transfer ownership of
that array straight into `Pending` rather than copying it again. Removes one allocation and one full copy
per response. The only care needed is that `Pending` segments currently must not exceed a write page (the
packing invariant in `DrainPendingIntoPages`), so an over-page segment has to be split or handled. This is
the piece worth doing on its own merits - the bridge is now ~47% of the remaining gap to Kestrel.

**Tier 2 - the actual BYO-pipe. Not a no-brainer; two things bite.**

*Ownership, not pinning, is the blocker.* `Flush` snapshots via `ToArray` precisely so the writer may reuse
its buffer immediately. Sending directly from pipe memory means the pump must not `AdvanceTo` until the
send completes, so `Connection.Send`/`Flush` need a completion signal (`ValueTask` / `IValueTaskSource`)
rather than today's fire-and-forget bool. That is the real design change, and it ripples into every
backend's send-completion path.

*"Pinned" is not a sufficient capability, because RIO does not take addresses.* RIO wants
`RIO_BUF { BufferId, Offset, Length }` where `BufferId` comes from `RIORegisterBuffer` - so caller memory
is unusable to it unless that slab is registered up front. Meanwhile io_uring only needs a stable address
(pinned suffices) and epoll needs nothing at all, since `send()` is synchronous and copies before
returning. So the contract is not one `IsPinned` bool; it is a per-backend capability, something closer to
"can you accept foreign memory, and on what terms" - with RIO able to answer "only if I registered it".

A workable shape: the caller supplies the pool, the SET registers/pins it once at construction (RIO
registers the slab; io_uring/IOCP just require pinning; epoll ignores it), and connections then hand out
memory from it. That inverts the direction - we accept a POOL, not a pipe - and sidesteps the per-buffer
capability question entirely. Probably the better design.

### 3. The AspNetDemo bridge - real, but much smaller than first claimed

**Correction.** This was described as "the bottleneck at large payloads" on the strength of the Linux
sweep. A controlled Windows comparison - same client, same pinning, same payload, bare SocketSet HTTP
responder vs the same transport behind the Kestrel bridge - puts the bridge at **12% at 16KB and 23% at
256KB**, not the ~4x it was blamed for:

| | 16KB | 256KB |
|---|---:|---:|
| bare SocketSet responder | 1,655 | 2,454 |
| AspNetDemo iocp/s16 (bridged) | 1,455 | 1,899 |
| AspNetDemo kestrel (own transport) | ~3,740 | ~9,417 |

The bridge is worth fixing (the per-flush `ToArray()` in `OutboundConnection.Flush` allocates a full-size
array per response, unpooled, on the ThreadPool thread that also runs Kestrel), but item 0 above is the
larger prize and it is in the transport itself.

### 3b. Original bridge write-up (superseded by the measurement above)

kestrel+tls hit 8,383 MiB/s against plaintext io_uring's 4,379 - a TLS leg beating a plaintext leg 2:1,
which is only possible because they are limited by different things. The SocketSet legs go through the
demo's Kestrel bridge (two `Pipe`s plus a copy per write in `SocketSetConnection`), and at large payloads
that bridge, not the transport or the cipher, is what is being measured.

If plaintext kestrel also lands near 8 GB/s, the bridge is costing 2-4x on big responses - which matters
more to the "ASP.NET Core over SocketSet" story than any transport difference measured so far.

### 4. kTLS: implement the RECVMSG + cmsg receive arm

Only worth doing if kTLS matters. The current kTLS path drives receive as io_uring `POLL` + `SSL_read`,
one syscall per message, forfeiting multishot receive and provided buffers - the design seam described in
`TlsFilter`'s notes (`RECVMSG` + `TLS_GET_RECORD_TYPE` cmsg) is what would let it keep them. kTLS does
already pull ahead of userspace io_uring TLS at 256KB (+65%), so the crossover is real; this is what
would make it competitive rather than merely present.

Note loopback has no NIC, so kTLS's largest win - inline NIC offload - cannot appear in any of the
numbers above. Real hardware required before drawing conclusions about kTLS itself.

### 5. Real-hardware Linux run

The rigs are in-repo and runnable: `bench/run-matrix.sh` (transport matrix) and `bench/run-tls-sizes.sh`
(payload sweep). Everything above was measured in a container on a WSL2 kernel over loopback, which is
adequate for correctness and weak for performance.

## Smaller / previously flagged

- **`close_notify` is never emitted on teardown**, on any backend. `TlsFilter.Shutdown` is implemented by
  both providers but no shard calls it; doing so means draining a final send before `closesocket`, which
  touches the teardown state machine. Deliberately deferred (2026-07-25) rather than changing teardown
  semantics on three backends inside a wiring change.
- **Negotiated cipher suite is not reported.** The AspNetDemo A/B pins the certificate so it is not a
  variable, but there is no way to confirm two legs landed on the same suite. ~40 lines:
  `SECPKG_ATTR_CONNECTION_INFO` for SChannel, `SSL_get_cipher`/`SSL_get_version` for OpenSSL, surfaced on
  `TlsFilter`. Worth having before publishing any numbers.
- **SChannel provider is `#if NET` only.** net472 would need substitutes for `X509ChainTrustMode`,
  `X509Certificate2.MatchesHostname` and `X509CertificateLoader`. The SSPI interop itself is fine there.
- **`Connection.Close()` is abortive** and truncates a queued send. Harmless on the client-close and
  abort paths, but a server-side graceful close with a pending write (or a Redis client closing with an
  unsent command) wants a flush-then-close primitive. Pre-existing; see `AspNetDemo/README.md`.
- **RESOLVED (2026-07-26): io_uring in Docker.** Two things were tangled together here, and the earlier
  entry got the conclusion wrong.
  1. Docker's default seccomp profile blocks the io_uring syscalls, so the backend silently falls back to
     managed sockets. Fix: `--security-opt seccomp=unconfined` (`--privileged` also works but changes far
     more than seccomp, which is what made this look kernel-related). Always check which backend was
     actually selected rather than assuming.
  2. With io_uring genuinely selected, every recv failed. That was *ours*, not the kernel's: multishot
     recv was submitted with a non-zero `len`, which the kernel rejects with `-EINVAL`. Fixed. io_uring
     now passes the full smoke matrix in a container, TLS included.

  Worth carrying forward: **io_uring reports operation failures as a negative `cqe.res`, not as a syscall
  error**, so a mis-built SQE presents as a healthy submit followed by silence - `strace` shows nothing
  wrong. `SS_URING_TRACE=1` now dumps every completion, which is what found it. Bisecting against a
  60-line raw-io_uring C probe (does NOP complete? does single-shot recv? multishot? with a buffer ring?)
  is what separated "kernel cannot" from "we asked wrongly".

  The claim in commits `925db23` / `1b48f8a` that io_uring was verified in a container was still wrong at
  the time - those runs were the managed backend, because seccomp had hidden io_uring from the probe.
