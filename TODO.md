# TODO

Engineering backlog — design calls and deferred work. Not user-facing (see `README.md` for that).

---

## START HERE (state as of 2026-07-27)

Orientation for picking this up cold.

**The benchmark host changed on 2026-07-27** - from a laptop (16C/32T) to a desktop (Ryzen 9 7900X,
12C/24T, mains). Every Windows number recorded before that date is from the old machine and **cannot be
compared with anything measured since**. The current baseline is in `AspNetDemo/RESULTS.md` under the
2026-07-27 headings. Two practical consequences: shard sweeps run at **4/8/12** here (12 = the server
half's logical core count), not 4/8/16; and this host is roughly an order of magnitude more repeatable
than the old one, with per-leg spreads of 0.2-2.4% rather than up to 6%.

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

**The biggest win now on the table: give RIO a bigger write page.** RIO trails IOCP 2.2-2.5x at >=16KB
because its send is quantised to one write page, and unlike IOCP it cannot scatter-gather (Windows caps
`maxSendDataBuffers` at 1 - attempted and refuted 2026-07-27). But page size alone recovers all of it and
more: at a 64KB page RIO goes **2,404 -> 10,969 MiB/s at 256KB (4.68x)** and **1,643 -> 4,449 at 16KB
(2.7x)**, with no penalty at 512B, and ends up faster than IOCP at *every* payload. Not yet a default
change - the blocker is pool sizing and the fact that pool exhaustion currently drops connections. See
items 0 and 0b.

**`fa97dd4` is now MEASURED and is the second-largest win in the project: +27.0% at 256KB**, +5.9% at
16KB, nothing at 512B (disjoint ranges; `Compare-Commits.ps1`, 2026-07-27). Pooling the out-of-band flush
snapshot matters because the old `ToArray()` allocated the whole response per flush, and past 85KB that
is a **Large Object Heap allocation on every response**.

Its pre-registered reading was "if it moves throughput, allocation was the cost; if it does not, copies
dominate". It moved throughput, so **allocation was the cost and copies are not the constraint** - which
is the finding that de-prioritises BYO-buffer. See item 2b.

**Before trusting any measurement, read `bench/README.md`.** It documents the eight confounders that each
produced clean-looking wrong numbers, and the noise floor (~6% between identical builds on this host).

**Direction of travel:** BYO-buffer, in measured steps — see 2b. The end goal is that a caller supplies
the memory (ideally pinned/registered) and we stop copying into it. Accepting single-shot reads, or
bypassing provided buffers, is an acceptable price for minimal copy. A robust fallback is required for
backends that cannot take foreign memory at all — RIO takes only registered `BufferId`s, never addresses.

---

---

## Dynamic shard growth (MinShards -> MaxShards)

**Status: proposed, not started. Intended behaviour from the outset; architecture surveyed 2026-07-27.**

Today `Options.Shards` is a fixed count materialised in the `SocketSet` constructor and never changed.
The intent is for it to become **MinShards**: start at a reasonable count, and when a connection cannot be
placed because every shard is full, spin up another (double-checked, synchronised) up to **MaxShards** and
place it there. Shards then ramp with load instead of being sized for the worst case up front.

**Why it matters beyond elasticity.** Per-shard memory is `SocketsPerShard x buffer sizes`, pre-allocated
and pinned. That product is what makes buffer sizing painful: at `SocketsPerShard` 4096 a 64KB receive
buffer costs 256MB *per shard* (measured 2026-07-27 - a 12-shard RIO server went 283MB -> 3,163MB
resident when the receive buffer followed a 64KB page). Growth lets `SocketsPerShard` be small, so each
shard's slabs are small and you only pay for shards that exist. It converts a worst-case allocation into
a demand-driven one, and it removes a hard failure: `TryPlace` currently returns null and the caller
drops or throws.

**The hook already exists** - `SocketSet.TryPlace` ends with
`return null; // every shard full (we may grow here later; for now the caller drops/throws)`. That is the
insertion point, and placement is already capacity-aware (it walks for a shard with room rather than
committing to one round-robin pick).

**Accept is NOT the obstacle it looks like, on Windows.** The concern is that accept only listens on
shards that existed at `Listen` time. That is true for exactly one of the two paths:

- *Multi-bind / reuse-port* (io_uring, IP): `Listen` binds a listener on EVERY shard and the kernel
  balances. A new shard needs the listen endpoints replayed onto it, so the set must remember what it was
  asked to listen on. This is the real work.
- *Single-listener* (Windows IOCP/RIO, `ListenHandle`, AF_UNIX): one shard owns the listener and bounces
  each accepted socket via `Parent.TryPlace()`, which re-reads `_shards` on every call
  (`IocpShard.cs:717`). **A new shard receives connections immediately with no listener changes.** So on
  the platform where the memory pressure actually bites, this part is free.

**What the work actually is:**

1. `_shards` becomes a swappable array (copy-on-write + `Volatile.Write`) so the hot readers - `TryPlace`,
   `RoundRobin` - stay lock-free and simply observe a longer array. Growth takes a lock; reads never do.
2. Starting a shard on a live set. The constructor's `CountdownEvent` startup gate assumes a fixed count
   and runs before anything is serving; growth needs an equivalent that does not stall the caller placing
   a connection. Note `UsesWorkerThreads` backends start a thread per shard and the managed backend
   initialises inline - two different paths.
3. Listen replay for the multi-bind path (remember endpoints + tokens; apply on new shards).
4. Teardown: `Dispose` must cover shards added after construction, including one being created
   concurrently with disposal.
5. Failure policy: if shard creation fails or `MaxShards` is reached, `TryPlace` still has to return null
   and the existing drop/throw path stands.
6. `MaxShards` currently exists on the FACTORY as a backend capability cap (the managed backend wants
   exactly 1) - a growth cap is a different thing and needs its own option; do not overload that name.

**Shrink is explicitly out of scope** unless asked for: idle shards holding pinned slabs is the mirror
problem, and reclaiming a shard means draining its connections first.

**Sequencing caveat, and it is the same one as everything else here:** this is a cross-cutting change
touching all four backends' startup/teardown. TLS was written twice, epoll made the send machinery a
third copy, `fa97dd4` paid an ownership rule out three times. Landing dynamic shards before the
IOCP/RIO factoring below means writing it N times too.

## Factor the shared IOCP/RIO data path

**Status: PARTLY DONE (2026-07-27), and the remaining scope is much smaller than this entry assumed.**

Done:

- `WindowsConnection` + `IWindowsShard` - identity, teardown state, send serialization and the
  Close/IsClosed/SubmitOutbound trio, written once. (This entry claimed `WindowsOutboundConnection`
  already existed as a shared base; it did not.)
- `WindowsShardBase<TConn>` - the whole TLS block, outbound staging, the out-of-band flush pump, slot
  release, and the options/slot-table/pool/TLS-scratch fields both shards declared identically.

Net so far: the two shards went from 1331 + 1134 lines to 1154 + 957, against 269 lines of shared base -
roughly 350 lines of duplication removed, plus ~150 from the connection types.

**The generic-struct ops parameter is not needed.** The base asks for only `CloseClient` and
`StartPendingSend`, both per-EVENT; the per-operation primitives (issue-a-send, arm-a-receive,
drain-completions) never route through the base, so there is no virtual call on the hot path to
devirtualise. `TConn` is generic purely so the slot table stays typed.

**What must NOT be unified: the send state machine.** `CompleteWrite` and `StartPendingSend` differ by 39
and 25 lines because IOCP has a send page array and RIO cannot have one - Windows caps
`maxSendDataBuffers` at 1. This entry was written when the two were near-identical; they diverged when
IOCP got scatter-gather. Forcing them back together would mean giving RIO a shape the OS refuses.

**What is left**, all in the control plane, and all *near*-identical rather than identical - so each wants
reading before moving, not a mechanical lift:

| method | lines differing |
|---|---|
| `BeginTls`, `FailSend`, `SendResponse` | 2-3 |
| `SubmitSendBuffer`, `HandleSend` | 5-8 |
| `CloseClient`, `DrainCrossThread`, `TryFinalize` | 9-10 |
| `Poke` | 12 |
| listen/accept/connect (`Listen`, `ListenHandle`, `StartAccept`, `PostAccept`, `HandleAccept`, `StartConnect`, `HandleConnect`, `AdoptAccepted`) | not yet diffed |

The differences are mostly RIO's extra teardown bookkeeping (`Rq`, `Commit*`) and IOCP's inline-completion
path (`SkipOnSuccess`, `QueueInline`). Worth doing, but the easy 500 lines are already gone and what
remains needs judgement per method.

---

*Original analysis follows.*

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

**Why it matters:** every feature is currently written twice. TLS interception was (2026-07-25), and the
epoll backend (below) landed 2026-07-26 as a third copy of the send machinery — the thing this entry
predicted, now realised rather than hypothetical. `fa97dd4` then paid it again: one ownership rule,
written out three times in `IocpShard`, `WindowsRioShard` and `EpollShard`. That is the argument — not
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

**Status: DONE (2026-07-26)**, in `b1f4286`. `src/SocketSet/Epoll`, with TLS. Passes the smoke matrix —
echo, byte-exact verify, out-of-band send, poke, churn, AF_UNIX, ALPN. Measured at parity with io_uring
and stock Kestrel on small-message plaintext (102.5k vs 105.2k vs 102.8k rps, ~3% spread). What remains
is the real-hardware measurement described below.

**It landed BEFORE the IOCP/RIO factoring, against the sequencing plan in this entry.** The plan was to
factor first so epoll would land on a shared base; instead it is a fourth independent copy of the send
machinery, which is now an argument *for* that factoring rather than a cost avoided by it. `fa97dd4` is
the evidence: a four-line ownership change had to be written out separately in `IocpShard`,
`WindowsRioShard` and `EpollShard`.

The rest of this entry is the original rationale, kept because the *why* still holds and the cost
estimate is worth checking against what it actually took.

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
the caveats in `AspNetDemo/RESULTS.md`. Partly done: the small-message parity numbers are in the status
above; the `--latency`/`--bandwidth` comparison on real hardware is still outstanding (item 5).

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

### 1c. Configurable read depth (multiple outstanding receives) - IOCP and RIO

**Status: proposed, not started. Feasibility confirmed 2026-07-27.**

Both Windows backends currently keep **exactly one receive outstanding per connection**: `RecvBuf` is a
single slab index leased for the connection lifetime and `RecvArmed` is a bool, so the cycle is strictly
complete -> deliver -> re-arm. RIO additionally hard-caps it, `RIOCreateRequestQueue(sock, 1, 1, ...)`.

io_uring does not have this limitation and never did - `IoUringShard.ArmRecv` uses
`IORING_RECV_MULTISHOT` against a provided-buffer ring, which is unbounded depth with no re-arm step. So
this is a Windows-only gap, and the backend that most wants fixing is the one whose entire design premise
is deep queues.

**Why it should help.** Between a completion and its re-arm there is a window with no buffer posted.
Data arriving then lands in the kernel socket buffer and is copied out on the next receive rather than
going straight into ours. At high message rates that window is hit constantly. It should also help the
fragmented-input case directly: successive segments land in successive pre-posted placeholders instead of
coalescing in the kernel buffer.

**Feasible on RIO** - `maxOutstandingReceive` accepts 4 and 16 (unlike `maxRecvDataBuffers`, capped at 1;
see item 0 for how that was established).

**Design points:**

- A `ReadDepth` option, defaulting to 1 so the change is opt-in until measured.
- `RecvBuf` becomes a ring of N leases per connection; the recv slab grows N x `SocketsPerShard`. That is
  the dominant cost.
- **Ordering becomes load-bearing.** Completions for one socket arrive in submission order and one loop
  thread drains them, so order holds - but it stops being structural. TLS is unforgiving here: records are
  sequence-numbered, so a single out-of-order delivery kills the connection rather than degrading it.
- RIO pays non-paged pool for `maxOutstandingReceive` per connection, on top of anything item 0 adds.

**Do first: build a receive-heavy benchmark.** None of the current rigs would show this. `/plaintext` is
ceiling-bound on this host, and both payload sweeps are response-heavy - large sends, tiny requests. Read
depth is a receive-side optimisation, so measuring it needs a large-request workload that does not exist
in `bench/` yet. `SmokeTest --verify-echo` is the closest thing in-tree and it is a correctness test.
Building the code first would repeat the pattern this file keeps warning about: a plausible mechanism
with no measurement able to confirm or refute it.

### 2. Re-run the size sweep now the plaintext controls are in

The first sweep had one plaintext control and it was the wrong one, so nothing at >=16KB is fully
interpretable yet. Plaintext `kestrel` and `epoll` legs added 2026-07-26; just needs a run.

### 0. Sends are page-quantised - **IOCP: FIXED. RIO: NOT FIXED, and now quantified.**

**Status (2026-07-27).**

- **IOCP: done.** `IocpShard.IssueSendPages` builds a `WSABUF` array and issues one `WSASend` with up to
  64 segments. Landed in `bb8007f`/`2be2663` (+133% at 16KB, +162% at 256KB, A/B controlled).
- **RIO: not done.** `WindowsRioShard.IssueSend` still posts `RIOSend(conn.Rq, &buf, 1, ...)` - buffer
  count 1 - and `CompleteWrite` still coalesces only "as many queued responses as fit into the write
  page". This entry claimed both backends were covered; only one was, and nothing recorded the difference
  until the 2026-07-27 sweep went looking for it.

**What RIO's page quantisation costs**, from `bench/Run-TlsSizes.ps1 -Shards 12` (goodput MiB/s, median
of 3):

