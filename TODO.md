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

**RIO's large-payload gap is real but has no cheap fix.** RIO *leads* IOCP at 512B (142.7 vs 138.0 MiB/s)
and trails it **2.5x at 16KB** (1,521 vs 3,741) and **2.2x at 256KB** (2,052 vs 4,483), because its send
is still quantised to one write page. The obvious fix - port IOCP's scatter-gather - was attempted on
2026-07-27 and **is impossible**: Windows caps `RIOCreateRequestQueue`'s `maxSendDataBuffers` at 1 and
returns WSAEINVAL for anything higher. The viable alternative is multiple *outstanding* single-buffer
sends, which is a different and larger change. See item 0 before picking this up.

**In flight and UNMEASURED:** `fa97dd4` makes the out-of-band flush snapshot rent from `ArrayPool`
instead of allocating. Its A/B was contaminated by a power loss mid-run and is void. Re-run:

```
.\bench\Compare-Commits.ps1 -Before HEAD~1 -After HEAD     # (while fa97dd4 is HEAD)
```

**Run it at a LARGE payload.** Its own commit message pre-registers the interpretation - "if it moves
throughput, allocation was the cost; if it does not, copies dominate" - but that question only has
meaning where the copies are large. At 512B neither allocation nor copies matter, and a sweep dominated by
small payloads would answer nothing. 256KB is the size that discriminates.

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

**Unknown until measured:** whether K deep single-buffer sends recover the 2.2-2.5x, or whether RIO's
per-send cost means the win is smaller than IOCP's scatter-gather win. Worth a spike before a full
implementation - and note the cheaper partial mitigation available today is simply a larger
`BufferPageSize` for RIO, since one send is one page and page size is then the only lever (the
2026-07-26 sweep put a 64KB page at 4.0x a 4KB page at 256KB, at the cost of waste at small payloads).

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

### 2b. BYO-buffer: caller-supplied (pinned) pipes, and killing the copies below them

**Measure three things before designing anything (added 2026-07-27).** The 2026-07-27 sweep puts IOCP at
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
