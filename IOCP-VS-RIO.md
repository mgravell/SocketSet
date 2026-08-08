# IOCP vs RIO: which Windows backend, and why

**Short version: use `iocp` unless you have measured a reason not to.** RIO wins only where its costs
amortise — deep pipelines or many busy connections — and it loses badly, by more than an order of
magnitude, on low-depth request/response traffic. That is a property of the Windows API, not of this
implementation, and the sections below are the evidence rather than the assertion.

This document exists because the "RIO collapses on Garnet" investigation (2026-08-07/08) produced four
successive explanations, three of which were wrong, and the wrong ones are as useful to record as the
right one. `TODO.md` carries the blow-by-blow; this is the conclusion in one place.

**Everything here is exploratory unless it says otherwise** — one machine, over loopback, unpinned, and
not scored to the `bench/README.md` standard. Ranges are given because medians alone have misled here
before. Do not difference these against the Linux tables in `RESULTS.md`.

---

## The structural difference, which is the whole story

**IOCP can complete an operation without producing a completion.** `IocpShard.cs:1051` sets
`FILE_SKIP_COMPLETION_PORT_ON_SUCCESS | FILE_SKIP_SET_EVENT_ON_HANDLE`, so a `WSARecv` that finds data
already buffered returns it *in the syscall* and posts no port packet at all. `DrainInline` picks such
completions up without the loop ever blocking. At depth 1 on loopback, IOCP turns an entire
request/response round trip without a single block and without a single completion object.

**RIO cannot.** Its request queues are drained in user mode with no per-op syscall — that is RIO's whole
selling point, and it is real — but *every* operation must round-trip through the completion queue.
There is no inline-completion equivalent. Learning that the CQ went non-empty additionally costs a
`RIONotify` arm, a block, and a wake, unless you spin.

So the two backends are optimised for opposite regimes:

| | IOCP | RIO |
|---|---|---|
| op with data already available | completes inline, **no completion** | completion, always |
| per-op syscall at high op rates | one per op | none (user-mode ring) |
| cost model | per-operation | per-completion-batch |
| best regime | low depth, latency-sensitive | deep pipelines, many busy connections |

## The measurement that pins it

The decisive number is not throughput, it is **how long a completion takes to appear while we poll for it
in user mode as fast as we can**. Measured with `SS_RIO_SPIN` + `SS_RIO_STATS` (see
`WindowsRioShard.DrainRioOrSpin`), which reports `us/hit`:

| pipeline depth | GET/s (3 passes) | µs per completion wait |
|---|---|---|
| `-P 1` | 24,521 – 28,915 | **7.00** |
| `-P 4` | 270,159 – 388,494 | **3.16** |
| `-P 16` | 327,278 – 542,519 | **3.32** |

`--shards 1`, one connection, `resp-benchmark -c 50 +m -t GET`. The probe self-calibrates: a spin *miss*
burns a known 1000 iterations and reports ~55µs, i.e. ~0.055µs per iteration, consistently across runs,
so the `Stopwatch` reads are not dominating what they measure.

**Read the third column, not the second.** There is a **hard ~3.2µs floor per completion that persists at
depth 16**, where the pipeline is full and the next request is certainly already sitting in the kernel
buffer. The wait is therefore not "waiting for data to arrive" — it is RIO's completion machinery. It is
never removed; it is only ever *amortised* across the ops in a batch. That single fact explains the whole
shape of the problem:

- **At `-P 1` the floor is paid per round trip.** Two completions per round trip (recv and send) at
  ~5-7µs each dominates a ~24µs round trip, against IOCP's ~1.8µs round trip total.
- **At `-P 16` it is divided by 16** and RIO reaches IOCP-comparable throughput.
- **On many busy connections it is hidden entirely**, because other connections' completions arrive
  during the wait.

## What that costs, at depth 1

Single connection, `-c 50 +m -P 1 -t GET`, `--shards 12`, six scored passes, in-session control:

| leg | scored range | median |
|---|---|---|
| `iocp` | 537,135 – 642,482 | **554,628** |
| `rio`, spin off (default) | 17,015 – 23,742 | **21,932** |
| `rio`, `SS_RIO_SPIN=1000` | 41,181 – 49,415 | **42,252** |

**25.3x behind by default; 13.1x behind with the spin.** This is the shape a `ConnectionMultiplexer`
produces — StackExchange.Redis holds one connection per endpoint, shared process-wide — so it is the
*default deployment* for a .NET Redis client, not a synthetic corner case.

## Where RIO is fine, and where it wins