| payload | rio/s12 | iocp/s12 | RIO vs IOCP |
|---|---:|---:|---:|
| 512 B | 142.7 | 138.0 | **+3.4%** |
| 16 KB | 1,521.1 | 3,741.1 | **-59%** (2.5x) |
| 256 KB | 2,051.6 | 4,483.4 | **-54%** (2.2x) |

RIO is *ahead* below one write page and 2.2-2.5x behind above it - the signature of the defect, isolated
to the one backend that still has it. Spreads 0.3-3.3%, so this is not close to noise. `rio+tls` tracks
plaintext `rio` (and at 256KB is marginally faster), confirming the constraint is the send path and not
the cipher.

#### RIO CANNOT do scatter-gather. Attempted 2026-07-27, refuted by the OS.

This entry used to end "RIOSend takes a buffer array exactly as WSASend does; copy
`IocpShard.IssueSendPages`". **That is wrong**, and it was wrong when first written. `RIOSend` takes an
array in its *signature*, but the buffer count is fixed at request-queue creation by
`RIOCreateRequestQueue`'s `maxSendDataBuffers`, and Windows accepts only **1**.

Measured directly - the full port of `IssueSendPages` was written, and every connection failed to
establish. Probing the parameter in isolation:

| `maxSendDataBuffers` | result |
|---|---|
| 1 | RQ created |
| 2, 3, 4, 8, 16, 64 | **WSAEINVAL (10022)** |

