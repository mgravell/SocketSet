# TODO

Engineering backlog — design calls and deferred work. Not user-facing (see `README.md` for that).

---

## START HERE (state as of 2026-07-28)

Orientation for picking this up cold.

**The agreed order of work — REVISED 2026-07-28 (end of day), because most of the old list is now done:**

1. ~~BYO-buffer phase 2, IOCP zero-copy (item 2b)~~ **DONE, and it under-delivered: +3.5% at 16KB and
   nothing elsewhere.** See `2b-result`. That is the single most useful negative result on this list.
2. ~~Write-pool exhaustion drops connections (item 0b)~~ **DONE**, with a wrong justification (the "208
   dropped connections" were my harness missing an ephemeral-port gate). The change is right anyway.
3. ~~Page-size defaults (item 0), blocked on the Linux sweep~~ **UNBLOCKED.** Linux is swept, io_uring's
   page cliff is fixed, and `ReceiveBufferSize` now reaches every backend. What is left is **not evidence
   but MECHANISM**: `BufferPageSize` is one global with a real default of 4096, so there is no way to tell
   "user asked for 4096" from "user said nothing". Needs a sentinel (0 = backend chooses) or a
   factory-supplied default, and that changes public option semantics — its own commit.
4. ~~Item 1, the 64KB->256KB collapse~~ **ANSWERED: it is the bridge**, 2.0-2.4% at 64KB and 24.5-41.8% at
   256KB, with the instability the bridge's too. See item 1 for the isolation.
5. **Dynamic shard growth.** Specified, untouched. Now the largest *unstarted* item.

**Where the remaining performance is, on the evidence rather than on intuition.** Three independent
results now agree that the Kestrel bridge — not the transport, not copies, not allocation — is what costs
on the ASP.NET path: zero-copy send removed one copy for +3.5%; the bare-vs-bridged isolation puts the
bridge at 24-42% at 256KB while the bare transport does not decline at all; and the same ~42% appears on
Windows against tuned RIO. **The next lever is fewer pipes and thread hops, not fewer copies** — and the
strongest form of that is no bridge at all (Kestrel talking to the transport directly), which is out of
scope here. Anything aimed at this number should be justified against that.

### THE LINUX HOST IS NOW BARE METAL, AND THE FIRST JOB IS A BASELINE (state as of 2026-07-28)

**The environment changed underneath this plan.** Linux measurement moved off Docker-on-WSL2 onto bare
metal: Pop!_OS 24.04, kernel 7.0.11, the *same* Ryzen 9 7900X desktop as the Windows numbers. io_uring
selects natively with no seccomp workaround. Setup, tooling and the traps specific to this box are in
`bench/README.md` ("The Linux bench host") — read that before running anything; the short version is that
the CPU governor ships as `powersave`, the SMT split in the shell rigs was measuring server and client on
the same physical cores (fixed, `bench/cpu-split.sh`), and `perf` needs a non-obvious package.

**Consequence: there is no Linux baseline.** Every Linux figure on file predates *both* the host change
(2026-07-27) and the OS change (2026-07-28), and was taken in a container on a WSL2 kernel. The tables
below are kept for their reasoning, not their numbers. Nothing can be compared against them.

**The baseline is DONE (2026-07-28)** — see `AspNetDemo/RESULTS.md`, "Linux baseline on bare metal", which
is now THE Linux reference and the "before" for the catch-up work below. Four things from it that change
what is worth doing:

1. **The host is a usable instrument**: within-leg spreads 0.2-5.7% against 37.4% in the container. A 2%
   effect is detectable on Linux now, and was not before.
2. **SocketSet beats stock Kestrel on both plaintext (+5.6%) and TLS (+22%)**, separating on disjoint
   ranges. The container's "parity" verdict was a ceiling at one-eighth the rate.
3. **kTLS is 15-22% SLOWER than userspace TLS at small messages, with 3x the p99** - the expected shape
   (we pay the RX syscall-per-message cost and collect no TX benefit when crypto is rounding error), but
   now measured. Whether it crosses over at large payloads is the open question and the size sweep's job.
4. **A Linux shard default must not be copied from Windows.** `s8` beats `s12` on `iouring` plaintext and
   costs less latency everywhere; `s12` only wins on the TLS legs. Windows chose 12 as a core count, not
   as a measurement.

### What has NOT reached the Linux backends (verified by inspection 2026-07-28, not recalled)

| feature | IOCP | RIO | io_uring | epoll |
|---|---|---|---|---|
| kTLS | - | - | **yes** | **no references at all** |
| `ReceiveBufferSize` (send/recv split) | yes | yes | **yes** (2026-07-28) | **yes** (2026-07-28) |
| write-pool exhaustion: stage and retry | yes | yes | no | no |
| BYO-buffer zero-copy SEND | yes | n/a by design | no | no |
| BYO-buffer zero-copy RECEIVE | **no** | **no** | **no** | **no** |

Reading of that table:

- **epoll has no kTLS path whatsoever** — this is a missing feature, not a missing harness leg. Do not
  add an `epoll+ktls` leg expecting it to work. **Scoped as item 3c (2026-07-29): the OpenSSL/kernel half
  is already shared, only the ~150-line readiness→`SSL_read` pump is missing, and epoll is the backend
  where kTLS should look best** — it has no multishot receive to forfeit, which is what makes kTLS cost
  io_uring ~15%.
- **kTLS is TX-only on the SYSTEM OpenSSL (3.0.13), and that is OpenSSL's limit, not ours** — measured
  2026-07-29: a self-built 3.5.7 gives RX=True on TLS 1.3 and moves `TlsRxSw` off zero (0 -> 8). On 3.0.13
  `TlsTxSw` moves and `TlsRxSw` stays at zero. Every kTLS number on file measures a
  half-offloaded path. That is what item 4 is about, and it now has direct evidence. **And per item 4b
  (2026-07-29) this is the ROOT of the "kTLS costs us multishot receive" story, not a side-effect of it:**
  multishot is unusable because RX is not offloaded, so OpenSSL must still see ciphertext and owns the
  reads. Turn RX on and multishot `IORING_OP_RECV` over the provided-buffer ring should return plaintext
  directly.
- **`ReceiveBufferSize` and stage-and-retry are Windows-only**, and both hypotheses about them have now
  been CHECKED rather than left as guesses (2026-07-28, by inspection):
  - *`ReceiveBufferSize`*: **epoll needs it, io_uring does not** — epoll's receive slab is per-SOCKET,
    exactly like Windows'. See item 0 step 2; this is what blocks the page default on Linux.
  - *Stage-and-retry*: **confirmed not needed on io_uring.** `IoUringConnection.EnsureRoom` falls back to a
    pinned-heap page when the pool is dry (`IoUringConnection.cs:226`) rather than closing, so pool depth
    costs allocation there, never connections. The Windows hazard genuinely does not exist on this path.
- **Zero-copy RECEIVE does not exist on any backend, and the reason is the API shape, not the backlog.**
  `Connection` has a `TrySendZeroCopy` and no receive counterpart. Inbound always lands in the transport's
  own slab and `PipeIoBridge.OnReceived(ReadOnlySpan<byte>)` copies it into `pipe.Output.Write(data)` —
  on IOCP exactly as on epoll. Under backpressure it is worse than one copy: a `PipeWriter` permits only
  one outstanding flush, so a receive arriving during a pending flush is rented from `ArrayPool` and
  copied a SECOND time into `_staged`.

  Reversing it means asking the pipe for memory (`pipe.Output.GetMemory`) and pinning it *before* arming
  the receive, which inverts who owns the buffer at arm time and is why item 2b lists inbound zero-copy as
  needing receive-parking. Parking is also the only thing that would make inbound backpressure real rather
  than advisory — the same mechanism buys both, which is an argument for doing it as one piece of work.

  Note the asymmetry this creates between the two buffer models: on the **callback** path receive is
  already copy-free (the callback gets a span over transport-owned memory), so this cost is specific to
  the **pipe** path, i.e. specific to the ASP.NET-shaped caller. That is the reverse of the send side,
  where the pipe path is the one that got zero-copy first.

### Both buffer models are wanted, and they are not competing

Stated 2026-07-28, and worth recording because the file's history keeps re-deciding it: the BYO-buffer
work and the library-owned-buffer path are **both** the destination, for different callers.

- **Caller-supplied pipes** (`ctx.UsePipe`) fit ASP.NET Core exactly, because Kestrel's transport contract
  already is a pair of pipes. That is what phases 2a/2b are for.
- **Library-owned raw buffers** are what an echo server or a Redis-style client wants: it has no pipe, it
  wants the transport's memory handed to it, and the pipe model would only add a copy and a thread hop.

So the callback path is not a legacy path to be migrated off. Supporting both is the goal, and a change
that improves one at the expense of the other is a regression even if the benchmark it was aimed at moved.

### The page-size default (item 0) — still the blocked item, and here is the block

`BufferPageSize` is shared with io_uring and epoll and has been swept **only on Windows**. Everything else
about it is settled.

**1. Sweep page size on both Linux backends.** `bench/run-tls-sizes.sh` is the rig. Compare 4KB / 16KB /
64KB at 512B / 16KB / 256KB payloads, exactly as the Windows matrix in `AspNetDemo/RESULTS.md` does.

*Pre-registered expectation, so the result can falsify something:* **both should be roughly
page-INSENSITIVE**, unlike RIO. io_uring already dispatches one writev over an `OutChain` of segments —
the same scatter-gather shape that made IOCP page-insensitive — and epoll sends directly from the TLS
output buffer with no page copy at all. If either turns out page-SENSITIVE, that is a finding worth
chasing, not a tuning result. If both are insensitive, the shared default can move on the Windows
evidence alone, because Linux does not care.

**2. Check whether Linux needs the send/receive split. ANSWERED BY INSPECTION 2026-07-28: epoll needs it,
io_uring does not.** `SocketSetOptions.ReceiveBufferSize` is honoured only by IOCP and RIO. Both Linux
backends still take one size for everything (`EpollShard._bufSize`,
`IoUringShard._readPageSize`/`_writeBufSize` — all `options.BufferPageSize`).

- **io_uring is fine, as guessed.** Its read pool is per-SHARD — `ManagedBufferPool(entries:
  BufferPagesPerShard=256)` — so a 64KB page is ~16MB per shard.
- **epoll allocates the slab per-SOCKET, like Windows.** `EpollShard._recvBuffer` is
  `PinnedWriteBufferPool(_socketsPerShard, _bufSize)`, commented *"one per live connection"*, leased at
  accept and released at close: 4096 x 64KB = 256MB per shard at a 64KB page. **But the resident cost is
  workload-dependent, and that is the part worth knowing.**

**MEASURED 2026-07-28 (`bench/run-recv-slab.sh`), and my own prediction was falsified before it was
confirmed.** Predicted: RSS diverges as `connections x page`, ~123MB at 2048 connections. Measured at 2048
connections with small GETs: **2.1MB**. Wrong variable - resident memory is `connections x TOUCHED depth`,
and a ~100-byte GET touches ONE 4KB page of a 64KB buffer, so buffer size never enters. Re-run with 64KB
POST bodies at 512 connections, it lands exactly: **+30.8MB against 30.7MB predicted (0.2%)**, and
`--recv-buffer 4096` recovers all of it (78,952KB -> 48,288KB, against p4K's 48,176KB).

**The axis is NOT per-socket vs per-shard - both the old claim and my correction had that wrong.** The
Windows 3,163MB was measured at **`-c 64`**: 64 connections holding 3.0GB resident, so that slab is
resident whether touched or not, because RIO *registers* it (locked pages). epoll's is `calloc`'d and
faulted on touch. Same structure, different residency policy.

**Practical reading:** on epoll a big page is free for small-request workloads and costs
`connections x page` for large-request ones (uploads, proxies). Workload-dependent rather than
unconditional - weaker than "epoll has the Windows problem", stronger than "Linux does not have it".
`ReceiveBufferSize` makes it safe either way and both Linux backends now honour it. Full tables in
`AspNetDemo/RESULTS.md`.

**3. Pipe mode on Linux — CONFIRMED WORKING 2026-07-28, nothing to do.** `SmokeTest --verify-echo
1048576 --pipe -z 65536` round-trips byte-exact on **both** epoll and io_uring through the universal
fallback (`PipeIoBridge`), with `pipe=True` in the banner so the flag is known to have been honoured. The
64KB chunk size is the one that exposed the flush-concurrency fault on Windows, so this was the run worth
making. Zero-copy send remains IOCP-only; io_uring is the natural second driver, since its send is
already a writev over segments and pointing those segments at pinned pipe memory is a smaller change
there than it was on IOCP.

**4. While there, the standing Linux backlog:** item 1 (io_uring TLS large-payload, investigated but never
reproduced outside a container — and the container is now gone, so this is finally testable), item 2
(re-run the size sweep now the plaintext controls exist), item 4 (kTLS RECVMSG + cmsg receive arm, whose
premise is now confirmed by `TlsRxSw` staying at zero), item 5 (**mostly delivered by the host change** —
what remains is specifically a second machine with a real NIC, since loopback cannot show kTLS device
offload no matter how good the host is).

**Read `bench/README.md` first.** Nine confounders, and one of them was reproduced on 2026-07-28 by
someone who had already read it — see item 0b. Any harness opening thousands of connections per cell needs
the ephemeral-port gate from `Run-Matrix.ps1`.

**One correction worth carrying forward, because it was made twice.** "Copies are not the constraint"
(established by `fa97dd4`'s A/B and by page size moving RIO 4.68x without changing bytes copied) is TRUE,
and was wrongly used to de-prioritise BYO-buffer. BYO-buffer's target was never the per-byte copies in
isolation — it is the BRIDGE, which was 14-19% when measured against the untuned transport and is ~42%
against the tuned one. Both statements hold; they are about different things.

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

**Confirmed end to end (2026-07-27):** through the Kestrel bridge, `--page 65536 --recv-buffer 4096` takes
RIO from 2,023 to **6,348 MiB/s at 256KB (3.14x)** and 1,484 to **3,735 at 16KB (2.52x)**, closing the gap
to stock Kestrel from 82% to 43.6% and to within 5.8% at 16KB — and tuned RIO then beats tuned IOCP at both
sizes. No data-path code changed. It is still not the default: see items 0 and 0b for the pool-sizing and
connection-dropping caveats that have to be settled first.

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
dominate". It moved throughput, so **allocation was the cost and per-byte copies are not the constraint**.
Note what that does and does not imply — see the correction at the top of this section: it rules out
chasing copies for their own sake, but not the caller-supplied-pipe work, whose target is the bridge.

**Before trusting any measurement, read `bench/README.md`.** It documents the eight confounders that each
produced clean-looking wrong numbers, and the noise floor (~6% between identical builds on this host).

**Direction of travel:** BYO-buffer, in measured steps — see 2a/2b. The API (`ctx.UsePipe`) and the
universal copying fallback landed 2026-07-27; what remains is the per-backend zero-copy path, IOCP first.
The end goal is that a caller supplies the memory (ideally pinned/registered) and we stop copying into it.
Accepting single-shot reads, or bypassing provided buffers, is an acceptable price for minimal copy. A
robust fallback is required for backends that cannot take foreign memory at all — RIO takes only
registered `BufferId`s, never addresses, which is exactly why the fallback is a permanent path and not a
stepping stone.

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

### 0c. ~~io_uring does not always exit on SIGINT after sustained load~~ — CLOSED 2026-07-28: NOT A DEFECT, and not io_uring, and not load

**Diagnosed and closed the same day it was raised.** The process ignores SIGINT because it was *told* to:
a shell **without job control** — which means any non-interactive script, i.e. every rig in `bench/` —
starts background (`&`) children with SIGINT and SIGQUIT set to `SIG_IGN`. That is POSIX, so a Ctrl+C at
the terminal cannot kill a background job. .NET honours the inherited disposition and never raises
`CancelKeyPress`, so the process ignores SIGINT outright.

Read from the kernel rather than inferred. Hung process: `SigIgn: ...1006` (0x2 SIGINT + 0x4 SIGQUIT on
top of the usual 0x1000 SIGPIPE), `SigCgt: ...44f8` — SIGINT **not caught**. Same binary from an
interactive shell: `SigIgn: ...1000`, `SigCgt: ...44fe`, exits in **250ms**. Nine foreground trials across
shard counts and `taskset` all exited in 250ms; every scripted-background trial hung.

**So all three parts of the original framing were wrong:** not io_uring (the backend is irrelevant), not
"after sustained load" (idle hangs identically — the original idle-exits observation was an interactive
run), and not a teardown stall. `TryFinalize` was already refuted structurally; this closes it empirically
too. **There is no connection-state leak here** — the earlier note that one might still lurk was raised
against a symptom that has turned out to have an unrelated cause, so it is not evidence of anything.

**`PosixSignalRegistration` does not rescue SIGINT** — tried and measured; it also declines a signal
inherited as `SIG_IGN`, which is the correct convention. **Use SIGTERM from a harness**; every rig already
does (`kill $pid`).

**What DID change (`SmokeTest/StopSignals.cs`).** SIGTERM's default disposition killed the process
outright, so anything printed at shutdown was unreachable from a rig. It is now handled, shuts down
cleanly, and a `[uring-stats:shutdown]` line was captured from a scripted run for the first time. **That
retires the workaround this entry forced** — "do not build a measurement that can only be read at
shutdown". The reporter's 2s timer is still worth keeping for a hard-killed process.

*Original entry follows.*

### ~~0c. io_uring does not always exit on SIGINT after sustained load~~ — OBSERVED 2026-07-28, not diagnosed

Noticed while building the send instrumentation, so it is a side-observation rather than a hunted bug, but
it reproduced every time. `SmokeTest --http --io-uring -n 8` shuts down promptly on SIGINT when idle, and
**fails to exit within 80s** on SIGINT after serving ~200k requests over 64 keep-alive connections. The
banner is the only thing in the log; no shutdown report is ever printed.

**That suspicion is REFUTED by inspection (2026-07-28), and the refutation is structural.** The entry used
to read: "teardown waits on `RecvArmed`/`SendBusy`/`CancelPending` clearing per connection
(`TryFinalize`), and something is not clearing after load". **Nothing waits on that.** Shard pump threads
are created at exactly one site (`SocketSet.cs:49-61`) with `IsBackground = true`, and `SocketSet.Dispose`
only calls `shard.Stop()` — it never joins them. A background thread cannot hold a .NET process open, so a
slot that never finalizes cannot be the cause of the hang no matter how stuck it is.

Note what that does and does not clear. It does NOT clear a connection-state leak: if `RecvArmed`/
`SendBusy`/`CancelPending` really do fail to clear under load, that still strands a slot during normal
operation, which is the part that would matter in production. It only says that defect, if real, is not
this symptom's mechanism. **They are two separate investigations and the file had merged them.**

"No shutdown report is ever printed" fits loop threads not exiting — but by the above that is a *fellow
symptom*, not the cause. What can actually hold the process: the main thread after `httpStop.Wait()`
returns (`SmokeTest/Program.cs:284`, then `HttpBench` — a `SocketSet` — disposes), a finalizer at exit, or
the SIGINT handler never running at all.

**Cheapest thing that would settle it in one run**, and it needs no debugger (none is installed on this
host — no `gdb`, no `eu-stack`, no `dotnet-dump`): reproduce, then read
`/proc/<pid>/task/*/comm` alongside `/proc/<pid>/task/*/wchan`. That names every thread and what each is
blocked in, which separates "stuck in `io_uring_enter`" from "stuck in `futex`" from "stuck in `close()`"
without guessing. `dotnet-trace` and `dotnet-counters` are installed if more is needed.

Practical consequence right now: **do not build a measurement that can only be read at shutdown.** The
`SS_URING_STATS=1` reporter therefore dumps on a 2s timer as well as at shutdown; the shutdown-only
version silently produced no data at all under exactly the load that mattered.

### 1. io_uring+TLS large-payload behaviour — REFRAMED 2026-07-28: the scope was wrong twice over

**Status: the entry below is superseded. It is not io_uring-specific, not TLS-specific, and not
container-specific — and the component that owns it is still not identified.**

Measured on bare metal 2026-07-28 through AspNetDemo (`bench/run-tls-sizes.sh`), 64KB -> 256KB:

| leg | 64 KB | 256 KB | change |
|---|---:|---:|---:|
| epoll (PLAINTEXT) | 10,523.3 | 6,689.0 | **-36%** |
| iouring (PLAINTEXT) | 10,483.5 | 7,979.9 | **-24%** |
| epoll+tls | 9,200.7 | 4,343.3 | **-53%** |
| iouring+tls | 8,493.2 | 4,040.5 | **-52%** |
| kestrel | 10,057.2 | 12,469.7 | **+24%** |
| kestrel+tls | 6,764.6 | 7,819.0 | **+16%** |

Ranges disjoint. Both Kestrel controls RISE in the same reshuffled passes, which rules out the client,
the box and the payload shape. So the decline is real and it affects **every SocketSet leg including
plaintext epoll** — the original entry's framing as an io_uring+TLS peculiarity was wrong, and its
"could not reproduce" was a property of the container, not of the defect.

**But the component is NOT identified, and the obvious inference does not hold.** The bare responder
shows no collapse at all (see `AspNetDemo/RESULTS.md`), which looks like a clean indictment of the Kestrel
bridge — except that comparison is cross-run, cross-shard-count, and confounded by `HttpBench` funnelling
all sends through two threads. Bridged io_uring at 16KB measures FASTER than bare io_uring at 16KB, and a
bridge cannot cost negative time. **Next step is a clean bare-vs-bridged isolation in one session at a
matched shard count**, not another sweep.

**RE-MEASURED 2026-07-28 AT SIX PASSES ON THE FIXED TRANSPORT: THE TABLE ABOVE STANDS, AND THE FIX IS
IRRELEVANT TO IT.** 98 cells, zero errors. Every leg reproduces its pre-fix value within ~2%: epoll
-36.8%, iouring -26.0%, epoll+tls -52.8%, iouring+tls -53.9%, against kestrel **+24.9%** and kestrel+tls
**+20.2%**. Full table in `AspNetDemo/RESULTS.md`.

*Why the fix could not move it, and this is a general point about which callers the defect could reach:*
the allocating branch (`EnsureRoom`'s `want > pageSize`) needs a caller that writes **one large contiguous
span**. Both bridges send a `ReadOnlySequence` of ~4KB PIPE segments and `Connection.Send(in
ReadOnlySequence)` loops `WriteAll` per segment, so `want` is never above a 4KB page. **The bridged path
never had the defect**; the bare responder (which `Send`s the whole response as one span) did. So the fix
is real for callback-style callers and worth nothing to the ASP.NET-shaped one.

*What six passes added that three could not:* at 256KB the SocketSet legs spread 9-17% across passes while
both Kestrel controls hold ~2%, and at 64KB everything is tight. **The bridged path is unstable at 256KB,
not merely slow** - a defect signature, and a new one.

*So the percentages below are now earned rather than provisional*, and the paragraph following this one
(which told you not to trust them) is superseded. Kept for the reasoning.

**AND THE COMPONENT IS NOW IDENTIFIED: IT IS THE BRIDGE. This entry is answered (2026-07-28).**
`bench/run-bare-vs-bridged.sh` ran the bare responder at the SAME 12 shards, same load, same pinning, same
client, **in the same session** as the bridged sweep - the isolation this entry asked for by name instead
of the cross-run comparison it refused:

| backend | 64 KB | 256 KB | change |
|---|---:|---:|---:|
| bare epoll | 10,744.8 | 11,437.3 | **+6.4%** |
| bare io_uring | 10,832.8 | 10,349.4 | -4.5% (ranges overlap: flat) |
| bridged epoll | 10,532.0 | 6,655.5 | -36.8% |
| bridged io_uring | 10,568.0 | 7,817.6 | -26.0% |

**The bare transport does not collapse; only the bridged path does.** As the bridge's own cost that is
**2.0-2.4% at 64KB and 24.5-41.8% at 256KB** - and the 41.8% independently reproduces the ~42% measured
for the bridge on WINDOWS against tuned RIO (11,030 bare vs 6,348 bridged). Two OSes, two transports, one
number for one component.

*The validity check that voided the last attempt passes:* bare beats bridged at every cell, so there is no
"bridge costs negative time" anomaly and the tables may be subtracted.

*And the instability is the bridge as well:* bare 256KB spreads are 2.7-3.0% against 9-17% bridged. The
bridge charges a variable 24-42%, which is a defect signature rather than an overhead.

**Consequence for the backlog:** this converges with `2b-result` from the opposite direction. Zero-copy
send removed one copy and bought +3.5%; the bare-vs-bridged split says the remaining cost is not copies at
all. **The next lever is fewer thread hops / fewer pipes, not fewer copies** - and the strongest form of
that is "no bridge at all", i.e. Kestrel talking to the transport directly, which `2b-result` already
flagged as out of scope here. Anything else aimed at this 24-42% should be justified against that.

**A MECHANISM WAS FOUND AND FIXED (2026-07-28), AND THIS ENTRY MUST BE RE-MEASURED BEFORE IT IS TRUSTED.**
io_uring took a pinned GC allocation of the WHOLE response, once per response, whenever the response did
not fit one buffer page - a hard goodput cliff triggered by exactly "responses got large". See item 0; it
is fixed, and io_uring gained +58-65% at 256KB. (An earlier version of this paragraph blamed the
single-in-flight-send gate and a second completion round trip. That was wrong: sends/response measured
1.000 at every page size.)

Two consequences for this entry, and the first is uncomfortable:

- **The bare-vs-bridged comparison it calls for should be re-run on the FIXED transport**, because every
  number in the table above was taken against a transport that allocated once per response at every
  payload in the sweep. The decline may be smaller, gone, or unchanged - none of those can be assumed.
- **The related claim "epoll beats io_uring by 58% at 256KB because io_uring copies every outbound byte
  into write pages" is SUPERSEDED** - that gap was this defect, and the two now measure level. This
  entry's structural framing borrowed that reasoning, so it no longer stands on its own.

What is untouched: both Kestrel controls RISE while every SocketSet leg falls, which no allocation story
explains by itself. So there is still something here - it just cannot be characterised from pre-fix data.

**~~And it needs six scored passes, not three.~~ DONE — see the six-pass re-measurement at the top of this
entry.** The concern was right: the true per-cell spread at 256KB is 9-17% on the SocketSet legs while
three consecutive passes can span 1.2%. Re-run at six, the figures land within ~2% of the three-pass ones
anyway, so the direction *and* the magnitudes hold — and the spread itself turned out to carry a finding
(SocketSet unstable at 256KB, Kestrel not).

*Original entry follows.*

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

That used to be dangerous rather than merely slow, because write-pool exhaustion CLOSED the connection.
Fixed 2026-07-28 (item 0b): it now stages and retries. So a shallow pool costs latency, not connections.

**RESOLVED 2026-07-27, except the default itself.**

1. *The memory blocker is gone.* `_writeBufSize` and `_recvBufSize` were the same option, and receive
   buffers are one per SOCKET - so a 64KB page cost 3,163MB instead of 283MB, 97% of it receive slab that
   gains nothing from being large. `SocketSetOptions.ReceiveBufferSize` splits them (0 = follow
   `BufferPageSize`). A 64KB send page with a 4KB receive buffer gives the full 4.66x at **283MB, the same
   as today**.
2. *Pool pressure: the prediction was wrong, and instructively so.* A shallow pool at a big page was
   expected to starve. It does not: RIO holds one write page per in-flight send, and at 4KB a 256KB
   response holds it across 64 sequential round trips versus 4 at 64KB, so occupancy time collapses and a
   bigger page RELIEVES pool pressure. Counting buffers without counting holding time is what got it
   backwards. **Note the error counts that originally accompanied this were a harness artifact (no
   ephemeral-port gate) and are withdrawn — see item 0b.** The goodput comparison stands; nothing about
   connection drops does.
3. *Plumbed end to end.* `SmokeTest` and `AspNetDemo` both accept `--page` / `--recv-buffer` /
   `--write-buffers`; `/config` reports them so a harness can verify the setting took, and combining them
   with `--kestrel` is rejected.

**What is deliberately NOT done: changing the default.** `64KB page + 4KB recv + 256 write buffers` is
faster at every concurrency tested and has strictly better error behaviour than what ships. It is still
not the default because these are Windows measurements at one payload shape on loopback, and
`BufferPageSize` is shared with io_uring and epoll where it has not been swept.

(An earlier version of this paragraph also cited "208 errors on the current default" as a blocking
defect. That was a harness artifact — see item 0b — and is not a reason for or against anything.)

**LINUX SWEPT 2026-07-28 — the memory objection is gone, and every backend now wants the same thing.**

`bench/run-page-sizes.sh` on the bare responder (full tables in `AspNetDemo/RESULTS.md`):

| backend | page sensitivity | RSS at 64KB page vs 4KB |
|---|---|---|
| RIO | wants 64KB badly (4.68x at 256KB) | was the blocker; solved by `ReceiveBufferSize` |
| IOCP | indifferent (+-2%) | n/a |
| epoll | **indifferent** at every payload | **flat** (72 -> 73 MB) |
| io_uring | **wants 64KB: 2.0x at a 16KB payload** | **37% CHEAPER** (122 -> 77 MB) |

Two things change here. First, the pre-registered "both Linux backends are page-insensitive" was **half
wrong** — io_uring is sensitive, so the shared default could not have been decided on Windows evidence
alone. Second, and more useful: **the memory argument against a larger default does not exist on Linux at
all.** It was the whole reason this was frightening, and it was a Windows-specific consequence of the
receive slab being per-SOCKET. On Linux a bigger page is free on epoll and actively cheaper on io_uring,
because collapsing buffer occupancy time reduces the fallback pinned allocations.

So every backend now benefits from or is indifferent to a 64KB page, and none of them pays for it in
memory once `ReceiveBufferSize` covers the Windows receive slab. **The case for raising the default is
much stronger than when this entry was written**, and what is left is mechanism, not evidence.

**Remaining:**

- Sweep past 64KB - RIO was still improving monotonically at the top of the range, so the peak is unknown.
- **"Does the response fit in ONE page" is CONFIRMED as the mechanism on io_uring (2026-07-28), and it
  turns item 0 from a tuning question into a defect.** Three pre-registered predictions, all held: only a
  page that FITS jumps (at a 256KB payload p256K buys nothing, p512K buys 1.48x); once it fits, more page
  buys nothing (p64K/p256K/p512K overlap at 16KB); and at a fixed p64K, walking the payload across the
  ~65.4KB boundary makes goodput fall off a cliff - 60,000 bytes at 11,341.6 MiB/s against 70,000 bytes at
  7,610.6, i.e. **17% more data for 33% less goodput**, ranges hugely disjoint. Full tables in
  `AspNetDemo/RESULTS.md`.

  **The cause is a PER-RESPONSE PINNED ALLOCATION, measured 2026-07-28 with `SS_URING_STATS=1`.** An
  earlier entry blamed the `SendBusy` single-in-flight-send gate and a second completion round trip; that
  was **wrong and is retracted**. Direct counts: every page size costs exactly **1.000 send SQE** carrying
  exactly **1.000 iovec segment**, with **zero** queued behind an in-flight send and **zero** partial
  resubmits. There is no extra round trip and the gate is never reached.

  What page size actually selects is where that one segment points: a response that FITS a pooled write
  page is sent from the pool; one that does not becomes **a pinned GC allocation of the whole response,
  one per response** (0.000 vs 1.000 pinned-alloc/resp either side of the boundary, coinciding exactly
  with the goodput cliff at a fixed page). At 256KB that is a pinned Large Object Heap allocation on every
  response - the same cost `fa97dd4` identified, arriving from another direction.

  **The fix is smaller and safer than the wrong diagnosis implied.** The machinery already exists and is
  simply unused here: `PumpFlush` sends up to `IovMax` (1024) segments in one `IORING_OP_WRITEV` and
  `HandleWriteV` already handles partial writes across a multi-segment chain. An oversized response should
  be assembled from **N pooled pages sent as one N-segment writev** instead of one big pinned allocation:
  same syscall count, same round trips, no allocation. **No change to ordering, `SendBusy`, `IO_LINK` or
  teardown is required** - so the risky option-B rewrite that the earlier diagnosis motivated is not
  needed for this.

  **So raising the default page only moves the boundary**, making the right default a function of expected
  payload - which a library cannot know, and which is worst for exactly the streaming workloads (downloads,
  video) where responses are unbounded and each would allocate its full size, pinned.

  **FIXED 2026-07-28.** `Connection.WriteAll` asked for `data.Length` as one contiguous span; it never
  needed contiguity (the loop always coped with a short span), but asking made `EnsureRoom` take its
  `want > pageSize` branch and allocate. io_uring now overrides `Connection.GetWriteSpan` to hand back its
  natural buffer, so an oversized write chains across pooled pages into one multi-segment `writev`.
  Per response at a 4KB page with a 16KB body: 1 pinned alloc -> **0**, 1 segment -> 5 pooled pages, send
  SQEs unchanged at 1.000. Goodput **+96% at 16KB** (4,363.7 -> 8,578.0) and **+58-65% at 256KB**
  (7,685.7 -> 12,706.8 at p4K). Byte-exact echo passes on both backends, callback and `--pipe`.

  **What this does to item 0: the io_uring half of the blocker is gone.** Page size no longer selects
  between two cost regimes on io_uring - every page now lands in the same band at 16KB and at 256KB - so
  the default no longer has to be chosen against expected payload for this backend's sake. RIO still wants
  a large page for its own (different, real) reason.

  **Deliberately opt-in per backend, and the follow-ups are the point:**
  - **RIO must NOT get this** without measurement: `maxSendDataBuffers` is capped at 1, so chaining would
    turn one send into N sequential sends - the same quantisation that already costs it 2.2-2.5x.
  - **IOCP probably should**: it scatter-gathers (up to 64 `WSABUF`s), which is the shape that benefits.
    Untested - this host is Linux.
  - **epoll needs nothing**: it does not share this defect at all. It goes through `OutboundConnection`,
    which accumulates into a pooled buffer writer and rents its flush snapshot from `ArrayPool` (the
    `fa97dd4` work), so a large response never becomes a pinned per-response allocation. That is why it
    was page-insensitive from the start. It keeps the contiguous default.
- ~~Confirm the page/pool-depth co-variation is inert on Linux.~~ **DONE 2026-07-28: it is inert, and the
  2.0x is the page.** With all pools pinned at 1024 the io_uring 16KB ratio is 1.97x against 1.98x
  uncontrolled. Two things had to be fixed to run it: the sweep rescales **three** pools, not one
  (`OutOfBandWriteBuffersPerShard` had no override at all - `--oob-write-buffers` added), and the old
  `FIXED_WRITE_BUFFERS` knob pinned only one of them, so it was not a control. `FIXED_POOL_DEPTH` pins
  all three and the rig now aborts a cell whose banner disagrees. Only io_uring ever needed this: epoll
  reads `BufferPageSize` and none of the pools, so `--page` moves one quantity for it.
- **The memory claim needs rewording, not retracting.** "A 64KB page is 37% cheaper" is mostly the pool
  rescale: at matched depth the page itself saves ~9MB of ~41MB. True of what ships; not a property of
  the page. Reworded in `AspNetDemo/RESULTS.md`.
- Decide the mechanism for a per-backend default. The backends want opposite things (RIO large, IOCP
  small/indifferent) and `BufferPageSize` is one global constant with a real default of 4096, so there is
  no way to distinguish "user asked for 4096" from "user said nothing". Needs a sentinel (0 = backend
  chooses) or a factory-supplied default; that changes public option semantics and wants its own commit.

### 0b. Write-pool exhaustion closes the connection instead of applying backpressure

**Status: DONE 2026-07-28. The justification I gave for it was wrong; the change is right anyway.**

*The wrong part.* This entry claimed the shipped defaults "drop 208 connections at `-c 2048`". That was
read off an error column without checking what the errors were. In isolation the same configuration
serves 73,852 requests with **zero** errors. The counts came from `Run-PoolPressure.ps1`, a harness
written the same day with **no ephemeral-port gate**, where `Run-Matrix.ps1` has three `Wait-Ports` calls
precisely because Windows has ~16k ephemeral ports with a multi-minute TIME_WAIT — and that run opens
about 74,000 connections. Client-side port pressure, i.e. confounder 2 of `AspNetDemo/RESULTS.md`,
reproduced by someone who had read the warning. **Any harness that opens thousands of connections per
cell needs the port gate; copy it from `Run-Matrix.ps1`.**

*The right part.* Closing a healthy connection because a write page was briefly unavailable is wrong on
its own terms, whatever the error counts said. Both Windows backends now stage the bytes into `Pending`
and queue the connection for retry (`WindowsShardBase.MarkAwaitingPage` / `DrainAwaitingPage`, drained
once per loop pass), instead of calling `CloseClient`. Retrying per pass rather than hooking every buffer
release is deliberate: pages are freed by whichever connection finishes a send, usually not one that is
waiting, so a per-release hook would fan out to every waiter anyway.

Verified directly rather than incidentally: a **4-buffer** write pool — which under the old code would
have torn connections down almost immediately — now round-trips 4MB byte-exact on both IOCP and RIO.

Still open: the drop path is invisible in Release (`Debug.WriteLine`), and there is no counter for how
often a connection had to wait. Worth adding before anyone tunes pool depth against it.

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

### 2a-bis. The AspNetDemo BYO bridge (`--byo`) - LANDED 2026-07-27, and it is at PARITY as predicted

`AspNetDemo --byo` selects a parallel bridge: the same two pipes, but handed to the transport via
`ctx.UsePipe` instead of `SocketSetConnection` copying inbound and running its own outbound pump.
Reported in `/config` as `byo=pipe` so a harness refuses a leg where the flag was ignored.

Measured on IOCP, 12 shards, `-c 64`, median of 3 scored passes (goodput MiB/s):

| payload | classic | byo | delta | classic passes | byo passes |
|---|---:|---:|---:|---|---|
| 512 B | 134.0 | 135.8 | +1.3% | 134,134,134 | 136,132,137 |
| 16 KB | 3,475.2 | 3,434.6 | -1.2% | 3475,3510,3421 | 3435,3372,3544 |
| 256 KB | 4,410.7 | 4,430.1 | +0.4% | 4410,4428,4411 | 4267,4430,4621 |

**Every per-pass range overlaps: this is parity, and parity was the prediction.** Phase 1 relocates the
bridge into the library rather than removing it - `PipeIoBridge` performs the same inbound copy and the
same `Connection.Send` that `SocketSetConnection` did. An earlier note in this file claimed switching the
demo to `UsePipe` would recover the measured 14-19% bridge cost "with no zero-copy work at all"; that was
wrong, and this measurement is what corrects it.

The value of the leg is that it is the vehicle phase 2 needs, and a like-for-like baseline: the zero-copy
work must be measured against THIS, not against the callback path, or it will be credited with what pipe
mode already costs.

### 2b-result. IOCP zero-copy send: LANDED 2026-07-28, worth +3.5% at 16KB and nothing elsewhere

Measured against the `--byo` bridge (the like-for-like baseline), IOCP, 12 shards, `-c 64`, median of 3:

| payload | classic bridge | byo + zero-copy | delta | classic passes | byo passes |
|---|---:|---:|---:|---|---|
| 512 B | 141.7 | 138.5 | -2.3% | 140,144,142 | 140,139,138 |
| 16 KB | 3,615.4 | **3,741.7** | **+3.5%** | 3671,3615,3587 | 3742,3748,3741 |
| 256 KB | 4,271.9 | 4,094.7 | -4.1% | 4314,4272,4139 | 4018,4329,4095 |

Only the 16KB row has disjoint ranges, so **+3.5% is the only defensible claim**; the other two are noise.

**Against the ~42% bridge cost this was aimed at, that is a poor return, and the reason is the finding.**
The bridge's cost is not mostly copies — it is the two Pipes, the scheduling hops between them, and
Kestrel's own pipeline. Removing one of the two copies bought 3.5%. This is the third independent arrival
at the same conclusion (the others being `fa97dd4`'s A/B and page size moving RIO 4.68x without changing
bytes copied): **per-byte copying is not what costs on this path.**

Two things worth trying before concluding the approach is exhausted, in rough order of expected value:

1. **The 64-segment cap probably binds at 256KB.** Kestrel's pool hands out 4KB blocks, so a 256KB
   response is ~64 segments — right at `MaxSendPages`. Anything over declines and silently falls back to
   copying, which would explain why the largest payload gained nothing. Fix by sending a PREFIX of the
   sequence instead of declining: the pump advances only by what was actually sent, which needs
   `TrySendZeroCopy` to report bytes rather than a bare bool. Instrument the decline rate first — do not
   assume it binds.
2. **Inbound zero-copy** (receive straight into `pipe.Output.GetMemory()`) removes the OTHER copy and, more
   interestingly, removes the staging introduced for backpressure. It also needs receive-parking, which is
   the only mechanism that makes backpressure real rather than advisory.

If neither moves it, the honest conclusion is that the bridge cost is structural — pipes and thread hops —
and the way to recover it is to not have a bridge, i.e. for Kestrel to talk to the transport directly,
which is out of scope here.

### 2e. Managed backend BYO send — ASSESSED 2026-07-29 and deliberately NOT built

The managed backend is the closest of any to zero-copy and the furthest from being worth the risk today.
Recording the decision so it is a choice rather than an omission.

**Why it looks attractive.** It is already one copy, not two: `ManagedConnection` accumulates into an
`ArrayPool` buffer and `Flush` hands *that array* to `SendAsync` via `SetBuffer` - no staging copy. Only
the `WriteAll` accumulation stands between it and BYO, `SocketAsyncEventArgs.BufferList` takes
`ArraySegment`s (the shape of a `ReadOnlySequence`), and **no pinning is needed** - the SAEA handles it.
That is the same mechanism vanilla Kestrel's own transport uses.

**Why it was not built.** The send path is a state machine over one `byte[]` plus `SendOffset` /
`CurrentLength`, and `PumpSend` re-issues `SetBuffer(data, offset, remaining)` on every partial send.
`BufferList` cannot coexist with `SetBuffer`, so supporting it means a *second*, parallel representation
with its own partial-send cursor across segments - inside a lock-based path, in the backend that is the
portable fallback (i.e. what runs wherever io_uring is unavailable, including Docker's default seccomp
profile), and which appears in **no** benchmark in this repo. New concurrency-adjacent code with no
measurement to catch a regression is the wrong trade while larger, measured wins are open.

**What would change the decision:** a managed leg in the rigs (so a regression would be visible at all),
or evidence that the managed path is on someone's hot path. The io_uring result (+45.1% at 256KB) says the
mechanism pays where it can be measured, so this is a sequencing call, not a doubt about the idea.

### 2d. The bridge's pipes are OURS, and they are almost entirely unconfigured (raised 2026-07-29)

**Status: proposed, not started. Cheap levers first, custom pipe second.** This is aimed at the term the
evidence actually blames — the bare-vs-bridged isolation puts the bridge at 24.5-41.8% at 256KB with the
transport not declining at all, and `2b-result` reached the same place from the other side (zero-copy send
removed a copy for +3.5%). If the cost is pipes and thread hops, the pipes are the thing to attack.

`AspNetDemo/SocketSetConnection.cs:58-61` constructs both pipes with:

```
readerScheduler: PipeScheduler.ThreadPool, writerScheduler: PipeScheduler.ThreadPool,
useSynchronizationContext: false, pauseWriterThreshold: 1MB, resumeWriterThreshold: 512KB
```

Everything else is default. Three knobs are sitting untouched, and none needs new abstractions:

1. **`PipeScheduler`.** ThreadPool on *both* ends of *both* pipes means a hop per direction per exchange.
   That is the "thread hops" term, named and unmeasured. **Do not naively set `Inline` on both** — an
   inline *reader* scheduler runs Kestrel's request pipeline on the transport's loop thread, which blocks
   the IO loop for every backend that has one (all but managed). Kestrel runs its own IO queues for exactly
   this reason. The safe experiment is one side at a time, with the loop-thread hazard in mind.
2. **`minimumSegmentSize`** (default 4096). This is why a 256KB response is ~64 segments — which is what
   `2b-result` suspects silently defeated IOCP's zero-copy send (it caps at 64 `WSABUF`s), and it is 64
   iterations of `WriteAll` per response on every other backend. A 64KB segment makes that 4.
3. **`MemoryPool`** (default, unpinned). `ctx.UsePipe(pipe, pinned: true)` exists and is *recorded and
   unused*; a pinned-block pool is what would make it mean something, and io_uring needs exactly "a stable
   address" (see 2b).

**Do these before writing a custom pipe** — they are configuration, they are individually A/B-able against
the `--byo` leg, and one of them (segment size) plausibly explains an existing null result rather than just
adding speed.

**Then the custom `IDuplexPipe` itself**, which is the part worth exploring rather than assuming.
`System.IO.Pipelines`' `Pipe` is general-purpose: it locks, it schedules, it manages its own segment
lifetime, and it is written to let arbitrary producers and consumers meet. Our half is neither arbitrary
nor general — one producer, one consumer, a known owner thread, and a transport that already owns pooled
(often pinned) memory. A purpose-built duplex pipe could:

- hand the app **the transport's own buffers** rather than copying into pipe segments — which is BYO in
  the direction we currently cannot do at all (there is no receive-side zero-copy on any backend), and
  which is the same mechanism that would make inbound backpressure real rather than advisory;
- drop the locking for an SPSC ring, since the producer/consumer identities are fixed;
- decide scheduling per direction instead of taking `PipeScheduler`'s general answer.

**The obvious risk, stated up front:** Kestrel's transport contract is `IDuplexPipe`, and anything we hand
it must behave exactly like a `Pipe` under every access pattern Kestrel uses (`ReadAsync`/`AdvanceTo`
combinations, examined-vs-consumed, cancellation, completion with exceptions). That is a correctness
surface much larger than it looks, and `SmokeTest --pipe` is the only harness that would catch a mistake.
Weigh that against the measured prize: the whole bridge is 24-42% at 256KB and ~2% at 64KB, so this is a
large-payload play, not a general one.

### 2b-result-2. io_uring zero-copy send: +45.1% at 256KB, and IOCP's null result is EXPLAINED (2026-07-29)

**Status: DONE for io_uring send. The measurement that motivated de-prioritising this work was wrong for
a specific, now-measured reason.**

A/B against the same `--byo` bridge, 12 shards, `-c 64`, 6 scored passes:

| payload | classic | byo + zero-copy | change |
|---|---:|---:|---:|
| 64 KB | 10,423.2 | 10,613.0 | +1.8% (ranges nearly overlap) |
| 256 KB | 7,950.2 [7795-8158] | **11,536.1** [11520-11682] | **+45.1%** |

Verified taken rather than declined: `zero-copy=11,680,439` segments, `pooled-page=0`,
`pinned-managed=0`.

**Why IOCP measured nothing at 256KB, now measured rather than suspected.** That same counter gives
**exactly 65.0 segments per response** - Kestrel's 4KB blocks make a 256KB body 64 segments, plus one for
headers. `IocpConnection.MaxSendPages` is **64**, so IOCP declined *every* 256KB response and fell back to
copying. `2b-result` guessed this ("probably binds at 256KB... instrument the decline rate first"); it is
now a number. io_uring's `IovMax` is 1024 and never hits it.

**Follow-up for a Windows host, pre-registered:** raise `MaxSendPages` above 65, or implement the
send-a-PREFIX fix `2b-result` sketched (`TrySendZeroCopy` reporting bytes instead of a bool), and IOCP
should show a large-payload gain of the same shape. If it does not, the cap was not the explanation.

**Consequences for the rest of this file:**

- **"Per-byte copying is not the constraint" is now bounded, not general.** It held for allocations
  (`fa97dd4`), for page size, and at 16KB. At 256KB, removing copies is worth +16.3% (epoll's `Flush`
  snapshot) and +45.1% (io_uring zero-copy). The constraint is payload-dependent and the old wording
  over-generalised from small-message evidence.
- **The bare responder is no longer a ceiling.** Bridged byo (11,536) beats the bare responder (10,352)
  because they no longer run the same code - `HttpBench` still copies via the callback path. Do not
  compute "bridge cost = bare - bridged" for a byo leg.
- **The gap to vanilla Kestrel at 256KB is 7.3%, from 36%** (12,450.5 vs 11,536.1). Kestrel is zero-copy
  in BOTH directions and pays no bridge; the obvious next term is the receive side, where we still copy
  and it does not - which is the zero-copy-receive/receive-parking work that has never been started.
- **The pin cost did not bite.** 65 `GCHandle` pins + disposes per response and it still won by 45%, so a
  pinned-block pool (item 2d) is upside on top, not a precondition.

### 2b. BYO-buffer, phase 2: per-backend zero-copy, IOCP first

**Status: designed, not started. Now the highest-value item on this list, with a measured target.**

Phase 1 measured at parity (above), so everything below is the part that pays — and the end-to-end run on
2026-07-27 says how much. Bare tuned RIO does 11,030 MiB/s at 256KB; through the Kestrel bridge it does
6,348. **The bridge costs ~42% on the best configuration**, up from the 14-19% measured the same day on the
untuned one. The bridge did not get worse; the transport got fast enough to expose it. That 42% is what
zero-copy is aiming at, and it is measured rather than assumed.

IOCP is the right first driver because the shape already fits: its send
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

### 3c. epoll + kTLS behind a toggle (raised 2026-07-29) — and epoll is where kTLS should look BEST

**Status: proposed, not started. The estimate "85% of the work is already done" was checked and holds.**

Already shared, i.e. nothing to write: `OpenSslTlsProvider.CreateKernelSsl` (the `SSL*` bound to the fd
with kernel offload), `KtlsProbe` (capability detection), the `NativeOpenSsl` bindings
(`SSL_do_handshake`/`SSL_read`/`SSL_write`/`SSL_shutdown`/`SSL_get_error`), and
`OpenSslTlsFilter.GetAlpnSelected`. None of that is io_uring-specific.

What is io_uring-specific, and is what epoll needs (~150 lines, `IoUringShard.cs:1066-1180`):
`StartKtls` → `KtlsPump` (drive the handshake) → `KtlsComplete` → `KtlsRead`/`KtlsRespond`, plus teardown
(`SSL_shutdown` while idle, `SSL_free` in finalize) and a per-connection plaintext receive buffer.

**And the mapping is EASIER on epoll, not harder.** io_uring drives kTLS receive as `POLL` + `SSL_read` -
it has to synthesise readiness, because its native model is completion. epoll *is* readiness, so
"`EPOLLIN` → `SSL_read`" is the backend's own idiom rather than an adaptation. TX needs nothing at all: it
is a plaintext `send()` that the kernel encrypts, and epoll already sends plaintext directly from its
buffer.

**The non-obvious reason to want it, beyond parity.** Every kTLS number on file is io_uring's, and
io_uring pays a structural penalty for kTLS that epoll does not: kTLS forfeits `IORING_RECV_MULTISHOT` and
provided buffers, dropping io_uring to one syscall per message (that is what item 4 is about). **epoll has
no multishot to give up** - it is one `recv` per readiness event either way - so kTLS should cost epoll
nothing structurally. If that holds, `epoll+ktls` is the configuration where kTLS finally competes on this
codebase, and it is measurable on loopback because it is a comparison against `epoll+tls` on the same
backend rather than a claim about NIC offload.

*Pre-registered:* `epoll+ktls` should land at or above `epoll+tls` at small messages, where
`iouring+ktls` trails `iouring+tls` by ~15% (597,486 vs 702,829 rps). If it trails on epoll too, the
"multishot forfeiture" explanation is wrong and the cost is in the kTLS record path itself.

**Factoring, since two backends will then want the same pump.** The Ktls\* methods are parameterised by
only two backend concerns - "tell me when the fd is readable" and "send these plaintext bytes" - so the
handshake drive and the read/respond loop can move to a shared helper with those as callbacks. Worth doing
as part of this rather than leaving a third copy of the send machinery, which is the mistake this file has
already recorded twice (TLS written twice, epoll's send path a third copy).

**Toggle:** ship it behind the existing `--ktls` selection path plus a capability check, and keep the
`bench/README.md` rule that `/config` must report what was actually negotiated - `tls_stat` verification
against the kernel counters is what caught "kTLS is TX-only" and must gate any epoll+ktls leg too.

### 4b. "kTLS costs us multishot RX" — the PREMISE IS WRONG, and the real blocker is that RX was never offloaded (2026-07-29)

Raised from an external, **unverified** claim: *io_uring can keep multishot RX with kTLS provided you use
TLS 1.3, because the kernel decrypts in place and plaintext lands in the provided-buffer ring; TLS 1.2
breaks it because the crypto layer's cmsg/header trick conflicts with `recvmsg_multishot`'s packed
layout.* Worth chasing, but the framing does not match what this codebase actually does, and the
correction matters more than the claim.

**What is verified here, not recalled:**

- We drive kTLS receive as io_uring `POLL` + `SSL_read` - one syscall per message, no multishot, no
  provided buffers (`IoUringShard.KtlsRead`).
- **That is not a sacrifice made FOR kTLS. It is forced because kTLS RX is not on.** `/proc/net/tls_stat`
  on this host reads `TlsTxSw = 3,612`, **`TlsRxSw = 0`** cumulative across every kTLS run ever made here.
  TX is offloaded; **receive has never been offloaded at all**, so OpenSSL still decrypts in userspace,
  still needs the ciphertext, and owns the fd (`SSL_set_fd`) - which is exactly why reads must go through
  `SSL_read` rather than through our own multishot recv.
- We never call `setsockopt(SOL_TLS)` ourselves. We set `SSL_OP_ENABLE_KTLS` and let OpenSSL decide, so
  whether RX engages is OpenSSL's choice, not ours.

**So the causal chain is the other way round from the claim.** Multishot is not lost *because* kTLS is on;
it is lost because kTLS RX is *off*, leaving userspace decryption in the path. **If RX offload were
active, plain `recv` returns plaintext, and `IORING_OP_RECV` + `IORING_RECV_MULTISHOT` over a provided
buffer ring should work unmodified** - that part of the external claim is mechanically sound, and it is
the prize.

**RESOLVED 2026-07-29: OpenSSL 3.0.13 declines kTLS RX for TLS 1.3; a self-built 3.5.7 grants it.**
Validated by building OpenSSL 3.5.7 with `enable-ktls` and running the same probe against it - same box,
same code, TLS 1.3 both times:

| OpenSSL | client | server | probe |
|---|---|---|---|
| 3.0.13 (system) | TX=True **RX=False** | TX=True **RX=False** | FAIL |
| **3.5.7 (self-built)** | TX=True **RX=True** | TX=True **RX=True** | **PASS** |

**Confirmed end to end in the real transport, not just the probe:** the io_uring kTLS echo against 3.5.7
moved `/proc/net/tls_stat`'s `TlsRxSw` off zero for the first time (**0 -> 8**) with the byte-exact echo
still passing. So **Path A works** and no protocol downgrade is needed: TLS 1.3 + kTLS RX + multishot is
available on OpenSSL 3.2+.

*(A first attempt using the flatpak runtime's 3.5.7 reported TX=False AND RX=False - kTLS not engaging at
all. That is not the same as declining RX, so it was discarded as inconclusive rather than quoted. The
probe now prints the loaded version and whether the build has kTLS, so this cannot be misread again.)*

**The backend now reports what it got**, once per process:
`[ktls] openssl=3.0.13 tx=True rx=False -- RX NOT offloaded: ... OpenSSL 3.2+ is required ...`. A silent
half-offload is what made every kTLS figure on file mean something other than it appeared to.

*How it was narrowed, kept for the method.* `SmokeTest --ktls-spike` takes two switches, so the candidates
could be separated in seconds:

| run | TLS version | client | server | probe |
|---|---|---|---|---|
| baseline | 1.3 | TX=True **RX=False** | TX=True **RX=False** | FAIL |
| `SS_KTLS_CLEAR_NO_RX=1` | 1.3 | TX=True **RX=False** | TX=True **RX=False** | FAIL - no change |
| **`SS_KTLS_FORCE_TLS12=1`** | **1.2** | TX=True **RX=True** | TX=True **RX=True** | **PASS** |
| both | 1.2 | TX=True RX=True | TX=True RX=True | PASS |

**So: OpenSSL 3.0.13 declines kTLS RX for TLS 1.3 and grants it for TLS 1.2.** The `SSL_MODE_NO_KTLS_RX`
theory is refuted (clearing it changes nothing). The "you never called `SSL_read`" and "you used a memory
BIO" theories were already excluded - this probe does neither, and still saw RX=False on 1.3.

**The common advice "enforce TLS 1.3 to keep multishot" is therefore backwards on this OpenSSL**: 1.3 is
exactly what keeps RX offload off. The honest options are (a) OpenSSL **3.2+**, which is *said* to support
TLS 1.3 kTLS RX - unverified here, this box has 3.0.13 - or (b) cap at TLS 1.2, which is a real security
and performance regression and is only sane as an experimental vehicle, not a shipping default.

**And the property multishot needs is directly confirmed.** In the passing runs the probe completes a
**plain `send` -> plain `recv` round-trip over the kTLS socket** - the kernel doing the crypto, no OpenSSL
in the data path. That is precisely what `IORING_OP_RECV` + `IORING_RECV_MULTISHOT` over a provided-buffer
ring requires, so the external claim's core mechanism holds; only its version advice was inverted.

#### The three ways out, and what each actually costs

**A. Require OpenSSL 3.2+ for kTLS RX.** The cleanest if it works. **Being validated** - the probe now
reports the loaded version and whether the build has kTLS at all (`OpenSSL_version_num` /
`OpenSSL_version(OPENSSL_CFLAGS)`), because a first attempt using the flatpak runtime's 3.5.7 returned
**TX=False RX=False** - kTLS not engaging *at all*, which is not the same as "declines RX" and must not be
read as evidence either way. Settling it needs a build configured `enable-ktls`.

**B. Extract the keys and call `setsockopt(SOL_TLS, TLS_RX)` ourselves, bypassing OpenSSL.** Possible, but
**the API usually named for this is the wrong one**: `SSL_export_keying_material` is the RFC 5705 / RFC
8446 §7.5 *exporter*. It derives fresh key material from the exporter master secret for other protocols
(channel binding, DTLS-SRTP); it does **not** return the record-protection traffic keys, and keys obtained
that way decrypt nothing. The actual route for TLS 1.3 is `SSL_CTX_set_keylog_callback` ->
`CLIENT_TRAFFIC_SECRET_0` / `SERVER_TRAFFIC_SECRET_0` -> HKDF-Expand-Label("key") and ("iv") per RFC 8446
§7.3 -> fill `tls12_crypto_info_aes_gcm_128/256`.

Three things make it harder than the snippet suggests, and all three fail *silently* (garbage plaintext or
a dead connection, not an error):
  - **The record sequence number must be exact at handoff.** `rec_seq` has to be OpenSSL's current read
    sequence, and the handoff must happen when OpenSSL holds no partially-consumed record - it may already
    have eaten post-handshake records (NewSessionTicket) and advanced past them.
  - **We inherit rekeying.** TLS 1.3 KeyUpdate arrives as a handshake record, so after handoff *we* must
    notice it (via the cmsg path) and push new keys. This kernel does support it - `/proc/net/tls_stat`
    here exposes `TlsRxRekeyOk` / `TlsRxRekeyError` / `TlsRxRekeyReceived` - but it becomes our job.
  - **It is a reimplementation of part of OpenSSL's key schedule**, in a place where a mistake is a
    security bug rather than a perf bug.
  So: a legitimate fallback for old OpenSSL, not the first thing to reach for.

**C. Cap at TLS 1.2 where kTLS RX matters.** Works today (measured above), needs no new code beyond a
version knob - and is a real security/performance regression. Defensible as an experimental vehicle for
measuring multishot+kTLS, not as a shipping default.

**Recommended order: validate A; if it holds, require 3.2+ for the kTLS-RX path and keep the current
TX-only behaviour as the fallback on older OpenSSL (which is what the code already does, correctly, by
discovering capability rather than assuming it). B only if A is unavailable and the win is proven.**

**If RX offload can be turned on, the follow-on work is small and the payoff is item 4's whole point:**

- Receive becomes `IORING_OP_RECV` multishot over the provided-buffer ring, returning plaintext - i.e. the
  same fast path as plaintext io_uring, which is the thing kTLS currently forfeits.
- **The one real complication is control records.** With kTLS RX, a non-`application_data` record (alert,
  KeyUpdate, close_notify) cannot be delivered through plain `recv`; the kernel fails the read and the
  record type is only retrievable via `recvmsg` + the `TLS_GET_RECORD_TYPE` cmsg. So the shape is:
  multishot `RECV` as the fast path, and on that specific failure fall back to a one-shot `RECVMSG` +
  cmsg, handle the record, and re-arm. `TlsContentType` already exists for exactly these values.
- The external note's `recvmsg_multishot` objection then does not apply, because the fast path is `RECV`
  multishot (no `io_uring_recvmsg_out` header), not `RECVMSG` multishot.

**Why it is worth doing:** kTLS currently trails userspace TLS on every leg (`iouring+ktls` 597,486 rps
against `iouring+tls` 702,829, -15%), and the standing explanation is precisely this forfeiture. This
would test that explanation rather than assume it - and item 3c predicts the same thing from the other
end, since epoll has no multishot to lose and should therefore not pay the penalty at all.

### 4. kTLS: implement the RECVMSG + cmsg receive arm

Only worth doing if kTLS matters. The current kTLS path drives receive as io_uring `POLL` + `SSL_read`,
one syscall per message, forfeiting multishot receive and provided buffers - the design seam described in
`TlsFilter`'s notes (`RECVMSG` + `TLS_GET_RECORD_TYPE` cmsg) is what would let it keep them. kTLS does
already pull ahead of userspace io_uring TLS at 256KB (+65%), so the crossover is real; this is what
would make it competitive rather than merely present.

Note loopback has no NIC, so kTLS's largest win - inline NIC offload - cannot appear in any of the
numbers above. Real hardware required before drawing conclusions about kTLS itself.

### 5. Real-hardware Linux run — PARTLY DONE 2026-07-28, and the rest has a route

The rigs are in-repo and runnable: `bench/run-matrix.sh` (transport matrix) and `bench/run-tls-sizes.sh`
(payload sweep).

**Done:** the container-on-WSL2 objection is gone. Linux now runs on bare metal (Pop!_OS 24.04, kernel
7.0.11) on the same desktop as the Windows numbers, with io_uring available natively. See
`bench/README.md` for the host and its setup.

**Not done, and not fixable here:** it is still ONE box over loopback. Two things stay structurally
invisible no matter how good the host is — kTLS inline NIC offload (`TlsTxDevice` is 0 and always will
be), and any effect where memory bandwidth is contended by a real NIC rather than shared by a client and
server on the same silicon. Every large-payload conclusion in this file carries that caveat.

**Route for the remainder (noted 2026-07-28):** a run on appropriate hardware is *planned to be
investigated later*, once the work is in a state worth testing. So this is a dependency with a plausible
path rather than a permanent ceiling — which is an argument for keeping the rigs and their
`/config` + `tls_stat` gates in a state someone else could run cold, and for writing results up to a
standard that survives being read by an expert audience.

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