- **Many busy connections**: 4 processes x 50 connections, three passes — `rio` 315,008 – 378,392 against
  `iocp` 314,742 – 358,637. Indistinguishable.
- **HTTP keep-alive at 512 B**: `rio` 145.5 MiB/s against `iocp` 143.5 (see `RESULTS.md`). No deficit —
  pipe mode batches many responses per flush via `OnLoopDrain`, which amortises the floor.
- **Deep pipelines**: `-P 16` above.

The pattern is consistent: **anything that puts more than one operation in flight per completion batch
hides the cost; anything strictly one-in-one-out exposes it.**

## Things that were tried and did NOT fix it

Recorded so nobody re-derives them. Each looked plausible and each was measured.

1. **Shard placement.** Ruled out: at `--shards 1` there is no placement freedom by construction, and RIO
   was still ~34x behind. (The IOCP arm of that sweep is inconclusive by construction — one shard already
   serves the load comfortably, so it discriminates nothing.)
2. **Concurrency / multiplexing.** `-c 1` and `-c 50` land in the same place.
3. **Small writes / `maxSendDataBuffers = 1`.** Falsified by the send counters: both backends issue ~22k
   sends for ~120k replies, i.e. both already coalesce ~5 replies per send. RIO is not fragmenting.
4. **The cross-thread flush hop.** A real defect, and fixed — `SubmitOutbound` marshaled every flush
   through a queue plus a `PostQueuedCompletionStatus` wake even when already on the loop thread. Fixing
   it cut notify-rearms 0.99 → 0.41/send and wakes 1.31 → 0.39/send, and moved throughput ~15% at most,
   with overlapping ranges. **Worth keeping, nowhere near sufficient.**
5. **The notify round trip itself.** The standing explanation until 2026-08-08. A bounded spin cut
   notify-rearms to **0.02/send** — a 50x reduction, i.e. blocking essentially eliminated — and closed
   only half the gap. **The cycle is therefore not the dominant cost**, and cannot be cited as one.
6. **Commit-per-send.** `commits: send=1.00/send` looks damning but is not a cost: `RIOSend` with
   `RIO_MSG_DEFER` is a user-mode ring write, and only `FlushCommits` enters the kernel, once per RQ per
   direction. A depth-1 round trip costs RIO two kernel transitions — exactly what IOCP pays with
   `WSARecv` + `WSASend`.
7. **Receive re-armed behind the send.** Checked, and it is not happening. `HandleRecv` dispatches to the
   application (which submits the send) at `:1043` and arms the next receive at `:1052`; both are
   `RIO_MSG_DEFER` and a single `FlushCommits` kicks both directions, so both completions are outstanding
   simultaneously. The two waits are **already overlapped**. Note the receive cannot be armed *before*
   dispatch without double-buffering: the receive buffer is the one the application is reading from.

## `SS_RIO_SPIN`, and why it is off by default

`SS_RIO_SPIN=N` makes the loop spin up to N iterations on the CQ before arming `RIONotify` and blocking.
`N=1000` is the measured optimum (`N=5000` is worse — the hit rate barely improves while the loop wastes
more time not returning). It roughly doubles depth-1 RIO, with non-overlapping ranges.

**It is off by default and should stay off until a measurement earns it.** A spin trades CPU on every
shard for latency on one, and the many-connections regression check (4x50: 322,055 – 505,018 spun against
354,519 – 500,903 unspun, ~9.9 against ~9.6 server cores) has ~40% pass-to-pass variance — that is *no
evidence of regression*, not *no regression*. What would earn a default change: a six-pass
many-connections A/B with tighter variance, and probably an **adaptive** spin that backs off when misses
dominate. The miss rate is already counted, so the input exists.

If the default ever moves, **the setting must appear in the `/config` banner** — house rule 1. It does not
today, because today it is off.

## Recommendation

- **Default to `iocp` on Windows.** It is never much worse and is sometimes 13-25x better.
- **Consider `rio`** when the workload is deep-pipelined, or has many continuously-busy connections, or
  is bulk-transfer shaped — and measure it against `iocp` on your own traffic before adopting it.
- **Do not use `rio` for low-depth request/response**, which includes any single multiplexed client
  connection. That is the regime where the completion floor is fully exposed.

## Caveats

One machine, loopback, unpinned, exploratory. The `-P` sweep is three passes, below the six-pass house
standard. Loopback flatters both backends by removing the NIC, and RIO's design advantages — registered
buffers, no per-op syscall — are aimed at high packet rates against real hardware, which this box cannot
show. **The ~3.2µs completion floor is the most portable finding here**, because it was measured directly
rather than inferred from throughput; the throughput ratios around it are the most environment-specific.