`maxRecvDataBuffers` is capped at 1 the same way. This matches Microsoft's own note on
`RIOCreateRequestQueue` that the Registered I/O extensions currently support a value of 1 for both. So
the IOCP fix has no RIO analogue: one `RIOSend` is one contiguous buffer, permanently.

#### What IS available: depth, not width

The *outstanding operation* counts are not capped that way. Probed on the same host, same call:

| parameter | 1 | 4 | 16 | 64 |
|---|---|---|---|---|
| `maxOutstandingSend` | ok | ok | ok | ok |
| `maxOutstandingReceive` | ok | ok | ok | - |

So the RIO-idiomatic fix for the same problem is **K single-buffer sends in flight** rather than one
send of K buffers: post each write page as its own `RIOSend` without waiting for the previous completion,
and let the RQ ring hold them. Completions for one RQ arrive in submission order, and the shard has one
loop thread draining the CQ, so stream ordering is preserved - but that ordering guarantee becomes
load-bearing rather than structural, which is the main risk to design around. This is a different change
from the IOCP one, not a port of it:

- `SendBusy` becomes a count (or a small ring), not a bool; `TryFinalize` must wait for all of them.
- Partial sends complete per-page, so the `SendSent`/`SendTotal` cursor becomes per-page bookkeeping.
- Teardown must reclaim every page still outstanding, not just page 0.
- `RIO_MSG_DEFER` already batches the kernel kick, so K submissions still cost one commit.

#### MEASURED 2026-07-27: page size fixes it, and the deep-queue spike is not needed

Page size is RIO's only lever, so it was swept against payload on the bare responder. Goodput MiB/s:

| payload | rio p4k | rio p16k | rio p64k | iocp p4k | iocp p64k |
|---|---:|---:|---:|---:|---:|
| 512 B | 154.1 | 153.9 | **154.5** | 152.4 | 149.0 |
| 16 KB | 1,642.9 | 2,967.6 | **4,448.9** | 4,357.0 | 4,255.6 |
| 256 KB | 2,404.1 | 6,948.8 | **10,968.8** | 5,495.5 | 5,873.4 |

**A 64KB page wins for RIO at every payload, monotonically, with no penalty at 512B** - 4.68x at 256KB
and 2.7x at 16KB. IOCP is page-insensitive (+-2%), which is exactly what scatter-gather buys it. And with
a large page RIO beats IOCP everywhere, inverting the standing: RIO was starved, not slow.

So the deep-send-queue rewrite described above is **not needed** to close this gap. Do not build it.

**But this is not yet a default change, and the blocker is not throughput.** Page size trades against
pool depth. Shipped defaults are `SocketsPerShard` 4096 against `WriteBuffersPerShard` 1024 (4:1
oversubscribed already); holding pinned memory constant at a 64KB page means ~64 buffers per shard, i.e.
**64:1**. The sweep ran `-c 64` over 12 shards - about 5 connections per shard - so it never went near
pool pressure.

That is dangerous rather than merely slow, because **write-pool exhaustion closes the connection**
(`CloseClient` in `SendResponse` and `StartPendingSend`) instead of queueing. See item 0b.

**RESOLVED 2026-07-27, except the default itself.**

1. *The memory blocker is gone.* `_writeBufSize` and `_recvBufSize` were the same option, and receive
   buffers are one per SOCKET - so a 64KB page cost 3,163MB instead of 283MB, 97% of it receive slab that
   gains nothing from being large. `SocketSetOptions.ReceiveBufferSize` splits them (0 = follow
   `BufferPageSize`). A 64KB send page with a 4KB receive buffer gives the full 4.66x at **283MB, the same
   as today**.
2. *Pool pressure: the prediction was wrong, and instructively so.* A shallow pool at a big page was
   expected to drop connections. Measured at 12 shards / 256KB / `-c 2048`, the config that drops
   connections is **today's default** (208 errors); every large-page config is clean at 0-1. RIO holds one
   write page per in-flight send, and at 4KB a 256KB response holds it across 64 sequential round trips
   versus 4 at 64KB - occupancy time collapses, so a bigger page RELIEVES pool pressure. Counting buffers
   without counting holding time is what got it backwards.
3. *Plumbed end to end.* `SmokeTest` and `AspNetDemo` both accept `--page` / `--recv-buffer` /
   `--write-buffers`; `/config` reports them so a harness can verify the setting took, and combining them
   with `--kestrel` is rejected.

**What is deliberately NOT done: changing the default.** `64KB page + 4KB recv + 256 write buffers` is
faster at every concurrency tested and has strictly better error behaviour than what ships. It is still
not the default because: these are Windows measurements at one payload shape on loopback;
`BufferPageSize` is shared with io_uring and epoll, where it has not been swept; and the 208 errors on the
current default are a pre-existing defect that deserves fixing on its own terms (item 0b) rather than
being masked by a page-size change.

**Remaining:**

- Sweep past 64KB - RIO was still improving monotonically at the top of the range, so the peak is unknown.
- Sweep page size on io_uring/epoll before touching a shared default.
- Decide the mechanism for a per-backend default. The backends want opposite things (RIO large, IOCP
  small/indifferent) and `BufferPageSize` is one global constant with a real default of 4096, so there is
  no way to distinguish "user asked for 4096" from "user said nothing". Needs a sentinel (0 = backend
  chooses) or a factory-supplied default; that changes public option semantics and wants its own commit.

### 0b. Write-pool exhaustion closes the connection instead of applying backpressure

**Status: proposed, not started. Surfaced by item 0 on 2026-07-27, and MEASURED: the shipped defaults drop
208 connections at `-c 2048` with 256KB responses on 12 shards.** Not a hypothetical - it is the current
default configuration, and it is the only configuration tested that failed. Large-page configs came in at
0-1 errors, which means a page-size change would MASK this rather than fix it. Fix it on its own terms.

`WindowsRioShard.SendResponse` and `StartPendingSend` both do
`if (!_writeBuffer.TryLease(...)) { CloseClient(slot); return; }`, and `IocpShard` has the same shape. So
running out of write buffers is a **dropped connection**, not a slow one.

That is tolerable while pools are generously sized relative to sockets, and it becomes a live hazard the
moment page size goes up and buffer counts come down (item 0). It also makes pool sizing a
correctness-adjacent decision rather than a tuning knob, which is the wrong shape for something an
operator is expected to set.

Wanted: queue the send (the `Pending` machinery already exists for exactly this) or refuse the write and
let the caller retry, rather than tearing down a healthy connection because a buffer was briefly
unavailable. Note the drop is currently invisible in Release - `Debug.WriteLine` is compiled out.

---

*Original write-up follows, for the reasoning.* `BufferPageSize` defaults to 4096 and the Windows send path
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

### 2a. BYO-buffer, phase 1: `ctx.UsePipe(IDuplexPipe, pinned)` - LANDED 2026-07-27 (fallback path)

Opt-in per connection from OnAccept/OnConnect. The pipe handed in is the TRANSPORT-side endpoint
(Kestrel's `Application` half): the transport writes received bytes to `pipe.Output` and reads outbound
bytes from `pipe.Input`. A connection that never calls it is completely unaffected, so this cannot regress
the existing path - and the whole feature is `#if NET` (netfx does not reference System.IO.Pipelines).

**This is deliberately the BAD path.** `PipeIoBridge` is written entirely against the existing public
surface - `Connection.Send(in ReadOnlySequence<byte>)` outbound, the normal receive callback inbound - so
it works on EVERY backend today, including ones that can never do better (RIO takes registered buffer ids,
never foreign addresses; DPDK would be similar). It costs one copy per direction, which is precisely what
the per-backend fast paths are supposed to remove. Having it first means each backend's fast path has a
correctness reference and a number to beat rather than being designed blind.

Verified byte-exact (4MB echo) on IOCP, RIO, managed, and IOCP+SChannel TLS. Confirmed the path is
actually taken rather than silently ignored: with `--pipe`, receive completions HALVE (2,080,047 ->
990,511) because the server half no longer enters `OnReceive`, while round-trip bytes still balance.
Costs about 5% throughput versus the callback path at 512B ping/pong - the extra copy and the thread hops.

Known limitations, all for phase 2:

- **Inbound backpressure is advisory.** The receive callback runs on the loop thread and cannot block, so
  a flush that does not complete synchronously is observed asynchronously while writing continues into the
  PipeWriter's buffer. Honouring it means PARKING the receive, which needs backend cooperation.
- **`pinned` is recorded and unused.** Nothing in the fallback pins anything, because it copies.
- **Read depth and the instant-response RawBuffer path are incompatible by construction** - both hand out
  transport-owned memory whose lifetime does not match a pipe segment's.
- Applications must not mix pipe mode with direct `Connection.Send`/IBufferWriter on the same connection;
  the outbound pump owns ordering on that half.

### 2b. BYO-buffer, phase 2: per-backend zero-copy, IOCP first

**Status: designed, not started.** IOCP is the right first driver because the shape already fits: its send
path takes a `WSABUF` ARRAY, and a `ReadOnlySequence<byte>` is exactly an array of segments. Outbound
becomes "build WSABUFs over the sequence's segments and issue one WSASend", with no staging copy at all.

- **Outbound:** replace the `Send(in ReadOnlySequence)` copy with WSABUFs pointing straight at pipe
  memory. The send must not complete-and-`AdvanceTo` until the WSASend completes, so the pump has to hold
  the `ReadResult` across the operation - this is the real design change, and it is what `pinned` is for
  (skip per-buffer pinning when the pool is already pinned; otherwise pin for the operation's duration).
- **Inbound:** `pipe.Output.GetMemory(hint)` -> pin -> WSARecv straight into it -> `Advance` -> flush,
  instead of receiving into the shard's slab and copying. This is where parking the receive on a pending
  flush becomes both possible and necessary.
- **RIO cannot follow** on the outbound half: `RIO_BUF` addresses a REGISTERED buffer by id, never a raw
  address, so foreign pipe memory is unusable unless the caller's pool is registered up front. RIO keeps
  the fallback, which is exactly why the fallback exists.
- Measure against the fallback's own numbers, not against the callback path, so the comparison isolates
  what zero-copy buys rather than what pipe mode costs.

### 2c. BYO-buffer: original notes on caller-supplied (pinned) pipes and the copies below them

> **DE-PRIORITISED 2026-07-27. The three measurements below were run, and they refute the premise.**
>
> The hypothesis was that the three copies dominate the large-payload gap. Two independent results say
> otherwise:
>
> - **Removing one ALLOCATION and zero copies** (`fa97dd4`) moved 256KB by **+27%**.
> - **Changing page size** moved RIO by **4.68x** at 256KB - and page size changes the number of segments,
>   not the bytes copied. 256KB is 256KB either way.
> - At a 64KB page RIO reaches 11,180 MiB/s against IOCP's best 6,083 while running an identical copy
>   path. If copies dominated, they would converge, not sit 84% apart.
>
> **Allocation and per-operation cost dominate; per-byte copying does not.** Tier 1's allocation half is
> already delivered by `fa97dd4`. Tier 2 buys the copies at the price of threading a completion signal
> through every backend's send path - aimed at a cost that is not binding.
>
> Not dead, but not next. Revisit if a real-NIC run (where memory bandwidth is contended, unlike loopback)
> shows a different shape, or if allocation/GC pressure turns out to matter for its own sake rather than
> for throughput. Also note the bridge measured at 14-19%, not the ~47% assumed below.

**Original reasoning, kept for the record.** The 2026-07-27 sweep puts IOCP at
4,483 MiB/s against Kestrel's 11,489 at 256KB - a 61% gap, and the largest open number in
`AspNetDemo/RESULTS.md`. The working hypothesis is that the three copies below dominate it. That is
plausible (the ratio, 2.56x, is about what 2-3 extra copies per byte would cost) but **not established**,
and two things argue for checking before committing to a design change that ripples into every backend's
send-completion path:

1. *Part of that 61% is the bridge, not the transport.* This sweep runs through AspNetDemo. The last
   controlled bare-vs-bridged comparison put the bridge at 23% at 256KB, and that was **before** IOCP got
   faster - the bridge's share was already noted as having grown from ~23% to ~47% of the remaining gap.
   Re-measure `SmokeTest --http` against the bridged transport on this host, same pinning, same payload.
   BYO-buffer cannot address the bridge's share.
2. *A per-byte cost should not widen as a fraction of the total, and this one does.* IOCP goes
   3,741 -> 4,483 MiB/s from 16KB to 256KB (+20%) while Kestrel goes 4,008 -> 11,489 (+187%). N fixed
   copies per byte would give a roughly constant relative penalty once fixed costs amortise. Something
   else also degrades with size - segment/page management, the `Pending` queue, or the bridge's per-write
   behaviour. Only the copies are in BYO-buffer's scope.

Cheapest discriminators, in order, all available today:

1. **Bare responder control** - separates bridge from transport. Nothing to build.
2. **The voided `fa97dd4` A/B, re-run at 256KB** - pre-registered: moves throughput => allocation was the
   cost; does not => copies dominate. Never yet run validly.
3. **Tier 1 below** - removes exactly one of the three copies, with no API change. If one copy of three
   buys roughly a third of the gap, the hypothesis is confirmed and Tier 2 is justified. If it buys
   nothing, BYO-buffer is aimed at the wrong target.

Only then Tier 2.

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

## Check UDS on Windows

**Status: RAISED as suspected-broken, then TESTED and it WORKS (2026-07-27).**

Evidence, IOCP backend, filesystem path under `%TEMP%`:

- `SmokeTest --iocp -s -c 4 -t 6 -u <path>` reports `transport=uds`, listens on the path, creates the
  socket file, opens 4 client connections and exits 0.
- `SmokeTest --iocp --verify-echo 1048576 -u <path>` round-trips 1,048,576/1,048,576 bytes with zero
  mismatches on either leg.

Checked deliberately, because "the test passed" is not evidence when the flag might have been ignored and
the run silently fallen back to TCP - the banner and the socket file are what confirm it did not.

**Still untested on Windows**, so the item stays open with a narrower scope:

- TLS over UDS (SChannel), and the out-of-band `--verify` path over UDS.
- Churn/teardown over UDS: stale socket files at bind, and whether the shared cleanup helper is on the
  Windows path at all.
- That `@abstract` names are rejected with a clear error rather than silently creating a file called
  `@name` - the abstract namespace is Linux-only.
- Path-length limits (`sockaddr_un` is ~108 bytes; a long `%TEMP%` could overflow it).

Scope, so the test is not run against the wrong thing:

- **IOCP only.** RIO is TCP/UDP and cannot do AF_UNIX; the code already routes AF_UNIX to the IOCP
  backend, so `--rio` plus a UDS endpoint is out of scope rather than a bug.
- **Filesystem paths only.** The abstract namespace (`@name`) is a Linux extension - Windows AF_UNIX is
  filesystem-path only, and the interop already says so (`sockaddr_un (Windows AF_UNIX - filesystem path
  only, no abstract namespace)`). A Windows UDS test must use a real path.
- **Single listener.** AF_UNIX cannot multi-bind on any platform, so it takes the
  one-shard-listens-and-bounces path rather than reuse-port.

**Reproduce first:** `SmokeTest --iocp -s -c 8 -u C:\Users\<you>\AppData\Local\Temp\ss-uds.sock -t 10`,
then the same with `--verify` and `--verify-echo`. Record what actually happens - bind failure, accept
failure, silent hang, or success - rather than assuming.

**Things known to differ on Windows** and worth checking against whatever fails: `AcceptEx` /
`GetAcceptExSockaddrs` behaviour on AF_UNIX, `ConnectEx` needing an explicit prior bind, stale socket
files needing removal before bind (the shared helper does this for the Linux backends - confirm the
Windows path calls it), and path-length limits. Datagram AF_UNIX and `SO_PEERCRED` do not exist on
Windows at all.

If it turns out never to have worked, the honest options are to fix it or to say so in the README and
have the factory reject AF_UNIX on Windows loudly - what is not acceptable is the current state, where
nobody knows.

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
