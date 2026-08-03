# Benchmark results - AspNetDemo transport matrix

Numbers produced by [`../bench/Run-Matrix.ps1`](../bench/Run-Matrix.ps1), which drives this project through
bombardier. Run it from the repo's `bench/` folder; raw CSV and per-leg logs land in `bench/results/`
(gitignored).

> Getting a trustworthy number out of a single-box loopback benchmark took six attempts. Every failed
> attempt produced clean-looking output - plausible magnitudes, zero failed requests - and none announced
> itself as an error. The confounders are documented at the bottom; they are the more transferable half of
> this document.

> **Two hosts appear in this file.** Everything dated 2026-07-27 is the current baseline, measured on a
> desktop. Everything dated 2026-07-26 or earlier was measured on a *different, weaker laptop* and is kept
> for the findings and the method, not as a baseline. **Never compare a number across the two.** Where an
> older section's conclusion has been re-tested, that is stated inline.

## WHERE THINGS STAND (2026-08-03) — the consolidated view, RESP era

Everything below this block is a dated investigation; this is the summary they add up to. The 2026-07-30
consolidated view (next section) is kept as the record of the HTTP/Kestrel era; on 2026-08-02 the
project's guidepost changed from "beat Kestrel" to the RESP ecosystem (see TODO's direction-change
section), and these are the numbers that era has produced. All same-session, interleaved, disjoint
unless marked; one box, loopback, governor `performance`.

**UNITS, ONCE (they apply to every RESP-era table below):** throughput numbers are **requests/second**
as reported by `redis-benchmark` (a value like `548,890` is req/s; `4.43M` is 4,430,000 req/s), and all
latency figures — `p50`/`p99`, and the second number in a `538,420 · 0.199` pair — are **milliseconds**
(so `0.199` = 199µs). The HTTP-era tables further down use **goodput MiB/s** where their captions say
so, and rps/µs where marked; when in doubt there, the caption above each table governs.

**Four consumers, one transport:**

| consumer | vs | headline |
|---|---|---|
| **RESP proxy** (`RESPite.Proxy` on SocketSet L3) | Envoy 1.39 | TCP: parity `-P 1`, **+177%** `-P 16`. Abstract UDS: **+28% `-P 1` disjoint**, +94-141% depth. TLS-originating (post-hook): **+7.5% `-P 1` disjoint, +139-151% depth** |
| same | hand-rolled SAEA `WorkerPool` | +15-28% both depths (level 1, before the fast path existed) |
| **Garnet** (`SocketSet.Garnet`, embedded) | stock SAEA layer | plaintext: parity, p99 lower all cells; **TLS: +9.5-24.2% all four disjoint**; abstract UDS: SET +11-12.5% disjoint both depths |
| **Client shape** (`run-client-shape.sh`) | direct | one loop thread ≈ 1.15M ops/s per connection at 47-103µs p99 through a full proxy hop; ~2M+ extrapolated for client mode |
| **redis-benchmark/redis-cli/redis-server** | upstream | `@abstract` UDS branches (issue #15577, PRs #15572/#15575); UDS = +80% over TCP loopback on redis-server itself, abstract ≡ pathname exactly |

**The mechanisms that got it there, in order of discovery:** shard-affine upstream placement
(`ConnectShard` + `CurrentShardIndex`; hop-free forward AND reply — v1 without affinity measured
NEGATIVE, which located it), callback-granularity flushing (the `-P 16` collapse fix), then
**batch-granularity flushing** (`OnLoopDrain`; killed a 3x send amplification, depth-TLS tax 28%→8.4%,
`-P 1` TLS p99 −25%). In-transport TLS is now directional (`TlsMode`) for the proxy shapes, verified by
refusal cell. The remaining known headrooms are small and recorded: ~8% TLS residue (one avoidable
ciphertext copy, designed not built), the SMT tail trade (shards ≤ physical cores ≈ Envoy-level p99 at
−22% throughput), and Envoy's one surviving cell (`-P 1` TLS tail).

**Instruments and honesty:** the confounder ledger stands at **#15** (latest: a Debug×tier-0 scanner
baseline that under-rated `RespReader` by ~5x — true steady state is ~9.5ns/frame, 105 Mframes/s, and
the scanner is NOT a lever). `verify-proxy.cs` (13 cells, RESP3-literate) gates every leg incl. Envoy
and Garnet; rigs quantisation-audit themselves; `TlsMode` has a refusal cell. What this box still
cannot see: kTLS NIC offload, real-network behaviour, handshake/churn costs — the lab questions.

## WHERE THINGS STAND (2026-07-30, extended through 2026-07-31) — the consolidated view of the HTTP/Kestrel era

Everything below this section is a dated investigation; this is the summary they add up to. Cells are
linked to the section that measured them. **Do not compare Linux and Windows rows** - same silicon, but
different OS and different dates.

### Headline numbers, Linux, THIS box — DEFINITIVE (2026-08-01, `bench/run-tls-sizes.sh` REPS=7 → 6 scored passes, pinned default, same-session Kestrel control)

7900X bare metal, 12 shards, `-c 64`, ASP.NET bridged path, `/payload` GET. Goodput MiB/s, **min-max of 6
scored passes**; a delta is only claimed where the ranges are DISJOINT. This table SUPERSEDES the scattered
cross-run numbers used earlier this session and corrects two of my own over-claims (see below).
**Governor caveat: `powersave` (the box reboots to it and I lacked root to set `performance`) — so RELATIVE
comparisons here are sound, ABSOLUTE MiB/s may sit a few % under a `performance`-governor run.**

| size | kestrel | io_uring | epoll | plaintext verdict | kestrel+tls | io_uring+tls | epoll+tls | TLS verdict |
|---|---:|---:|---:|---|---:|---:|---:|---|
| 512 B | 342-346 | 322-331 | 331-335 | **Kestrel +3-5%** | 244-260 | 283-294 | 290-299 | **us +13-18%** |
| 4 KB | 2379-2473 | 2290-2372 | 2311-2436 | Kestrel (vs io_uring) | 1648-1671 | 1778-1915 | 2042-2088 | **us +10-25%** |
| 16 KB | 7331-7496 | 6915-7152 | 7165-7308 | **Kestrel +2-6%** | 4525-4648 | 5167-5346 | 5694-5856 | **us +13-26%** |
| 64 KB | 9954-10402 | 10122-10407 | 10356-10510 | wash (overlap) | 6629-6789 | 8041-8406 | 8959-9051 | **us +20-35%** |
| 256 KB | 12239-12529 | 12610-12965 | 12328-12681 | **io_uring +2%**; epoll ≈ | 7732-8265 | 3202-3604 | 4142-4205 | **Kestrel +>50%** |

**Corrected bottom line:**
- **Plaintext `/payload`: Kestrel leads the small-to-mid range (512 B-16 KB, disjoint +2-6%)**, it's a wash
  at 64 KB, and io_uring edges ahead only at 256 KB. My earlier "we win 64 KB / small plaintext" was WRONG
  for `/payload` — the +5.6% small-message win is the 2-byte `/plaintext` endpoint (`Results.Text`), a
  lighter path than `/payload` (`Results.Bytes` + dictionary). Keep those two endpoints distinct.
- **TLS is the real strength: we win decisively and disjoint from 512 B through 64 KB (+13-35%)** — the
  in-transport OpenSSL vs `SslStream` advantage, larger and broader than the "+22%" I'd quoted. Only at
  256 KB does userspace TLS lose (structurally — the wire bytes are encrypted, so zero-copy send can't
  apply; kTLS narrows it, 256 KB ktls 5,006-5,544 vs tls 3,202-4,205, but doesn't reach Kestrel's 7,732+).
- The 256 KB PLAINTEXT parity is real and is the pinned-pool fix; the old "−14 to −16%" there was the
  unpinned-pool confounder. Bare epoll still hits 13,107 at 256 KB (above Kestrel) — the transport is not
  the limit; the bridge is, and only for plaintext where we're already at parity.

### SE.REDIS ON SOCKETSET, v1: BOTH PREDICTIONS FALSIFIED — classic ahead in ALL EIGHT CELLS, and the mechanism is already confirmed (2026-08-03)

The first client-seat A/B (`bench/mux-ab` + `bench/run-mux-ab.sh`: one ConnectionMultiplexer, D
concurrent awaiting workers, identical generator both legs, only `ConfigurationOptions.Tunnel` differs;
stock-Garnet server; counting-tunnel engagement gate; 6 passes interleaved, reshuffled). Pre-registered
P1 (depth-1 parity ± 10%) and P2 (depth-64 tunnel ≥ +15%) both FALSIFIED — the falsification is the
finding:

| cell (req/s · p99 ms) | classic | tunnel (transport mode v1) | verdict |
|---|---:|---:|---|
| GET d1 | 51,463-52,506 · 0.022-0.023 | 43,061-45,985 · 0.025-0.027 | **classic +14% DISJOINT** |
| SET d1 | 51,279-52,154 · 0.022-0.023 | 45,049-46,093 · 0.025-0.026 | **classic +12% DISJOINT** |
| GET d64 | 1,081,983-1,104,296 · 0.090-0.100 | 147,148-157,891 · 0.462-0.564 | **classic ~7x DISJOINT** |
| SET d64 | 1,021,738-1,026,719 · 0.099-0.104 | 144,167-156,416 · 0.460-0.569 | **classic ~6.8x DISJOINT** |
| TLS legs | same shape | same shape | classic +8% d1, ~6.5x d64, all DISJOINT |

**The mechanism, confirmed by counter before any fix (SS_URING_STATS on a tunnel d64 cell): send SQEs
≈ ops (1.56M SQEs for ~1.5M ops), iov-segments == SQEs (ONE ~40-byte segment per send), and
`queued-behind-inflight` = 99.8% of all sends.** Every command's flush queues behind the inflight send
and is then drained ONE JOB PER COMPLETION (`IoUringShard.DrainNext` dequeues a single `PendingJob`) —
so the connection is a syscall-latency conveyor at ~6µs/op ≈ the observed ~170k cap, while classic
packs ~64 commands per write. This is the proxy's send-amplification lesson in its third costume
(peer-granular TLS records, then per-callback flushes, now per-op client flushes) — and the natural
batch boundary ALREADY EXISTS: the pending queue itself. The fix shape is drain-time coalescing
(merge all consecutive queued chains into one writev, bounded by IovMax). The depth-1 deficit
(~2.7µs/op) is the loop-thread hop for staged sends and was NOT expected to move with this fix.

**POST-FIX (same day, commit 5ec2b65 + the anchor rev 289c426; smoke 60/60 and tunnel-selftest 5/5
before re-measuring; 6 passes, same rig):** the counters first — ~13 ops/SQE (was ~1), and a second
LATENT coalescing layer surfaced: staging already shared pages across ops (~9 ops/segment), which the
one-job drain had never let matter. Then the table:

| cell (req/s · p99 ms) | classic | tunnel (post-fix) | verdict |
|---|---:|---:|---|
| GET d1 | 51,600-52,238 · 0.022-0.023 | 44,764-45,686 · 0.025-0.026 | classic +13% DISJOINT (unmoved, as predicted) |
| SET d1 | 51,694-52,207 · 0.022 | 44,513-45,679 · 0.025-0.026 | classic +13% DISJOINT (unmoved) |
| GET d64 | 1,063,051-1,108,720 · 0.091-0.099 | 1,028,455-1,047,990 · **0.080-0.093** | classic +4.4% disjoint on THROUGHPUT; tunnel p99 better |
| SET d64 | 1,020,440-1,034,408 · 0.098-0.100 | **1,077,327-1,103,895** · **0.082-0.096** | **tunnel +6.2% DISJOINT** |
| GET d64 TLS | 940,872-956,021 · 0.099-0.114 | 925,948-942,126 · **0.089-0.101** | −1.5% overlapping — parity, tunnel p99 better |
| SET d64 TLS | 914,654-930,863 · 0.104-0.107 | **982,457-999,055** · **0.089-0.102** | **tunnel +7.4% DISJOINT** |

**Reading:** the collapse was entirely the drain granularity. Post-fix, the tunnel DISJOINTLY WINS both
SET depth cells and holds better p99 in all four depth cells; GET depth is −4.4%/parity. The SET-wins/
GET-trails asymmetry is mechanism-consistent with the known receive copy (SET replies are tiny `+OK`,
GET replies carry the value — the leg that receives more, pays more), which promotes
receive-into-caller-buffer from "deferred" to the next transport lever. Depth-1 stays classic's by
8-13% — the staged-send loop hop, real and recorded; the SE.Redis client regime is depth (a busy
multiplexer), but sequential-await apps exist and would feel it.

**"WHY ISN'T IT BETTER" — THE CEILING CONTROL THE RIG LACKED (same day, Marc's question):** raw
`redis-benchmark -c 1 -P 64` against the same pinned stock Garnet: **2.2-2.4M ops/s at p50 0.023ms** —
so BOTH SE.Redis legs sit at ~46% of the single-connection wire ceiling, and the missing ~34µs of
average per-op latency (57µs vs 23µs) is SE.REDIS MACHINERY (encode/bridge/complete/continuation),
identical on both legs. Amdahl: the IO engine is the minority term of this regime, and no transport
can 2x a client whose bottleneck is above the transport. Two more probes the same hour: (a) per-core
CPU is a WASH at d64 (~11 cores both legs — the 64 awaiting workers dominate; the thread-economics
question needs the multi-mux fixed-core-budget test, queued); (b) **the EXTREME TAIL is the buried
headline: classic p999 1.18-1.66ms vs tunnel 0.22-0.37ms — 3-5x better, every rep, pinned or not**
(the rig's summary surfaced p99 only; p999 was in the CSV all along). For the client seat, where the
tail is the product, that plus the SET-depth disjoint wins is the current honest value statement.

**Epoll null result (same day):** the obvious follow-up — does epoll's send path have the same
disease? — measured NO before any speculative fix: tunnel GET d64 epoll 707-714k vs io_uring 716-727k
(3 interleaved unpinned probes, `MUXAB_BACKEND=epoll`, banner-reported; RELATIVE read only — unpinned
probes run ~30% below the pinned rig). Epoll writes synchronously via `send()` and only queues on
EAGAIN, so it never had the completion conveyor; its per-op syscall cost roughly washes with io_uring's
submission batching at this depth. No fix warranted on this evidence.

### GARNET ON SOCKETSET (2026-08-02, late): parity-to-ahead on day one, better tails in every cell

Consumer #4, suggestion-to-measurement in one day. `src/SocketSet.Garnet` hosts Garnet via the embedding
ctor's `IGarnetServer[]` parameter (pure `PackageReference Microsoft.Garnet`, no fork); `GarnetDemo
--stock` hosts their own `GarnetServerTcp` on identical options, so the A/B is one flag on one binary.
Gate first: `verify-proxy.cs` **13/13 on BOTH legs**. Interleaved, banner-gated, 5 passes, server on 3
physical cores with shards=6 matched to its logical CPUs (the SMT lesson applied):

| cell | stock SAEA (req/s · p99 ms) | on SocketSet (req/s · p99 ms) | verdict |
|---|---:|---:|---|
| `-P 1` GET | 548,890 · p99 0.247 | 538,420 · p99 **0.199** | overlapping — parity |
| `-P 1` SET | 518,480 · p99 0.311 | 538,379 · p99 **0.215** | +3.8% overlapping |
| `-P 16` GET | 7,055,504 · p99 0.303 | 7,055,504 · p99 **0.263** | identical medians — parity, and both legs sit at the CLIENT ceiling (~7.06M vs direct's 7.2M), so this cell is ceiling-capped |
| `-P 16` SET | 4,285,714 · p99 0.751 | **4,615,384** · p99 **0.439** | **+7.7% DISJOINT** |

**Never behind on throughput, one disjoint win, and p99 LOWER in all four cells** (the depth-SET tail
nearly halved) — from a v1 that still pays an extra receive copy the SAEA path does not (their SAEA
receives directly into the handler buffer; we copy from the transport-owned span). The copy is the known
first lever if this ever needs more; the tails suggest the loop-thread model is already paying for it.

### TLS-ORIGINATION, POST-HOOK RERUN (2026-08-03): the Envoy gap widens to +139-151%, and `-P 1` goes DISJOINT

The pre-hook TLS-origination table carried a note that it should widen on the fixed build. Measured,
same rig, same pinned trust, n=60M at depth:

| cell | envoy, BoringSSL (req/s · p99 ms) | socketset post-hook (req/s · p99 ms) | Δ (was, pre-hook) |
|---|---:|---:|---|
| `-P 1` GET | 372,058 · p99 **0.271** | **399,960** · p99 0.431 | **+7.5% DISJOINT** (was +4.8% overlapping) |
| `-P 1` SET | 363,603 · p99 **0.279** | **390,206** · p99 0.463 | **+7.3% DISJOINT** (was +4.6%) |
| `-P 16` GET | 1,803,969 · p99 0.831 | **4,527,618** · p99 **0.527** | **+151.0%** (was +80.0%) |
| `-P 16` SET | 1,701,548 · p99 0.895 | **4,067,521** · p99 **0.559** | **+139.0%** (was +67.9%) |

Both pre-registered predictions held: the depth gap moved into the plaintext-shaped band once our TLS
leg stopped throttling itself on send amplification, and `-P 1` flipped from crypto-parity-overlap to a
disjoint lead (fewer syscalls pay at every depth). The BoringSSL-parity conclusion therefore narrows to:
**parity in crypto cost, behind in transport efficiency** — and Envoy's one surviving cell is its `-P 1`
TLS tail (0.27 vs 0.43 ms), unchanged and kept honest.

### THE BATCH-END FLUSH HOOK (2026-08-03): the depth-TLS tax falls 28% → 8.4%, and every leg got faster

The diagnosed send amplification (peer record-granular writes x per-callback flush = 3x send SQEs) is
fixed by **`SocketSet.OnLoopDrain`** — a batch-end hook the loop backends fire once per event batch
(io_uring after each CQE batch, epoll after each `epoll_wait` batch; managed never) — with the proxy's
`DrainDeferred` moved from per-`OnReceive` to the hook. Gates first: smoke 60/60 (default no-op = the
existing world), verify-proxy 13/13 on plaintext AND TLS-originating configs.

**Every pre-registered prediction held, two over-delivered:**

| metric | pre-hook | post-hook |
|---|---:|---:|
| TLS `-P 16` send SQEs / 10M ops | 1,226,824 (3x plaintext) | **~285k — below old plaintext's 406k** |
| plaintext `-P 16` SQEs (control) | ~406k | ~300k (the hook helps the clean case too) |
| TLS `-P 16` throughput, req/s (same-session vs plaintext) | **−28%** | **−8.4%** (4,443,786-4,527,960 vs 4,799,616-4,897,559, disjoint, n=60M) |
| TLS `-P 1` p99 (ms) | 0.543-0.591 | **0.319-0.447** (~−25%; the "unchanged envelope" prediction over-delivered) |
| TLS vs plaintext p99 at depth (ms) | — | **identical** (0.62-0.64 both) |

**~70% of the depth-TLS tax was send amplification, not crypto.** The ~8% residue is the honest
encrypt-path cost, now cleanly separated. A quantisation lesson re-learned on the way: the first
post-hook run (n=10M ≈ 2.5s tests) showed TLS *equal* to plaintext — tick-pegged values; the n=60M
confirm resolved the real −8.4%. The SQE counts, being exact, never needed the re-run.

Note for any future Envoy TLS-origination rerun: this morning's +68-80% table was measured PRE-hook;
the same comparison on this build should widen it.

### TLS-ORIGINATION SHOWDOWN (2026-08-03): BoringSSL is a real peer; the depth win is the TRANSPORT's

The last quadrant: both sidecars plaintext-downstream, both dialing the SAME TLS-only Garnet with
identical pinned trust and SNI — ours via `TlsMode.Connect`/OpenSSL, Envoy via
`UpstreamTlsContext`/BoringSSL. Interleaved, 5 passes, banner-gated:

| cell | envoy, BoringSSL (req/s · p99 ms) | socketset, OpenSSL (req/s · p99 ms) | Δ | verdict |
|---|---:|---:|---:|---|
| `-P 1` GET | 363,636 · p99 **0.295** | 380,916 · p99 0.543 | +4.8% | overlapping |
| `-P 1` SET | 355,556 · p99 **0.303** | 372,058 · p99 0.583 | +4.6% | overlapping |
| `-P 16` GET | 1,777,304 · p99 0.871 | **3,199,488** · p99 0.703 | **+80.0%** | **DISJOINT** |
| `-P 16` SET | 1,701,548 · p99 0.903 | **2,856,735** · p99 0.959 | **+67.9%** | **DISJOINT** |

**The pre-registered fork resolved to the informative branch: the TLS advantage is specifically over
`SslStream`, not over userspace TLS at large.** BoringSSL holds crypto parity at `-P 1` (we lean +4.7%,
overlapping — and Envoy's `-P 1` tails BEAT ours there, 0.30 vs 0.55 ms). The disjoint depth wins are
the underlying transport advantage surviving TLS, not a crypto-stack win.

**And a self-critical delta worth its own line:** at depth, TLS costs US ~28% off our own plaintext
(4.43M → 3.20M GET) while Envoy's depth numbers barely move from its plaintext — its depth bottleneck is
parse/dispatch, not crypto, whereas OUR out-of-band encrypt path at depth has measurable headroom. That
is the same structural shape the HTTP-era work found on the TLS large-payload path, now visible in RESP —
and it is the first concrete perf lead for the TLS side of the transport. (Cross-day plaintext references
are qualitative only; the disjoint claims above are all same-session.)

### THE SIDECAR SHOWDOWN (2026-08-03): over abstract UDS, we beat Envoy at BOTH depths — the first `-P 1` disjoint win

Yesterday's caveat ("no Envoy-relative UDS claim until a same-session Envoy-UDS leg runs") is retired.
Envoy 1.39's pipe listener accepts `@`-abstract paths (validated, kernel-confirmed); both proxies ran on
their own abstract names, same session, interleaved, 5 passes, same 3-physical-core pinning,
`--concurrency 6` matching our 6 shards, TCP upstream to the same Garnet:

| cell | envoy (req/s · p99 ms) | socketset, L3 affine (req/s · p99 ms) | Δ | verdict |
|---|---:|---:|---:|---|
| `-P 1` GET | 399,968 (298,454-416,597 — WIDE) · p99 0.351 | **512,715** · p99 0.399 | **+28.2%** | **DISJOINT** |
| `-P 1` SET | 499,900 · p99 **0.175** | 499,850 · p99 0.415 | ±0 | tick-tied (50 rps = one timer quantum) |
| `-P 16` GET | 1,713,404 · p99 1.415 | **4,136,790** · p99 **0.623** | **+141%** | **DISJOINT** |
| `-P 16` SET | 1,874,180 · p99 0.807 | **3,635,042** · p99 0.735 | **+94%** | **DISJOINT** |

**The finding inside the finding: UDS is not neutral ground — Envoy REGRESSES on it** (`-P 1` GET 452k
TCP → 400k UDS, with an unstable range) **while we improve** (491k → 513k). At depth we are 2.2-2.4x.
This is the first DISJOINT `-P 1` throughput win over Envoy in any configuration — on TCP that cell was
parity at the generator ceiling. Kept honest: Envoy's `-P 1` SET tail (0.175 ms) beats ours (0.415) in
that one cell, and this ad-hoc run had no quantisation audit — but the claimed deltas (+28/+94/+141%)
are an order of magnitude above the ~2.5% tick.

So the sidecar story completes: **app → @abstract → this proxy → TCP/TLS upstream is faster than the
same shape through Envoy at every depth measured, with no filesystem footprint.**

### GARNET OVER ABSTRACT UDS (2026-08-02, last run of the day): the TCP pattern repeats, elevated

Both legs on the IDENTICAL abstract name (`@gd-abs`), plaintext, interleaved, 5 passes. Getting stock
there needed a workaround worth recording: **Garnet's embedding path (`servers == null`) demands
`UnixSocketPath` and unconditionally `File.Delete`s it — impossible for an abstract name (NUL byte), so
embedded-abstract is an upstream gap** (reported upstream 2026-08-03: garnet discussion #2012, alongside
the A/B numbers from this file); `GarnetServerTcp` itself is abstract-clean (path only used for a
guarded chmod), so the demo constructs it directly. Also note the spelling split: .NET's
`UnixDomainSocketEndPoint` wants `"\0name"` for abstract while SocketSet maps `"@name"` itself — same
kernel name either way, which is what the (patched) benchmark dials.

| cell | SAEA (req/s · p99 ms) | SocketSet/io_uring (req/s · p99 ms) | Δ | verdict |
|---|---:|---:|---:|---|
| `-P 1` GET | 777,605 · p99 0.143 | 799,726 · p99 **0.111** | +2.8% | overlapping |
| `-P 1` SET | 717,875 · p99 0.247 | **799,726** · p99 **0.103** | **+11.4%** | **DISJOINT** |
| `-P 16` GET | 9,227,930 · p99 0.239 | 9,227,930 · p99 **0.167** | 0.0% | IDENTICAL values — generator ceiling (its UDS ceiling is 9.2M vs 7.2M over TCP) |
| `-P 16` SET | 4,443,128 · p99 0.807 | **4,997,502** · p99 **0.319** | **+12.5%** | **DISJOINT** |

The TCP A/B's shape repeats but stronger: SET now disjointly ahead at BOTH depths (TCP showed it at
depth only), p99 lower in all four cells (−58%/−60% on the SETs). UDS itself lifted stock +42% and ours
+49% over their own TCP numbers — and **800k `-P 1` ops/s at ~0.10 ms p99 through a full Garnet server on
an abstract socket is the best small-op figure of the day.** First real load on SocketSet's io_uring UDS
path (previously smoke-only): clean.

### GARNET TLS A/B (2026-08-02, info-grade): in-transport OpenSSL beats SslStream in ALL FOUR cells, disjoint

The differential the plaintext A/B could not show. Same shared cert (pfx for stock, PEM for ours —
identical key material, so the comparison is stacks not certificates), interleaved, banner-gated
(`tls=sslstream` vs `tls=openssl`), 5 passes, TLS-capable `redis-benchmark` (fork build against a
user-prefix OpenSSL 3.5.4 — no system ssl headers on this box).

| cell | SslStream (req/s · p99 ms) | in-transport OpenSSL (req/s · p99 ms) | Δ req/s | Δ p99 |
|---|---:|---:|---:|---|
| `-P 1` GET | 397,772 · 0.439 | **444,247 · 0.247** | **+11.7% DISJOINT** | −44% |
| `-P 1` SET | 377,858 · 0.663 | **443,115 · 0.239** | **+17.3% DISJOINT** | −64% |
| `-P 16` GET | 5,213,764 · 0.687 | **5,707,762 · 0.319** | **+9.5% DISJOINT** | −54% |
| `-P 16` SET | 3,557,453 · 0.967 | **4,417,937 · 0.583** | **+24.2% DISJOINT** | −40% |

**The HTTP-era precedent (+13-35% over `SslStream`) TRANSFERS to the small-record RESP regime** at
+9.5-24.2%, every cell disjoint, tails halved-to-thirded. The relative framing is the sharp one: TLS
costs stock ~27% off its own plaintext; it costs ours ~17% — the OpenSSL leg runs at ~83% of our
plaintext numbers. Structurally this is the purest venue the claim has ever had: Garnet's TLS machinery
is IDLE by construction on our leg (the transport hands the session plaintext), so nothing differs but
the TLS stack.

**Info-grade caveats, pre-stated:** no kTLS leg (loopback cannot show its NIC half and the record-path
cost would misrepresent it — the lab question stands); one cert, one negotiated suite (TLS 1.3 both);
handshake/churn cost out of scope (keep-alive throughout, the repo's standing gap).

### THE UDS SIDECAR HOP: +30% THROUGHPUT AND NEARLY HALF THE TAIL vs the TCP hop (2026-08-02 evening)

Marc's premise — a proxy hosted on a Unix socket is the SIDECAR deployment shape, and the local hop
should be cheaper over UDS than TCP-loopback. Measured: same proxy (L3 affine + deferred flush), same
session, only the LISTEN differing (`--listen-uds /tmp/... `vs TCP), `CORES=6:3:3`, 5 passes:

| `-P 1` GET | ops/s | % of ceiling | p99 |
|---|---:|---:|---:|
| direct *(no proxy)* | 548,977 | 100% | 0.191 ms |
| **proxy over UDS** | **491,194** (491,159-499,964) | **89%** | **0.591 ms** |
| proxy over TCP | 378,358 (358,956-378,358) | 69% | 0.943 ms |

SET matches (491,159 vs 368,402). At `-P 16`: GET 4,363,108 vs 3,692,118 (**+18%**), SET 3,944,557 vs
3,469,545 (+14%), tails lower throughout. **The UDS hop is +30% throughput at `-P 1` with p99 nearly
halved — the sidecar shape is the RIGHT deployment for this proxy, and it also retires the whole
ephemeral-port/TIME_WAIT confounder class for local benching.** A same-session Envoy-over-UDS leg (its
listener supports `pipe:` addresses) is the follow-up before claiming anything Envoy-relative here;
cross-session subtraction stays forbidden.

**The @abstract leg silently VANISHED from this run, and the cause chain is confounder material:** the
`BENCH_EXE` override pointing the rig at the patched benchmark had been "applied" by a command that
self-terminated on its own `pkill -f` (the command line matched itself) BEFORE the patch line ran — so
the run used the STOCK binary, which cannot dial `@` (that is what the fork patch adds). The probe
failed, the measurement produced EMPTY output, and the rig's parse loop wrote NO row at all — the leg
read as "not run" rather than "failed". Two fixes: the override is now real (verified with `bash -x`,
not memory), and an empty measurement now writes a NORESULT row. The pathname/TCP/direct numbers above
are unaffected (stock handles all three).

**Re-run on the genuinely-patched binary, and it completes the picture cleanly: ABSTRACT == PATHNAME, to
the microsecond.** Same medians (`-P 1` GET 491,194 both; SET 482,692 both; `-P 16` ~4.3M both) and the
same p99 (0.591 ms both). Exactly as the kernel model predicts — the data path is identical, abstract
merely skips the filesystem — so the recommendation is unhedged: **use `@abstract` for the sidecar hop;
it costs nothing and removes the socket file, its cleanup, and its permissions entirely.**

### THE CLIENT SHAPE, MEASURED FOR THE FIRST TIME (2026-08-02 evening) — and the scanner baseline

**`bench/run-client-shape.sh`** — one connection, deep multiplex, tail-scored: the SE.Redis regime,
which no rig had ever measured. `-c 1`, GET 32 B, 4 passes, direct vs one affine-SocketSet hop:

| depth | direct (req/s · p99) | + the hop (req/s · p99) | reading |
|---|---:|---:|---|
| `-P 1` | 49,963 (p99 23µs) | 25,377 (p99 47µs) | hop adds ~20µs/op — exactly one extra RTT |
| `-P 16` | 755,683 | 367,952 (p99 63µs) | ~0.5x: the latency CHAIN doubled; structural to any proxy |
| `-P 64` | 2,586,490 | 1,175,478 (p99 95µs) | same |
| `-P 256` | 6,736,842 | **1,149,958 — PLATEAU** (p99 103µs) | **one loop thread saturates at ~1.15M ops/s** |

The plateau is the finding: with affinity, a single connection's whole chain (frame → forward → reply
route) lives on ONE shard thread, and that thread caps at ~1.15M ops/s while direct scales to 6.7M. For
the SE.Redis IO-core question this reads WELL: a real client does roughly half the proxy's per-op work
(it is the endpoint — nothing is re-forwarded), so the per-connection ceiling extrapolates to ~2M+ ops/s
at double-digit-µs p99 — comfortably above what a multiplexed client connection carries. The tail column
is the headline: **47-103 µs p99 through a full extra network hop, flat across passes.**

**`bench/scan-respreader.cs`** — the frame-scanner ISOLATION baseline (single thread, best-of-10):

> **SUPERSEDED 2026-08-03 — the table below measured DEBUG code running TIER-0 JIT (confounder #15,
> two layers deep: `dotnet run --file` builds Debug by default, and the bench finishes before tiered
> promotion; caught by `perf` frames tagged `[MinOptJitted]`/`[QuickJitted]`). The TRUE steady-state
> numbers (`-c Release`, tiering off; the bench now pins both): small replies **9.5 ns/frame
> (105 Mframes/s)**, GET commands **24.3 ns (~8 ns/element)**, mixed proxy shape **16.2 ns**, bulks
> length-skipped at a nominal ~1 TB/s. The scanner is ~2.5% of a proxy shard's budget at post-hook
> rates: it is NOT a lever, the timeboxed tuning attempt is closed as a measured null, and the real
> finding is that `RespReader` is ~5x better than this file previously said.**

| mix | Mframes/s | ns/frame |
|---|---:|---:|
| small replies (`+OK`/`:int`/`$5`) | 20.07 | **49.8** |
| GET commands (`*2 $3 $16`) | 7.05 | **141.9** |
| bulk-1k / bulk-16k | 12.1 / 12.3 | ~82 |
| mixed proxy shape | 11.18 | 89.4 |

Two shapes worth knowing before tuning: **the array-of-bulks COMMAND path costs ~3x a simple reply**
(141.9 vs 49.8 ns — element walking, not byte scanning; the bulk mixes confirm payload bytes are
length-skipped, never touched, hence bulk-16k's nominal 200 GB/s). And the budget arithmetic: at the
proxy's 4.4M ops/s across 6 shards, ~200 ns of scan per op is roughly **15% of a shard's budget — so a
2x scanner win is worth ~+7% at depth**, consistent with the "a few %" framing. At client-mode reply
rates the scanner is ~5% of one core: NOT a client bottleneck.

### THE TAIL, INVESTIGATED (2026-08-02 evening): it is an SMT TRADE plus a rig artifact, not a defect

The definitive run left "our p99 is ~3x Envoy's" open. Bisected in three steps, each pre-registered:

1. **GC: exonerated.** `DOTNET_gcServer=1` vs default, interleaved: p99 ranges overlap (0.42-0.56 vs
   0.46-0.50). Not the tail.
2. **A rig artifact owned a third of it: loop-thread OVERSUBSCRIPTION.** The "3.1x" run had `--shards 8`
   pinned onto 6 logical CPUs — two CPUs hosted two loop threads each, and their clients ate the queueing
   delay. 6-on-6 alone took p99 0.871 → ~0.50 ms. Rigs now guard against shards > proxy CPUs.
3. **The remainder is SMT sibling contention, and it is a genuine TRADE** (same session, 3 passes each):

| leg | req/s | p50 (ms) | p99 (ms) |
|---|---:|---:|---:|
| envoy (6 workers, same 3 physical cores) | 388,867 | 0.151 | **0.263-0.271** |
| l3, 6 shards (both SMT siblings) | **405,774** | **0.135** | 0.503-0.583 |
| l3, 3 shards (one per PHYSICAL core) | 301,062 | 0.199 | **0.279-0.335** |

One shard per physical core MATCHES Envoy's tail at −22% throughput; using both siblings buys +30%
throughput and the best p50 at ~2x the tail. **Pinning is exonerated too**: `--no-pin` at 6 shards leaves
p99 unmoved (0.56-0.61 vs 0.48-0.54, if anything worse), so it is the sibling contention itself, not the
inability to migrate off a busy core. For the SE.Redis client shape, where the tail IS the product, the
guidance is shards ≤ physical cores.

**The honest residual, recorded rather than hand-waved: Envoy runs 6 workers on the SAME 3 physical
cores and keeps its 0.27 ms p99.** Why its event loops tolerate sibling sharing and ours do not is
UNEXPLAINED — that is a profiling question (suspects: wait/wake shape under partial idleness, io_uring
submission timing under SMT), and no further config sweeps will answer it.

### FINAL (2026-08-02): ONE configuration — Envoy PARITY unpipelined, 2.7x Envoy pipelined, CONFIRMED

The capstone, 5 passes, pinned, quantisation audit flagging only ceiling-pegged cells. The configuration
is L3 (shard-affine upstream legs) + CALLBACK-GRANULARITY FLUSHING (stage during the receive callback,
one flush per registrant on the way out — the same event-loop-iteration batching Envoy does; both halves
of its pre-registration held: `-P 1` unmoved, depth recovered and then some):

| depth | test | envoy (req/s) | socketset L3+deferred (req/s) | verdict |
|---|---|---:|---:|---|
| `-P 1` | GET | 451,584 (444,416-466,636) | **451,584** (444,416-451,584) | identical medians — PARITY |
| `-P 1` | SET | 451,584 (437,445-458,986) | 437,473 (430,743-444,416) | overlapping — parity |
| `-P 16` | GET | 1,599,502 (1,599,289-1,608,435) | **4,430,496** (4,430,496-4,499,719) | **2.77x, DISJOINT** |
| `-P 16` | SET | 1,531,361 (1,515,183-1,539,547) | **4,055,881** (3,999,556-4,113,815) | **2.65x, DISJOINT — 83% of the NO-PROXY ceiling** |

L1 (the pipe bridge) for scale: 243,453 / 2,482,245 GET at the two depths. The full arc in one line:
**vs Envoy at `-P 1`, −48% → −22% → parity; at `-P 16`, +49% → −29% (the collapse) → +177%.**

Standing caveats, unchanged: the `-P 1` cells are CLIENT-LIMITED (both proxies at ~90% of the generator's
ceiling), so "at least parity" is the strongest supportable claim there and separation needs more client
capacity; Envoy 1.39.0 is RESP2-only as shipped (`protocol_version` is 1.40-track), so it never pays the
RESP3 prefix space `RespReader` scans; and the p99 TAIL question is still open — the depth win is
throughput-shaped, and a client library cares about the other end of the distribution.

### LEVEL 3 (shard-affine upstream legs): ENVOY PARITY at `-P 1` — and a pre-registered COLLAPSE at `-P 16` (2026-08-02, superseded by FINAL above)

One upstream leg PER SHARD, placed on that shard (`SocketSet.ConnectShard`, new), each client routed to
the leg sharing its loop thread (`SocketSetShard.CurrentShardIndex`, new; captured in `OnAccept`, which
runs ON the owning loop). Forward and reply never cross threads — the Envoy architecture on our
transport. The path here matters as much as the result: the PING-vs-GET discriminator put the whole
residual gap in the upstream leg (+96µs/req vs Envoy's +56µs), and **v1 (SocketSet upstream WITHOUT
affinity) was NET NEGATIVE** — it traded the parked-reader wake chain for a cross-shard marshal per page,
and the leg-count optimum flipping (2→5) was the tell. Affinity is the load-bearing property; v1 failing
without it is the evidence. Smoke matrix 60/60 after the shared-code additions; `verify-proxy.cs` 12/12
on the affine leg, first run.

**`-P 1`, pinned, 5 passes — the arc of the day in one column (GET):**

| leg | ops/s | % of ceiling |
|---|---:|---:|
| direct *(ceiling, NOT a peer)* | 499,929 | 100% |
| **envoy** | 451,584 (451,584-458,986) | 90% |
| **socketset L3 (affine)** | **451,613 (451,584-451,613)** | **90%** |
| socketset L2-u2 | 358,919 | 72% |
| worker-saea | 187,889 | 38% |

**Statistically identical to Envoy** (SET likewise: 444,444 vs 444,416), both legs pinned at 90% of a
CLIENT-LIMITED ceiling — so "at least parity" is the strongest claim this generator can support, and
separating them needs more client capacity. p50 confirms independently: ours 119µs vs Envoy 127µs. From
−48% at level 1, via −22% at level 2, to parity. The quantisation audit flags the pegged-at-ceiling cells
(direct, and the two 90% legs at 2 distinct values), which is exactly what pegging looks like.

**AND THE DEPTH REGIME INVERTED — L2/L3 were measured at `-P 16` for the FIRST time here, and they
collapse:** L3 1,124,965 / L2-u2 925,878 against **L1's 2,461,118** (and Envoy's 1,590,563). So the
regime split is now INTERNAL: **L1 wins depth, L3 wins latency, no configuration wins both yet.**
Mechanism (legible, and the same lesson as `inline-both` this morning): at depth, L1's pump tasks
parallelise parsing across the ThreadPool and coalesce replies per send; L2/L3 serialise
16-commands-per-read on 8 loop threads and `SendRawSynchronized` does stage+flush **per reply frame** —
16 flushes per receive callback. The fix is callback-granularity flushing (stage during `Feed`, one
`Flush` at callback end, same deferral on the leg's `_outBuffer`) — Envoy batches at event-loop-iteration
granularity for the same reason. Pre-registered in TODO: it should restore most of the depth loss and
move `-P 1` not at all; either failure falsifies the mechanism.

**RESP3/HELLO — RETRACTED AND RE-MEASURED (same day), and the retraction carries confounder #13.** Two
"measured" claims here were BOTH artifacts of one broken harness: (a) "our proxy forwards HELLO and
poisons the shared leg" — FALSE; (b) "Envoy swallows HELLO with no reply" — FALSE. The harness read
replies with `timeout N head -c BIG`: `head -c` blocks until it has ALL requested bytes, and when
`timeout` kills it first it exits WITHOUT printing the partial read — so every reply SHORTER than the
requested count printed as nothing, indistinguishable from silence. `+PONG` (7 bytes) against `head -c
40` "measured" as a swallowed reply. Compounding it, the original "poisoning" run also had a DEAD
backend (the preceding Envoy dead-upstream control had killed Garnet, and that test lacked a restart
guard). **Confounder #13: a read harness that cannot print a partial read converts every short reply
into "no reply". Use `timeout N cat`, which writes bytes as they arrive.** Diagnosed via controls (PING
and FOOBAR through the same harness also "vanished", which is what implicated the harness).

**What is actually true, re-measured with the fixed harness and locked by a new gate cell:**
- **Our proxy ALREADY intercepts HELLO locally** — it always did (`KnownCommands.Hello` → local error);
  answered `-ERR unknown command`, now improved to **`-NOPROTO unsupported protocol version`**, the
  protocol-correct refusal that clients treat as "RESP2 server, downgrade gracefully". Never forwarded;
  no leg poisoning; subsequent commands unaffected. `verify-proxy.cs` gains a `hello-local-error` cell
  (HELLO → leading `-`, then PING → `+PONG` on the same connection): 13/13 PASS.
- **Envoy 1.39 replies `-NOPROTO` too** (not silence). So both proxies refuse RESP3 identically today.
- **Still true and schema-validated: `protocol_version` (RESP2/RESP3 negotiation) is 1.40-track** — the
  field does not exist in 1.39.0 — and Envoy's RESP3 design is all-or-nothing per listener. The parse
  asymmetry also stands: `RespReader` scans the full RESP3 prefix space; Envoy 1.39 parses RESP2 only.
  Per-client RESP3 on a multiplexed leg remains unbuilt by anyone. A structural parse-cost tilt toward Envoy that is fair to us to state, since
real clients now default to RESP3. **And our proxy is WORSE than Envoy here today: it FORWARDS the HELLO,
poisoning the shared leg** — after one client's `HELLO 3`, a plain RESP2 GET on a different connection
gets no reply. Same bug class as the fixed SELECT issue; recorded in TODO as a correctness item (intercept
HELLO per-client, never forward).

### LEVEL 2 (framing on the loop thread): the Envoy deficit goes −48% → −22%, and it UNLOCKS a second lever (2026-08-02)

Level 2 replaces level 1's transport → `PipeIoBridge` → two `Pipe`s → `pipeReader.AsStream()` path with
`RespReader` framing directly off the span `OnReceive` hands you ON THE LOOP THREAD, replying via
`Connection.Send`. No pipe, no pump, no ThreadPool hop, no `Stream` wrapper. Built in
`toys/RESPite.Proxy/SocketSetProxyClient.cs`; **12/12 on `verify-proxy.cs` first run**, including 1 MB
values spanning many receive callbacks, a 512-deep in-order pipeline, and the mixed local/forwarded
ordering cell. It came together because `RespStream` already exposes a PUSH seam
(`GetReceiveBuffer()`/`OnAfterReceive()`) that `SocketProxyClient` drives from SAEA completions — so this
is a transport adapter, not a new parser, and all partial-frame/CycleBuffer handling is reused.

`-P 1`, pinned, 5 passes, quantisation audit clean, same session:

| leg | GET (req/s) | % of ceiling | vs Envoy |
|---|---:|---:|---|
| direct *(ceiling, NOT a peer)* | 509,017 | 100% | — |
| **envoy** | 451,613 | 89% | — |
| socketset L1 (pipe bridge) | 239,292 | 47% | **−47.0%** |
| socketset **L2** | 269,200 | 53% | −40.4% |
| socketset **L2 + 2 upstream legs** | **349,965** | **69%** | **−22.5%** |

SET tracks it: L2-u2 354,412 vs Envoy 451,584, **−21.5%**, from −48.3% at L1.

**MY PREDICTION WAS WRONG AND THE CORRECTION IS THE INTERESTING PART.** I expected the pipe bridge to BE
the per-request cost we lose to Envoy on. It is not: removing pipes, pump, hop and stream wrapper entirely
buys **+12.5%**, against a ~2x gap. The larger lever was somewhere nobody had swept — the number of sticky
UPSTREAM legs, where the intuition is backwards: more legs is monotonically WORSE (level 2, unpinned:
1→333k, 2→375k, 3→353k, 5→316k, 16→207k, 32→167k, 64→143k), because fewer legs means more client commands
coalesce into each upstream write. Batching, not parallelism.

**AND THE TWO ARE NOT INDEPENDENT — which is the actual finding.** Measured directly, `upstream=2` against
the default `upstream=5`:

| leg | upstream=5 (GET req/s) | upstream=2 (GET req/s) | effect |
|---|---:|---:|---|
| worker-saea | 195,771 | 197,155 | **none** — ranges overlap |
| socketset L1 | 239,292 | 241,368 | **none** — ranges overlap |
| socketset **L2** | 269,200 | **349,965** | **+30%** |

**The upstream lever is worth NOTHING on level 1 or on the hand-rolled SAEA path, and +30% on level 2.**
So it is not a free config default that any implementation collects — it is a SECOND bottleneck that only
becomes reachable once the pipe bridge stops being the first. The full +46% over level 1 REQUIRES level 2;
the config change alone is inert. That also means the transport work owns the win rather than sharing it
with a knob — the opposite of what was predicted an hour earlier, and it is why the decomposition was run
instead of quoting the combined number.

**Still −22% to Envoy at `-P 1`, and we remain ~+50% ahead at `-P 16`.** The remaining `-P 1` gap is
unexplained and is the open question; note also that Envoy sits at 89% of a CLIENT-LIMITED ceiling there,
so its true figure may be higher and −22% is a floor. Give the generator more cores before chasing it.

### RESP PROXY vs ENVOY: we LOSE 2x unpipelined and WIN 1.5x pipelined — a crossover, pre-registered (2026-08-02)

**Read this before the SAEA comparison below it.** That section reports beating our own hand-rolled
transport by 15-28%, which is true and was measured against the wrong bar. Envoy is the bar that travels,
and against Envoy the answer depends entirely on pipelining depth — which the rig header pre-registered as
"a win at one depth can be a loss at the other; that would be a finding, not noise". It is the finding.

Envoy 1.39.0 (static binary, `bench/envoy-redis.yaml`, `redis_proxy` filter, catch-all route, MAGLEV,
`--concurrency` = the same logical-CPU count our proxy gets, pinned to the same cores). Same generator,
same backend, same 5 passes, same reshuffled leg order. **Envoy passes the identical 12-cell
`verify-proxy.cs` gate**, including 1 MB values and the interleaved local/forwarded pipeline, so this is
like-for-like rather than a comparison against something doing less work.

| depth | test | envoy (req/s) | socketset/io_uring (req/s) | socketset/epoll (req/s) | worker-saea (req/s) |
|---|---|---:|---:|---:|---:|
| `-P 1` | GET | **451,613** (90% of ceiling) | 231,374 **−48.8%** | 227,620 −49.6% | 195,771 −56.7% |
| `-P 1` | SET | **451,584** (92%) | 227,613 **−49.6%** | 223,971 −50.4% | 202,869 −55.1% |
| `-P 16` | GET | 1,635,732 (23%) | 2,419,762 **+47.9%** | **2,440,182 +49.2%** | 1,932,419 +18.1% |
| `-P 16` | SET | 1,523,262 (33%) | 2,303,558 **+51.2%** | 2,303,632 +51.2% | 1,985,659 +30.4% |

**THE MECHANISM IS LEGIBLE FROM THE "% OF CEILING" COLUMN, which is why that column exists.** At `-P 1`
every command is its own round trip, so PER-REQUEST cost dominates: Envoy runs at 90-92% of the no-proxy
ceiling; we run at 46%. At `-P 16` many commands arrive per read, so PER-BYTE / parse throughput dominates:
Envoy falls to 23-33% of ceiling while we reach 34-50%. **So our per-request overhead is poor and our
parsing throughput is good** — exactly the signature of LEVEL-1's two `Pipe`s plus thread hops, a fixed
cost per request that amortises away under depth. Note the hand-rolled SAEA path ALSO beats Envoy at depth
(+18-30%), so the pipelined win is a property of the .NET path generally, not of SocketSet specifically;
the unpipelined loss is likewise shared (worker is −55 to −57%).

**This makes the level-2 client (RespReader inline in `OnReceive`, no pipe, no pump, no hop) aimed at a
MEASURED defect rather than at a hypothesis.** It targets precisely the per-request cost that `-P 1`
isolates. Whether it closes a 2x gap is open, and should be measured rather than assumed.

**Caveat, and it runs in our disfavour: at `-P 1` Envoy sits at 90% of a CLIENT-LIMITED ceiling** (the
generator is pinned to a third of the box and tops out near 500k), so Envoy's true unpipelined capability
may be higher and the gap is a FLOOR, not an estimate. At `-P 16` Envoy is at 23% of ceiling — genuinely
proxy-limited — so that half of the table is clean. Fixing the `-P 1` half needs more client capacity, and
until then no upper bound on Envoy should be quoted from it.

**Rig bug found here, worth recording because it made the audit lie:** the quantisation audit passed a
single-pass cell as "clean" (1 distinct value > half of 1 pass), i.e. it certified a cell that resolved
nothing. Now requires n>=3 before a range can be claimed at all. Separately, a patch inserting an
apostrophe (`rig's`) into the single-quoted awk block silently TRUNCATED the program — the summary died
while the measurements themselves completed, so the data survived and only the report was lost.

### RESP PROXY: SocketSet beats a hand-tuned SAEA WorkerPool by 15-28% on a real workload (2026-08-02)

**This is the first number in this file measured with the APPLICATION HELD CONSTANT and the transport as
the only variable**, and it is therefore the first one that is straightforwardly about the transport.
Every other table here is scored through Kestrel, whose bridge costs 24-40% and whose "control" leg is a
different application path, so bridge cost and transport cost never separate.

Rig: `bench/run-proxy-ab.sh`. The proxy is `toys/RESPite.Proxy` on `StackExchange/StackExchange.Redis`
branch `marc/proxy-spike2`, hosted either on its own hand-rolled `WorkerPool`/`WorkerSocketAsyncEventArgs`
layer or on SocketSet via the pre-existing `RunClientAsync(IDuplexPipe)` seam — `ProxyClient` and all RESP
framing/routing are byte-for-byte identical between legs. Backend `garnet-server` 2.1.1, generator
`redis-benchmark` 7.4.2, `-c 64 -d 32`, governor `performance`, THREE-way physical-core split (client /
proxy / backend each get their own cores and both SMT siblings). 5 passes, leg order reshuffled per pass.

| depth | test | worker-saea, peer baseline (req/s) | socketset/io_uring (req/s) | socketset/epoll (req/s) |
|---|---|---:|---:|---:|
| `-P 1` | GET | 194,412 (187,889-204,350) | **241,363 (239,292-245,597) +24.2%** | 235,270 (235,255-237,264) +21.0% |
| `-P 1` | SET | 201,410 (199,971-202,869) | **233,302 (231,374-235,262) +15.8%** | 231,374 (229,486-231,382) +14.9% |
| `-P 16` | GET | 1,932,471 (1,919,488-1,945,420) | 2,461,118 (2,440,264-2,482,416) +27.4% | **2,482,416 (2,461,118-2,504,000) +28.5%** |
| `-P 16` | SET | 1,985,769 (1,985,604-1,999,556) | **2,360,269 (2,341,083-2,360,269) +18.9%** | 2,341,083 (2,322,131-2,379,850) +17.9% |

**All eight comparisons are DISJOINT.** And the win is understated, because this is the **LEVEL-1**
integration: it bridges through two `Pipe`s — the same shape that costs 24-40% in the ASP.NET bridge — and
uses the default (unpinned) pool. The level-2 client (`RespReader` inline in `OnReceive`, no pipe, no
hop) is the actual design SocketSet was shaped for and is not built yet.

**io_uring and epoll are effectively tied** (each leads two of four cells, ranges overlapping), which is
what a pipe-bridge-dominated path should look like: at level 1 the bridge, not the backend, is the cost.

**The controls that make this readable.** `direct` (generator straight to Garnet, no proxy) is a CEILING
REFERENCE, not a peer — one fewer process, one fewer hop. Its job is to prove the backend has headroom,
and it does: proxy legs sit at **27-49% of it**, so the backend never limited anything. Had a proxy leg
approached it, every column would have been measuring Garnet. Correctness first, too:
`bench/verify-proxy.cs` is 12/12 on all three legs plus the direct control — byte-exact 1 B to 1 MB, a
512-deep pipeline verified IN ORDER, local/forwarded commands interleaved in one burst, and 32 concurrent
clients.

**A RIG DEFECT THAT FAKED PRECISION, and the guard now in place — eleventh in this file's tradition.**
`redis-benchmark` resolves elapsed time to ~250 ms. On a 6.5 s test that is a 3.8% quantum, so all six
passes snapped to 2-3 distinct rps values and the min-max range came out TIGHT — which reads as
reproducibility and is exactly the opposite: the rig could not see variation smaller than one tick. The
implied elapsed times landed on 3.000 / 6.250 / 6.500 / 7.250 / 7.500 s, which is what gave it away. Fixed
by running each test ~30 s (`-n` 7M at `-P 1`, 72M at `-P 16`), and the rig now performs a **quantisation
audit**: it counts distinct values per cell and warns that a range is timer-quantised, and that no
DISJOINT verdict from it is evidence, whenever a cell resolves half or fewer of its passes distinctly. In
the table above the audit flags ONLY the `direct` ceiling cells (fastest legs, shortest tests); every
compared cell resolves cleanly. **The first, discarded run of this A/B reported the same +15-20% direction
from ranges that were pure quantisation** — right answer, worthless evidence.

**Two bugs in the integration, found by the rig rather than by the tests, both fixed and re-verified
(12/12 both backends after).** (a) `OnClosed` was not overridden, so a disconnected peer never had its
inbound writer completed: `PipeIoBridge` does NOT do this for you — the ASP.NET bridge completes it
explicitly — so the proxy's read loop never terminated and **every closed connection leaked a task and two
pipes**. A keep-alive benchmark cannot surface that; connection churn would. (b) A protocol fault left the
socket OPEN, so a malformed request presented as a client HANG rather than an error. Both now tear down,
with abortive `Close()` on the fault path ONLY — matching the bridge's contract that `Close()` cancels
queued sends and must not be used on a normal exit.

**The table above was measured on the PRE-fix binary, so the fixes were re-measured rather than argued
throughput-neutral.** The reasoning said neither path executes under steady-state load (no faults, no
disconnects until teardown) — true, but reasoning is not measurement. Post-fix spot-check, `-P 1`,
io_uring: **GET 241,354 vs 241,363; SET 233,310 vs 233,302** — within 0.01% of the definitive run, so the
table stands. (`worker` read 186,637 vs 194,412 on GET, but its ranges overlap across the two runs and it
is consistently the noisier leg.) Post-fix correctness re-verified 12/12 on both backends, and the inline
`PING` probe now exits with an error instead of timing out.

**Found on the way, and it is a real compatibility gap rather than a rig problem: the proxy does not
accept INLINE commands.** `redis-benchmark -t ping` runs PING_INLINE first, sending literal `PING\r\n`
rather than a RESP array; `RespReader` rejects the `'P'`. Redis and Garnet both accept inline. Worth
knowing because stock `redis-benchmark` defaults hit it, and because it is what health checks and
telnet-style debugging use. The rig now probes with `ping_mbulk`.

### The flush fix, VERIFIED ON LINUX (2026-08-02) — and it reaches ONE Linux backend, not three

The `PooledBufferWriter` high-water fix (`a264998`) was measured on Windows/IOCP and landed as shared
code, so the Linux READ-FIRST carried a pre-registered expectation for this run. Rig: the new
`bench/compare-commits.sh` (the Linux port of `Compare-Commits.ps1`), isolated worktrees, interleaved with
the leading side alternating per pass, 7 passes with pass 1 discarded, min-max ranges.

**Governor: `performance` (both `scaling_governor` and `energy_performance_preference`), for every leg.**
That differs from the 2026-08-01 Linux headline table above, which was measured under `powersave` — so
**do not compare absolute MiB/s between this section and that one.** The A/B is self-contained: both sides
of every comparison ran in one session under one power state, which is the only claim being made.

`a264998` is a four-piece squash, so it was checked to isolate cleanly here first: with `SS_PIPE_SCHED`
unset both bridge changes are inert (both schedulers resolve to the same instance, banner suffix empty)
and the owned-staging piece is IOCP-gated. On Linux the only live delta is the writer fix.

**EPOLL, `--classic --tls` — the prediction held in full:**

| payload | before (MiB/s) | after (MiB/s) | change | verdict |
|---|---:|---:|---:|---|
| 16 KB | 5947.1 (5914-6067) | 6065.4 (6015-6106) | +2.0% | *overlapping* |
| 256 KB | 4132.6 (4094-4155) | 4851.4 (4694-5013) | **+17.4%** | **DISJOINT** |
| 1 MB | 2319.8 (2316-2322) | 3012.9 (2995-3026) | **+29.9%** | **DISJOINT** |

**EPOLL, plaintext `--byo` — the control, also as predicted:** nothing at any size (16 KB −1.0%,
256 KB +0.4%, 1 MB −8.6%, all overlapping). Zero-copy send skips `Flush`, so there is no re-growth to pay.
The 1 MB cell is the noisiest on the board (before 6622-7293) and its −8.6% is noise, not a regression.

So the mechanism reproduces cross-platform: the win grows with payload, is confined to the out-of-band
path, and vanishes at 16 KB. Windows/IOCP measured +18.8% at 256 KB and +58.6% at 1 MB; **256 KB agrees
almost exactly, 1 MB is about half the Windows figure.** Shape is what is being compared here, not
magnitude — these are different OSes and different send paths, and this file forbids subtracting across
them.

**THE CORRECTION, and it is the more transferable half.** `TODO.md`'s Linux READ-FIRST said "**io_uring,
epoll and managed all reach it**". Only **epoll** does. `OutboundConnection` — the class holding the fixed
writer — is derived from by `WindowsConnection` (IOCP/RIO) and `EpollConnection` only; `IoUringConnection`
and `ManagedConnection` derive from `Connection` directly and have their own send paths. `TakeArray` has
exactly two call sites (`OutboundConnection.Flush` and `WindowsShardBase`), so io_uring's TLS writers are
reusable scratch that never detach, never re-rent from empty, and **never paid the pessimisation at all**.

That was not free: a full interleaved run on `BACKEND=io-uring` returned a clean, tight, entirely
meaningless null (`--classic --tls` at 1 MB: 1866.8 → 1874.2, +0.4%, ranges ~5% wide — far too tight to
hide a 58% effect). It reads exactly like "the fix does nothing on Linux". **The identical-binary guard
cannot catch this**: the binaries genuinely differ, it is REACHABILITY that does not hold. Rule 2 —
confirm the path was TAKEN — has to be applied to the *backend*, not just the flag. The rig header now
says so. The one useful thing that run does establish is that io_uring did not regress underneath the
Windows fix.

**A rig defect found the same way, making TEN in this file's tradition.** Three measurements died as
`NOSTART`, all on the `before` side, from `bind()` errno 98 `EADDRINUSE`. Two independent causes, both
mine and both inherited from porting: `PORT_BASE=41000` was carried over from `Compare-Commits.ps1`, and
41000 sits INSIDE Linux's `ip_local_port_range` (32768-60999) where the load generator's own sockets can
hold it — on Windows the same constant is safe, since the dynamic range there starts at 49152. And a
*fixed* base makes back-to-back legs reuse ports still in `TIME_WAIT` from 64 keep-alive connections. A
dropped measurement is not neutral: it silently lowers the scored-pass count for one cell on one side.
Fixed by moving the base below the ephemeral range, randomising it per run, warning on overlap, and
retrying once on a clear port. **The results above survive it** — every cell kept 5-6 scored passes
against a floor of 3, and a NOSTART yields no number, so it cannot shift the values that were recorded.

### The read-side thread hop on Linux/io_uring: CONFIRMED per-request, and it retires two other claims (2026-08-02)

`bench/run-pipesched.sh` (new; the Linux port of `Run-PipeSched.ps1`), io_uring, `--byo`, 12 shards, c64,
6 scored passes, mode order reshuffled every pass, **vanilla Kestrel control in the same passes**,
governor=`performance`. Gate: `off` is refused unless the banner LACKS `pipesched=`, so a leaked env var
cannot silently make `off` be `inline-both`.

| payload | `off` vs kestrel | `inline-read` vs kestrel | `inline` vs kestrel | `inline-both` vs kestrel |
|---|---|---|---|---|
| 512 B | −5.5% *disjoint* | **−1.2% — PARITY** | −3.8% *disjoint* | −9.3% *disjoint* |
| 4 KB | −3.5% *disjoint* | −2.8% overlapping | −1.8% *disjoint* | −7.7% *disjoint* |
| 16 KB | −5.5% *disjoint* | −7.0% *disjoint* | −3.2% *disjoint* | −8.2% *disjoint* |
| 256 KB | **+1.8% AHEAD** *disjoint* | +1.7% parity | +2.4% parity | +1.6% ahead *disjoint* |

**THE PRE-REGISTERED PREDICTION HELD.** The read hop is a per-REQUEST cost, so the gain had to be largest
where request rate is highest and had to vanish at large payloads; growth WITH payload would have
falsified it. `inline-read`'s gain over `off` runs **+4.5% → +0.7% → −1.5% → −0.1%** across
512 B / 4 KB / 16 KB / 256 KB — monotonic to nothing. At 512 B it converts a disjoint −5.5% deficit into
statistical parity with Kestrel. **This replicates the Windows finding on a different OS and a different
backend**, which is much stronger than either result alone: the small-payload deficit to Kestrel is the
read-side thread hop, and the case for an inbound half-pipe now rests on two independent measurements.

Calibrate it honestly: the Linux effect is NARROWER than the Windows one. Windows reached parity at 512 B
AND 4 KB and went disjointly AHEAD at 16 KB; Linux/io_uring reaches parity at 512 B only, and 4 KB
"overlaps" on a wide range (2366-2559) rather than convincingly. The ceiling here is ~4% at the smallest
payload, not 2-4% across the small range.

**RETRACTION CANDIDATE: the recorded `inline` −28% does not reproduce at any size.** That figure
(2026-07-31) reads in `TODO.md` as having "killed the pump-hop hypothesis" and is load-bearing for
treating the OUTBOUND hop as settled. Measured now, `inline` is **positive at every payload** — +1.8% /
+1.7% / +2.5% / +0.6% over `off`, disjoint at 16 KB. The conditions differed (pre-flush-fix, and before
the pinned-pool default landed), so this does not prove the old number wrong — but **it cannot be quoted
as current**, and "the outbound hop costs you" is now the better-supported reading on today's defaults.

**NEW, and it constrains the half-pipe design: `inline-both` is worse than EITHER knob alone**, at every
size — −4.0% / −4.4% / −2.8% / −0.2% against `off`, disjoint at three of four. Two individually-positive
changes composing into a net loss is an interaction, not noise: both readers resume on the io_uring loop
thread and serialise against each other. So "move work off the ThreadPool" is not monotonically good; the
loop thread is a shared resource with its own contention, and a design that inlines both directions is
worse than one that inlines neither.

**Also worth recording: at 256 KB we are DISJOINTLY AHEAD of Kestrel** (`off` 12,831-13,013 vs Kestrel
12,237-12,796) and the scheduler knob does nothing there — every mode is within noise of every other. The
knob's entire effect lives at small payloads, which is the same null the Windows run reported at 256 KB.

#### THEN EPOLL FALSIFIED THE GENERALISATION (same rig, same session, same day)

The io_uring result above was written up as "the small-payload deficit IS the read hop, replicated on a
second OS". **Running the identical rig on epoll shows that is a property of io_uring, not of the bridge.**
`inline-read`'s gain over `off`, both backends:

| payload | io_uring | epoll |
|---|---:|---:|
| 512 B | **+4.5%** (reaches PARITY with Kestrel) | +0.9% (median stays −3.9% behind) |
| 4 KB | +0.7% | −1.4% |
| 16 KB | −1.5% | **−11.5%** |
| 256 KB | −0.1% | +0.9% |

**On epoll the read hop is NOT the small-payload deficit.** `off` trails Kestrel −4.8% at 512 B and no
scheduler mode recovers it; at 16 KB inlining the inbound reader is a −11.5% catastrophe (6,278-6,676 vs
6,984-7,515, disjoint). The epoll 512 B "overlapping — parity" verdict is an artifact of a WIDE range
(339.1-355.7), not of approaching the control, which is exactly why this rig prints ranges next to the
verdict rather than the verdict alone.

**So the honest scope of the finding is: the read-side hop owns the small-payload deficit on
Windows/IOCP and on io_uring — the two backends whose completion model is "the kernel hands you finished
work" — and does NOT own it on epoll, whose loop is readiness-driven and must do the `recv` itself.** That
is a plausible mechanism rather than a proven one, and it is written here as a hypothesis, not a result.

**What this does to the inbound half-pipe:** the case SURVIVES but narrows. io_uring is the auto-detected
default on Linux, so the ~4.5% at 512 B is on the default path — but it is one backend at one payload
size, not a general bridge win, and epoll would gain nothing. Anyone costing that work should use ~4% at
the smallest payload on io_uring only.

**And the OTHER unexplained epoll result now has company.** epoll pays a 40.3% bridge cost at 256 KB
against io_uring's 23.9%, at equal copy counts, while being the FASTER backend bare (12,971 vs 10,352).
That has been unexplained since 2026-07-31. Now a second, independent measurement says epoll pays
something at the bridge boundary that io_uring does not, and neither is copies and neither is scheduling.
The remaining named suspect is the per-flush marshalling/wake shape (epoll's `SubmitFlush` enqueues a
`byte[]` and pokes an eventfd; io_uring enqueues an `OutChain` and pokes an eventfd). **Two independent
symptoms with one unexplained cause is the most concrete open question on the Linux bridge path.**

Consistent across BOTH backends, and therefore the one safe generalisation here: **`inline-both` is worse
than either knob alone at every size on both** (io_uring −4.0/−4.4/−2.8/−0.2%; epoll −7.3/−6.5/−8.7/+0.6%
against `off`), and **`inline` (outbound) is mildly POSITIVE at every size on both** (io_uring
+1.8/+1.7/+2.5/+0.6%; epoll +0.8/+1.9/+0.6/+1.1%) — so the recorded −28% for `inline` fails to reproduce
on two backends, not one.

### Stability soak (2026-08-01, before switching OS)

`bench/run-smoke-matrix.sh` with `CHURN_REPS=15` (vs the usual 5): **60/60 cells PASS, every churn cell
15/15 clean** across io_uring / epoll / managed, plaintext and TLS. Heavy slot-reuse / teardown stress
surfaced no intermittent lifetime fault (the item-0e shape). So the Linux backends are handed off clean.

### Concurrency: we lead at c64 but degrade MORE than Kestrel above it (2026-08-01, 256 KB plaintext, 3 passes, ranges)

| c | kestrel (MiB/s) | io_uring (MiB/s) | epoll (MiB/s) |
|---|---:|---:|---:|
| 64 | 12,416-12,569 | 12,487-12,888 | 12,653-12,822 |
| 128 | 9,941-10,111 | 9,511-9,686 | 9,684-9,798 |
| 256 | 7,440-7,506 | 7,212-7,277 | 7,355-7,413 |

At **c64 we lead** (epoll disjoint above Kestrel, io_uring ≈ parity/ahead). By **c128 Kestrel pulls ahead
of both, DISJOINT (+2-6%)**, and holds a narrower disjoint lead at c256. So the crossover is between c64
and c128, and the gap is real, not noise. Sub-finding: **epoll degrades LESS than io_uring under
concurrency** (epoll ≥ io_uring at both c128 and c256, disjoint) — plausibly io_uring's single-issuer ring
contends worse than epoll's readiness loop when many connections' sends pile up. This is the concrete
motivation for the "two half-pipes" work (`TODO.md`): the leading suspect is the per-connection `Task.Run`
pump contending on the ThreadPool as connections multiply. Everyone still drops sharply c64→c256 because
loopback itself saturates — so c64 is the sweet spot and these higher-c numbers are about *relative*
scaling, not peak throughput.

### Windows catch-up after the OS switch back (2026-08-01) — correctness only, no new throughput numbers

Windows last ran 2026-07-29 and shared code changed underneath it across two Linux sessions. This is the
catch-up, and it is deliberately all correctness: **nothing in this section is a throughput measurement**,
so nothing here should be compared with, or added to, any table above it. (The throughput re-baseline that
followed it later the same day is the section immediately above — "Headline numbers, Windows — DEFINITIVE
(2026-08-01)".)

| gate | result |
|---|---|
| `bench/Run-SmokeMatrix.ps1` (transport; 48 cells, IOCP/RIO/managed x plaintext/SChannel) | **48/48 PASS** |
| `bench/Verify-AspNet.ps1` (bridge, NEW; 18 cells, backend x mode x TLS) on main | **18/18 PASS** |
| ...the same rig on `package-aspnetcore-lib` | **18/18 PASS, and IDENTICAL to main on every cell** |
| `bench/Verify-TlsFloor.ps1` (SChannel min-protocol, NEW; 12 cells) | **12/12 PASS** |

Four things worth carrying forward:

- **The shared-code changes are clean on Windows.** The stale-completion detectors, dynamic shard growth
  and the geometry sentinel all landed without moving a Windows backend. In particular
  `rio+tls/verify-oob-4m` passes in **0.3s** where it was a 15.2s FAILURE before the geometry fix (item 0d),
  and `rio+tls/churn` is **5/5 clean** — the item-0e access violation that used to strike about one run in
  two did not appear.
- **The geometry sentinel resolves per-backend on Windows, and says so.** RIO reports
  `page=65536 recvbuf=4096 writebufs=256 oobwritebufs=64 readpages=64`; IOCP reports
  `page=4096 ... writebufs=1024`. Distinct, and **no `0` anywhere**, so no read site missed the
  "backend chooses" sentinel. This is the Windows half of the check Linux did on 2026-07-31.
- **`--half-pipe` is byte-exact on IOCP and RIO**, plaintext and TLS, at every size to 8MB. It was merged
  to main off-by-default with only the *argument* that it "uses only cross-platform `Connection.Send`, so it
  SHOULD work". It does. That claim is now measured rather than reasoned — but note it is a CORRECTNESS
  result only; the half-pipe's throughput crossover is measured on Linux (§ "The size crossover") and has
  NOT been measured on Windows.
- **The library extraction is behaviour-preserving, measured rather than inspected.** Running the identical
  18-cell rig on main and on the branch gave zero differences in Result *or* Detail (accepts, sendFalse,
  full resolved-geometry string). A refactor that worked but shifted the geometry or moved a counter would
  pass a one-sided "does it still work" check and fail this one.

**A rig bug is recorded here because it produced a clean-looking wrong answer, which is what this file is
for.** The first `Verify-AspNet.ps1` run FAILED every TLS cell with "no /config after 20s" while the server
logged a successful bind — it looked exactly like a broken SChannel provider. The cause was entirely in the
harness: a PowerShell **scriptblock** assigned to `ServerCertificateCustomValidationCallback` throws "There
is no Runspace available to run scripts in this thread" when the handshake runs on a TLS worker thread, and
the resulting `HttpRequestException` presents as a TLS failure. Use the framework's static
`DangerousAcceptAnyServerCertificateValidator`. That makes **nine** confounders in `bench/README.md`'s
tradition, and the general lesson is the familiar one: the harness is a suspect too, and a timeout is not a
diagnosis — the rig now reports the last connect exception instead of the bare timeout.

### Headline numbers, Windows — CURRENT (2026-08-01 evening, POST `PooledBufferWriter` fix, `bench/Run-TlsSizes.ps1`, 12 shards, `-c 64`, 6 scored passes, ONE session)

8 legs reshuffled into the same passes, zero errors. Goodput MiB/s, **min-max of 6 scored passes**; a delta
is quoted only where ranges are DISJOINT. **This replaces the morning table below**, which was measured
before the flush fix.

| payload | kestrel | iocp/s12 | rio/s12 | httpsys | kestrel+tls | iocp+tls/s12 | rio+tls/s12 | httpsys+tls |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 512 B | 144.5-145.9 | 137.2-141.7 | 142.9-144.8 | 118.8-120.7 | 134.6-136.6 | 128.7-136.7 | **137.7-139.9** | 99.8-100.9 |
| 16 KB | 3949-4005 | 3759-3880 | 3849-3941 | 2739-2817 | 3111-3168 | **3314-3385** | **3541-3568** | 2188-2250 |
| 256 KB | 10028-11479 | 10550-11324 | 7762-8268 | **11464-11845** | 6557-6836 | 4407-4753 | 4707-5811 | 10168-10402 |
| 1 MB | 5864-6140 | 5961-6090 | 3909-4224 | **12522-12785** | 4264-4525 | 3686-3744 | 2988-3374 | 7210-7541 |

**THE HEADLINE CHANGE: we now BEAT vanilla Kestrel on TLS at 16 KB, on both backends, disjoint.**

| payload | vs `kestrel+tls` | was (morning) |
|---|---|---|
| 512 B | `iocp+tls` overlapping — parity; **`rio+tls` +2.5% ahead**, disjoint | parity |
| 16 KB | **`iocp+tls` +6.6% AHEAD**, **`rio+tls` +13.7% AHEAD**, both disjoint | overlapping |
| 256 KB | Kestrel ahead: +46% vs iocp, +21% vs rio | Kestrel **+83%** |
| 1 MB | Kestrel ahead: **+19%** vs iocp | Kestrel **+100%** |

The large-payload TLS deficit is **halved at 256 KB and cut from +100% to +19% at 1 MB**. It is still a
real structural loss (encrypted wire bytes cannot use zero-copy send) but it is no longer the collapse the
morning table described.

**Cross-session comparison is legitimate here, and only because the controls say so.** The legs our code
cannot touch — `kestrel`, `kestrel+tls`, `httpsys`, `httpsys+tls` — reproduce across the two sessions to
within **1.6%** on every one of 12 cells (most under 1%). So the movement in our legs is attributable to
the change rather than to the session. Absent that agreement this comparison would be exactly the
cross-run subtraction this file forbids elsewhere.

**The mechanism confirmed itself through WHICH legs moved**, which is stronger than the sizes of the
moves:

- **RIO plaintext gained a lot** (256 KB 6772 → 7897, 1 MB 3236 → 4102) — RIO has no zero-copy send, so
  *all* its traffic goes through `Flush` and paid the re-growth.
- **IOCP plaintext did not move** (16 KB and 1 MB medians within 1%) — plaintext BYO uses zero-copy send
  and skips `Flush` entirely. This was pre-registered and held.
- **Every TLS leg moved**, on both backends — all TLS output is out-of-band.

So the fix helped precisely the legs that use the fixed path and left alone precisely those that do not.

#### TODO item 7 (`rio+tls` beats `iocp+tls`) — RESOLVED, and the answer is "it depends on size now"

| payload | `rio+tls` (MiB/s) | `iocp+tls` (MiB/s) | verdict |
|---|---:|---:|---|
| 16 KB | 3541-3568 | 3314-3385 | **RIO still ahead, +6.6% disjoint** |
| 256 KB | 4707-5811 | 4407-4753 | *overlapping* — **the gap is GONE** |
| 1 MB | 2988-3374 | 3686-3744 | **INVERTED — IOCP now ahead +14%, disjoint** |

The anomaly was largest at large payloads and that is exactly where the flush fix helped IOCP most, so
most of it was this bug rather than a property of the two send paths. **What survives is a ~6.6% RIO lead
at 16 KB only** — much smaller, and no longer the top Windows item. Anyone resuming the "why does RIO's
TLS send beat IOCP's" investigation should note that three of its four data points have evaporated.



### Headline numbers, Windows — SUPERSEDED morning table (2026-08-01, pre-flush-fix)

> ⚠ **SUPERSEDED by the CURRENT table above. Kept because it is still the record of the http.sys
> crossover and of the plaintext-parity result, both of which the evening run reproduces.
> STALE FOR OUR TLS LEGS. Measured hours BEFORE the `PooledBufferWriter` fix
> later the same day**, which is disjointly worth **+18.8% at 256 KB and +58.6% at 1 MB on TLS** and
> **+33.3% on `--classic` plaintext at 1 MB**. So every `iocp+tls` / `rio+tls` figure below UNDERSTATES
> current `main`, and the plaintext `iocp`/`rio` rows are unaffected only because BYO plaintext uses
> zero-copy send and skips the fixed path. The `kestrel*` and `httpsys*` columns are untouched by our
> code and remain valid.
> **Consequences:** (a) the "our TLS collapses at large payloads" finding below is still directionally
> true but its MAGNITUDES are wrong; (b) finding 4 (`rio+tls` beats `iocp+tls`) is unresolved — both legs
> move; (c) **re-running this sweep is the top documentation item.** Do not quote a TLS delta from this
> table until it is re-run.

7900X, IOCP/RIO/Kestrel/**http.sys**, ASP.NET bridged path, `/payload` GET, keep-alive. Goodput MiB/s,
**min-max of 6 scored passes** (pass 1 discarded as host warm-up), zero errors in all 224 runs. All eight
legs are reshuffled into the same passes, so **every comparison here is within-session**. A delta is only
quoted where the ranges are DISJOINT. This SUPERSEDES the 2026-07-30 Windows tables below, which predate
two default flips (BYO became the default bridge, and the pipe pool became pinned) — their
"classic *(default)*" column header is simply wrong now.

| payload | kestrel | iocp/s12 | rio/s12 | **httpsys** | kestrel+tls | iocp+tls/s12 | rio+tls/s12 | **httpsys+tls** |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 512 B | 140.8-146.0 | 135.2-142.3 | 143.3-144.7 | 117.3-119.3 | 132.3-136.0 | 130.3-136.3 | 138.1-139.7 | 99.3-101.1 |
| 16 KB | 3962-4008 | 3837-3898 | 3809-3909 | 2724-2794 | 3056-3148 | 3103-3292 | 3462-3515 | 2204-2240 |
| 256 KB | 10487-10934 | 9683-10819 | 6496-7055 | **11629-11875** | 6626-6906 | 3531-3828 | 5055-5237 | **10144-10591** |
| 1 MB | 5727-6151 | 6040-6130 | 2985-3295 | **12742-13223** | 4446-4625 | 2260-2316 | 2575-2712 | **7269-7523** |

#### 1. http.sys does NOT dominate — it CROSSES OVER, and the pre-registered prediction is half falsified

The prediction going in (the repo owner's, stated before the run) was that http.sys would "kick us into
orbit". It does, at large payloads, and it is **last** at small ones. Both halves are disjoint:

| payload | http.sys vs the best user-mode plaintext leg |
|---|---|
| 512 B | **−18%** (117-119 vs kestrel 141-146) — http.sys LAST of all 8 legs |
| 16 KB | **−30%** (2724-2794 vs kestrel 3962-4008) — http.sys LAST of all 8 legs |
| 256 KB | **+9%** (11629-11875 vs kestrel 10487-10934) — http.sys FIRST |
| 1 MB | **+112%** (12742-13223 vs iocp 6040-6130) — http.sys FIRST, by more than 2x |

So "the kernel stack is always faster" is false here, and so is "we are competitive with the kernel at
large payloads". **The honest reading is that this workload flatters user-mode at small messages and the
kernel at large ones**, and the mechanism is visible in the p99 column: at 1 MB http.sys runs p99
**11.8 ms** against IOCP's 36.1 ms and Kestrel's 30.5 ms, i.e. it is not just moving more bytes, it is
doing so with a third of the tail. At 512 B its p99 (1,044 us) is no better than anyone else's, and it
loses on rate.

**What this does NOT show, and the limitation is the interesting part.** The rig is **keep-alive only at
c64**, so connection accept — the thing a kernel-mode stack should win most decisively — is entirely out
of scope. This table therefore measures http.sys where it is least differentiated. Exercising accept on
Windows is genuinely hard (TIME_WAIT; `bench/README.md`'s ephemeral-port gate exists because omitting it
once manufactured a fake "208 dropped connections" defect), so it needs a purpose-built RST-closing
client rather than a flag on this rig — see TODO. Until that exists, **no claim about accept cost, in
either direction, is supported by anything in this file.**

#### 2. Our plaintext is at parity with Kestrel almost everywhere; the one disjoint loss is 16 KB

| payload | iocp/s12 vs kestrel |
|---|---|
| 512 B | *overlapping* — parity |
| 16 KB | **−2.6%** (3837-3898 vs 3962-4008), disjoint — the one real plaintext loss |
| 256 KB | *overlapping* — parity |
| 1 MB | *overlapping* — parity (iocp median is actually 0.2% higher) |

That is a better plaintext position than the 2026-07-30 table suggested, and it is what the BYO+pinned
defaults bought. Do not compute that improvement as a delta against the old table — different session.

#### 3. THE BAD NEWS, stated as prominently as the good: our TLS collapses at large payloads

`iocp+tls` is **LAST of all eight legs at both 256 KB and 1 MB**, and the gap to Kestrel's `SslStream` is
disjoint and enormous:

| payload | kestrel+tls vs iocp+tls |
|---|---|
| 512 B | *overlapping* — parity |
| 16 KB | *overlapping* (3056-3148 vs 3103-3292) |
| 256 KB | **Kestrel +83%** (6626-6906 vs 3531-3828) |
| 1 MB | **Kestrel +100%** (4446-4625 vs 2260-2316) |

This is the SAME SHAPE the Linux table records ("Only at 256 KB does userspace TLS lose (structurally) —
the wire bytes are encrypted, so zero-copy send can't apply"), so it is consistent rather than surprising
— but on Windows it starts earlier and bites harder, and Linux's compensating **+13-35% win at 512 B-64 KB
does not reproduce here**. On Windows the small-message TLS story is parity, not a win.

#### 4. NEW, UNEXPLAINED, and the most actionable thing in this table: `rio+tls` beats `iocp+tls` everywhere ≥16 KB

| payload | rio+tls/s12 | iocp+tls/s12 | |
|---|---:|---:|---|
| 16 KB | 3462-3515 | 3103-3292 | **RIO +7%**, disjoint |
| 256 KB | 5055-5237 | 3531-3828 | **RIO +39%**, disjoint |
| 1 MB | 2575-2712 | 2260-2316 | **RIO +18%**, disjoint |

That is backwards from plaintext, where RIO trails IOCP badly (256 KB 6496-7055 vs 9683-10819; 1 MB
2985-3295 vs 6040-6130, both disjoint). And `rio+tls` at 16 KB is our **only disjoint TLS win over
Kestrel** in the whole table (3462-3515 vs kestrel+tls 3056-3148, **+11%**).

**Hypothesis, pre-registered before anyone tests it:** it is the PAGE SIZE, via the geometry sentinel.
RIO resolves `page=65536`, IOCP resolves `page=4096` (both confirmed in this session's `/config`). The TLS
out-of-band send chunks ciphertext into page-sized segments, so at a 4 KB page IOCP issues ~16x the
segments RIO does for the same response — the same mechanism that made RIO's *plaintext* send need a 64 KB
page. Plaintext IOCP escapes it because zero-copy send bypasses the page path entirely; TLS cannot,
because the bytes must be produced by the record layer first.

**What would falsify it:** run `--iocp --tls --page 65536`. If the hypothesis holds, `iocp+tls` at 256 KB
should move from ~3,700 toward RIO's ~5,100 or better. If it does not move, the page is not the mechanism
and the difference is somewhere in the two backends' send paths. **This is untested — it is the top
Windows perf item, ahead of the standing RIO plaintext item, because it is a bigger gap on the leg that
is currently last.**

#### 5. RIO plaintext still needs its page fix, and this quantifies it

`rio/s12` trails `iocp/s12` disjointly at 256 KB (−33%) and 1 MB (−47%), while being at parity or ahead
at 512 B and 16 KB. That is the standing item-0/item-5 RIO send quantization, unchanged and now measured
in the same session as everything else.

#### Caveats that belong next to these numbers rather than under them

- **The http.sys legs are not peer rows.** Its TLS is terminated in the KERNEL against a certificate bound
  out of band by `netsh` (`bench/Enable-HttpSysTls.ps1`), so `httpsys+tls` is a different experiment from
  `kestrel+tls` (SslStream, in-process cert) and from `iocp+tls` (our SSPI, in-process cert) — same
  RSA-2048/SHA-256 parameters, different key, different termination point. And the rig pins the server
  process and the load generator to disjoint core sets, but **http.sys works on kernel threads that
  user-mode affinity does not constrain**, so it is not pinned in the sense the other legs are. Treat both
  http.sys columns as an outer bound, not as a row to subtract from.
- **Loopback.** Client and server share this host; absolute values are not comparable to a two-machine
  test, and this is exactly where a kernel stack's advantages are most distorted.
- ~~**One open confounder on the TLS legs.**~~ **RETIRED the same day — the TLS 1.3 floor is FREE.** A
  dedicated `iocp+tls-min12` leg overlaps the 1.3 leg at all four sizes, so the floor costs no measurable
  throughput and these numbers ARE continuous with earlier Windows figures. See the falsification section
  above. *Original caveat text follows, for its reasoning.*
- **One open confounder on the TLS legs.** These are the first Windows TLS numbers taken with the new
  SChannel **TLS 1.3 floor** (2026-08-01). Every SocketSet TLS leg here negotiated 1.3; there is no
  same-session 1.2 comparison. It cannot explain finding 4 (both RIO and IOCP got the same floor), but it
  is unmeasured against the older 1.2-capable configuration, so do not read these as continuous with any
  pre-2026-08-01 Windows TLS figure.

### FALSIFIED: the IOCP+TLS page hypothesis is wrong, and a bigger page makes 1 MB WORSE (2026-08-01, same day, second session)

The table above proposed that `iocp+tls` trails `rio+tls` at ≥16 KB because the TLS out-of-band path
chunks ciphertext into page-sized segments and IOCP resolves `page=4096` where RIO resolves `page=65536`.
**Pre-registered falsifier, written before the run: "if the page is the mechanism, `iocp+tls` at 256 KB
moves from ~3,700 toward RIO's ~5,100+."** It does not. 7 legs x 4 sizes x 6 scored passes, one session,
zero errors, `--page` banner-gated (`Want = page=65536`) so the flag is confirmed TAKEN, not just passed.

| payload | `iocp+tls` (baseline) | **`iocp+tls-p64k`** | verdict | `rio+tls` (target) | `kestrel+tls` (ceiling) |
|---|---:|---:|---|---:|---:|
| 512 B | 127.7-135.2 | 134.2-137.0 | *overlapping* | 136.3-139.6 | 134.4-135.8 |
| 16 KB | 3155-3267 | 3226-3325 | *overlapping* | 3381-3498 | 2984-3106 |
| 256 KB | 3547-3950 | 3952-4065 | **+6.6%**, disjoint by 1.9 MiB/s — see below | 4551-5355 | 6715-6902 |
| 1 MB | 2244-2304 | **2014-2060** | **−9.5%, disjoint — a REGRESSION** | 2568-2767 | 4440-4588 |

**The verdict is falsified, not "inconclusive", and the 256 KB row is why the distinction matters.** That
row *is* technically disjoint (min 3951.8 against max 3949.9 — by **1.9 MiB/s**, which is a rounding
error dressed as a result), and it is the row the hypothesis pointed at. But the pre-registered bar was
"toward RIO's ~5,100+", and 4,023 closes roughly **17%** of the gap to RIO and **6%** of the gap to
Kestrel. A mechanism that explained the anomaly would have closed most of it. This one does not, it does
nothing at 16 KB, and **at 1 MB it is a disjoint 9.5% regression** — so a bigger page is not merely "not
the explanation", it is not a fix either, in the direction of actively harmful at the largest payload.

**Three controls, and they are what make the null result trustworthy rather than an experiment that
simply failed to do anything:**

1. **The plaintext control moved nothing, exactly as pre-registered.** `iocp-p64k` vs `iocp` overlaps at
   every one of the four sizes. That was predicted (plaintext IOCP escapes the page path via zero-copy
   send), so the experiment demonstrably discriminates — the flag is not inert everywhere.
2. **The page was the ONLY variable.** `SmokeTest`'s `--page` rescales three pool depths to hold pinned
   memory constant, which would have confounded this outright — so it was checked rather than assumed:
   the demo's `/config` geometry reads `writebufs=1024 oobwritebufs=256 readpages=256` at BOTH 4 KB and
   64 KB pages, i.e. the demo's `--page` does not co-vary the pools. The two rigs differ here; do not
   carry the SmokeTest caveat across to `AspNetDemo`.
3. **The flag was confirmed taken**, not just parsed — the leg is gated on `page=65536` appearing in the
   banner. Without that gate this whole table would be indistinguishable from one where `--page` did
   nothing at all.

#### Settled as a side-effect: the TLS 1.3 floor costs NOTHING, so the morning's caveat is retired

The 2026-08-01 headline table carried an open caveat: it was the first Windows TLS measurement taken with
the new SChannel TLS 1.3 floor, with no same-session 1.2 comparison, so it was not continuous with any
earlier Windows TLS figure. Measured here as its own leg:

| payload | `iocp+tls` (1.3 floor) | `iocp+tls-min12` (1.2 floor) | |
|---|---:|---:|---|
| 512 B | 127.7-135.2 | 134.0-136.8 | *overlapping* |
| 16 KB | 3155-3267 | 3213-3291 | *overlapping* |
| 256 KB | 3547-3950 | 3661-3985 | *overlapping* |
| 1 MB | 2244-2304 | 2209-2356 | *overlapping* |

**Overlapping at every size.** The floor is free, the caveat is retired, and the 2026-08-01 TLS numbers
ARE continuous with earlier Windows figures after all. (Note this also means the 1.3 default costs no
throughput to keep — the security argument does not have to be traded against anything.)

#### REPLICATED in an independent session: `rio+tls` really does beat `iocp+tls`

This was the surprising cell in the morning table, so it matters that a second session reproduces it with
disjoint ranges at all three larger sizes:

| payload | `rio+tls/s12` | `iocp+tls/s12` | |
|---|---:|---:|---|
| 16 KB | 3381-3498 | 3155-3267 | **RIO +8%** |
| 256 KB | 4551-5355 | 3547-3950 | **RIO +38%** |
| 1 MB | 2568-2767 | 2244-2304 | **RIO +17%** |

Two independent sessions, same direction, same rough magnitudes, while RIO trails IOCP badly on
*plaintext* at the same sizes. It is a real property of the two TLS send paths, and the buffer geometry is
now ruled out as the cause.

**What to do next, and the honest answer is instrument before hypothesising again.** The page was a
plausible mechanism and it was wrong; the next guess deserves data first. `SS_IOCP_STATS=1` already exists
and reports zero-copy declines and segment counts per response — it is what turned the "IOCP zero-copy
does nothing at 256 KB" mystery into the measured 65.00-segments-against-a-64-cap answer. Point it at the
TLS path and compare against RIO before proposing a fix.

### INSTRUMENTED: neither buffer count nor syscall count is the IOCP+TLS bottleneck — something per-BYTE is (2026-08-01)

Following the falsification above, the next step was recorded as "instrument, do not hypothesise again".
Done, with `SS_IOCP_STATS=1` / `SS_RIO_STATS=1` under identical 256 KB TLS load (`-c 64`, 8 s, 12 shards).
**These are single short runs for MECHANISM, not scored throughput** — the rates are consistent with the
scored table but are not a substitute for it.

| | WSABUFs / response | WSASends / response | rps |
|---|---:|---:|---:|
| `iocp+tls`, page 4096 | **65.0** | **2.0** | 12,115 |
| `iocp+tls`, page 65536 | **5.0** | **1.0** | 13,013 |

**This is the strongest form of the falsification, and it is mechanical rather than statistical.** The page
flag did *exactly* what it was supposed to: 65 buffers collapsed to 5, two `WSASend`s collapsed to one — a
**13x reduction in buffer count and a halving of send syscalls — and it bought about 7%.** A hypothesis
that survives its own mechanism working perfectly and delivering nothing is dead. (65 pages x 4 KB =
266,240 B, which is the 256 KB payload plus TLS record framing — so the accounting is exactly as expected;
nothing is being mis-measured.)

Meanwhile, from the same load, RIO:

```
[rio-stats] RIOSends=634,264 (31,770 MiB, 52,523 B/send) commits: send=1.00/send
            notify-rearms=0.22/send port-wakes=0.28/send  out-of-band flushes=126,840  staged=31,725 MiB
```

**5.0 RIOSends per response against IOCP's 1.0 — RIO issues FIVE TIMES the send calls and is still ~22%
faster.** So send-call count is not the constraint either, in either direction.

**One thing this settles outright:** `declined: tls=96,906` against `zero-copy sends=0` — the IOCP TLS path
declines zero-copy on **100%** of responses, by design (the record layer must produce ciphertext, so there
is no caller buffer to send from). Every TLS byte goes through the copying path. That is now measured
rather than inferred, and it is why the plaintext and TLS legs behave so differently: plaintext IOCP wins
by *not copying*, and that option does not exist here.

**Where that leaves the RIO-beats-IOCP anomaly — hypothesis, explicitly NOT yet tested.** Both backends
copy exactly once (RIO's `staged=31,725 MiB` ≈ its total send bytes). What differs is what the send call
must do with those bytes: `WSASend` probes and page-locks the user buffers on **every call**, whereas RIO
sends from buffers **registered once** with `RIORegisterBuffer` — which is the entire reason RIO exists.
That would make the cost per-BYTE-locked rather than per-call, which fits every number above: it is
insensitive to buffer count, insensitive to syscall count, and scales with payload (the gap grows 16 KB →
256 KB → 1 MB).

*The previous hypothesis in this file was plausible, mechanical, and wrong, so this one is written down
with its test attached rather than acted on.* **A discriminating test:** if page-locking is the cost, then
IOCP+TLS throughput should be roughly invariant to how the same bytes are arranged across buffers (already
consistent with the 65→5 result) but should improve markedly if the same payload is sent from memory that
is already locked/pinned. **The real fix it implies is not a tuning knob:** let the TLS record layer frame
ciphertext *directly into* the pinned send buffers, so the IOCP zero-copy path becomes reachable for TLS
at all — which would convert a 100% decline rate into the same fast path plaintext already uses. That is a
design item, not a flag, and it is now the highest-value Windows TLS work.

### A PESSIMISATION IN THE FLUSH HAND-OFF, CROSS-PLATFORM: +58.6% on TLS at 1 MB (2026-08-01)

`PooledBufferWriter.TakeArray()` detaches the writer's buffer and leaves it EMPTY, so the next use
re-rents at the first size hint and grows by DOUBLING — and every doubling in `Ensure()` is a
`Buffer.BlockCopy` of everything written so far. `OutboundConnection.Flush` uses `TakeArray` on **every
out-of-band flush on every backend** (io_uring, epoll, IOCP, RIO, managed), so since the hand-off landed
the per-connection accumulator has restarted from empty on every response and re-paid that growth. Fixed
by remembering the high-water capacity and re-renting at it.

Interleaved A/B (`Compare-Commits.ps1`, isolated worktrees, 4 scored passes), IOCP, bridged:

| leg | payload | before (MiB/s) | after (MiB/s) | change |
|---|---|---:|---:|---|
| `--classic` plaintext | 16 KB | 3,364.1 | 3,301.6 | −1.9% — *overlapping, noise* |
| `--classic` plaintext | 256 KB | 4,567.2 | 4,968.3 | +8.8% — *overlapping, unproven* |
| `--classic` plaintext | 1 MB | 2,977.3 | **3,970.1** | **+33.3%, DISJOINT** |
| `--tls` | 256 KB | 3,907.8 | **4,642.4** | **+18.8%, DISJOINT** |
| `--tls` | 1 MB | 2,362.6 | **3,748.0** | **+58.6%, DISJOINT** |

**The size-dependence is the mechanism showing itself**: more bytes = more doublings = more re-copying,
so the win grows with payload and vanishes at 16 KB. And **TLS gains most because ALL TLS traffic takes
the out-of-band path**, so it always paid the growth, where plaintext BYO uses zero-copy send and skips
`Flush` entirely.

**Note the shape of the mistake, because this file's history now contains it twice.** `fa97dd4` replaced
an unpooled `ToArray()` with a rent PLUS a copy and concluded "allocation was the cost, per-byte copying
is not"; its successor removed that copy via hand-off and reintroduced copying through the growth path.
Each step measured better than the last, and each left a copy behind. The lesson is not "copies are the
cost" — it is that a change measured only against its predecessor can carry a new regression under a net
win.

**This also moves the IOCP+TLS picture, but does NOT settle it.** `iocp+tls` at 1 MB goes ~2,363 →
~3,748, which is above the `rio+tls` figure of ~2,658 recorded earlier the same day — *but that RIO number
predates this fix and RIO uses the same `Flush` path*, so it will move too. **The RIO-beats-IOCP-on-TLS
anomaly must be re-measured on this branch before anything is concluded about it; do not read the
inversion off these two numbers.**

**UNVERIFIED ON LINUX.** This is a shared-code change on the hottest path, and io_uring / epoll / managed
all reach it through `OutboundConnection.Flush`. It is plausibly a Linux win of similar shape and has not
been built or run there.

### THE THREAD HOPS ARE THE SMALL-PAYLOAD DEFICIT TO KESTREL, and half of them had never been measured (2026-08-01, branch `tls-zerocopy-send`)

`SS_PIPE_SCHED=inline` has existed for a while and made the hop question look settled (Linux io_uring:
−28%). It was not: **that knob only ever moved the OUTBOUND reader** (the SocketSet pump). The INBOUND
reader — the one that resumes *Kestrel's request pipeline* when data arrives — was hard-wired to
`PipeScheduler.ThreadPool`, so every "thread hop" figure on file describes the write side only.
`inline-read` / `inline-both` (new) and `bench/Run-PipeSched.ps1` measure the missing half: same binary
every leg, modes reshuffled per pass, banner-gated on `pipesched=`, 6 scored passes, **a vanilla-Kestrel
control in the same passes**.

| payload | off *(today's default)* | inline-read | **inline-both** | *kestrel (control)* |
|---|---:|---:|---:|---:|
| 512 B | 136.8-141.1 | 143.2-144.3 | **144.0-145.3** | *144.0-145.4* |
| 4 KB | 1044.1-1059.7 | 1071.9-1087.9 | **1089.5-1110.6** | *1084.4-1102.0* |
| 16 KB | 3744.9-3893.7 | 3907.5-3979.2 | **3990.0-4033.6** | *3933.6-3974.3* |
| 256 KB | 10540-11291 | 10280-11220 | 10732-11274 | *(not run)* |

**Against the same-session control:**

| payload | default vs Kestrel | inline-both vs Kestrel |
|---|---|---|
| 512 B | **−2.8%**, disjoint | *overlapping* — **parity** |
| 4 KB | **−3.2%**, disjoint | *overlapping* — **parity** |
| 16 KB | **−2.9%**, disjoint | **+1.7%, DISJOINT — ahead** |
| 256 KB | *(all modes overlap — no effect)* | |

**So the ~3% disjoint small-payload deficit to vanilla Kestrel is the thread hops, essentially in full.**
Removing both erases it at 512 B and 4 KB and inverts it at 16 KB. That is the first mechanism found for
that deficit; every previous candidate (copies, pool pinning, segment counts) was measured and did not
explain it.

**The pre-registered prediction held, which is why this is a mechanism and not a coincidence.** Written
before the run: the read hop is a per-REQUEST cost, not per-byte — one resumption per request whatever the
body size — so the gain must be largest where request rate is highest and must fade to nothing at large
payloads; **and a gain that GREW with payload would falsify it**. Observed: disjoint at 512 B / 4 KB /
16 KB, and every mode overlapping at 256 KB. The falsifier did not trigger. (Worth noting against the rest
of this session, in which three separate pre-registered hypotheses were killed.)

**NOT A SHIPPABLE DEFAULT, and the distinction is the whole point of the item.** An inline INBOUND reader
runs Kestrel's entire request pipeline **on the transport's loop thread**, blocking that loop for every
backend that owns one (all but managed) — Kestrel runs its own IO queues for exactly this reason. This
measurement is an **upper bound** on what removing the read hop can be worth, not a configuration to
adopt. What it does is convert the "two half-pipes" proposal from a plausible idea into a costed one:

- The outbound half-pipe is already built and merged (off by default) — it removes the write hop *properly*,
  with no loop blocking.
- **The INBOUND half-pipe is now justified by a number rather than by argument**, and the number is
  ~2-4% at small payloads. Real and repeatable, but calibrate expectations: it is not a step change, and a
  real half-pipe would likely capture somewhat less than `Inline` does, since `Inline` also skips work a
  correct implementation still has to do.
- 256 KB is untouched by any of this, consistent with everything else on file.

### What changed on 2026-07-30, because it moves several conclusions in this file

- **An ACCESS VIOLATION in RIO+TLS under churn was found and fixed** (item 0e). It was present on the
  default configuration at roughly **one run in two**, and had been for months. Found while validating an
  unrelated change; the reason it survived is that **no benchmark in this repo churns connections** -
  every one holds keep-alive and measures steady state. There is now a soak rig that does.
- **The zero-copy segment cliff is gone** (item 2f option 2). `TrySendZeroCopy` reports bytes accepted
  rather than a bool, so a cap costs an extra send instead of falling back to copying an entire
  response: **+80.6% at 1MB**, and the copying path is now unused on the pipe path at every size
  measured. `--pipe-segment 65536` is no longer needed to make zero-copy *engage* - it is only a
  segments-per-send tuning knob now.
- **Backends choose their own buffer geometry** (item 0), which fixed item 0d - RIO's `verify-oob-4m`
  cell went from a 15.2s failure to passing in 0.2s. **Unverified on Linux**; epoll and io_uring are
  unchanged by construction, not by test.

### What changed on 2026-07-31 (Linux cold-start after the Windows work)

- **The shared changes are correctness-clean on Linux.** A new Linux correctness gate,
  `bench/run-smoke-matrix.sh` (the long-missing `.sh` counterpart of `Run-SmokeMatrix.ps1`), runs 52 cells
  across io_uring / epoll / managed x plaintext / OpenSSL TLS and reports **51/52 PASS**. The rewritten
  `PipeIoBridge` pump (every echo-pipe cell), the new endpoint validation (abstract-UDS cells), and the
  `Flush` hand-off (verify cells) all pass on both Linux backends.
- **The one FAIL was NOT a geometry problem — it was an unbounded writev, now FIXED.** `iouring+tls/verify-oob-4m`
  stalled (received=0), and `--page 65536` "fixed" it, which first looked like RIO's item 0d. It is not:
  the real cause is an oversized `writev`. io_uring's TLS out-of-band send chunks the ciphertext into
  page-sized segments and issued them as ONE `IORING_OP_WRITEV`; a ~4MB response at a 4KB page is ~1024
  segments, which hits `IOV_MAX` (1024) and the kernel rejects the whole send with -EINVAL. The plaintext
  OOB path already split chains at `IovMax`; the TLS path (`TlsSend`) bypassed it. Discriminating tests
  nailed it: at page 4096, 3MB (~768 segs) passes and 5MB (~1280 segs) fails; at page 8192, 4MB (~512
  segs) passes — the boundary tracks SEGMENT COUNT, not page-vs-record (the 64KB/1MB TLS cells pass at 4KB
  page despite 7000-byte records). Fixed by routing both OOB paths through one `DispatchChainSplit`; TLS
  verify now passes at 4MB / 5MB / **16MB** at the default 4KB page. So this is a bug fix, not a case for a
  different `DefaultGeometry` — and the page-size sweep (below) confirms io_uring wants no page change.
- **Dynamic shard growth now works on the reuse-port path** (io_uring + epoll over IP), which it never did
  before. It needed two fixes, not the one the handover named — a grown shard was given no listener (Gap A,
  both backends) and io_uring never triggered growth on a full local accept (Gap B, silent drop). Both
  fixed and proved by `bench/run-shard-growth.sh` (each backend grows 2→12 under load, holds at 2 with
  growth off); churn cells stay clean. Details in `TODO.md`'s dynamic-shard-growth section.
- **io_uring wants NO page/geometry change (handover §3 item 2 answered).** `bench/run-page-sizes.sh` on
  the bare responder, pages 4/16/64KB x payloads 512B/16KB/256KB, confirms the pre-registered prediction:
  io_uring is page-INSENSITIVE. Medians (MiB/s, min-max in brackets): 512B 469.6 [469-471] / 468.3 / 472.1;
  16KB 8,611 [8464-8809] / 8,388 / 8,719; 256KB **12,644 [12359-12950] (p4096)** vs **11,600 [11369-12615]
  (p65536)** — ranges overlap and if anything the 64KB page is slightly WORSE at 256KB. Unlike RIO (which
  can't scatter-gather, so page size is its only large-send lever and worth 4.68x), io_uring dispatches one
  writev over a segment chain, so page size is not a throughput lever. With the writev-cap bug fixed, there
  is no correctness reason for a bigger page either. Verdict: leave io_uring on `BufferGeometry.Default`.
- **epoll gained a real kTLS path (item 3c).** `--epoll --ktls` now runs kernel TLS — epoll was the last
  backend with no kTLS code at all. TX is kernel-offloaded (plaintext `send()`, reusing the normal send
  path), RX is `EPOLLIN → SSL_read` (userspace decrypt on this box's OpenSSL 3.0.13: `[ktls/epoll]
  tx=True rx=False`). Correctness-clean: smoke matrix 60/60 (incl. later prefix cells) with new `+ktls` cells, ALPN over the kernel
  path. **The throughput question it was built to answer is measured, and the pre-registered prediction is
  FALSIFIED:** epoll+ktls does NOT reach epoll+tls — same-session, 4 scored passes, disjoint, it trails
  **−9.3% at 512 B** (~537k vs ~592k rps) and −12.3% at 4 KB, comparable to io_uring+ktls's −11.7% / −7.3%.
  Since epoll forfeits no multishot receive, most of the kTLS small-message penalty is the **record path
  itself** (per-message RX `SSL_read`), not multishot forfeiture. TX-only offload here (RX userspace <
  OpenSSL 3.2); the RX-offloaded picture is still unmeasured. Full write-up: item 3c in `TODO.md`.
- **io_uring zero-copy prefix sends (§3 item 3).** Measured that the >IovMax (1024-segment) decline is a
  hard cliff: at 256KB the pipe path is 100% zero-copy, but an 8MB response was **100% copy** (`zero-copy=2`
  of ~61k segments, overflowing into per-response pinned allocations). Fixed: `TrySendZeroCopy` returns
  bytes-accepted and caps iovecs at IovMax, streaming a large sequence as several zero-copy writevs. After:
  8MB is **100% zero-copy**. Byte-exact under concurrency; new `echo-pipe-8m-deep` smoke cell.
- **TLS renegotiation audited + hardened.** The default is TLS 1.3 (no renegotiation; KeyUpdate already
  handled), but TLS 1.2 is reachable and advertised "Secure Renegotiation IS supported" — the CVE-2011-1473
  DoS shape. The SERVER now sets `SSL_OP_NO_RENEGOTIATION` (refuses client-initiated renegotiation, survives
  cleanly); the CLIENT deliberately does not (would break legit server-initiated reneg from peers we dial).
  And **the default TLS floor is now TLS 1.3** (`OpenSslTlsProvider(minProtocol:)`, default `Tls13`) — a
  TLS-1.2-only client is rejected outright, retiring the whole 1.2 surface (renegotiation included) on the
  default path; 1.2 is opt-in (`--tls-min12`). Smoke matrix stays green (all cells negotiate 1.3).

- **epoll got BYO zero-copy send, worth +41% on the bridged 256KB path.** The Kestrel-bridge
  investigation (measure-first) ran a same-session bare-vs-bridged isolation: bare epoll is the FASTEST
  thing measured at 256KB (13,107 MiB/s, above io_uring and Kestrel), yet bridged epoll was the SLOWEST
  (7,732) — a 41% bridge cost. The cause was concrete, not structural: epoll had no `TrySendZeroCopy`, so
  the pipe path fell back to `Connection.Send` and COPIED the whole response, where io_uring sends
  zero-copy. Unlike RIO (registered buffer ids only), epoll's `writev` takes arbitrary addresses, so it can
  send straight from the pinned pipe segments. Implemented (a marshaled `writev` with an EPOLLOUT
  partial-write drain holding the pins, IovMax prefix like io_uring): bridged epoll 256KB **7,732 →
  10,894 MiB/s (+41%)**, three passes tight, now level with io_uring bridged (11,586) and near Kestrel
  (12,470). Byte-exact on the smoke matrix incl. the 8MB deep-window prefix cell.
  **CLEAN SAME-SESSION A/B (2026-08-01, closing the cross-run gap): `--classic` (copy bridge) vs the
  default zero-copy BYO at 256KB, 3 passes, disjoint — epoll 6,786 → 12,468 MiB/s (+84%), io_uring 7,452 →
  12,708 (+71%), both zero-copy legs ≥ Kestrel (12,069-12,602).** The clean number is LARGER than the
  +41% above because that compared classic against the UNPINNED zero-copy path; classic → PINNED zero-copy
  is the full win (the copy removal composed with the pinning fix). This retires the cross-run caveat.
- **epoll got BYO zero-copy RECEIVE too — and measuring it RESOLVED item 7 (the copy is not the
  constraint).** epoll's readiness model makes this the cheap backend for it: `EPOLLIN` says data is
  waiting, so it reads straight into the pipe's `GetMemory()` — no speculative arm-ahead, which is what
  makes the io_uring version hard. Implemented as the springboard item 7 asked for (a new
  `PipeIoBridge.TryBeginReceive`/`CommitReceive`, used by epoll's `PumpReceive` for non-TLS pipe
  connections; falls back to the copy path when a flush is pending — ~0.1% of receives). It engages 100%
  (`SS_BRIDGE_STATS`: `zero-copy-recv=100.0% of receives`, staged=0) and is byte-exact. **But the A/B says
  it buys nothing: 256KB uploads to a `/drain` endpoint, zero-copy receive ON vs OFF (`SS_NO_ZC_RECV`),
  interleaved — ~54.7k vs ~56.3k req/s, ranges fully overlapping (~14,000 MiB/s inbound either way).** So
  the inbound copy is rounding error against the recv syscall + pipe + Kestrel — confirming item 7's
  estimate empirically. **The finding was then triangulated across THREE workloads after a good challenge
  (was the HTTP asymmetry / Kestrel overhead hiding the win?):** (1) Kestrel `/drain` upload — no win;
  (2) BARE symmetric pipe echo (no Kestrel) — no win (~13 GB/s round-trip, ON/OFF overlapping); (3) the
  discriminating case, a PURE unidirectional receive-flood (epoll `--sink` pipe server, flooding
  `--pipeline` client, receiver ≫ sender, so nothing dilutes the copy) — STILL no win (~4,700 MiB/s
  inbound, ON/OFF overlapping at both 64KB and 256KB). Why it holds even there: `recv()`'s kernel→user copy
  is unavoidable and dominates, and the zero-copy path's own `GetMemory` + lock roughly cancels the
  transport copy it removes; the bottleneck is the syscall/pipe, not memcpy. **Conclusion: do NOT
  speculatively build io_uring's much-harder zero-copy receive.** Remaining honest caveats: real-NIC
  memory-bandwidth saturation across many cores is invisible on loopback, and the callback path is already
  copy-free so this is moot for callback-based (Redis/RPC-style) transports anyway.

- **CORRECTION — "Kestrel wins at 256KB" was a POOL-DEFAULT confounder in our own demo, not a transport
  or thread-model deficit.** Chasing the residual bridge gap: an inline-pipe-scheduler experiment
  (`SS_PIPE_SCHED=inline`, resume the pump on Kestrel's thread instead of the ThreadPool) made io_uring
  *worse* (−28%), which killed the thread-hop hypothesis. The real cause was **per-segment pinning**:
  vanilla Kestrel backs its pipes with a `PinnedBlockMemoryPool` by default, so its zero-copy send needs no
  per-op pinning — but AspNetDemo defaulted to `MemoryPool.Shared`, so OUR zero-copy send pinned ~64
  segments (`GCHandle` per 4KB block) on every 256KB response. Same-session, 256KB, matched pools:

  | | MiB/s | vs Kestrel |
  |---|---:|---:|
  | kestrel (control) | ~12,535 | — |
  | io_uring, default `MemoryPool.Shared` | ~10,500 | −16% |
  | **io_uring, pinned pool** | ~12,650 | **+1%** |
  | epoll, default `MemoryPool.Shared` | ~10,750 | −14% |
  | **epoll, pinned pool** | ~12,660 | **+1%** |

  With matched (pinned) pools BOTH backends reach parity / edge ahead at 256KB — and we already led at
  64KB (+5%) and small messages (+5.6% plaintext, +22% TLS). **So with a fair pool we are ≥ Kestrel across
  the plaintext size range.** Fixed by making the demo's pipe pool **pinned by default** (matches Kestrel;
  `--pipe-unpinned` opts out). This reframes the older "the bridge costs 24-42% at 256KB / Kestrel pays no
  bridge" narrative below: much of that was the unpinned-pool handicap, and the structural pump-hop is NOT
  the 256KB bottleneck (the inline experiment proved it). Standing caveat: the pinned pool costs ~2.7x RSS
  at 2048 connections (item 2d) — the same tradeoff Kestrel makes by default.

Reading order if you are picking this up cold: `TODO.md`'s top sections, then item 0e, then 2f.

### Linux flat-check: the shared changes are throughput-neutral, and the one thing that moved was a default flip (2026-07-31)

Handover §2 step 4 — re-run `bench/run-tls-sizes.sh` (7 legs x 5 sizes, 3 scored passes, same rig and
same-session Kestrel controls) and compare against the payload-sweep table above, which should be flat.
It is: **every cell reproduces within ~3% except one**, and the Kestrel plaintext control lands at
12,450.9 against a recorded 12,450.5 (0.0%), so the session is sound and the host is the same instrument.

**The one mover is io_uring plaintext at 256KB: 7,882.6 → 11,586.6 MiB/s (+47.0%), three passes within
0.1%.** It is NOT a transport change and NOT noise — it is commit `29da643` making the BYO bridge the
default. The recorded baseline measured the classic copy path (BYO was opt-in then); the leg now measures
zero-copy. Attribution nailed down three ways rather than inferred across runs:

1. **Path taken, not just enabled** (rule 2): `SS_URING_STATS=1` at 256KB reports
   `pooled-page=0 pinned-managed=0 zero-copy=2,602` — every segment goes zero-copy.
2. **Same-session A/B** (rule 6): io_uring 256KB plaintext, zero-copy vs `--classic`, interleaved 3 passes:
   **6,270 vs 3,660 MiB/s = +71%** (2 shards, so lower absolutes than the 12-shard sweep; the ratio is the
   point). The zero-copy path is where io_uring's large-payload plaintext performance lives.
3. **epoll is unchanged** (7,739.1 → 7,744.3): it has no zero-copy send, so the default flip cannot touch
   it — which is exactly why only the io_uring cell moved.

So the shared PipeIoBridge/Flush changes did not move the Linux throughput baseline; the transport is
neutral, and the visible delta is a demo default now pointing at the faster path. Under TLS the win does
not appear (iouring+tls 256KB flat at ~3,963) because the payload is encrypted into a separate buffer, so
there is no pipe memory to send from zero-copy.

### Feature / backend matrix

Verified by inspection 2026-07-29, not recalled. This table has been wrong twice (it claimed
`ReceiveBufferSize` reached only Windows, and that neither Linux backend had a per-socket receive slab), so
each row names what it is asserting.

The last column is **vanilla Kestrel's own socket transport** - the control every benchmark here is run
against. It is included because several of our "no" cells are things Kestrel already has, and because it
is the reason the comparison at 256KB goes the way it does. That column describes the framework's design
rather than code in this repo, so it is marked as such and should be re-checked before being quoted.

| feature | IOCP | RIO | io_uring | epoll | managed | *Kestrel (sockets)* |
|---|---|---|---|---|---|---|
| TLS in-transport | SChannel | SChannel | OpenSSL | OpenSSL | yes (per-conn gate) | *no - `SslStream` layered above* |
| ALPN | yes | yes | yes | yes | yes | *yes (via `SslStream`)* |
| kTLS | - | - | **yes** (TX always; **RX needs OpenSSL 3.2+**) | **yes** (TX always; RX needs 3.2+) | - | *no* |
| `ReceiveBufferSize` split | yes | yes | yes | yes | **no** | *n/a - pool block size (4KB)* |
| multi-segment send | 64 x `WSABUF` | **capped at 1** | writev <=1024 iov | n/a - direct `send()` | n/a - one `SetBuffer` | *yes - SAEA `BufferList`* |
| chained pooled pages (`GetWriteSpan`) | no | must not | **yes** | n/a | no | *n/a* |
| BYO zero-copy **send** | **yes** - any length (256 segs/send, then a PREFIX) | impossible (registered ids) | **yes** - any length (1024 iov/send, then a PREFIX) | **yes** - `writev` <=1024 iov, then a PREFIX | no | ***yes - sends from the pipe*** |
| BYO zero-copy **receive** | no | no | no (needs receive-parking) | **yes** (readiness reads into `GetMemory()`) | no | ***yes - into `GetMemory()`*** |
| internal zero-copy echo | no | no | **yes** (borrowed read buffers) | no | no | *n/a* |
| pipe mode (`UsePipe`) | yes | yes | yes | yes | yes | *it **is** pipes* |
| write-pool exhaustion | stage + retry | stage + retry | pinned-heap fallback | n/a (`ArrayPool` staging) | n/a | *n/a* |
| read depth > 1 | no | no (capped) | **yes, multishot** | n/a (level-triggered) | no | *no* |
| AF_UNIX | yes | **no** (TCP/UDP) | yes | yes | yes | *yes* |
| reuse-port multi-bind | no | no | **yes** (IP) | **yes** (IP) | no | *no* |
| loop thread per shard | yes | yes | yes | yes | **no** | *no - thread pool + IO queues* |

**The two cells that explain the 256KB result — LARGELY SUPERSEDED 2026-07-31, read the correction bullet
at the top of this section first.** The story below (Kestrel pays no bridge; the bridge costs 24-42%; the
end-state is fewer pipes/hops) was written before two 2026-07-31 findings retired most of it: (a) io_uring
and epoll both got BYO zero-copy SEND, removing the outbound copy; (b) the residual 256KB gap turned out
to be a POOL-DEFAULT confounder (our unpinned `MemoryPool.Shared` vs Kestrel's `PinnedBlockMemoryPool`),
not the adapter — with matched pinned pools BOTH backends reach parity/edge ahead at 256KB, and an
inline-scheduler experiment proved the pump thread-hop is NOT the bottleneck. So "fewer pipes and hops" is
no longer the lever for PLAINTEXT; the one remaining large-payload gap is userspace-TLS (which can't
zero-copy the wire bytes) → kTLS/real-hardware. The original text, kept for the reasoning that led here:
Kestrel is zero-copy in *both* directions **and pays no
bridge at all** - its transport contract already *is* a pair of pipes, so there is nothing to adapt. Every
SocketSet leg in the ASP.NET tables pays an adapter that Kestrel-on-sockets does not have, plus at least
one copy that Kestrel does not make. That is the structural statement behind "the bridge costs 24-42% at
256KB", and it is why the honest end-state for the ASP.NET path is fewer pipes and hops rather than fewer
copies.

Note also what SocketSet has that Kestrel does not, since the matrix cuts both ways: TLS terminated inside
the transport (worth +22% at small messages against `SslStream`), kTLS, multishot receive, reuse-port
multi-bind, and a loop thread per shard.

**Copies on the outbound pipe path, which is what "BYO" is about.** `Connection.Send(in
ReadOnlySequence)` is not overridden anywhere, so every backend except IOCP copies the caller's pipe
segments into its own writer:

| backend | the copies | total |
|---|---|---:|
| *Kestrel (sockets)* | *none - there is no "our buffer"; SAEA sends the pipe segments* | ***0*** |
| IOCP with `--byo` (`TrySendZeroCopy`) | none - `WSABUF`s point straight at pipe memory | **0** |
| io_uring with `--byo` (`TrySendZeroCopy`) | none - the writev's iovecs point straight at pipe memory | **0** |
| io_uring, callback path | `WriteAll` into pooled pages; writev sends those pages | 1 |
| managed | `WriteAll` into `_wbuf`; SAEA `SetBuffer(_wbuf)` sends it | 1 |
| epoll | `WriteAll` into the accumulator; `Flush` **hands that buffer over** (was: rent + copy) | 1 *(was 2)* |
| IOCP classic / RIO | accumulator -> `StageOutbound` pooled staging -> drain into write pages (the `Flush` snapshot copy is gone) | **2-3** *(was 3-4)* |

The epoll and Windows rows dropped by one on 2026-07-29 - see the test below, which is what removed it.
**On Windows the change is correct and worth nothing measurable**: the smoke matrix passes on
IOCP/RIO/managed, and two interleaved same-session A/Bs of that one commit on the bridged IOCP path give
+0.8% and -0.0% with overlapping ranges - an epoll-sized win is excluded. The copy-count correlation in
the table above therefore does NOT predict Windows: removing one of 3-4 copies there did nothing, where
removing one of 2 on epoll was worth +16.3%.

`IoUringConnection` derives from `Connection` and owns its `OutChain` writer, which is why it escapes the
`Flush` snapshot; `EpollConnection` and `WindowsConnection` both derive from `OutboundConnection` and pay
it.

**A correlation worth testing, not yet a finding.** Bridge cost at 256KB tracks copy count across the two
Linux backends: io_uring (1 copy) pays **24.5%**, epoll (2 copies) pays **41.8%** - and epoll is the
*faster* of the two bare (11,437 vs 10,349), so it is not that epoll is simply weaker. Windows tuned RIO
(3-4 copies) pays ~42%. Three points and a plausible mechanism, which is exactly the shape of argument
this file has twice had to retract, so it is written down as a hypothesis with a direct test rather than
as a conclusion: **remove epoll's `Flush` snapshot copy (hand ownership over via `PooledBufferWriter.
TakeArray`, which exists for this) and re-measure at 256KB.** Pre-registered: if copies cost at large
payloads, epoll's bridge cost should fall by roughly the io_uring-epoll gap (~15 points); if it does not
move, the correlation is coincidence and BYO work aimed at the bridge is aimed at the wrong term.

That test is much cheaper than BYO and it gates it: this is the same question IOCP's zero-copy measurement
(+3.5% at 16KB, nothing at 256KB) was supposed to answer but could not, because IOCP caps at 64 `WSABUF`s
and a 256KB response through Kestrel's 4KB blocks is ~64 segments - so it plausibly declined and fell back
to copying at exactly the payload of interest.

### TESTED 2026-07-29: the copy DID cost, +16.3% at 256KB, and the prediction was right to within a point

`OutboundConnection.Flush` no longer rents a snapshot and copies into it; it hands over the accumulator's
own pooled buffer (`PooledBufferWriter.TakeArray`) and lets the writer re-rent on next use. One copy
removed from every out-of-band flush on epoll, IOCP and RIO. Re-measured with the full 7-leg sweep, 12
shards, `-c 64`, 7 passes with the first discarded - so the four untouched legs are within-session
controls rather than a cross-day comparison:

| leg | before | after | change |
|---|---:|---:|---:|
| **epoll** | 6,655.5 [6083-6700] | **7,739.1** [7557-7861] | **+16.3%** |
| **epoll+tls** | 4,342.9 [4306-4359] | **4,830.6** [4620-4851] | **+11.2%** |
| iouring *(control)* | 7,817.6 | 7,882.6 | +0.8% |
| iouring+tls *(control)* | 3,934.4 | 3,943.8 | +0.2% |
| iouring+ktls *(control)* | 5,600.1 | 5,486.7 | -2.0% |
| kestrel *(control)* | 12,515.8 | 12,450.5 | -0.5% |
| kestrel+tls *(control)* | 8,030.4 | 8,026.5 | -0.1% |

**Both epoll ranges are fully disjoint from their previous ones; every control is inside 2%.** Zero errors
across 98 cells. At 64KB epoll is unchanged (10,485.8 against 10,532.0), which is the right shape: the
bridge costs only ~2% there, so there is nothing for a copy to be a large fraction *of*.

**What this settles.** **Per-byte copying does cost at large payloads.** The standing conclusion
"allocation and per-operation cost dominate; per-byte copying does not" needs its scope narrowed rather
than reversed: it came from `fa97dd4` (+27% at 256KB for removing an *allocation*), from page size moving
RIO 4.68x without changing bytes copied, and from IOCP zero-copy buying +3.5% *at 16KB*. None of those
removed a copy at 256KB with everything else held still. This does. **Copies are cheap next to allocations
and syscalls, and they are not free at a quarter-megabyte per response.**

#### But the reason the test was run is REFUTED: this did not move the bridge's share at all

The prediction had two halves and only one survived. The throughput half was right to within a point. The
*causal* half - that the bridge costs epoll more than io_uring **because** epoll made an extra copy, so
removing it should drop epoll's bridge cost by ~15 points - is wrong.

The bare responder flushes through `OutboundConnection` too, so it got the same win
(`bench/run-bare-vs-bridged.sh`, re-run on the fixed build, io_uring untouched as a control):

| backend | bare, before | bare, after | change |
|---|---:|---:|---:|
| epoll @ 256KB | 11,437.3 | **12,971.1** | **+13.4%** |
| io_uring @ 256KB *(control)* | 10,349.4 | 10,352.0 | +0.03% |
| epoll @ 64KB | 10,744.8 | 10,759.4 | +0.1% |
| io_uring @ 64KB *(control)* | 10,832.8 | 10,606.2 | -2.1% |

Both sides of the subtraction rose, so the ratio is almost unchanged:

| backend @ 256KB | bare | bridged | bridge cost | was |
|---|---:|---:|---:|---:|
| epoll | 12,971.1 | 7,739.1 | **40.3%** | 41.8% |
| io_uring | 10,352.0 | 7,882.6 | **23.9%** | 24.5% |

**epoll and io_uring now make the same number of outbound copies (one each) and the bridge still costs
epoll 40.3% against io_uring's 23.9%.** So copy count does not explain the difference between them, and
the correlation recorded above - io_uring 1 copy/24.5%, epoll 2 copies/41.8% - was coincidence as far as
the *bridge* is concerned. It was flagged as "three points and a plausible mechanism, the shape of
argument this file has twice had to retract"; it has now been retracted by its own test, which is the
outcome that framing was there to make possible.

**What is still unexplained, and is now the sharper question:** why does the same bridge cost epoll 40%
and io_uring 24%, at equal copy counts, when epoll is the *faster* backend bare (12,971 vs 10,352)? Both
run the same `PipeIoBridge`, the same two `Pipe`s and the same Kestrel. The remaining candidates are the
per-flush marshalling shape (epoll's `SubmitFlush` enqueues a byte[] and pokes an eventfd; io_uring
enqueues an `OutChain` and pokes an eventfd) and the wake/scheduling cost per flush - not the copies.

**The managed backend is one step from BYO, and it is the cheapest step available.** It already sends
directly from the buffer it accumulated into - no staging copy, unlike epoll - so the only thing between
it and zero-copy is that `WriteAll` accumulation. `SocketAsyncEventArgs` takes a `BufferList` of
`ArraySegment`s, which is exactly the shape of a `ReadOnlySequence`, and needs **no pinning** because the
SAEA handles that. That is the same mechanism Kestrel's own transport uses. Untested and unbuilt.

### Headline numbers, Linux (bare metal, Ryzen 9 7900X, Pop!_OS 24.04, kernel 7.0.11)

**Small messages** - `bench/run-matrix.sh`, `-c 128`, 2-byte responses, median rps, 3 scored passes:

| leg | best rps | vs Kestrel |
|---|---:|---:|
| **iouring** (s8) | **823,328** | **+5.6%** |
| epoll (s12) | 797,725 | +2.4% |
| kestrel | 779,303 | - |
| **iouring+tls** (s12) | **702,829** | **+22%** |
| epoll+tls (s12) | 689,190 | +20% |
| iouring+ktls (s12) | 597,486 | +3.7% |
| kestrel+tls | 576,069 | - |

**Payload sweep through the ASP.NET bridge** - goodput MiB/s, 12 shards, `-c 64`. Rows marked \* are the
six-pass re-measurement of 2026-07-28; the rest are three-pass from the sweep earlier that day, whose
64KB/256KB cells reproduce within ~2%, which is what licenses quoting its smaller payloads:

| payload | kestrel | kestrel+tls | epoll | iouring | epoll+tls | iouring+tls | iouring+ktls |
|---|---:|---:|---:|---:|---:|---:|---:|
| 512 B | 346.6 | 255.6 | 338.9 | 332.0 | 290.2 | 285.4 | 249.9 |
| 4 KB | 2,422.6 | 1,676.2 | 2,385.8 | 2,339.2 | 2,032.1 | 1,914.6 | 1,753.1 |
| 16 KB | 7,351.3 | 4,448.6 | 7,122.3 | 7,053.9 | 5,722.7 | 5,333.2 | 4,315.0 |
| 64 KB\* | 10,060.9 | 6,631.4 | **10,485.8** | **10,495.8** | 9,148.8 | 8,495.0 | 7,279.0 |
| 256 KB\* | **12,450.5** | 8,026.5 | **7,739.1** | 7,882.6 | **4,830.6** | 3,943.8 | 5,486.7 |

The 64KB and 256KB rows are the 2026-07-29 sweep, *after* the `OutboundConnection` copy removal - so the
two epoll cells at 256KB are +16.3% and +11.2% on what this table held the day before, and every other
leg is a within-session control that did not move. The pre-copy-removal values are kept in the test
section below rather than here, so this table always shows current behaviour.

> **STALE for io_uring as of 2026-07-31, and the reason is instructive.** These io_uring cells were
> measured while `--byo` was OPT-IN, so `run-tls-sizes.sh`'s `--io-uring` leg (which passes no `--byo`)
> measured the CLASSIC copy bridge. Commit `29da643` then made BYO the default, so the same command now
> measures the zero-copy path. Re-measured 2026-07-31 (see "Linux flat-check" below): io_uring 256KB goes
> **7,882.6 → 11,586.6 (+47.0%)**, small payloads drift **-2 to -5%** (byo pipe overhead where there is no
> large copy to save). epoll and every non-io_uring cell are unchanged. Do not quote the io_uring row here
> as current.

**Bare transport, no bridge**, same 12 shards and load - the control that localises the 256KB collapse.
Re-measured 2026-07-29 after the `OutboundConnection` copy removal, which lifted bare epoll too:

| payload | io_uring | epoll |
|---|---:|---:|
| 64 KB | 10,606.2 | 10,759.4 |
| 256 KB | 10,352.0 | **12,971.1** |

Bridge cost at 256KB is therefore **23.9% on io_uring and 40.3% on epoll** - essentially unchanged by the
copy removal, because both sides of the subtraction rose. Copy count does NOT explain why the same bridge
costs epoll nearly twice what it costs io_uring; see the refutation below.

### Headline numbers, Windows — ~~the current picture~~ SUPERSEDED (2026-07-30, IOCP, 12 shards, `-c 64`)

> **SUPERSEDED by the 2026-08-01 table at the top of this file.** Kept for its reasoning — the
> classic-vs-byo comparison is what justified the default flip and is not reproduced elsewhere — but
> **do not quote these absolute numbers**, and do not subtract them from the 2026-08-01 ones: different
> session, and two defaults changed in between (BYO became the default bridge, the pipe pool became
> pinned). The column header "classic *(default)*" below is stale for exactly that reason.

**One session, all legs reshuffled into the same passes, 6 scored passes, zero errors** — so every
comparison in this table is within-session and the vanilla-Kestrel control is a real control rather than
a remembered number. Goodput MiB/s, median [min-max]:

| payload | classic *(default)* | `--byo` | **`--byo --pipe-segment 65536`** | *kestrel (control)* |
|---|---:|---:|---:|---:|
| 512 B | 113.2 | 112.3 | 112.4 | 126.6 |
| 16 KB | 3,317.4 | 3,197.4 | 3,442.2 | 3,558.6 |
| 256 KB | 4,052.8 | 7,422.2 | **10,238.8** | 10,199.4 |
| 1 MB | 2,341.9 | 4,422.0 | **5,640.6** | 4,938.4 |

Against the same-session Kestrel control, ranges disjoint unless stated:

| payload | default bridge | best configuration |
|---|---:|---|
| 512 B | *overlapping* | *overlapping* — ceiling-bound, and every leg spreads 10-18% here |
| 16 KB | **-6.8%** | *overlapping* — parity |
| 256 KB | **-60.3%** | *overlapping* — **parity** |
| 1 MB | **-52.6%** | **+14.2% — SocketSet is FASTER** |

**The BYO bridge became the DEFAULT on 2026-07-31 on the strength of this table** (`--classic` opts
out). The justification is the `classic` vs `--byo` columns above, which are the same code either way -
the flag only chooses which bridge is constructed:

| payload | classic | `--byo` | |
|---|---:|---:|---|
| 512 B | 113.2 | 112.3 | overlapping |
| 16 KB | 3,317.4 | 3,197.4 | overlapping — **byo's median is 3.6% lower here**, the one row that is not a win |
| 256 KB | 4,052.8 | 7,422.2 | **+83%** |
| 1 MB | 2,341.9 | 4,422.0 | **+89%** |

So it is never significantly worse and is enormous where it matters. The 16KB row is stated rather than
buried: it is inside the noise (both legs spread ~10% there), but it is the cell to watch if the bridge
is ever tuned further.

**This is the first time anything in this repo has beaten vanilla Kestrel at a large payload**, and it is
the combined effect of the day's work: the segment cap became a prefix (so zero-copy engages at any
size), the cap itself was split from `MaxSendPages`, and the geometry mechanism let RIO stop being
misconfigured. At 256KB the default bridge is still **-60.3%**, which is the number that matters for
anyone not opting in.

**Two honest caveats.** The 512B and 16KB rows are noisy here (10-18% per-leg spread against 4-6% at the
large payloads), which is why almost everything overlaps there — they are not evidence of parity so much
as absence of evidence. And these absolute values sit below the previous section's (10,238 vs 11,394 at
256KB) because that was a different session; **both the SocketSet legs and the Kestrel control moved
together**, which is exactly why only within-session comparisons are quoted.

*And note `--pipe-segment` still earns its keep at 1MB* — `byo-seg64k` beats plain `byo` by **+27.6%**
there and **+37.9%** at 256KB. It is no longer what makes zero-copy *engage*, but fewer, larger segments
per send is a real second effect. Its 3.2x memory bill is unchanged, so `--pipe-pinned` remains its
companion (and costs nothing: the pinned leg is inside the unpinned leg's range at every size here).

### Headline numbers, Windows (2026-07-29, superseded by the table above)

Re-measured 2026-07-29 at 12 shards, 6 scored passes. Both Kestrel controls reproduce the 2026-07-27
figures within ~1%, so the two Windows tables *are* comparable with each other - unlike the Linux rows
above, which are a different OS.

| leg | 16 KB (MiB/s) | 256 KB (MiB/s) |
|---|---:|---:|
| kestrel (bridged) | 3,991.9 | 11,620.0 |
| iocp (bridged) | 3,745.2 | 5,273.3 |
| **iocp, bridged, `--byo --pipe-segment 65536 --pipe-pinned`** | - | **11,557.7**\* (**level** with a same-session kestrel; 346-388 MB at 2048 conns) |
| iocp, bridged, `--byo --pipe-segment 65536` | 9,042.4\* | 11,394.6\* (same throughput, **1.28 GB** at 2048 conns) |
| iocp, bridged, `--byo` (no flag, after the cap split) | - | 8,756.9\* |
| rio (bridged, default 4KB page) | 1,530.9 | 2,108.2 |
| *rio, bare, tuned* (64KB page + 4KB recv, 2026-07-27) | *4,365.8* | *11,030.2* |

\* From `Run-Byo.ps1`, a different session from the rest of the column - so it is the right row to read
for "what can this transport do on Windows", and the wrong one to subtract from the `kestrel` cell.
Its 64KB figure is not comparable with the 16KB column either; it is the 64KB payload, which the sweep
above does not cover.

### How to read all of it

1. **SocketSet beats stock Kestrel at small messages on Linux** - +5.6% plaintext, +22% TLS, disjoint
   ranges. The TLS margin is the more interesting one: our in-transport OpenSSL against `SslStream`.
2. **The TLS lead holds to 64KB and the plaintext legs are level with Kestrel there**, then everything
   inverts at 256KB.
3. **That inversion is the BRIDGE, not the transport.** The bare transport does not collapse - epoll
   *rises* to 11,437 at 256KB. The bridge costs 2.0-2.4% at 64KB and 24.5-41.8% at 256KB, and charges a
   *variable* amount (9-17% spread bridged against 2.7-3.0% bare).
4. **512 B is ceiling-bound** and is not a transport comparison; all seven legs sit inside ~30%.
5. **kTLS trails everywhere**, which is expected rather than damning: loopback has no NIC, so inline
   offload - kTLS's whole point - cannot appear at any size.
6. **RIO is starved, not slow.** At its default page it is the worst leg in any table; at a 64KB page with
   a 4KB receive buffer it is the fastest thing measured on Windows.
7. **Zero-copy send changes the 256KB picture completely, on both backends that can do it.**
   io_uring with `--byo` does **11,536.1** at 256KB against 7,950.2 classic (**+45.1%**), cutting the gap
   to vanilla Kestrel from 36% to **7.3%**. IOCP looked like an exception until 2026-07-29, when the
   reason turned out to be a 64-segment cap declining a 65-segment response - measured, not inferred.
   With that fixed IOCP does **11,136.2** against 4,918.2 classic and lands **-2.4% from a same-session
   vanilla Kestrel**, from -56.9%. **On both backends the large-payload story is the same one**: not
   fewer copies, but no copying path at all.

### The best-measured configuration on Linux today (2026-07-29)

Not a default — a statement of what the code can do, and what it costs. io_uring, 12 shards, `-c 64`:

| payload | default bridge (classic) | **`--byo --pipe-segment 65536`** | vs vanilla Kestrel |
|---|---:|---:|---:|
| 16 KB | 6,950.6 | **7,388.0** (+6.3%) | Kestrel 7,351.3 -> now level/ahead |
| 256 KB | 7,950.2 | **12,363.6** (+55.5%) | Kestrel 12,450.5 -> **within 0.7%** |

That closes the 256KB gap to vanilla Kestrel from **36%** to **0.7%**, from two changes: zero-copy send
(+45.1%) and the pipe block size (+7.5%). Neither is on by default, and the second costs **2.7x resident
memory at 2048 connections**, so this is a large-payload/modest-concurrency profile rather than a
recommendation. 512B and 64KB are unmoved by both.

### Known gaps in this picture

- ~~Nothing on Windows has been re-measured since the 2026-07-28 page-chaining fix~~ **CLOSED
  2026-07-29** - Windows is re-measured, and IOCP's zero-copy send turned out to be the large one
  (+117.3% with `--pipe-segment`, +61.1% without after the cap split). **What remains open there:** which
  of the six commits between the 2026-07-27 and 2026-07-29 baselines owns the +17.6% on the classic
  bridged leg (it is not `dd8cdce` - that A/B is flat), and `--pipe-segment`'s memory bill on Windows,
  which is 2.7x RSS at 2048 connections on Linux and has never been measured here.
- **The managed fallback appears in none of these tables**, despite being what actually runs wherever
  io_uring is unavailable (Docker's default seccomp profile blocks it).
- **`ReceiveBufferSize` does not reach the managed backend** (`ManagedSocketShard` reads `BufferPageSize`).
- **Linux bridged legs below 64KB have not been re-run at six passes.** The fix cannot affect them (see
  item 1), but they are three-pass numbers.

## Outbound half-pipe A/B — a flat ~3–5% throughput win at 1 KB, NOT the concurrency-contention story I pre-registered (2026-08-01, branch `cyclebuffer-halfpipe`)

`bench/run-halfpipe.sh`, io_uring, 12 shards, `/payload?n=1024`, REPS=7 → 6 scored passes, same session,
shuffled leg order, banner-gated (`half-pipe=1` / `byo=off` / `byo=pipe`). Three legs: **classic** (outbound
`Pipe` + ThreadPool pump), **byo** (transport-driven pipe, zero-copy send), **half-pipe** (`CycleBuffer`
`PipeWriter` draining to `Connection.Send` on Kestrel's flush thread — no pump, no hop; copies on send).
Median rps, min-max of 6 scored passes:

| c | byo | classic | half-pipe | half-pipe vs classic | ranges |
|---|---:|---:|---:|---|---|
| 64  | 656,782 [650–669k] | 666,387 [653–683k] | **702,713 [684–717k]** | +5.5% | disjoint (barely) |
| 128 | 717,958 [709–727k] | 734,856 [726–751k] | **758,033 [757–774k]** | +3.2% | **disjoint** |
| 256 | 756,852 [744–767k] | 777,671 [767–788k] | **807,271 [781–826k]** | +3.8% | overlap (don't quote) |

Half-pipe also beats byo everywhere (+5.6–7.0%): at 1 KB the transport's zero-copy send is not worth its
iovec/pin overhead against a cheap copy, so byo is the SLOWEST leg here.

**The pre-registered hypothesis is NOT supported.** I predicted the win would GROW with concurrency because
N per-connection pump tasks contend more as `c` rises (TODO "Two half-pipes" #1). It doesn't: the lead is
roughly FLAT (+5.5 / +3.2 / +3.8%), if anything largest at c64. So the gain is a **per-request machinery
saving** — cheaper `CycleBuffer` cycle (measured 2.2–3.5× vs `Pipe` in isolation) + no pump `Task` + no
ThreadPool hop — not concurrency-contention relief. The specific mechanism I bet on was wrong; the flat
win is the finding.

**And it costs tail latency, worsening with concurrency** — the opposite direction from throughput, because
the drain + `Send` now runs synchronously on the Kestrel request thread (the pump offloaded it to the
ThreadPool). p99 (µs), median of 6:

| c | classic p99 | half-pipe p99 | penalty |
|---|---:|---:|---|
| 64  | 449 | 519  | +16% |
| 128 | 835 | 1058 | +27% |
| 256 | 1490 | 2330 | +56% |

**Bottom line:** a real but modest throughput win at small payloads (range-clean at c64/c128, overlapping
at c256), bought with a growing p99 penalty. **Caveats:** `powersave` governor (relative comparisons sound,
absolute low); single-box loopback; this is the outbound half only — the inbound `PipeReader` half (real
backpressure) is not built.

### The size crossover — half-pipe wins small→mid, BYO retakes at 256 KB (2026-08-01, `bench/run-halfpipe.sh` SIZES sweep, c64, 6 scored passes)

Same rig, same session, fixed c64, sweeping payload. Median rps; "vs" is half-pipe over that leg; range-clean
unless noted:

| payload | byo | classic | half-pipe | hp vs classic | hp vs byo |
|---|---:|---:|---:|---|---|
| 256 B | 669,793 | 685,938 | **719,957** | +5.0% | +7.5% |
| 1 KB | 654,529 | 660,298 | **702,291** | +6.4% | +7.3% |
| 4 KB | 586,454 | 596,322 | **636,210** | +6.7% | +8.5% |
| 16 KB | 449,318 | 448,318 | **463,732** | +3.4% | +3.2% |
| 64 KB | 165,363 | 165,235 | 167,774 | +1.5% (overlap) | +1.5% (overlap) |
| 256 KB | 50,840 | 29,508 | 32,513 | +10.2% | **−36.0%** |

**The crossover is real and sharp.** Half-pipe is the throughput winner across 256 B–16 KB (+3–8.5%, ranges
disjoint), a three-way wash at 64 KB, and at 256 KB **BYO's zero-copy send dominates** (12,710 vs 8,128
MiB/s) — the copy the half-pipe reintroduces finally costs, exactly as pre-registered. Note half-pipe still
beats *classic* at 256 KB (+10%, both copy), but both copy-legs are ~35–45% behind BYO. So: **half-pipe for
the small-to-mid API/JSON range, BYO for large payloads** — and both are runtime toggles, so pick per
workload. (p99 tax holds across sizes: +12–18% at small-mid.)

### Allocation/RSS — a WASH, so the win is CPU/scheduling, not GC (2026-08-01, `bench/run-halfpipe-alloc.sh`, 1 KB, 1M reqs/leg)

`GC.GetTotalAllocatedBytes` + `CollectionCount` diffed over a FIXED request count, one leg per process:

| leg | gen0 | bytes/req | RSS MB |
|---|---:|---:|---:|
| classic | 192 | 1343 | 137 |
| byo | 213 | 1482 | 130 |
| half-pipe | 193 | 1354 | 137 |

Half-pipe allocates the SAME as classic per request (the CycleBuffer's zero-alloc steady state is a small
fraction of ASP.NET's per-request allocation, which dominates); BYO allocates slightly MORE (its zero-copy
send bookkeeping). So the "leaner machinery → fewer allocations" claim does **not** hold — the half-pipe's
throughput win is CPU-cycle/scheduling (no pump task, no ThreadPool hop, cheaper buffer ops), not GC
pressure. Honest correction to the isolation-bench framing.

## FIXED 2026-07-30: the access violation was a stale RIO request-queue handle

`closesocket` destroys the RIO request queue, but `conn.Rq` was only cleared in `TryFinalize` — which
early-returns whenever an op is still in flight, i.e. the normal case under churn. `FlushCommits` guards
on `Socket != 0 && Rq != 0`, and `Socket` is deliberately held non-zero as the claimed marker until
finalize, so both guards passed while the queue was already gone and it posted `RIO_MSG_COMMIT_ONLY`
against a dead handle. Zeroing `Rq` where the socket actually closes fixes it; `ArmReceive`/`IssueSend`
gained the same guard because a post-close completion can still reach them.

**100 runs (4 configs x 25), zero crashes, against ~50 expected at the pre-fix rate.**

**The bisection that found it is the transferable part** — no debugger, no symbols, just removing one
variable at a time from the churn cell and re-measuring the rate (`bench/Bisect-RioChurnCrash.ps1`):

| variant | crash rate | what it ruled in or out |
|---|---:|---|
| baseline | 4/8 | - |
| **`--sockets 4096`** | **0/8** | needs slot REUSE - and it does not reduce concurrency, so this is a lifetime signal, not a load one |
| graceful close (no RST) | 4/8 | does NOT need the abortive path |
| `-n 1` (one shard) | 0/8 | needs multiple shards |
| `--close-after 64` | 0/8 | scales with CLOSES, not traffic |
| `-c 8` | 0/8 | needs many concurrent racers |
| rio plaintext *(control)* | 0/8 | TLS-only confirmed |
| iocp+tls *(control)* | 0/8 | RIO-only confirmed |

**And one signal still does not fit**, which is recorded rather than smoothed over: the tight-table
dependence should not matter to a stale-`Rq` window. Either slot reuse merely shortens the window, or a
second lifetime bug is being masked. See TODO item 0e.

*The pre-fix investigation follows.*

## An ACCESS VIOLATION in RIO+TLS under churn, on the default configuration (2026-07-29)

**Found while validating an unrelated change, and it is the most serious thing in this file.** The
process dies with **0xC0000005**, intermittently, usually inside one second, under
`--rio --tls-schannel` connection churn. No managed exception: `### UNHANDLED ###` never prints, so this
is a fault in unsafe/native-interop code.

**Re-measured 2026-07-30 with a stuck Firewall dialog cleared** — the first version of this table was
taken with one pending against the binary under test (a documented 2.8x confounder) and understated the
rate about threefold. 8 reps per cell, both builds:

| page | write-buffers | pristine `e104568` | today's HEAD |
|---:|---:|---|---|
| **4096** | **1024** *(the DEFAULT)* | **5/8 crash** | **4/8 crash** |
| 4096 | 512 | 4/8 crash | 4/8 crash |
| 4096 | 128 | - | 2/8 crash |
| 4096 | 64 | - | 0 crash, **8/8 wedge** |
| 65536 | 512 | 3/8 crash | 3/8 crash |
| 65536 | 256 | 3/8 crash | 6/8 crash |

**Roughly one run in two, on the configuration the library ships** - and the pristine build crashes at
least as often as the current one, which is what makes "predates 2026-07-29" solid rather than inferred.
Neither page size nor pool depth causes it. Full analysis and where to start: TODO item 0e.
`bench/Repro-RioChurnCrash.ps1` reproduces it with nothing but an exit code.

**Why no benchmark in this file ever caught it**, which is the transferable part: every rig here holds
keep-alive connections and measures steady state. **Not one of them churns connections.** The only thing
that opens and closes sockets in anger is the smoke matrix's `churn` cell, which runs once per invocation
against an ~1-in-6 fault. An intermittent crash under a suite you run once is indistinguishable from a
flaky harness - and that is exactly how it first read ("no churn result line"). `Run-SmokeMatrix.ps1` now
names crash exit codes instead of letting them fall through as missing output.

### Firewall-confounder audit of everything above (2026-07-30)

A stuck Windows Firewall dialog was cleared on 2026-07-30, and nobody knew how long it had been pending.
That is a documented 2.8x confounder here, so rather than assume, this is what was at risk and how each
part was cleared. **The exposure is path-shaped**: the dialog fires for binaries at paths Windows has not
seen before, so the question for any result is "did it run from a fresh path?"

| result | ran from | verdict |
|---|---|---|
| item 0e crash rates | `%TEMP%\av-check\pristine` (**new**) | **AFFECTED — re-measured**, rate was understated ~3x |
| the three `dd8cdce` A/Bs | `%TEMP%\ss-ab\*` (**new**) | cleared by level agreement, below |
| the pin-handle A/Bs | `%TEMP%\ss-ab\*` (**new**) | cleared by level agreement, below |
| smoke matrices | repo `Release` (in use all day) | not at risk — a blocked socket fails cells, it cannot fake a PASS |
| `Run-TlsSizes`, `Run-Byo`, `Measure-PipeMemory` | repo `Release` | cleared by control agreement, below |

**The repo-path runs are cleared by their own controls.** `Run-TlsSizes`' vanilla-Kestrel legs reproduced
the 2026-07-27 figures within ~1% (3,991.9 vs 4,007.7; 11,620.0 vs 11,488.9). A 2.8x throttle cannot hide
inside a 1% agreement with a two-day-old number.

**The worktree A/Bs are cleared by level agreement with a repo-path run.** They measured the classic
bridged IOCP leg at 256KB at 5,083 / 5,249 / 5,145 MiB/s across three runs; `Run-TlsSizes` measured the
same leg at **5,273.3** from the allow-listed path on the same day. Same number, different paths, so the
worktree binaries were not being throttled - and all three A/Bs agreed with each other anyway. The
"copy removal is worth nothing on Windows" conclusion stands.

**What the confounder did change** is only the item 0e frequency table, which is corrected above - and it
made the bug *worse* than recorded, not better.

## The receive path, measured for the first time — and it retires item 7's premise (2026-07-31)

**No rig in this repo had ever measured the inbound path.** `/echo` has consumed request bodies since the
demo was written and **not one harness had ever POSTed to it**: every benchmark sends a small request and
scores a large response. That is the same blind spot that let item 0e hide behind a suite that never
churned connections, and it meant the receive-side work was about to be done blind.

`bench/Run-Upload.ps1` (new) fixes it. POST `/echo`, goodput scored on the REQUEST body — **not
comparable with the response tables**. IOCP, 12 shards, `-c 64`, 6 scored passes, same-session Kestrel
control, zero errors:

| body | classic | byo | byo-pin | *kestrel (control)* | best vs kestrel |
|---|---:|---:|---:|---:|---|
| 4 KB | 697.7 | 699.0 | 702.0 | 735.2 | **-4.5%** (disjoint) |
| 64 KB | 1,650.6 | 1,721.6 | **1,832.5** | 1,750.8 | *overlapping* |
| 1 MB | 1,256.2 | 1,343.2 | 1,390.2 | 1,514.0 | *overlapping* |

### What this retires

The standing story was: *"Kestrel is zero-copy in BOTH directions while we still copy inbound, and copy a
second time into `_staged` under backpressure — that is the last structural term."* It is the stated
premise of order-of-work item 7 (zero-copy receive + receive parking), described there as the largest
remaining item.

**Measured, the inbound gap to Kestrel is at most ~5%, and is only disjoint at 4KB.** At 64KB and 1MB
the ranges overlap — SocketSet is not measurably behind at all, and `byo-pin`'s 64KB median is *above*
Kestrel's.

And the second copy, which parking exists to remove, is **almost never paid** (`SS_BRIDGE_STATS=1`, new):

| body | receives | flushes that went async | **staged (second copy)** |
|---|---:|---:|---:|
| 4 KB | 1,884,842 | 0 | **0** |
| 64 KB | 2,240,617 | 0 | **0** |
| 1 MB | 1,616,016 | 2,719 (0.2%) | **1,721 — 0.1% of receives, 0.7 of 6,289 MiB** |

**So flushes complete synchronously essentially always, and staging costs 0.011% of inbound bytes at
worst.** Receive parking cannot be justified as a throughput optimisation; there is nothing there to
win. Zero-copy receive removes copy #1, whose total available prize is the ≤5% above — and only part of
that gap is the copy.

**What remains true, and is now the ONLY argument for parking:** inbound backpressure is *advisory*
today. A flush that does not complete synchronously is observed asynchronously and the writer keeps
accepting bytes, so a slow consumer is not actually throttled. That is a **correctness/semantics** gap,
not a performance one, and it should be argued and scheduled on that basis. The 0.2%-async figure above
also says how rarely it currently bites.

## Windows validation after the OS switch (2026-07-29)

Windows had run nothing since 2026-07-27 while shared code changed underneath it on Linux. This is the
catch-up: correctness first, then the one experiment that had a measured explanation waiting for it.

### The `OutboundConnection.Flush` hand-off is correctness-clean on Windows

`dd8cdce` stopped `Flush` renting a snapshot and copying into it; it hands over the accumulator's own
pooled array (`PooledBufferWriter.TakeArray`). `EpollConnection` **and `WindowsConnection`** both derive
from `OutboundConnection`, so IOCP and RIO inherited that change on Linux and had never executed it. The
hazard was specific: the handed-over array can be **much larger than the `length` argument** (it grows by
doubling) where the old snapshot was `Rent(length)`, so any consumer inferring payload size from
`data.Length` would over-send.

`bench/Run-SmokeMatrix.ps1` (new, and the first scripted form of the correctness gate AGENTS.md has
always asked for by hand): IOCP / RIO / managed x plaintext / SChannel TLS x out-of-band verify at
64KB/1MB/4MB, echo-verify on the callback and pipe paths, poke, and churn. **47 of 48 cells PASS,
mismatches zero everywhere.** The 4MB out-of-band cell is the sharpest one - it is the `Flush` path, at a
payload where the handed-over array is substantially larger than `length`.

The one failure is **not** this change; see below.

### The one FAIL: RIO+TLS out-of-band send is starved at the default page, and it pre-dates the change

`rio+tls/verify-oob-4m` delivered 7,815,168 of 12,582,912 bytes inside the harness's 15s deadline, with
**zero mismatches** - a rate problem, not a corruption one.

Bisected against `dd8cdce^` in a worktree, which **also fails** (11,747,328/12,582,912). Interleaved A/B
of the 1MB cell, 5 passes each: pre 2.68-5.18s, post 2.68-5.20s. **Ranges overlap completely, so there is
no quotable delta** - the hand-off neither caused nor measurably moved this.

What it is instead, measured on the same host in one session:

| leg | 3MB out-of-band verify | note |
|---|---:|---|
| rio+tls, page 4096 (default) | 2.68-5.20s (5 passes) | ~0.6-1.1 MiB/s |
| **rio+tls, page 65536** | **0.21-0.22s** (3 passes) | ranges fully disjoint, **15-25x** |
| rio, page 4096 *(control)* | 0.08s | so it is TLS-specific |
| iocp+tls, page 4096 *(control)* | 0.22s | so it is RIO-specific |

#### RETRACTED, same day: the "encrypted record must fit the page" mechanism below is WRONG

The section that follows was committed as a diagnosis and is refuted by its own confirming test. Read the
retraction first; the original is kept because the four *negative* results in it stand and are what
remains true.

**The model said** the stall is "one encrypted record does not fit one send page", the record being the
application's write size plus ~29 bytes of framing. That predicts the cliff MOVES with the write size.
`--verify-seg` was added to test exactly that (4 reps per cell, `--recv-buffer` pinned at 4096):

| | page 4096 | page 8192 |
|---|---|---|
| seg=1000 (~1029 B record - **model says fast at 4096**) | **2.34-5.07s** | 0.18-0.53s |
| seg=7000 (~7029 B record) | 2.71-7.39s | 0.18-0.54s |
| seg=15000 (~15029 B record - **model says slow at 8192**) | 2.10s | **0.18-0.53s** |

**Both predictions fail.** A 1KB record that fits a 4KB page eight times over is still slow at 4096; a
15KB record that cannot fit an 8KB page at all is already fast at 8192. The cliff sits between a 4096 and
an 8192 byte send page and **does not move with the application's write size**, so record framing is not
the mechanism.

**And it is not the pool depths either**, which `--page` silently rescales (all three, to 4MB/page), so
every "page" result had always also been a "pools" result. Crossed over, 4 reps each:

| | pools 1024 (4k-native) | pools 512 (8k-native) |
|---|---|---|
| **page 4096** | 2.06-5.50s | 3.03-5.20s |
| **page 8192** | 0.51-0.53s | 0.18-0.56s |

The effect follows the **page size** cleanly and the pool depth not at all.

**So what is established is a set of exclusions, and the mechanism is open.** It is the send page, at a
step between 4096 and 8192; it is TLS-only (plaintext RIO at the same page does 12.58MB in 0.15s) and
RIO-only (IOCP+TLS at page 4096 does it in 0.24s); it is **not** pool depth in either direction, **not**
the TLS record size, **not** the receive buffer (worth 2.6x on its own against the page's ~25x), and
**not** a busy or over-kicking loop. Anyone picking this up starts from a much smaller search space than
"it is slow", and should not start from the paragraph below.

*The original write-up follows, for the negative results and the method.*

#### SUPERSEDED: the send page must hold a whole ENCRYPTED RECORD, and 4KB does not

Added `SS_RIO_STATS=1` - RIO was the only backend with no instrumentation at all (io_uring has
`SS_URING_STATS`, IOCP now has `SS_IOCP_STATS`). Four things fell out, in order.

**1. The loop is not busy; it is waiting.** 12.58MB through `rio+tls` at a 4KB page: 1,683 `RIOSend`s at
4,086 B/send, `commits 1.00/send`, **`notify-rearms 0.17/send`, `port-wakes 0.17/send`**, 2.0 completions
per send. A loop that was spinning or over-kicking would not look like that, and ~8ms per send is far too
slow to be syscall cost.

**2. Not pool depth.** `--write-buffers 4096`, `--oob-write-buffers 4096`, and both: 2.73-3.60s against a
2.68-5.20s baseline. Stage-and-retry is not what is waiting. Hypothesis refuted.

**3. `--page` moves two things at once, and the note above did not separate them.** The receive buffer
follows `--page` unless `--recv-buffer` overrides it. Split (3MB out-of-band verify, 3 reps):

| send page | recv buffer | time |
|---:|---:|---|
| 4096 | 4096 *(default)* | 3.03, 4.58, 6.15s |
| 4096 | 65536 | 1.15, 1.78, 1.77s |
| **65536** | **4096** | **0.18, 0.18, 0.18s** |
| 65536 | 65536 | 0.21, 0.20, 0.23s |

**The send page is the dominant term (~25x); the receive buffer is worth ~2.6x on its own.** The original
attribution was right and the confound was real but secondary.

**4. The cliff is a RECORD boundary, not a smooth quantisation.** Send-page sweep, `--recv-buffer` pinned
at 4096 throughout:

| send page | 4096 | 8192 | 16384 | 32768 | 65536 |
|---|---|---|---|---|---|
| time | 3.01-4.87s | 0.18-0.53s | 0.18-0.19s | 0.18-0.20s | 0.18s |

**One doubling does almost all of it and it is flat thereafter** - a step, not a slope, which is what
names the mechanism. `--verify` writes 7,000-byte segments; those encrypt to a **~7,029-byte record**.
That does not fit a 4KB page, so it goes as two sends - and **the receiver cannot decrypt a partial
record**, so it stalls until the second arrives. With one send in flight per connection that is a stall
per record. It does fit an 8KB page. Plaintext never pays it, because a partial buffer is immediately
usable by the receiver.

*Pre-registered and only partly right:* the guess was that the step would land at 16384, SChannel's
maximum record size. It lands at **8192**, which says the size that matters is **the caller's write size
plus overhead**, not the protocol maximum. Varying the application write size and watching the step move
with it is the confirming test, and is not yet done.

**The rule:** on RIO with TLS the send page must exceed one encrypted record, and the record is sized by
what the application writes per call. Scatter-gather would dissolve it, and RIO cannot have scatter-gather
(`maxSendDataBuffers` capped at 1). This is a far stronger argument for the page-size default (TODO item
0) than the throughput numbers were, and it is the **only one that is a correctness-gate failure** rather
than a tuning result.

### The IOCP zero-copy probe, and the 65-segment decline confirmed on Windows

IOCP had no equivalent of io_uring's `zero-copy=` counter, so the experiment below could not have been
interpreted: a fast path that silently declines measures identically to one that ran and did not pay
(`bench/README.md` rule 2). Added `SS_IOCP_STATS=1` to `IocpShard`, mirroring `SS_URING_STATS` - gated on
a `static readonly bool` so the default build pays a never-taken branch, dumped on a 2s timer as well as
at shutdown because rigs kill the server.

40 x 256KB responses, `--iocp --byo`, 12 shards:

| leg | zero-copy sends | declined too-fragmented | mean segs | max segs | copying-path WSASends |
|---|---:|---:|---:|---:|---:|
| `--byo` (default ~4KB pipe blocks) | 2 | **40** | **65.00** | **65** | 80 (2,600 WSABUFs) |
| `--byo --pipe-segment 65536` | 42 | **0** | - | - | **0** |
| classic, no `--byo` *(control)* | 0 | 0 | - | - | 82 |

**Every 256KB response declined, at exactly 65 segments against a cap of 64.** Off by one. This was
inferred from io_uring's segment counter on 2026-07-29; it is now measured on Windows directly, and the
64KB pipe block takes the decline count to zero and silences the copying path entirely.

### THE EXPERIMENT: IOCP zero-copy is worth +117.3% at 256KB once it can actually run

`bench/Run-Byo.ps1` (new), IOCP, 12 shards, `-c 64`, 7 passes with the first discarded, legs reshuffled
each pass, zero errors across the sweep. Goodput MiB/s, median of 6 scored passes:

| payload | classic *(default)* | byo | **byo-seg64k** | classic-seg64k *(control)* |
|---|---:|---:|---:|---:|
| 64 KB | 8,264.0 [8173-8379] | 8,298.4 [7817-8474] | **9,042.4** [8851-9188] | 8,299.1 [8095-8446] |
| 256 KB | 5,177.4 [5092-5268] | 5,243.6 [5055-5507] | **11,393.3** [10878-11564] | 5,269.5 [5021-6004] |

| comparison | 64 KB | 256 KB |
|---|---:|---:|
| **byo-seg64k vs byo** | +9.0% | **+117.3%** |
| **byo-seg64k vs classic-seg64k** (zero-copy alone, block size held equal) | +9.0% | **+116.2%** |
| classic-seg64k vs classic (**pipe block size alone**) | *ranges overlap* | *ranges overlap* |
| byo-seg64k vs classic (both changes vs default) | +9.4% | **+120.1%** |

**The pre-registered prediction was right, and by more than it asked for.** It expected "something like
io_uring's +45.1%" if the cap was the explanation. It is 117%, and the counter column proves the
mechanism rather than inferring it: the `byo` leg declined 194,804 responses at mean 65.00 segments while
`byo-seg64k` took 425,734 zero-copy sends and declined none.

**The control is the important half, and it separates Windows from Linux.** Pipe block size *on its own*
moves nothing here at either payload - overlapping ranges, and 18.6% spread on `classic-seg64k` at 256KB.
On Linux the same flag was worth **+7.5% at 256KB** independently of zero-copy. So the two platforms want
`--pipe-segment` for different reasons: on io_uring it is a genuine second effect, on IOCP it is *purely*
the enabler that gets the response under the 64-segment cap. Without this leg the +117% would have been
apportioned wrongly between two changes.

**At 64KB both byo legs already take the fast path** (0 declines either way - a 64KB response is ~17
segments, well under the cap), so the +9.0% there is *not* engaging-vs-declining. It is fewer, larger
segments per send: ~17 pins and 17 `WSABUF`s become ~2. Smaller effect, different mechanism, same flag.

**What this does NOT yet establish**: where the tuned configuration sits against vanilla Kestrel. The
2026-07-27 Windows table has kestrel at 11,488.9 MiB/s at 256KB, which is tantalisingly close to 11,393.3
- but that is a **cross-day comparison** and this file has produced confident nonsense from those before.
`Run-Byo.ps1` has since gained a `kestrel` leg so the control runs in the same reshuffled passes; **that
has now run - see two sections down, and the answer is -2.4%.**

### Splitting the zero-copy cap from the write-page cap: +61.1% with NO flag, and a tail-latency bill

The +117.3% above needed `--pipe-segment 65536`, which configures *Kestrel's* pipes from the *demo's*
command line. A library caller with its own pipes still fell off the same cliff. `MaxSendPages` was
bounding two unrelated things through one constant: how many **pooled write pages** one send may span
(an internal, OS-shaped limit) and how fragmented the **caller's sequence** may be (a caller-shaped one).
Split into `MaxZeroCopySegments = 256`, with `ZcPtrs`/`ZcLens` allocated on **first use** rather than per
connection - which makes the larger cap cost *less* than the old eager 64, since a callback-path
connection now allocates nothing at all instead of 768 bytes.

Same rig, 6 scored passes, `--iocp`, 12 shards, `-c 64`. The two untouched legs are the controls:

| leg | before the split (MiB/s) | after the split (MiB/s) | |
|---|---:|---:|---|
| **byo** (default ~4KB pipe blocks, **no flag**) | 5,243.6 [5055-5507] | **8,447.9** [8228-8578] | **+61.1%, disjoint** |
| byo-seg64k *(control)* | 11,393.3 [10878-11564] | 11,136.2 [11003-11230] | ranges overlap: unchanged |
| classic *(control)* | 5,177.4 [5092-5268] | 4,918.2 [4724-5219] | ranges overlap: unchanged |

Both controls hold across the two sessions, which is what licenses reading the `byo` row. The counter
confirms the mechanism rather than inferring it: declines go from 194,804-at-mean-65.00 to **zero**, with
the default pipe configuration and no flag.

**And with a same-session vanilla-Kestrel control, the headline is finally sayable:**

| leg @ 256KB | goodput | vs kestrel |
|---|---:|---:|
| kestrel *(control, same session)* | 11,411.5 [11385-11480] | - |
| **iocp `--byo --pipe-segment 65536`** | **11,136.2** [11003-11230] | **-2.4%** (disjoint) |
| iocp `--byo` (no flag) | 8,447.9 [8228-8578] | -26.0% |
| iocp classic *(default)* | 4,918.2 [4724-5219] | **-56.9%** |

**The gap to vanilla Kestrel at 256KB closes from 56.9% to 2.4%** - and unlike every previous version of
that claim in this file, the control ran in the same reshuffled passes.

#### The bill: p99 nearly triples on the no-flag path

| leg | p99 median | range |
|---|---:|---|
| byo-seg64k | 4,509us | [3,000-4,515] |
| kestrel | 6,009us | [3,114-7,162] |
| classic | 6,291us | [5,778-7,209] |
| **byo** (default blocks) | **15,255us** | [13,509-15,690] |

**So the raised cap is a throughput win and a tail-latency regression, in the same change.** 65 pins, 65
`WSABUF`s and one much longer send occupancy per response, against one send in flight per connection, is
head-of-line blocking; at 64KB blocks the same response is ~5 segments and p99 is the *best* in the
table. The honest reading is that the cap raise removes a **silent cliff** - a caller one segment over
the line lost 2.2x with no way to see it - and that large pipe blocks remain the configuration worth
recommending. Do not read +61.1% as "the flag is now unnecessary".

**~~Still a cliff, just moved~~ — REMOVED 2026-07-30, see below.** Measured directly, a 1MB response is
**257** segments and a 4MB response **1,025**, so both still declined at a 256 cap. That is what the
send-a-PREFIX design fixed.

### The segment cliff is GONE: zero-copy send now reports bytes accepted (+80.6% at 1MB)

`Connection.TrySendZeroCopy` returns **bytes accepted** rather than a bool. IOCP sends the first
`MaxZeroCopySegments` segments and says how many bytes that was; `PipeIoBridge` advances its reader by
exactly that much and re-offers the remainder. A cap that used to make a whole response fall back to
copying now just costs an extra send. io_uring keeps all-or-nothing behaviour, unchanged.

Isolated worktrees, interleaved, 6 scored passes, `--byo`, 12 shards:

| payload | before | after | |
|---|---:|---:|---|
| 256 KB | 7,346.8 [7168-7743] | 7,329.0 [7035-7545] | **-0.2%, overlapping** — no cost where the cap already fit |
| **1 MB** | **2,422.0** [2399-2503] | **4,374.1** [4365-4449] | **+80.6%, fully disjoint** |

That is the shape the change was predicted to have: nothing where the sequence already fitted, a large
gain where it did not. The counter shows why, at the default ~4KB pipe blocks:

| payload | segments/response | before | after |
|---|---:|---|---|
| 256 KB | 65 | zero-copy | zero-copy, 0 prefixes |
| 1 MB | 257 | **declined → 200 WSASends copying** | 80 zero-copy sends, 40 prefixes, **copying path silent** |
| 4 MB | 1,025 | **declined → 680 WSASends copying** | 200 zero-copy sends, 160 prefixes, **copying path silent** |

**The copying path is now unused on the pipe path at every size measured.** And `--pipe-segment 65536`
is no longer what makes zero-copy *engage* — it is only a tuning knob for how many segments each send
carries. Its memory bill is unchanged, so where it is used, `--pipe-pinned` is still required.

### The memory bill for `--pipe-segment`, on Windows — and `--pipe-pinned` is not optional after all

`bench/Measure-PipeMemory.ps1` (new; the Windows counterpart of the RSS half of the Linux rigs). Peak
working set under load, 12 shards, 4KB payload, `--iocp`. **Two independent runs at 2048 connections**,
because a claim this consequential should not rest on one:

| leg | 64 conns | 512 conns | **2048 conns** | rps @ 2048 |
|---|---:|---:|---:|---:|
| classic | 228 MB | 258 MB | 404 / 386 MB | 208k / 206k |
| byo | 224 MB | 231 MB | 659 / 453 MB | 225k / 219k |
| **byo-seg64k** | 222 MB | 438 MB | **1,282 / 1,285 MB** | **181k / 175k** |
| **byo-seg64k-pin** | 213 MB | 250 MB | **388 / 346 MB** | 227k / 216k |

**Three things, and the third was not expected.**

**1. The bill is real and it is Windows' too.** `--pipe-segment 65536` takes peak RSS to ~1.28 GB at 2048
connections against ~0.39 GB for the default bridge - **~3.2x** - and the 1,282/1,285 MB pair is the most
reproducible number in this file. Quoted against `byo` rather than `classic` it is 1.95x and 2.84x on the
two runs; the `byo` leg itself swings 453-659 MB, so **quote it against `classic` or in absolutes**, not
as a byo-ratio. Linux's figure was 2.7x, so the two platforms agree on the shape.

**2. At 64 connections it is invisible** (0.99x), which is the same trap the receive-slab table fell into
on 2026-07-28: resident cost scales with **connections x block**, so a small-connection run measures
nothing and reads as "free". Any memory claim here needs 2048.

**3. `--pipe-pinned` removes the bill AND the throughput penalty, which reverses the Linux reading.** On
Linux pinning measured +0.7% and "not separable", so it was filed as a nice-to-have. On Windows the
pinned pool lands at **346-388 MB - at or below `classic`** - and restores throughput to 216-227k against
the unpinned pool's 175-181k. So at 2048 connections the unpinned 64KB block pool is **both the most
expensive and the slowest leg measured**, and pinning is not a refinement of `--pipe-segment`, it is its
**required companion**.

**What this settles for item 2f:** `--pipe-segment 65536` must not be defaulted on its own. The
configuration that is actually defensible is `--byo --pipe-segment 65536 --pipe-pinned`.

#### And the pinned configuration costs nothing at 256KB — it is level with vanilla Kestrel

The gap this file named a paragraph ago (throughput measured at `-c 64`, memory at `-c 2048`, never the
pinned leg at a large payload) is now closed. `Run-Byo.ps1` gained a `byo-seg64k-pin` leg; 6 scored
passes, 256KB, 12 shards, same session, zero errors:

| leg | goodput MiB/s | vs kestrel |
|---|---:|---|
| kestrel *(control)* | 11,715.7 [10592-11828] | - |
| **byo-seg64k-pin** | **11,557.7** [11328-11777] | **ranges overlap: no difference** |
| byo-seg64k | 11,394.6 [11274-11818] | ranges overlap |
| byo | 8,756.9 [8509-8887] | -25.3% |
| classic-seg64k | 5,323.5 [5219-5702] | -54.6% |
| classic *(default)* | 5,276.1 [5161-5467] | **-55.0%** |

**Pinning is free on throughput and worth 3.2x on memory**, so there is no trade to weigh:
`byo-seg64k-pin` and `byo-seg64k` overlap at 256KB (11,558 vs 11,395), and only one of them is 346-388MB
at 2048 connections.

*On the Kestrel comparison, stated precisely:* this run puts the tuned configuration **level** with
vanilla Kestrel (overlapping ranges); the earlier run put the unpinned variant at -2.4% (disjoint). The
difference is the **control**, not us - Kestrel's own leg spread 0.8% in that run and 10.5% in this one.
The defensible claim is *parity at 256KB*, not a signed percentage, and the default bridge's -55.0% is
the number that actually moved.

**And 2b-result's reading is now retracted for IOCP.** "Zero-copy send removed one copy and bought +3.5%,
so copies are not the cost" was measured on a path that declined at the payload of interest. Both halves
of that sentence were true and the conclusion did not follow.

### The Windows baseline, re-measured — and why it does NOT say what it was meant to

`bench/Run-TlsSizes.ps1 -Shards 12 -Sizes 16384,262144 -Repetitions 7`, 6 scored passes, reshuffled leg
order, zero errors across 84 cells. Goodput MiB/s, median [min-max]:

| leg | 16 KB (MiB/s) | 256 KB (MiB/s) |
|---|---:|---:|
| kestrel | 3,991.9 [3990-4014] | 11,620.0 [10843-11779] |
| kestrel+tls | 3,143.9 [3086-3171] | 7,096.6 [6999-7182] |
| iocp/s12 | 3,745.2 [3704-3782] | 5,273.3 [5205-5320] |
| iocp+tls/s12 | 3,331.2 [3259-3395] | 4,683.7 [4614-4817] |
| rio/s12 | 1,530.9 [1521-1535] | 2,108.2 [2081-2127] |
| rio+tls/s12 | 1,446.7 [1413-1462] | 2,182.7 [2172-2194] |

Against the 2026-07-27 table, **both Kestrel controls reproduce to within ~1%** (3,991.9 vs 4,007.7;
11,620.0 vs 11,488.9), which is the check that makes the rest of the column readable at all - the host,
the harness and the client are behaving the same way two days later.

On that basis: IOCP is **+17.6% at 256KB** (4,483.4 -> 5,273.3) and unchanged at 16KB (+0.1%); RIO is
+2.8% and +0.6%.

**But that +17.6% CANNOT be attributed to the copy removal, and §2 of the cold-start plan was wrong to
expect it could.** The plan said "compare against the 2026-07-27 numbers". Six commits touching
Windows-reachable code sit in that window - `963143b` and `ff1a1c1` (the Windows shard factoring),
`efcb1cc` (BYO phase 1, which edits both Windows shards), `30756c2` (IOCP zero-copy **and a backpressure
bug fix**), `be09aed` (write-pool exhaustion: stage and retry instead of closing), and `dd8cdce`. Any of
the last three could move a 256KB bridged number. A cross-day delta over six commits is a changelog, not
an attribution.

The attributing measurement is a same-session A/B of the one commit, on the bridged path - see below.

### FALSIFIED: the copy removal is worth NOTHING measurable on the bridged Windows path

The cold-start plan predicted the opposite, and said why: "the Windows path had *more* copies to start
with (3-4), so removing one should show at least as well" as Linux epoll's +16.3%.

`bench/Compare-Commits.ps1 -Before dd8cdce~1 -After dd8cdce -Bridged -Backend iocp -Sizes 262144
-Shards 12 -Repetitions 7` - isolated worktrees, one commit, same session, sides interleaved and
alternated within each pass. Run twice:

| run | before | after | change |
|---|---:|---:|---:|
| first | 5,083.0 [5064-5392] | 5,124.1 [4929-5451] | +0.8% |
| second (per-measurement warm-up added) | 5,248.8 [5039-5542] | 5,247.4 [4653-5306] | **-0.0%** |

**Both centre on zero with heavily overlapping ranges, so there is no effect to report in either
direction.** What the two runs *can* exclude is an epoll-sized win: +16.3% on the second run's before
median would be 6,105 MiB/s, far outside the after side's entire range.

So **the +17.6% in the table above does not belong to this commit**, and one of the other five in the
window owns it. Which one is not established here.

*A reading, not a measurement:* epoll went from 2 copies to 1 - halving them - for +16.3%. The Windows
bridged path went 3-4 to 2-3, and still stages through `StageOutbound` before draining into write pages.
Removing one of four is a smaller fraction of a larger total, and the staging step it does not remove is
the one unique to this path. That is consistent with the +117.3% above, where what moved the number was
not removing *a* copy but removing the whole copying path.

*Two rig changes came out of this, both in `Compare-Commits.ps1`:* a `-Bridged` switch (it only ever
measured the bare responder, while every headline Windows table is bridged), and **interleaving** - it
used to measure all of `before` then all of `after`, which puts every before-pass earlier in wall-clock
than every after-pass, so any drift lands entirely on one side of the subtraction. The per-measurement
warm-up was added on the theory that it would tighten the 6-10% spread; **it did not** (9.6% and 12.4%
on the second run), so that spread is a property of this measurement rather than of cold processes, and
the rig cannot currently resolve anything below about 10% at 256KB. It is kept because the reasoning for
it is still right; it just was not the cause.

**One anomaly worth recording rather than explaining away:** at 256KB, `rio+tls` (2,182.7 [2172-2194])
is **faster than plaintext `rio`** (2,108.2 [2081-2127]), and the ranges are disjoint. A TLS leg beating
its own plaintext control is the signature `Run-TlsSizes.ps1`'s own header warns about - it means the two
are bounded by different things, not that TLS is free. Both RIO legs are page-quantised (item 0d), which
is the obvious suspect, but this is not diagnosed and should not be quoted as a TLS result.

## Environment (current baseline, 2026-07-27)

| | |
|---|---|
| Host | Windows 11, AMD Ryzen 9 7900X, 12 cores / 24 logical processors, desktop on mains |
| Date | 2026-07-27 |
| Toolchain | .NET SDK 10.0.302, `net10.0`, Release |
| Load generator | bombardier v1.2.6, fasthttp client, same box (loopback) |
| Server | `AspNetDemo`, Release, HTTP/1.1 pinned on every leg |
| TLS certificate | one shared RSA-2048/SHA-256 self-signed cert across **all** legs, Kestrel's included |
| Route | `/plaintext` - minimal work, so the transport is as visible as it can be |
| Pinning | server -> logical CPUs 0-11, generator -> 12-23; `DOTNET_PROCESSOR_COUNT`/`GOMAXPROCS` = 12 |
| Method | `-c 128 -d 15s`, 4 passes in reshuffled order, pass 1 discarded as host warm-up, median of 3 |

Shard counts are swept at **4/8/12**, not 4/8/16: the server half of this box is 12 logical CPUs, so s12
is the leg that matches core count. The older `s16` legs are not the equivalent configuration.

**This host is roughly an order of magnitude more repeatable than the previous one.** Per-leg spreads are
0.2-2.4% where the laptop's were up to 6% and once 58%. A 2% effect is detectable here and was pure noise
there - so the "anything under ~6% is unproven" rule in `bench/README.md` is specific to the old host, and
over-conservative for this one.

## Keep-alive, steady state (2026-07-27)

All legs zero errors. `SpreadPct` is each leg's own run-to-run range - the honesty check.

| leg | med rps | vs kestrel | spread |
|---|---:|---:|---:|
| rio-s12 | 305,984 | +0.3% | 0.4% |
| **kestrel** *(control)* | 304,989 | - | 0.5% |
| iocp-s12 | 302,840 | -0.7% | 0.3% |
| managed | 301,861 | -1.0% | 0.3% |
| rio-s12+tls | 301,171 | -1.3% | 0.9% |
| iocp-s12+tls | 293,799 | -3.7% | 0.2% |
| kestrel+tls | 290,979 | -4.6% | 0.6% |
| managed+tls | 289,817 | -5.0% | 1.0% |
| rio-s8 | 274,122 | -10.1% | 1.3% |
| iocp-s8 | 269,990 | -11.5% | 0.2% |
| rio-s8+tls | 264,116 | -13.4% | 0.5% |
| iocp-s8+tls | 259,484 | -14.9% | 0.5% |
| rio-s4 | 181,583 | -40.5% | 1.3% |
| rio-s4+tls | 174,673 | -42.7% | 0.8% |
| iocp-s4 | 171,945 | -43.6% | 2.4% |
| iocp-s4+tls | 162,801 | -46.6% | 1.6% |

p99 is deliberately omitted from this table. Every leg reported 1,503-1,504µs, which is a measurement
artifact - see "p99 is quantised" below.

### Read this before reading the table above

**At `-c 128` this box is saturated, and the top eight legs are all sitting on the same ceiling.** The
differences between them are not transport differences. See the next section - it is the most important
result of this run, and it makes "SocketSet reaches parity with Kestrel on small messages" an
*unsupported* reading of the top of that table.

What the table does support:

**Shard count is the dominant variable**, by a wide margin. IOCP 171.9k -> 270.0k -> 302.8k across 4/8/12
shards; RIO 181.6k -> 274.1k -> 306.0k. Anyone benchmarking these backends should match shards to
available cores before comparing anything else. Note the s4 and s8 legs are *below* the ceiling and so are
the only ones in this table making a real transport comparison.

**RIO edges IOCP at every shard count**, as it did on the old host: +5.6% at s4, +1.5% at s8, +1.0% at
s12. This reproduces across two machines and is the one small-message ordering worth any confidence.

**Low shard counts are sensitive to background host load.** The s4 legs moved 3.6-5.2% depending on what
else was running on the machine, while the s12 legs moved under 0.5%. At low shard counts each loop thread
is itself the bottleneck, so stolen cycles come straight off throughput; at s12 the work is spread and
absorbed. Quiesce the host before trusting any low-shard-count number.

**TLS cost is NOT usefully measured here.** With the plaintext legs pinned against a ceiling, the apparent
TLS costs (-1.6% for RIO, -3.0% for IOCP, -4.6% for Kestrel) are compressed by however much headroom the
plaintext leg was denied. The honest small-message statement is that this run cannot measure TLS cost. The
256 KB legs in the payload sweep below are not ceiling-bound and are where TLS cost is visible.

## The ~300k ceiling is real, and it is the box (2026-07-27)

This needs stating carefully, because **a ceiling was claimed once before in this document and retracted**
- confounder 3 below, where a pending firewall dialog held everything to ~95k and was misread as a
generator limit. That retraction is why the evidence here is of a different kind, and why it was gathered
before any conclusion was drawn.

**Throughput does not move with offered concurrency.** Each leg run alone, 3 scored passes:

| leg | -c 64 (rps · p99) | -c 128 | -c 256 |
|---|---:|---:|---:|
| kestrel | 289,822 *(p99 1,005µs)* | 303,978 *(1,503µs)* | 299,225 *(2,000µs)* |
| iocp-s12 | 301,051 *(1,188µs)* | 302,862 *(1,503µs)* | 303,441 *(2,000µs)* |
| rio-s12 | 304,122 *(1,503µs)* | 305,498 *(1,503µs)* | 304,971 *(2,001µs)* |

A 4x change in offered concurrency moves throughput by less than the run-to-run spread, while p99 rises in
proportion. That is the textbook signature of a saturated system: added concurrency becomes queueing, not
work. **The knee is at or below `-c 64`, so the `-c 128` default used by every Windows figure in this file
is past it.**

**Both pinned halves saturate together.** Sampling per-core utilisation during a measured window, split
into the server's cores (0-11) and the generator's (12-23):

| leg | rps | server half | client half |
|---|---:|---:|---:|
| kestrel | 300,536 | 90.6% | 89.5% |
| iocp-s12 | 297,922 | 98.1% | 97.7% |
| rio-s12 | 303,804 | 98.4% | 97.9% |

Neither side is *the* bottleneck; they run out together, which is what a loopback test converges to when
both endpoints are CPU-bound on one machine. Unlike the retracted firewall case there is no external agent
involved, the firewall binaries are explicitly allow-listed, and the effect survives a 4x concurrency
sweep.

**Consequence.** Small-message rps on this box, at this operating point, is a property of the host and not
of the transport. Any transport able to reach ~300k reports ~300k. Separating them needs either a load
point below the knee, a second machine, or a workload where the transport is not competing with its own
load generator for the same silicon.

### p99 is quantised at ~500µs on this platform

Observed p99 values across every run land on 1,005 / 1,188 / 1,495 / 1,503 / 2,000 / 2,001µs - clustering
on half-millisecond steps. That is timer granularity in the Go client on Windows, not transport behaviour,
and it is why sixteen different legs all reported an identical 1,503µs. **Do not quote p99 from this
harness below about 2ms.** The larger values in the payload sweep (6.6-14ms) are well above the quantum
and are usable.

### Backends do not idle hot

Server started, listening, zero traffic, CPU sampled over the server's cores:

| leg | idle CPU | idle cores |
|---|---:|---:|
| rio-s12+tls | 2.7% | 0.32 |
| iocp-s12 | 3.5% | 0.42 |
| kestrel | 3.8% | 0.46 |
| kestrel+tls | 3.8% | 0.45 |
| rio-s12 | 4.1% | 0.50 |
| iocp-s12+tls | 6.4% | 0.76 |

Worth recording because the opposite was hypothesised and is intuitive: twelve dedicated loop threads
sounds like it should burn CPU while waiting. It does not - `iocp-s12` idles slightly *below* stock
Kestrel. The shard threads block properly rather than spinning.

## Payload sweep, and RIO's unfixed send path (2026-07-27)

`bench/Run-TlsSizes.ps1 -Shards 12`, `-c 64 -d 8s`, 4 passes reshuffled, first discarded, median of 3.
Goodput MiB/s:

| payload | kestrel | kestrel+tls | iocp/s12 | iocp+tls/s12 | **rio/s12** | **rio+tls/s12** |
|---|---:|---:|---:|---:|---:|---:|
| 512 B | 146.3 | 135.5 | 138.0 | 135.3 | **142.7** | **139.4** |
| 16 KB | 4,007.7 | 3,107.2 | 3,741.1 | 3,326.9 | **1,521.1** | **1,418.9** |
| 256 KB | 11,488.9 | 7,165.1 | 4,483.4 | 4,040.6 | **2,051.6** | **2,123.3** |

Per-leg spreads 0.3-3.3%, zero errors. The 512 B row is ceiling-bound (see above) and should not be read
as a transport comparison; the 16 KB and 256 KB rows are not.

### RIO is 2.2-2.5x behind IOCP at large payloads, and it is a known defect

**RIO leads IOCP at 512 B (142.7 vs 138.0) and trails it 2.5x at 16 KB (1,521 vs 3,741) and 2.2x at
256 KB (2,052 vs 4,483).** A deficit that appears only once the payload exceeds one write page, in exactly
one backend, is not a tuning difference:

- `IocpShard.IssueSendPages` builds a `WSABUF` array and issues one `WSASend` with up to 64 segments.
- `WindowsRioShard.IssueSend` still posts `RIOSend(conn.Rq, &buf, 1, ...)` - buffer count **1** - and
  `CompleteWrite` still coalesces only "as many queued responses as fit into the write page".

So RIO retains the page-quantised send that was diagnosed and fixed for IOCP (see the 2026-07-26 section
below). The fix was described at the time as applying to both; only IOCP received it.

**The plaintext `rio` control is what makes this attributable**, and it was added to the harness for this
run. `rio+tls` tracks plaintext `rio` almost exactly and at 256 KB is marginally *faster* (2,123 vs
2,052). When the cipher is invisible, the constraint is upstream of it - the same reasoning that read the
old 16 KB numbers correctly. Without a plaintext RIO leg this pattern is indistinguishable from "SChannel
is slow on RIO".

**But the IOCP fix does not port, and this was tested rather than assumed.** `RIOSend` takes a buffer
array in its signature, yet the count is fixed at request-queue creation by `RIOCreateRequestQueue`'s
`maxSendDataBuffers`, and Windows accepts only 1 - `2, 3, 4, 8, 16, 64` all return **WSAEINVAL (10022)**.
A full port of `IssueSendPages` was written on 2026-07-27 and every connection failed to establish. So
"RIO has the same shape as WSASend" was wrong, and the 2.2-2.5x is not recoverable by copying `IocpShard`.

What RIO *does* allow is depth: `maxOutstandingSend` accepts 4/16/64. The alternative is K single-buffer
sends in flight instead of one send of K buffers - a different, larger change, and unmeasured. See
`TODO.md` item 0.

### What the scatter-gather fix bought, and what is left

At 16 KB, IOCP now runs within **6.7%** of Kestrel (3,741 vs 4,008). The laptop-era figures had bridged
IOCP at ~1,449 against ~3,740, a 61% deficit. Different machines, so this is directional rather than an
A/B - the controlled measurement is the `Compare-Commits.ps1` run recorded below.

At 256 KB IOCP is still **61% behind** Kestrel (4,483 vs 11,489). That is now the largest open gap in this
file and it is the regime the BYO-buffer work targets.

**TLS cost, measured where it is actually visible.** These legs are not ceiling-bound, unlike the
small-message table:

| stack | 256 KB plaintext -> TLS | cost |
|---|---|---:|
| Kestrel + SslStream | 11,488.9 -> 7,165.1 | **-37.6%** |
| iocp-s12 + SChannel | 4,483.4 -> 4,040.6 | **-9.9%** |
| rio-s12 + SChannel | 2,051.6 -> 2,123.3 | **+3.5%** |

Read with care. Kestrel's -37.6% is a real record-layer cost at a rate where the cipher matters. IOCP's
-9.9% is *partly* real and partly the same "cipher is cheap relative to whatever else binds us" effect;
RIO's positive figure is entirely that effect and is not evidence that TLS is free. A backend has to be
fast enough for the cipher to show up.

## What actually costs at 256 KB: allocation and op-count, not copying (2026-07-27)

Three measurements, run to decide whether to start the BYO-buffer work. They decided against it.

### 1. `fa97dd4` validated: pooling the flush snapshot is worth +27% at 256 KB

`bench/Compare-Commits.ps1 -Before fa97dd4~1 -After fa97dd4 -Shards 12`, isolated worktrees, back to back,
median of 3 scored passes. This A/B had been outstanding since a power loss voided it on the old host.

| payload | before | after | change | before passes | after passes |
|---|---:|---:|---:|---|---|
| 512 B | 152.1 | 152.4 | +0.2% | 152.4, 152.0, 152.2 | 152.1, 152.8, 152.9 |
| 16 KB | 4,088.9 | 4,331.0 | **+5.9%** | 4083.5, 4100.1, 4094.4 | 4334.9, 4341.5, 4327.1 |
| 256 KB | 4,332.1 | 5,501.7 | **+27.0%** | 4389.4, 4302.1, 4362.0 | 5578.3, 5425.1, 5686.5 |

Ranges are disjoint at 16 KB and 256 KB and overlapping at 512 B - the size-dependence a real effect
predicts, and far outside the noise floor.

The mechanism: the old `WrittenSpan.ToArray()` allocated an array the size of the whole response on every
flush. At 256 KB that is past the 85 KB threshold, so **every response allocated on the Large Object
Heap**. Nothing at 512 B, moderate at 16 KB, dramatic at 256 KB.

That commit pre-registered its own interpretation - *"if it moves throughput, allocation was the cost; if
it does not, copies dominate"*. It moved throughput. **Allocation was the cost.**

### 2. The Kestrel bridge costs 14-19% at 256 KB, not ~47%

Bare SocketSet HTTP responder (`SmokeTest --http`, no Kestrel, no pipes) against the same transport
behind the bridge, same client, pinning, payload and shard count:

| backend | bare (MiB/s) | bridged (MiB/s) | bridge cost |
|---|---:|---:|---:|
| iocp/s12 | 5,538.6 | 4,483.4 | **19.0%** |
| rio/s12 | 2,387.2 | 2,051.6 | **14.1%** |

Consistent with the 23% measured on the old host, and not the ~47% share it was last estimated at. Worth
fixing; not the main event.

Incidentally this retires a caveat: `--page 4096` and the default differ only in pool depth (1024 vs 256
buffers per shard), and they measured 2,387.7 vs 2,387.2 MiB/s - 0.02% apart. The pool-depth co-variation
that confounded the original page sweep has no effect at this payload.

### 3. Per-byte copying is not the binding constraint

The out-of-band path copies three times per response, and the BYO-buffer proposal exists to remove them.
Two independent results say those copies are not what costs:

- **Page size moves RIO 4.68x** (2,387 -> 11,180 MiB/s at 256 KB, below). Page size changes the NUMBER of
  segments, not the BYTES copied - 256 KB is 256 KB either way.
- **Removing one allocation moves 256 KB by 27%** (above) while removing zero copies.
- At a 64 KB page RIO does 11,180 against IOCP's best 6,083 while executing an identical copy path. If
  copies dominated, those would converge rather than sit 84% apart.

**Allocation and per-operation cost dominate this path; per-byte copying does not.** BYO-buffer Tier 2
targets the copies specifically, at the price of a completion signal threaded through every backend's
send path - so it is aimed at a cost this data says is not binding. Tier 1's allocation half is already
delivered by `fa97dd4`.

Two limits on that conclusion: this is loopback at one payload size, where memory bandwidth is not
contended as it would be behind a real NIC; and BYO-buffer also removes allocation and GC pressure, which
is a separate benefit these measurements say nothing about.

### 4. Page size x payload: RIO wants a big page at EVERY size; IOCP does not care

Bare responder, `--page` swept against payload, median of 3 scored passes. Goodput MiB/s:

**RIO**

| payload | 4 KB page | 16 KB page | 64 KB page |
|---|---:|---:|---:|
| 512 B | 154.1 | 153.9 | **154.5** |
| 16 KB | 1,642.9 | 2,967.6 | **4,448.9** |
| 256 KB | 2,404.1 | 6,948.8 | **10,968.8** |

**IOCP**

| payload | 4 KB page | 16 KB page | 64 KB page |
|---|---:|---:|---:|
| 512 B | **152.4** | 151.3 | 149.0 |
| 16 KB | **4,357.0** | 4,276.5 | 4,255.6 |
| 256 KB | 5,495.5 | **5,971.7** | 5,873.4 |

**There is no trade-off for RIO.** 64 KB wins at every payload, monotonically, with no penalty at 512 B -
the opposite of the pre-scatter-gather IOCP result that a big page loses on small responses. IOCP is now
page-INSENSITIVE (+-2% at 512 B and 16 KB, 8.7% at 256 KB), which is what scatter-gather buys: the
coalescing moved into the `WSABUF` array, so the page stopped being the unit of transfer. RIO has no
such mechanism, so for RIO the page IS the unit of transfer and its size is everything.

With a 64 KB page RIO beats IOCP at every payload (4,449 vs 4,357 at 16 KB; 10,969 vs 5,972 at 256 KB),
inverting the current standing. RIO is not a weak backend; it has been starved.

### 5. The two sizes were one option, and splitting them makes the win free

The 4.68x above cost 11.2x the memory: a 12-shard RIO server went from **283 MB to 3,163 MB** resident.
The cause was that `_writeBufSize` and `_recvBufSize` both read `Options.BufferPageSize`, and receive
buffers are **one per socket for the connection's lifetime** - at `SocketsPerShard` 4096 across 12 shards
a 64 KB receive buffer is 3.0 GB, against 192 MB at 4 KB. The receive slab was 97% of the growth and
gains nothing from being large; only the SEND page does.

`SocketSetOptions.ReceiveBufferSize` now splits them (0 = follow `BufferPageSize`, so no behaviour change
unless set). Measured, 12 shards, `-c 64`, median of 3:

| leg | 512 B | 16 KB | 256 KB | RSS |
|---|---:|---:|---:|---:|
| rio, 4 KB page | 153.2 | 1,637.0 | 2,367.6 | 283 MB |
| rio, 64 KB page (coupled) | 153.5 | 4,462.8 | 10,835.7 | 3,163 MB |
| **rio, 64 KB page + 4 KB recv** | 153.2 | 4,365.8 | **11,030.2** | **283 MB** |
| iocp, 4 KB page | 151.4 | 4,335.2 | 5,628.5 | 70 MB |
| iocp, 64 KB page + 4 KB recv | 151.6 | 4,345.5 | 5,922.5 | 70 MB |

**The split is free**: full throughput of the expensive config at the memory of the cheap one - 4.66x at
256 KB, 2.67x at 16 KB, no regression at 512 B, zero errors. IOCP gains ~5% at 256 KB at unchanged memory.

### 6. Under load, the LARGE page is the safe one - the current default is not

The write slab is `WriteBuffersPerShard x page`, which is the other memory term (at the library default of
1024 buffers, a 64 KB page is 64 MB per shard - AspNetDemo at 12 shards measured 1,030 MB). Shrinking that
pool looked dangerous, because **write-pool exhaustion closes the connection** (`CloseClient` in
`SendResponse` and `StartPendingSend`) instead of queueing. Measured at 12 shards, 256 KB payload, errors
being the metric that matters:

| config | -c 64 | -c 512 | -c 2048 | errors @2048 | RSS |
|---|---:|---:|---:|---:|---:|
| **4 KB page, 1024 buffers (today's default)** | 2,392.0 | 1,506.3 | 1,281.9 | **208** | 283 MB |
| 64 KB page + 4 KB recv, 1024 buffers | 11,357.4 | 5,189.8 | 3,624.0 | **0** | 1,003 MB |
| 64 KB page + 4 KB recv, 256 buffers | 11,461.2 | 5,474.5 | 3,467.4 | **0** | 427 MB |
| 64 KB page + 4 KB recv, 64 buffers | 11,475.9 | 5,199.7 | 3,477.6 | 1 | 283 MB |

> **RETRACTED 2026-07-28: the error column above is a harness artifact, not a server defect.** The claim
> made here — that the default configuration "drop 208 connections at -c 2048" — is withdrawn. Re-run in
> isolation, that exact configuration served **73,852 requests with zero errors of any kind**. The
> harness that produced the error counts (`Run-PoolPressure.ps1`, written the same day) runs twelve
> 2048-connection cells back to back with **no ephemeral-port gate**, where `Run-Matrix.ps1` has three
> `Wait-Ports` calls for exactly this reason. Windows has ~16k ephemeral ports with a multi-minute
> TIME_WAIT and that run opens on the order of 74,000 connections, so the errors are client-side port
> pressure. This is confounder 2 in this very document, reproduced by the person who wrote the warning.
>
> What survives: the goodput column (the large-page configurations really are 2.7-4.8x faster), and the
> mechanism below. What does not: any claim about connection drops, in either direction.

**The prediction about pool depth was wrong in an interesting way**, independent of the error counts: a
larger page does not starve the pool, it relieves it.

The mechanism, in hindsight: RIO holds exactly ONE write page per in-flight send, and at a 4 KB page a
256 KB response occupies that page across **64 sequential round trips**. At 64 KB it needs 4. Pool
*occupancy time* collapses, so a bigger page relieves pool pressure rather than adding to it. The original
reasoning counted buffers and ignored how long each is held.

So `64 KB page + 4 KB recv + 256 write buffers` is faster at every concurrency tested and costs 144 MB
across 12 shards. (An earlier version of this line also claimed "strictly better error behaviour"; that
rested on the retracted error counts and is withdrawn.)

**Still not changed as a default**, deliberately: these are Windows measurements at one payload shape on
loopback, and `BufferPageSize` is shared with io_uring and epoll where it has not been swept. The knobs
are now plumbed end to end -
`SmokeTest` and `AspNetDemo` both take `--page` / `--recv-buffer` / `--write-buffers`, and `/config`
reports them so a harness can verify the setting actually took.

## End to end: the tuning survives the bridge, and moves the bottleneck (2026-07-27)

Everything above measures the bare responder or an isolated A/B. This is the full path — Kestrel over the
SocketSet transport, `/payload`, 12 shards, `-c 64`, median of 3 scored passes, each leg verified through
`/config`. "tuned" is a 64 KB send page with a 4 KB receive buffer; nothing else differs.

| payload | kestrel | iocp/default | iocp/tuned | rio/default | **rio/tuned** |
|---|---:|---:|---:|---:|---:|
| 512 B | 146.5 | 135.2 | 133.8 | 141.1 | 141.8 |
| 16 KB | 3,963.0 | 3,403.5 | 3,529.2 | 1,484.4 | **3,734.7** |
| 256 KB | 11,259.4 | 4,472.1 | 4,766.8 | 2,023.1 | **6,348.5** |

**The tuning is not lost to the bridge.** RIO at 256 KB goes 2,023 -> 6,348 MiB/s (**3.14x**) and at 16 KB
1,484 -> 3,735 (**2.52x**). Tuned RIO beats tuned IOCP at both sizes, having been the worst leg in the file
that morning. IOCP gains only ~6%, which is the expected result — scatter-gather already made it
page-insensitive.

Against stock Kestrel, RIO closes from **82% behind to 43.6% behind** at 256 KB, and to within **5.8%** at
16 KB. No data-path code changed; this is `--page 65536 --recv-buffer 4096`.

The 512 B row is saturation-bound (~300k rps ceiling, see above) and ranks nothing.

### The bottleneck moved to the bridge

Bare tuned RIO measured **11,030** MiB/s at 256 KB (page x payload matrix above); bridged it is **6,348**.
So the Kestrel bridge now costs about **42%** on the fastest configuration, against the 14-19% measured the
same day on the untuned one. Nothing about the bridge changed — the transport got fast enough that the
bridge became the binding constraint.

This is the second time in this file that fixing a transport bottleneck promoted the bridge to the top of
the list, and it is the number that should drive what happens next: the caller-supplied-pipe work
(`TODO.md` item 2b) now has a measured 42% target on the best configuration rather than a speculative one.

## Not measured: connection establishment

The churn shape (`-IncludeChurn`, `Connection: close`) is off by default. Windows has ~16k ephemeral ports
with a multi-minute `TIME_WAIT`, so sustained churn exhausts the pool and corrupts every later leg - it
poisoned an entire 16-leg matrix before being caught. Keep-alive at `-c 128` opens 128 connections per leg
and never approaches the limit.

The cost of that decision is real and should not be glossed: **accept-path and TLS handshake cost are
unmeasured**, and the handshake is exactly where SChannel, SslStream and OpenSSL differ most. Steady state
exercises only the record layer. Measuring handshakes properly wants a bounded connection count with
explicit port accounting, or a second machine.

## Linux page size x payload, on the bare responder (2026-07-28)

`bench/run-page-sizes.sh`, `SmokeTest --http` - no Kestrel, no bridge, no pipes. 8 shards, `-c 64`, four
passes with the first discarded, reshuffled, median of three. Goodput MiB/s, min-max in brackets.

| payload | epoll p4K | epoll p16K | epoll p64K | iouring p4K | iouring p16K | iouring p64K |
|---|---:|---:|---:|---:|---:|---:|
| 512 B | 475.6 | 475.7 | 477.3 | 461.7 | 467.5 | 467.5 |
| 16 KB | 9,096.1 | 9,036.5 | 9,027.2 | 4,459.9 [4302-4530] | 4,396.1 [4377-4423] | **8,825.6** [8651-8858] |
| 256 KB | 12,640.8 | 12,647.4 | 12,676.7 | 8,016.4 | 7,853.3 | 7,824.3 |

### epoll is page-insensitive; io_uring is NOT, and that falsifies the prediction

TODO pre-registered that **both** Linux backends would be roughly page-insensitive, because io_uring
already dispatches one writev over an `OutChain` of segments - the same scatter-gather shape that made
IOCP page-insensitive - and epoll sends straight from its buffer with no page copy.

- **epoll: confirmed.** Every payload, all three pages overlap. Nothing to tune.
- **io_uring: falsified.** At a 16KB payload a 64KB page is **2.0x** a 4KB one, ranges disjoint.

Note p16K is *no better* than p4K at a 16KB payload. The response is headers + body, so a 16KB body
overflows a 16KB page and spills into a second one: the penalty behaves like "more than one page" rather
than "many pages" (5 pages at p4K and 2 at p16K cost the same; 1 page at p64K costs half). At 256KB every
page size spans multiple pages and all three converge, which fits - the per-request penalty is amortised
over a much larger response.

### Memory: the Windows blocker does not exist here, and a big page makes io_uring CHEAPER

RSS sampled under load (`-c 64`, 256KB responses, 8 shards). Idle RSS is useless for this - untouched
pages are not resident - so these are measured with traffic flowing.

| backend | page 4KB | page 64KB |
|---|---:|---:|
| epoll | 72 MB | 73 MB |
| io_uring | 122 MB | **77 MB** |

On Windows a 64KB page took a 12-shard RIO server from 283MB to 3,163MB, because the receive slab is
per-SOCKET at `SocketsPerShard` 4096. **Neither Linux backend does that**: epoll is flat, and io_uring is
**37% cheaper** at the larger page. That is the same effect already recorded for RIO's pool pressure -
a bigger page collapses buffer occupancy time, so the path falls back to pinned GC allocations far less
often. Counting buffers without counting holding time gets it backwards.

Single sample per cell, one payload shape, so treat as indicative rather than a measurement of record.

#### CORRECTION 2026-07-28: "neither Linux backend does that" is WRONG about epoll, and 64 connections is why it looked right

Established by inspection, not by recall. **epoll has exactly the Windows shape**: `EpollShard._recvBuffer`
is `new PinnedWriteBufferPool(_socketsPerShard, _bufSize)` - the field's own comment says *"one per live
connection"* - leased at accept (`EpollShard.cs:405`) and released only at close (332). So its receive slab
is `SocketsPerShard` x `BufferPageSize` **per shard**, 4096 x 64KB = **256 MB per shard** at a 64KB page,
which is the per-SOCKET scaling that cost RIO 3.0 GB. Only io_uring is genuinely different: its read pool
is per-SHARD (`ManagedBufferPool(entries: BufferPagesPerShard=256)`, `RawIOUringRing.cs:184`), so 64KB
there is ~16 MB per shard.

| backend | receive slab | count | at a 64KB page, per shard |
|---|---|---|---:|
| IOCP / RIO | per-socket | `SocketsPerShard` (4096) | 256 MB, **resident** (registered/locked) |
| epoll | **per-socket** | `SocketsPerShard` (4096) | 256 MB **virtual** |
| io_uring | per-shard | `BufferPagesPerShard` (256) | 16 MB |

**Why the measurement missed it, and why that is not a measurement error.** The slab is one
`NativeMemory.AllocZeroed` - `calloc`, which for a slab this size is an anonymous `mmap`, so pages are
faulted in only on first touch. At `-c 64` over 8 shards, 64 of the 4096 per-socket buffers are ever
touched. Resident receive memory is therefore ~`connections x page`, not `SocketsPerShard x page`. The row
above is a true reading of a configuration that could not have exposed the scaling.

That also retro-fits the number quantitatively: predicted delta between p4K and p64K at 64 connections is
64 x 60KB = **~3.8 MB**, against an observed 72 -> 73 MB. The effect was present and below the noise.

**The pre-registered prediction was: RSS diverges as `connections x page`, so ~123 MB more at 2,048
connections. MEASURED, AND FALSIFIED** (`bench/run-recv-slab.sh`, peak RSS under load):

| backend | page | c=64 | c=2048 | delta |
|---|---|---:|---:|---:|
| epoll | 4 KB | 45,696 KB | 70,452 KB | +24.8 MB |
| epoll | 64 KB | 45,540 KB | 72,584 KB | +27.0 MB |
| io_uring | 4 KB | 64,408 KB | 69,164 KB | +4.8 MB |
| io_uring | 64 KB | 63,284 KB | 71,896 KB | +8.6 MB |

epoll costs ~10 KB/connection more than io_uring, which is the per-socket buffer showing up - but the
delta **does not grow with the page**: 2.1 MB between p4K and p64K where 123 MB was predicted.

**The prediction had the wrong variable.** Resident memory is `connections x TOUCHED depth`, not
`connections x buffer size`, and a bombardier `GET` is ~100 bytes - so each connection touches exactly ONE
4 KB page of its 64 KB buffer. The buffer size never enters. Re-run with a workload that touches the
buffer deeply - 64 KB POST bodies, 512 connections, 8 shards - it appears exactly as predicted:

| epoll config | peak RSS | vs p4K |
|---|---:|---:|
| p4K (recv follows page) | 48,176 KB | - |
| p64K (recv follows page) | 78,952 KB | **+30.8 MB** |
| **p64K + `--recv-buffer 4096` (the split)** | 48,288 KB | **+0.1 MB** |

`connections x (64KB - 4KB)` = 512 x 60 KB = 30.7 MB predicted, **30.8 MB measured** - within 0.2%. And
the split recovers **all** of it, which is the same shape as the Windows fix (there: full throughput at
283 MB instead of 3,163 MB).

**So the real axis is NOT per-socket vs per-shard, and both the original claim and this correction had it
wrong.** The decisive number is on the Windows side: that 3,163 MB was measured at **`-c 64`** - 64
connections holding 3.0 GB resident, i.e. the slab is resident whether or not anything touches it, because
RIO *registers* it (`RIORegisterBuffer` locks the pages). epoll's slab is `calloc`'d and faulted on touch.
**Identical structure, different residency policy.** That is why Windows blew up at 64 connections and
epoll needs 512 connections x 64 KB requests to show the same effect at 1% of the size.

**What this means in practice.** On epoll a big page is genuinely free for small-request workloads (the
common case, and what the flat table above measured), and costs `connections x page` for large-request
ones - uploads, proxies, anything with real request bodies. That is a workload-dependent hazard rather
than an unconditional one, which is weaker than this correction first claimed and stronger than the
original "neither Linux backend does that". `SocketSetOptions.ReceiveBufferSize` is what makes it safe in
either case, and epoll and io_uring both honour it as of 2026-07-28.

**Decomposed 2026-07-28 (see the control section below), and the attribution was mostly wrong.** The
saving is real but it is overwhelmingly the *pool rescale*, not the page. Re-measured at a 16KB payload,
8 shards, `-c 64`, sampling peak RSS under load:

| config | RSS under load |
|---|---:|
| p4K, depth 1024 (default) | 98.5 MB |
| p64K, depth 64 (default - page rescales the pool) | **57.2 MB** |
| p64K, depth 1024 (pool pinned - page is the only change) | 87.8 MB |

So of the 41 MB the default 64KB page saves, only ~9 MB is attributable to the page itself; the other
~31 MB is the pool rescaling from 1024 buffers to 64. The headline "a big page is CHEAPER" survives as a
statement about **what the default does**, because the rescale is default behaviour and is what a user would get -
but it is not a property of the page, and the earlier wording implied it was.

Note also that pinned pool size and resident set are only loosely related: at p64K/depth-1024 the three
pools total ~192 MB per shard, ~1.5 GB across 8 shards, and RSS is 87.8 MB. Untouched pages are not
resident, which is the same reason idle RSS is useless here.

### The co-variation control: it IS the page (2026-07-28)

The sweep above varied two things at once. `--page N` rescales **three** pool depths to 4MB/N -
`WriteBuffersPerShard`, `OutOfBandWriteBuffersPerShard`, `BufferPagesPerShard` - so a 4KB page ships 1024
buffers and a 64KB page ships 64, a 16x swing alongside the page change. On Windows depth was shown inert
at 256KB, but that was one payload on a different backend.

**Only io_uring needed the control.** Checked by inspection rather than assumed: `IoUringShard` reads all
three pools (`BufferPagesPerShard` is its receive pool, the other two its send pools), while `EpollShard`
reads only `BufferPageSize`. So `--page` moves exactly one quantity for epoll - its sweep was never
confounded and its "insensitive at every page" result needs no control.

`FIXED_POOL_DEPTH=1024` pins all three, so `p4096` is configured identically to the uncontrolled run and
only `p64K`/`p16K` change. io_uring, 16KB payload:

| | p4K | p64K | ratio |
|---|---:|---:|---:|
| uncontrolled (depth 1024 / 64) | 4,459.9 [4302-4530] | 8,825.6 [8651-8858] | **1.98x** |
| controlled (depth 1024 / 1024) | 4,363.7 [4338-4403] | 8,586.6 [8545-8698] | **1.97x** |

**Pool depth contributed nothing.** Deepening p64K's pools 16x moved it 8,825.6 -> 8,586.6, and those
ranges overlap, so there is no difference to claim. The 2x is the page size, and the "does the response
fit in ONE page" reading of the p16K result stands.

### 3 scored passes is not enough at a 256KB payload

An accident of the control run: `p4096` was identically configured in both sessions, giving a free
cross-session repeatability check. At 512B and 16KB it reproduced (ranges overlap). At 256KB it did not -
8,016.4 [8007-8102] against 7,845.6 [7759-7850], **disjoint**, which by the rule at the top of this file
would license a 2.1% claim about two identical configurations.

Re-run at 256KB with **six** scored passes, all three pages:

| page | median | min-max |
|---|---:|---|
| p4K | 7,685.7 | [7405-8003] |
| p16K | 7,770.3 | [7684-8024] |
| p64K | 7,758.4 | [7604-7997] |

Two results. First, **io_uring is page-insensitive at 256KB** - all three overlap heavily, and the p16K
dip that looked disjoint on three passes was noise. Second, and the more useful one: the per-cell spread
at 256KB is **~8%**, but any three consecutive passes can span as little as 1.2%. Three passes at this
payload manufacture falsely tight ranges, and disjointness computed from them is not evidence.

This does not disturb the large findings here - "epoll beats io_uring by 58% at 256KB" and "p64K is 2.0x
at 16KB" are far outside an 8% band - but it does mean **any 256KB claim in the low single digits, in this
file or elsewhere, that rests on three passes should be treated as unproven.** Small-payload cells are not
affected: 16KB spreads run ~1.5% over the same pass count.

### The mechanism, pre-registered and confirmed: it is "does the response fit in ONE page"

The 2x is not "bigger pages are faster". Three predictions were written down before the runs, and all
three held. io_uring, bare responder, pools pinned so page is the only variable.

**1. A page that FITS jumps; a page that merely gets bigger does not.** At a 256KB payload the response is
body + headers, so it needs a page *larger* than 256KB to fit:

| page at 256KB payload | pages spanned | goodput |
|---|---|---:|
| 64 KB | 5 | 7,686.8 [7642-7873] |
| 256 KB | 2 (headers spill) | 7,838.8 [7766-7940] |
| **512 KB** | **1** | **11,409.3** [10649-12955] |

p256K is a 4x bigger page than p64K and buys nothing. p512K is only 2x bigger than p256K and buys 1.48x,
disjoint from both. The step is at the fit, not at the size.

**2. Once it fits, more page buys nothing.** At a 16KB payload, where 64KB already fits: p64K 8,623.1
[8522-8658], p256K 8,684.7 [8596-8868], p512K 8,583.3 [8582-8785]. All overlap.

**3. The sharpest one - hold the page fixed and walk the payload across the boundary.** p64K, so the
boundary is ~65.4KB once headers are counted. Goodput normally *rises* with payload as per-request cost
amortises, so a drop here can only be the boundary:

| payload | pages | goodput |
|---|---|---:|
| 32,768 | 1 | 9,432.1 [9424-9899] |
| 60,000 | 1 | **11,341.6** [11226-12018] |
| 70,000 | 2 | **7,610.6** [7587-7758] |
| 131,072 | 3 | 6,474.6 [6373-6661] |

**A 70,000-byte response carries 17% more data than a 60,000-byte one and delivers 33% LESS goodput.**
Ranges hugely disjoint. That is a hard discontinuity at the one-page boundary, and it is the whole effect.

### The code-level cause: a per-response PINNED ALLOCATION, measured directly

An earlier version of this section blamed the single-in-flight-send gate (`SendBusy`) and claimed the
cliff was a second completion round trip. **That was wrong, and instrumentation (`SS_URING_STATS=1`)
falsified it outright.** Recording the error because the reasoning was plausible and still wrong:

| page (16KB body) | send SQEs/resp | iov segments/resp | queued behind in-flight | partial resubmits |
|---|---:|---:|---:|---:|
| 4 KB | **1.000** | **1.000** | 0.000 | 0.000 |
| 16 KB | **1.000** | **1.000** | 0.000 | 0.000 |
| 64 KB | **1.000** | **1.000** | 0.000 | 0.000 |
| 256 KB | **1.000** | **1.000** | 0.000 | 0.000 |

Every page size costs **exactly one** send SQE carrying **exactly one** iovec segment. Nothing is ever
queued behind an in-flight send; no send is ever resubmitted. So there is no extra round trip to remove,
the `SendBusy` gate is never even reached, and the response is never split into page-sized segments.

The actual variable is *which memory that single segment points at*:

| page (16KB body) | pooled page/resp | **pinned GC alloc/resp** | goodput |
|---|---:|---:|---:|
| 4 KB | 0.000 | **1.000** | 4,363.7 |
| 16 KB | 0.000 | **1.000** | 4,364.6 |
| 64 KB | 1.000 | 0.000 | **8,586.6** |
| 256 KB | 1.000 | 0.000 | ~8,600 |

**A response that fits one pooled write page is sent from the pool. A response that does not becomes a
pinned GC allocation of the WHOLE response, one per response.** That is the cliff, and it is exactly
binary - which is why 5 pages and 2 pages cost the same as each other and twice as much as 1.

Confirmed against the boundary at a *fixed* 64KB page, where the switch and the goodput cliff coincide:

| body | segment source | goodput |
|---|---|---:|
| 60,000 | pooled page | **11,341.6** |
| 70,000 | **pinned GC alloc** | 7,610.6 |

And at a 256KB body *every* page allocates - including a 256KB page, because headers push the response
over - which is precisely why only `p512K` jumped in the sweep above. The "does it fit in one page"
reading was right; the reason is allocation, not round trips.

This also rejoins `fa97dd4`, whose pre-registered reading was "if it moves throughput, allocation was the
cost". It did, and this is the same finding arriving from a different direction: at 256KB the per-response
allocation is a Large Object Heap allocation, pinned, on every single response.

### What to fix, and it is not the send gate

The machinery to do this properly **already exists and is simply not used on this path**: `PumpFlush`
sends a chain of up to `IovMax` (1024) iovec segments in one `IORING_OP_WRITEV`, and `HandleWriteV`
already handles partial writes across a multi-segment chain. So an oversized response could be assembled
from **N pooled pages sent as one N-segment writev** instead of one big pinned allocation. Same syscall
count, same round trips, no allocation, and page size would stop selecting between two cost regimes.

Notably this needs **no change to ordering, `SendBusy`, `IO_LINK`, or connection teardown** - the reason
the earlier (wrong) diagnosis mattered is that it pointed at a far riskier change than the real defect
requires.

Two further consequences:

- **Raising the default page is a workaround.** It only moves the boundary, making the right default a
  function of expected payload - which a library cannot know, and which silently punishes any user whose
  responses grow past whatever is picked. Worse for streaming workloads (downloads, video), where the
  response is unbounded and every one of them would allocate its full size, pinned.
- **Still a candidate for TODO item 1's large-payload decline**, and a stronger one now that the mechanism
  is measured rather than inferred: the cliff fires exactly when responses outgrow the buffer. Not yet
  established for the bridged legs or for epoll, and must not be assumed.

### What this does NOT establish: that the 256KB bridged collapse is the bridge

The payload sweep through AspNetDemo shows every SocketSet leg collapsing at 256KB. On the bare responder
there is **no collapse** - epoll rises 9,096 -> 12,641 from 16KB to 256KB and io_uring rises
4,460 -> 8,016. It is tempting to conclude the bridge owns the cliff. **That conclusion is not supported**,
for three reasons, and the third is the one that actually kills it:

1. The two runs used different shard counts (bridged `s12`, bare `s8`).
2. They are different runs minutes apart, and rule 1 of `bench/README.md` says that is not evidence.
3. **`HttpBench` has a bottleneck of its own that was not accounted for.** It funnels every connection's
   sends through TWO background threads. At 16KB, epoll at every page and io_uring at p64K all land at
   ~570k rps - that is the harness ceiling, not the transport.

The tell: bridged io_uring at 16KB (7,054 MiB/s) is **faster** than bare io_uring at 16KB (4,460). A
bridge cannot cost negative time, so the cross-rig comparison is measuring something other than the
bridge. **A clean isolation needs both configurations in ONE session at a MATCHED shard count**, and
ideally a bare responder that does not serialise sends through two threads.

### FIXED 2026-07-28: chain pooled pages instead of allocating, and the cliff disappears

`Connection.WriteAll` asked for `data.Length` as one contiguous span. It never needed contiguity - the
loop has always coped with a short span - but asking for it made the backend treat it as a requirement,
and `IoUringConnection.EnsureRoom` then took its `want > pageSize` branch and allocated the whole response
on the pinned heap. The fix is for io_uring to hand `WriteAll` its natural buffer instead, so an oversized
write chains across pooled pages and goes out as one multi-segment `writev`.

Per-response accounting either side (16KB body, 200k requests):

| page | before | after |
|---|---|---|
| 4 KB | 1 segment, **1 pinned alloc** | **5 pooled pages, 0 allocs** |
| 16 KB | 1 segment, **1 pinned alloc** | **2 pooled pages, 0 allocs** |
| 64 KB | 1 pooled page, 0 allocs | 1 pooled page, 0 allocs |

Send SQEs stay at exactly 1.000 per response throughout - this removes an allocation, not a syscall.

Goodput, io_uring, bare responder:

| payload | page | before | after |
|---|---|---:|---:|
| 16 KB | 4 KB | 4,363.7 [4338-4403] | **8,578.0** [8546-8651] |
| 16 KB | 16 KB | 4,364.6 [4325-4404] | **8,252.0** [8069-8533] |
| 16 KB | 64 KB | 8,586.6 [8545-8698] | 8,763.8 [8484-8858] |
| 256 KB | 4 KB | 7,685.7 [7405-8003] | **12,706.8** [12693-13019] |
| 256 KB | 16 KB | 7,770.3 [7684-8024] | **12,829.0** [11595-12966] |
| 256 KB | 64 KB | 7,758.4 [7604-7997] | 11,733.9 [10055-12842] |

Roughly **+96% at 16KB** and **+58-65% at 256KB** for any page smaller than the response, and **page size
stops selecting between two cost regimes** - the whole reason item 0 was blocked on io_uring. 512B is
unchanged on both backends, and epoll is unchanged everywhere (it was never on the allocating path).

Verified byte-exact on both Linux backends, callback and `--pipe`, at a 64KB chunk size.

**Scope, deliberately narrow.** The behaviour is opt-in per backend (`Connection.GetWriteSpan`), enabled
for io_uring only. RIO caps `maxSendDataBuffers` at 1, so chaining segments there would turn one send into
N sequential sends - precisely the quantisation that already costs RIO 2.2-2.5x at large payloads. IOCP
scatter-gathers and would probably benefit, but **is untested from this Linux host.**

epoll needs nothing: it routes through `OutboundConnection`, which accumulates into a pooled buffer writer
and rents its flush snapshot from `ArrayPool` rather than allocating per response - which is *why* it was
page-insensitive from the first sweep. Only the io_uring writer had the allocating branch.

### The bridged sweep RE-MEASURED on the fixed transport, six passes (2026-07-28): nothing moved, and that is the finding

TODO item 1 said its 64KB -> 256KB table could not be trusted, because every number in it was taken
against a transport that allocated once per response, and demanded a re-run at six scored passes (three
having been shown to manufacture falsely tight ranges at 256KB). Done: `bench/run-tls-sizes.sh`,
`SIZES="65536 262144" REPS=7 SHARDS=12`, 98 cells, zero errors, pass 1 discarded.

Goodput MiB/s, median of 6 scored passes, min-max in brackets:

| leg | 64 KB | 256 KB | change | pre-fix change (3 passes) |
|---|---:|---:|---:|---:|
| epoll | 10,532.0 [10417-10723] | 6,655.5 [6083-6700] | **-36.8%** | -36% |
| iouring | 10,568.0 [10451-10722] | 7,817.6 [7351-8098] | **-26.0%** | -24% |
| epoll+tls | 9,201.3 [9102-9269] | 4,342.9 [4306-4359] | **-52.8%** | -53% |
| iouring+tls | 8,531.7 [8458-8656] | 3,934.4 [3648-4255] | **-53.9%** | -52% |
| kestrel | 10,018.3 [9966-10241] | 12,515.8 [12425-12666] | **+24.9%** | +24% |
| kestrel+tls | 6,682.8 [6464-6844] | 8,030.4 [7893-8146] | **+20.2%** | +16% |

**Every cell reproduces its pre-fix value within ~2%.** The allocation fix that gave the bare responder
+96% at 16KB and +58-65% at 256KB is worth **nothing** here, and the shape item 1 describes - every
SocketSet leg falling while both Kestrel controls rise - survives intact at six passes. The percentages
have now earned the precision they are quoted with.

**Why the fix could not move it, mechanically.** The defect was `Connection.WriteAll` asking for
`data.Length` as one contiguous span, which made `EnsureRoom` take its `want > pageSize` branch. Through
the bridge that branch is unreachable: both bridges send a `ReadOnlySequence<byte>` of PIPE segments, and
`Connection.Send(in ReadOnlySequence)` loops `WriteAll` **per segment** over ~4KB blocks - the same 4KB
block size already noted in this file as the reason a 256KB response is ~64 segments. `want` is therefore
never above a 4KB page. **Only a caller writing one large contiguous span could trigger the defect**, which
is exactly what the bare responder does (`HttpBench` queues to two sender threads that call
`c.Send(Response)` on the whole response). So the fix is real, and it is real for callback-style callers;
the ASP.NET-shaped caller never had the problem.

That also removes the discomfort item 1 recorded about its own data: the table did not need re-running
because the transport had changed under it, and now it has been re-run and it stands.

**One thing the six passes add that three could not: a variance asymmetry.** At 256KB the SocketSet legs
spread 9-17% across passes (`iouring+tls` 3648-4255) while both Kestrel controls hold ~2%
(`kestrel` 12425-12666). At 64KB every leg is tight, SocketSet included. So the bridged path at 256KB is
not merely slower - it is **unstable**, and only at the payload where it collapses. That is a defect
signature rather than a throughput result, and no allocation story explains it.

**What remains open is unchanged and is now the only open part:** which component owns the decline. That
is answered in the next section.

### RESOLVED: the 64KB -> 256KB decline is the BRIDGE, and so is the instability (2026-07-28)

The comparison this file twice refused to make cross-run, made properly: `bench/run-bare-vs-bridged.sh`
runs the BARE responder at the **same 12 shards**, same `-c 64`, same duration, same CPU split, same
client, same payloads, **in the same session** as the bridged sweep above. Six scored passes each.

| backend | 64 KB | 256 KB | change |
|---|---:|---:|---:|
| bare epoll | 10,744.8 [10636-10880] | 11,437.3 [11290-11602] | **+6.4%** |
| bare io_uring | 10,832.8 [10401-10931] | 10,349.4 [10290-10594] | -4.5% (ranges overlap: flat) |
| bridged epoll | 10,532.0 | 6,655.5 | **-36.8%** |
| bridged io_uring | 10,568.0 | 7,817.6 | **-26.0%** |

**The bare transport does not collapse.** epoll rises, io_uring is flat within its own spread. The
collapse exists only with the bridge in the path, so the bridge owns it.

Read as the bridge's own cost, it does not degrade gently - it detonates:

| backend | bridge cost at 64 KB | bridge cost at 256 KB |
|---|---:|---:|
| epoll | 2.0% | **41.8%** |
| io_uring | 2.4% | **24.5%** |

That 41.8% is independent corroboration of a number this file already had from the other platform: on
Windows, bare tuned RIO at 256KB does 11,030 against 6,348 bridged, i.e. **~42%**. Two operating systems,
two transports, the same figure for the same component.

**The validity check that voided the previous attempt passes this time.** The reason the earlier
comparison was refused is that bridged io_uring at 16KB measured FASTER than bare, and a bridge cannot
cost negative time. Here bare beats bridged at **every** cell (by 2.0-2.5% at 64KB and 24-42% at 256KB),
so the two harnesses are comparable and the subtraction is legitimate.

**The instability is the bridge too**, which is the part that makes this a defect rather than an overhead.
Bare 256KB spreads are tight - epoll 2.7%, io_uring 3.0% - against 9-17% for the same transports bridged.
So the bridge is not just charging 24-42% at large payloads, it is charging a *variable* amount.

**What this rules out.** Not the transport's per-byte copying: the bare responder copies every outbound
byte into write pages too and does not decline. Not the allocation defect: fixed, and shown above to be
unreachable through the bridge. Not the client, the box, or the payload shape: the Kestrel controls rise
in the same reshuffled passes. What is left is what `2b-result` reached independently from the other
direction - two `Pipe`s, the scheduling hops between them, and Kestrel's own pipeline - and the honest
reading of both is that **the bridge cost is structural, and zero-copy send addresses the wrong part of
it** (it removed one copy for +3.5% at 16KB).

*Caveat carried forward:* bare epoll at 256KB (11,437) now measures ~10% ABOVE bare io_uring (10,349),
ranges disjoint, where the 8-shard post-fix sweep had them level. Different shard count, so this is not a
contradiction, but "they are level at 256KB" is an 8-shard statement and should not be quoted at 12.

### io_uring zero-copy send: +45.1% at 256KB — and it explains IOCP's null result exactly (2026-07-29)

`Connection.TrySendZeroCopy` implemented for io_uring: the writev's iovecs point straight at the caller's
pipe segments instead of at pooled pages we copied into. A/B against the same `--byo` bridge
(`bench/run-byo.sh`, 12 shards, `-c 64`, 7 passes, first discarded), so the comparison isolates zero-copy
rather than what pipe mode itself costs:

| payload | classic | **byo + zero-copy** | change |
|---|---:|---:|---:|
| 64 KB | 10,423.2 [10362-10537] | 10,613.0 [10482-10673] | +1.8% |
| 256 KB | 7,950.2 [7795-8158] | **11,536.1** [11520-11682] | **+45.1%** |

The 256KB ranges are enormously disjoint (min 11,520 against max 8,158). 64KB is a marginal overlap and
should not be quoted as more than "no worse".

**Verified taken, not silently declined** - the failure mode this rig exists to catch, since a declined
path measures identically to one that ran and did not pay. `/config` reports `byo=pipe`, and
`SS_URING_STATS=1` over a 256KB load reports `OP_WRITEV=179,703` carrying `zero-copy=11,680,439` segments
with **`pooled-page=0` and `pinned-managed=0`**: every outbound segment came from the caller, none from
our buffers.

#### 65.0 segments per response — and IOCP's cap is 64

That same counter gives 11,680,439 / 179,703 = **exactly 65.0 iovec segments per response**: Kestrel's
pool hands out 4KB blocks, so a 256KB body is 64 of them, plus one for the headers.

`IocpConnection.MaxSendPages` is **64**. So IOCP's `TrySendZeroCopy` declined **every single 256KB
response** and silently fell back to copying - which is exactly why it measured +3.5% at 16KB and nothing
at 256KB. That was recorded in `2b-result` as a suspicion ("probably binds at 256KB... instrument the
decline rate first - do not assume"). It is now measured, on the backend whose `IovMax` is 1024 and which
therefore does not hit the ceiling.

**Concrete prediction for whenever a Windows host is available:** raise `MaxSendPages` above 65 (or send a
PREFIX of the sequence and have `TrySendZeroCopy` report bytes rather than a bare bool, which is the fix
`2b-result` already sketched), and IOCP should show a large-payload gain of the same shape. If it does
not, the segment cap was not the explanation after all.

#### Two things this changes

**The bare responder is no longer a ceiling, and the bare-vs-bridged subtraction does not apply to this
leg.** Bridged byo at 256KB (11,536) now *exceeds* the bare responder (10,352), which sounds impossible
until you notice they no longer run the same code: `HttpBench` uses the callback path and still copies
every byte into pooled pages, while byo copies nothing. So "bridge cost = bare - bridged" is meaningless
for byo; the bare number is just another configuration, and a slower one.

**Against vanilla Kestrel at 256KB the gap is now 7.3%, from 36%.** Kestrel's own transport does 12,450.5;
classic io_uring did 7,950 and byo does 11,536. Kestrel is zero-copy in both directions and pays no
bridge, so it should still lead - and the residual is now small enough that the *receive* side (where we
still copy and Kestrel does not) is the obvious next term.

**The pin cost was pre-registered as possibly fatal and was not.** With an unpinned `MemoryPool` this
takes one `GCHandle` pin per segment - 65 pins and 65 disposes per response here - and it still won by
45%. A pinned-block pool (TODO 2d) removes that branch entirely, so this is a floor rather than a ceiling.

### Pipe options: block size is worth +6-8%, pinning is not, and the memory bill is 2.7x (2026-07-29)

The bridge's two `Pipe`s are ours to configure and were left at framework defaults. The zero-copy work
produced the number that made this worth testing: a 256KB response is **exactly 65.00 iovec segments** at
the default ~4KB block, and **5.00** at a 64KB block (`SS_URING_STATS`, measured both ways). Legs are
byo-vs-byo so the reading isolates the pipe change rather than what pipe mode costs.
`bench/run-pipe-opts.sh`, io_uring, 12 shards, `-c 64`, 4 scored passes, goodput MiB/s:

| payload | classic | byo | **byo + 64KB seg** | byo + 64KB + pinned |
|---|---:|---:|---:|---:|
| 512 B | 327.6 | 321.4 | 321.2 | 329.9 |
| 16 KB | 6,950.6 [6822-7005] | 6,741.6 [6685-6830] | **7,388.0** [7369-7439] | 7,108.2 [6950-7338] |
| 64 KB | - | 10,626.4 | 10,452.5 | 10,589.2 |
| 256 KB | 7,950.2 (earlier run) | 11,501.5 [11402-11638] | **12,363.6** [12325-12440] | 12,452.4 [12289-12501] |

**Block size is the lever: +7.5% at 256KB and +6.3% at 16KB over byo, both disjoint.** 512B and 64KB are
unmoved, which is the predicted shape - at those sizes a response is 1-2 segments either way.

**Pinning is not.** `--pipe-pinned` adds +0.7% at 256KB with overlapping ranges. That is not a
disappointment so much as an arithmetic consequence: pinning saves one `GCHandle` per segment, and the
64KB block already cut segments from 65 to 5, so there were only 5 pins left to save. The two levers
overlap; the segment one does the work. (Pinning *alone*, at 4KB blocks and 65 pins per response, was not
measured separately - that is the open cell in this table.)

**And byo alone is NOT universally better than the default bridge.** It is -3.0% at 16KB (ranges barely
touching) and level at 512B; its win is concentrated at 256KB (+45%). It is **byo + big blocks** that
beats classic everywhere measured.

#### The bill: 2.7x resident memory at 2048 connections

Peak RSS under load, io_uring, 8 shards, 16KB responses:

| connections | byo (4KB blocks) | byo + 64KB seg | delta |
|---|---:|---:|---:|
| 64 | 164,824 KB | 181,124 KB | +16 MB |
| 512 | 264,788 KB | 484,220 KB | **+219 MB** |
| 2048 | 360,144 KB | 977,328 KB | **+617 MB** |

About **300 KB per connection** - two pipes, each holding several 64KB blocks in flight rather than
several 4KB ones. This is the same `connections x buffer-size` trade that the receive-slab work ran into,
arriving from the other side of the bridge, and it is invisible to a throughput-only reading.

**So there is no single right default, for the same reason page size had none:** the best block size is a
function of expected payload *and* expected concurrency, and a library cannot know either. Large blocks
are right for big responses at modest connection counts and wrong for a connection-heavy small-message
server. Both are now flags (`--pipe-segment`, `--pipe-pinned`), reported in `/config`, and gated by the
rig both ways so two legs cannot silently be the same configuration.

### SUPERSEDED: "epoll beats io_uring by 58% at 256KB" was this defect, not a structural difference

The section below concluded that io_uring trails epoll at 256KB because it copies every outbound byte into
write pages while epoll sends straight from its buffer. **That gap was the per-response pinned allocation.**
With it removed, io_uring at 256KB runs 11,734-12,829 against epoll's ~12,470 - level, at every page size.
The structural reading was wrong, and the note TODO item 1 makes about io_uring's copy shape should not be
leaned on until it is re-established against the fixed transport. *Original section follows.*

### What DOES survive: epoll beats io_uring by 58% at 256KB on the bare transport

12,640.8 vs 8,016.4 at p4K, and the same picture at every page size. Ranges are hugely disjoint and both
legs run at ~50k and ~32k rps - far below the ~570k harness ceiling, so neither is limited by `HttpBench`.
This is a transport-level difference and it matches the structural note in TODO item 1: io_uring copies
every outbound byte into write pages, epoll sends directly from its buffer. That note was written about
the TLS path; this is plaintext, and the same shape appears.

## Linux baseline on bare metal (2026-07-28) — THE current Linux reference

First Linux measurement on the current host: Pop!_OS 24.04, kernel 7.0.11, Ryzen 9 7900X 12C/24T, bare
metal, governor `performance`, server and load generator on disjoint physical cores (`0-5,12-17` against
`6-11,18-23`, affinity verified on the live processes rather than assumed). `bench/run-matrix.sh`,
`-c 128 -d 10s`, `GET /plaintext` (a 2-byte response, so this is the SMALL-MESSAGE end), four passes with
the first discarded, reshuffled each pass, median of three.

Nothing here is comparable with the 2026-07-26 section below: different machine, different OS, no
container. Treat this as the baseline that catch-up work on the Linux backends is measured against.

### The host is a usable instrument

Within-leg spreads **0.2-5.7%, mostly ~2%**, against **37.4%** for the same harness in the container.
That is the single most important line in this section: a 2% effect is now detectable on Linux, and was
unprovable before. Comparable to the Windows desktop's 0.2-2.4%.

Note the two noisiest legs are both `s12` (`iouring/s12` 5.7%, `epoll+tls/s12` 4.1%) while their `s8`
counterparts sit at ~2%. Contention at one loop thread per logical CPU is the obvious suspect.

### Throughput (median rps, 3 scored passes)

| leg | s4 | s8 | s12 | best p99 |
|---|---:|---:|---:|---:|
| iouring | 497,820 | **823,328** | 805,866 | 656us |
| epoll | 473,154 | 762,463 | **797,725** | 577us |
| iouring+tls | 416,445 | 677,050 | **702,829** | 699us |
| epoll+tls | 384,087 | 610,931 | **689,190** | 874us |
| iouring+ktls | 324,720 | 524,187 | **597,486** | 796us |
| kestrel | - | 779,303 | - | 469us |
| kestrel+tls | - | 576,069 | - | 565us |

**SocketSet beats stock Kestrel on both plaintext and TLS, and both separate on disjoint ranges:**
`iouring/s8` 823,328 against `kestrel` 779,303 (**+5.6%**, min 822,872 > max 780,875), and
`iouring+tls/s12` 702,829 against `kestrel+tls` 576,069 (**+22%**). The TLS margin is the larger and the
more interesting one, since it compares TLS terminated in the transport (OpenSSL, our record layer)
against Kestrel's `SslStream`.

The 2026-07-26 container run concluded **parity** on plaintext (105k rps, three legs within 3%). That
conclusion was an artifact of a ceiling at one-eighth this rate, and is superseded rather than refined.

### kTLS is SLOWER than userspace TLS here, at every shard count

| shards | iouring+tls | iouring+ktls | delta |
|---|---:|---:|---:|
| 4 | 416,445 | 324,720 | **-22.0%** |
| 8 | 677,050 | 524,187 | **-22.6%** |
| 12 | 702,829 | 597,486 | **-15.0%** |

All three pairs separate. Latency is worse too, and by more than throughput: `iouring+ktls/s12` p99 is
**3,169us against `iouring+tls/s12`'s 1,017us**.

**This is the expected shape, not a defect**, and it is the small-message end of exactly the trade
`bench/ktls-verify.sh` documents. At a 2-byte response the crypto is rounding error, so TX offload has
nothing to win; meanwhile the kTLS path has given up io_uring's multishot receive and provided buffers in
favour of `POLL` + `SSL_read`, one syscall per message. We are paying the RX cost and collecting none of
the TX benefit.

It does NOT answer whether kTLS is worth continuing with. Responses dominate requests in an ASP.NET
workload, and TX is the offloaded half, so the question is whether the TX win crosses over the RX penalty
as payload grows. That is `run-tls-sizes.sh`'s job, with `iouring+tls` as the control leg.

### Shard count is not settled on Linux

`s4 -> s8` is worth ~65% everywhere. `s8 -> s12` splits by leg:

- **every TLS leg improves and separates** (`iouring+tls` +3.8%, `epoll+tls` +12.8%)
- **`epoll` plaintext improves and separates** (762,463 -> 797,725)
- **`iouring` plaintext does NOT separate** (823,328 vs 805,866, ranges overlap) - and the median moves
  the wrong way
- **p99 degrades at s12 on every leg**, from 656us to 840us on `iouring` and from 796us to 3,169us on
  `iouring+ktls`

Plausible mechanism, untested: the server half is 6 physical cores / 12 logical CPUs, so `s12` puts one
loop thread on every logical CPU and leaves nothing for the ThreadPool that runs Kestrel and the bridge
pump. Windows chose 12 because it was the server half's logical core count, never because it measured
best. **A Linux shard default should not be copied from the Windows one**, and `s8` is the better
starting guess here for throughput-per-latency.

### What this baseline does NOT establish

- **Whether the top legs are compressed by the load generator.** The legs span 153.6% and the top ones do
  separate, so the box is not flattening everything - but `iouring/s8`, `epoll/s12` and `kestrel` sit
  within 8% of one another at the top, and rule 7 of `bench/README.md` says to establish a ceiling by
  sweeping concurrency rather than inferring it. A `-c 64/128/256` check on the top three would settle it.
- **Anything about large payloads.** `/plaintext` is a 2-byte response.
- **Anything about handshakes.** Keep-alive only, as everywhere else in this file.

## Linux: epoll vs io_uring vs kTLS (2026-07-26, SUPERSEDED — old host AND old OS)

> **SUPERSEDED 2026-07-28, twice over.** These numbers are from the *laptop*, not the current desktop
> (the host changed 2026-07-27), and from a Docker container on a WSL2 kernel rather than the bare-metal
> Pop!_OS install that replaced it. Nothing here can be compared with anything measured since, and no
> Linux baseline exists on the current host until `bench/run-matrix.sh` is re-run. Kept for the method and
> the retraction, not the figures.
>
> One thing here does survive and is worth carrying: the TLS legs were **not separable** — 37.4% worst
> within-leg spread against a 7.7% between-leg range. On bare metal with the governor pinned to
> `performance`, that spread should collapse; if it does not, the instrument is still wrong.

Run with `bench/run-matrix.sh` inside a Docker container (`--security-opt seccomp=unconfined`, without
which Docker's seccomp blocks io_uring and the backend silently falls back to managed sockets). Same
method as above: `-c 128 -d 10s`, reshuffled passes, first discarded as host warm-up, median of the rest.
This is a **container on a WSL2 kernel over loopback** - weaker ground than the Windows numbers, and the
spreads show it.

### Plaintext - clean, and the answer is "no meaningful difference"

| leg | med rps | spread | med p99 |
|---|---:|---:|---:|
| io_uring | 105,187 | 3.3% | 3,049µs |
| kestrel | 102,788 | 1.6% | 3,379µs |
| epoll | 102,453 | 3.5% | 3,186µs |

Three passes each, all legs within ~3% of one another with ~3% run-to-run spread. **The epoll backend is
at parity with both the completion backend and stock Kestrel.** io_uring leads by 2.6%, right at the edge
of the noise. For a fallback, parity is the result you want.

### TLS - four variants, and none of them are separable

Six scored passes (seven run, first discarded), because four passes were not enough to tell.

| leg | med rps | spread | min-max | med p99 |
|---|---:|---:|---|---:|
| kestrel+tls (SslStream) | 81,644 | 18.1% | 78.9k-93.7k | 4,413µs |
| iouring+tls (OpenSSL userspace) | 80,830 | 37.4% | 63.3k-93.6k | 3,131µs |
| epoll+tls (OpenSSL userspace) | 76,384 | 24.3% | 69.2k-87.8k | 4,214µs |
| iouring+ktls (kernel offload) | 75,834 | 21.7% | 65.5k-82.0k | 3,893µs |

Between-leg range **7.7%**; worst within-leg spread **37.4%**. Every leg's range overlaps every other's.

**No ordering among these four is supported by this data** - including the one the medians suggest. Do
not quote the ranking.

The only thing that looks like it might be real is p99: `iouring+tls` sits ~1.3ms below the others across
passes. Suggestive, not established.

### A retraction

An earlier four-pass run put `iouring+ktls` at 58,475 rps - far below the userspace TLS legs - and it was
tempting to explain: the kTLS path deliberately bypasses io_uring's data ops, driving the socket through
`POLL` + `SSL_read`/`SSL_write`, so it loses the batching that makes io_uring fast for 512-byte messages,
and kernel crypto has to beat that syscall overhead.

That explanation is plausible, fits the code, and **is not supported by the data.** With six scored
passes kTLS lands at 75,834, within noise of everything else. The 58k figure was one arm of a bimodal
distribution being read as a result. The mechanism may still be real; this measurement says nothing about
it either way.

### kTLS is TX-only here — and the cause is OpenSSL's version, not ours (2026-07-28, corrected 2026-07-29)

Independent of the throughput numbers above, and it changes how all of them should be read. `/config`
reports that kTLS was *configured*; `/proc/net/tls_stat` reports what the kernel actually did. Driving
traffic through the `--ktls` leg:

| counter | before | after 2 connections | meaning |
|---|---:|---:|---|
| `TlsTxSw` | 1 | 3 | transmit **is** offloaded into the kernel |
| `TlsRxSw` | 0 | 0 | receive is **not** offloaded, at all |
| `TlsTxDevice` | 0 | 0 | no NIC offload — loopback, and permanently so here |

So "kTLS" in every figure in this file means **TX-only offload**. `bench/ktls-verify.sh` gates the kTLS
legs on this counter so a socket the kernel never took cannot be measured as if it had been.

**CORRECTED 2026-07-29: "that is a property of our integration" was WRONG. It is OpenSSL's version.**
This section blamed our receive path (io_uring `POLL` + `SSL_read` rather than the `RECVMSG` +
`TLS_GET_RECORD_TYPE` design). That reversed cause and effect: we drive receive that way *because* RX was
never offloaded, so OpenSSL still had to decrypt and therefore still had to own the reads.

Measured with `SmokeTest --ktls-spike`, which asks OpenSSL directly via `BIO_get_ktls_send/recv`, same
box, same probe, TLS 1.3 throughout - only the library differs:

| OpenSSL | client | server | plaintext round-trip |
|---|---|---|---|
| 3.0.13 (system) | TX=True **RX=False** | TX=True **RX=False** | FAIL |
| **3.5.7** (self-built, `enable-ktls`) | TX=True **RX=True** | TX=True **RX=True** | **PASS** |

**OpenSSL 3.0.x declines kTLS RX for TLS 1.3; 3.2+ grants it.** Confirmed end to end in the real
transport, not just the probe: running the io_uring kTLS echo against the 3.5.7 build moved `TlsRxSw` off
zero for the first time (0 -> 8) while the byte-exact echo still passed.

Two things were ruled out on the way, both by measurement rather than argument: clearing
`SSL_MODE_NO_KTLS_RX` changes nothing, and a flatpak-supplied 3.5.7 reported TX=False *and* RX=False -
kTLS not engaging at all, which is not the same as declining RX and was discarded as inconclusive rather
than quoted. The common advice to "enforce TLS 1.3 so multishot keeps working" is therefore **backwards on
OpenSSL 3.0.x**, where 1.3 is exactly what keeps RX off.

The backend now says which it got, once per process - `[ktls] openssl=3.0.13 tx=True rx=False -- RX NOT
offloaded...` - because a silent half-offload is what made every kTLS figure in this file mean something
other than it appeared to, for months. See TODO item 4b.

### kTLS is still ~20% behind userspace TLS even with BOTH directions offloaded (2026-07-29)

The standing explanation for kTLS trailing was "it forfeits multishot receive and provided buffers, so it
pays one syscall per message". Now that kTLS RX can actually be enabled (OpenSSL 3.2+; see the correction
above), that can be half-tested - and the half that could be tested says offload is not the missing piece.

Same OpenSSL (self-built 3.5.7), same box, back-to-back, io_uring, 12 shards, 4 scored passes:

| payload | `iouring+tls` (userspace crypto) | `iouring+ktls` (**TX and RX in kernel**) | kTLS vs TLS |
|---|---:|---:|---:|
| 512 B | 602,794 [600707-618626] | 505,905 [503951-518677] | **-16.1%** |
| 16 KB | 349,724 [344650-354893] | 275,927 [275118-286061] | **-21.1%** |

Ranges disjoint at both sizes; `TlsRxSw` climbed throughout, so the offload was genuinely on.

**So full offload does not close the gap - it is roughly the same ~20% that TX-only showed.** And moving
RX into the kernel on its own made things slightly *worse* (-4.3% at 512B against the TX-only build,
ranges disjoint, though that comparison changes the whole library so it is not attributable to RX alone).

**Why that is consistent rather than surprising, and what it leaves.** Our receive path did not change:
`KtlsRead` still drives `POLL` + `SSL_read` per message. With RX offloaded, `SSL_read` returns plaintext
the kernel decrypted instead of decrypting it itself - so the *crypto* moved but the *syscall per message*
did not. Offload was never the lever; it is what makes the lever reachable.

**This does not test the multishot hypothesis - it clears the way to test it.** The only remaining
explanation for the ~20% is the receive architecture, and the work is now unblocked: with kTLS RX active,
plain `recv` returns plaintext, so `IORING_OP_RECV` + `IORING_RECV_MULTISHOT` over the provided-buffer
ring becomes possible (proven in `KtlsProbe`'s plain-send/plain-recv round-trip). That is TODO item 4, and
it now has a number to beat: **~20%, twice measured, at two payload sizes.**

### What would move this forward

Not more passes in a container. A real Linux host, ideally two machines, and a message size large enough
that per-record crypto dominates per-syscall overhead - at 512 bytes the transport and the TLS record
layer are both drowned by fixed costs. Connection churn would also need to come back, since handshake
cost is where these TLS stacks genuinely differ and it is entirely out of scope here.

## Windows: payload sweep, and finding the real bottleneck (2026-07-26, PREVIOUS HOST)

> **Different machine.** Everything in this section was measured on the earlier laptop (16C/32T), not the
> current desktop. The absolute numbers are superseded by the 2026-07-27 sweep above and must not be
> compared with it. The section is kept because the *diagnosis* - page-quantised sends - and the
> controlled A/B that confirmed it are still the reasoning behind current work, and because the RIO half
> of that fix is still outstanding.

> **Power state.** The host is a laptop, where battery vs mains is a large and *variable* difference in
> sustained power limit rather than a few percent - so it has to be stated. Every figure in this section
> was taken **on mains**.
`bench/Run-TlsSizes.ps1`, `-c 64`, 16 shards, server pinned to logical CPUs 0-15, generator to 16-31,
first pass discarded, median of the rest.

### Goodput MiB/s, payload sweep

| payload | kestrel | kestrel+tls | iocp | iocp+tls | rio+tls |
|---|---:|---:|---:|---:|---:|
| 16 KB | ~3,740 | ~3,430 | ~1,449 | ~1,486 | ~1,524 |
| 256 KB | ~9,417 | ~7,493 | ~1,927 | ~1,939 | ~2,045 |

The tell is that **plaintext and TLS are within ~1% of each other on our transports, across two different
backends**. When the cipher is invisible, the constraint is upstream of it. Kestrel meanwhile pays a
visible ~12-20% for SslStream, which is what a TLS cost is supposed to look like.

### Shard scaling (goodput MiB/s)

| payload | s2 | s4 | s8 | s16 | s32 |
|---|---:|---:|---:|---:|---:|
| 16 KB, iocp | 447 | 767 | 1,165 | 1,455 | 1,445 |
| 16 KB, iocp+tls | 425 | 725 | 1,118 | 1,434 | 1,417 |
| 256 KB, iocp | 527 | 1,001 | 1,528 | 1,899 | 2,054 |
| 256 KB, iocp+tls | 567 | 1,090 | 1,549 | 1,937 | 2,116 |

Near-linear to 8, plateauing at 16 = the pinned core count, nothing from 32. Shard count was *not*
self-inflicted harm (a hypothesis this refuted). It scales that way **because** per-connection sends were
serialised - parallelism across connections was the only lever available.

### Isolating the bridge

Bare SocketSet HTTP responder (`SmokeTest --http`, no Kestrel, no bridge) vs the same transport behind
the AspNetDemo bridge, same client, same pinning, same payload:

| | 16 KB | 256 KB |
|---|---:|---:|
| bare responder | 1,655 | 2,454 |
| AspNetDemo iocp/s16 | 1,455 | 1,899 |

So the bridge costs **12% at 16 KB, 23% at 256 KB** - not the ~4x it had been blamed for. The gap was in
the transport itself.

### The cause: sends were quantised to one write page

`BufferPageSize` defaults to 4096 and the send path kept **one page in flight per connection**, so a
256 KB response left as 64 sequential `WSASend`s, each costing a completion-port round trip. Kestrel
issues one `SendAsync` over the whole buffer.

Page-size sweep on the bare responder at 256 KB (median of 2 scored passes):

| page | goodput |
|---|---:|
| 4 KB | 885 |
| 16 KB | 2,503 |
| 64 KB | 3,556 |

Page size is a trade-off rather than a dial, though: at a 16 KB payload the best page is 16 KB
(2,103 MiB/s) and 64 KB is *worse* (1,273), because most of the page is wasted.

### Fix: one scatter-gather WSASend per send

`WSASend` always took a buffer array; only the call site passed 1. A send is now a set of pages issued as
one call with up to 64 `WSABUF`s, segments packed (so small queued responses still coalesce) and partial
sends resuming across pages.

> **IOCP only, and necessarily so.** This was written up as applying to IOCP and RIO alike. It was never
> applied to RIO, and on 2026-07-27 it turned out it *cannot* be: RIO fixes its buffers-per-send at
> request-queue creation and Windows only permits 1. The claim that "`RIOSend` likewise takes a buffer
> array" confused the signature with the implementation. The 2026-07-27 sweep measures what RIO's page
> quantisation costs (**2.2-2.5x at >=16 KB**) and the RIO section above records what is actually
> available instead.

Measured with `bench/Compare-Commits.ps1` - the two commits built in isolated git worktrees and measured
back to back on mains, median of 3 scored passes, **default 4 KB page**:

| payload | before | after | change | before passes | after passes |
|---|---:|---:|---:|---|---|
| 512 B | 138.9 | 142.6 | +2.7% | 138.4, 140.0, 139.3 | 141.5, 143.6, 144.0 |
| 4 KB | 785.8 | 1,112.1 | **+41.5%** | 793, 779, 793 | 1095, 1132, 1130 |
| 16 KB | 1,664.3 | 3,882.8 | **+133%** | 1770, 1730, 1598 | 3828, 3937, 3942 |
| 256 KB | 2,462.2 | 6,447.8 | **+162%** | 2528, 2510, 2414 | 6593, 6303, 6855 |

Ranges are disjoint at 4 KB and above, far outside the ~6% noise floor. 512 B shows a small consistent
gain (every "after" pass above every "before" pass) and, importantly, **no regression at small payloads** -
which was the specific risk, since the code this replaced was tuned for exactly that case.

**This is the only properly controlled measurement in this document**, and it took four attempts. The
first three were void for three different reasons - a cross-run comparison, a power-state change midway,
and the A/B harness corrupting the repository so that both halves measured the same build (see
`bench/Compare-Commits.ps1` for the details, which are worth reading before writing another harness).

Two claims made earlier and now superseded: "+57%" (cross-run, understated the effect by ~3x) and
"a 2-6% regression" (both halves were the same binary). The unexplained 885 MiB/s figure came from a
`--page 4096` run, which also raises `OutOfBandWriteBuffersPerShard`/`BufferPagesPerShard` from 256 to
1024 - so page size and pool size moved together there and that whole sweep is confounded. The
page-quantisation diagnosis survives because this A/B confirms it directly; the page-size *sweep* should
not be quoted.

End-to-end through AspNetDemo the win does not show (256 KB: 1,899 -> 2,041): the bridge is now the
binding constraint, having gone from ~23% of the gap to ~47%. The bottleneck moved rather than vanished.

## Confounders found

Each produced *believable* numbers, which is the dangerous kind of wrong.

1. **The demo measured its own logger - 38x.** No `appsettings.json`, so the default level was
   `Information` and `Hosting.Diagnostics` wrote two lines per request behind a console lock. Every
   transport, Kestrel included, was capped at ~2,000 rps. Caught because the *control* leg was impossibly
   slow and latency scaled linearly with connection count - the signature of serialisation. Fixed in
   `Program.cs`.
2. **Churn exhausted the ephemeral-port pool**, corrupting every leg measured after the first churn run
   (`rio-s4`: 90,563 rps in one run, 7,628 in the next). Now opt-in and bounded by request count.
3. **A pending Windows Firewall prompt was inspecting loopback - 2.8x.** Held everything to ~95k rps;
   throughput jumped mid-run when the dialog was cleared. This also invalidated a conclusion drawn from
   it, that a ~100k "generator ceiling" made every leg ceiling-bound. **Check for pending firewall prompts
   before benchmarking.**
4. **`DOTNET_PROCESSOR_COUNT` / `GOMAXPROCS` unset while pinned.** Both runtimes size themselves from the
   processor count seen at startup, before affinity is applied - so the server built a ThreadPool and
   Server GC heaps for 32 CPUs then ran on 16, and bombardier ran 32 Go procs on 16. Both oversubscribed
   against their own pinning.
5. **Host thermal/frequency decay across the first pass.** A cold machine runs at boost clocks: pass 1
   opened at ~258k rps and decayed through itself to ~115k, while passes 2 and 3 held a steady ~111k
   median and agreed to within 3% per leg. Per-*request* warm-up cannot fix this; the transient spans a
   whole pass. The harness now discards pass 1 (`-WarmupPasses`).
6. **Fixed leg order.** With one measurement per leg in a fixed order, anything that accumulates is
   indistinguishable from a property of whichever leg runs late. Passes are now reshuffled and each leg's
   own spread is reported and compared against the between-leg range, with an explicit verdict when the
   noise rivals the signal.

7. **Measuring at a saturated operating point - the whole top of a table becomes meaningless.** `-c 128`
   is past the knee on the current host, so eight legs converged inside 1.3% and looked like parity. Found
   by sweeping concurrency (`-c 64/128/256`) and seeing throughput refuse to move while p99 rose in
   proportion. **Sweep concurrency before believing that two transports are equal.**
8. **p99 quantised at ~500µs** by Go-client timer granularity on Windows, which made sixteen legs report
   an identical 1,503µs. A latency figure that is *identical* across unrelated legs is an instrument
   reading, not a result.
9. **Attributing CPU cost per request - three failed attempts, and the reason is not the instrument.**
   Comparing CPU per request under a rate-limited load produced per-leg spreads of 38-174% and TLS legs
   *cheaper* than their own plaintext controls, every time. The first two attempts sampled
   `\Processor(N)\% Processor Time` at 1Hz, which is obviously the wrong instrument. Replacing it with
   `Process.TotalProcessorTime` deltas - kernel-accounted, exact, scoped to the server process - **did not
   help**: the same leg swung 63.55 -> 38.24 core-µs/req between two passes.

   The operating point is the confound. At a fixed sub-saturation rate the server has idle gaps between
   arrivals, and threads that wake, find no work and spin briefly before sleeping charge that time to
   whichever request follows. It also explains the persistent inversion: a slower per-request path (TLS)
   leaves fewer idle gaps to spin in, so it can measure *cheaper* per request than its own plaintext
   control while genuinely doing more work.

   **Measure cost per request at saturation, not under a rate limit** - there are no idle gaps to
   misattribute. All three attempts here are discarded; the question remains open. **A TLS leg beating its
   own plaintext control is a reliable "this run is noise" signal** - build that gate into anything
   comparing cost.

Two further notes for anyone extending the harness: a BOM-less `.ps1` containing non-ASCII (an em-dash)
is read as ANSI by Windows PowerShell 5.1, which turns it into a string delimiter and reports a syntax
error hundreds of lines away - keep the script ASCII. And `[IntPtr]0xFFFF0000` silently becomes negative,
because PowerShell types the literal as `Int32`; compute affinity masks in `Int64`.

The through-line: on a single-box loopback benchmark, **the harness is the most likely source of any
interesting-looking result.** Check the control leg first, and distrust anything whose run-to-run spread
approaches the effect being claimed.

## Standing caveats

- **Loopback.** Client and server share the host. Absolute numbers are not comparable to a two-machine
  test; treat these as relative between legs.
- **Saturation.** On the current host `-c 128` is past the knee for small messages, so the small-message
  table ranks nothing at the top end. Large payloads are bandwidth-bound rather than ceiling-bound and do
  not share this problem.
- **Half the machine each.** Server and generator get 6 physical cores apiece on the current host (8 on
  the previous one). A 12-shard server on 6 physical cores is oversubscribed, which may flatter lower
  shard counts.
- **Minimal route.** `/plaintext` is close to the best case for exposing transport differences; a real
  application would dilute them further.
- **RIO** trades bulk throughput for latency by design, so judge it on percentiles as much as on rps -
  noting that p99 is unusable below ~2ms here.

## Open

- **RIO's large-payload gap (2.2-2.5x at >=16 KB).** Quantified and attributed, but the obvious fix is
  ruled out - RIO cannot do scatter-gather. Options are K outstanding single-buffer sends (larger change,
  unmeasured) or simply a bigger `BufferPageSize` for RIO. See `TODO.md` item 0.
- **The 256 KB gap to Kestrel (4,483 vs 11,489 MiB/s).** Hypothesis is that the three copies in the
  out-of-band path dominate, which is what BYO-buffer targets. Not yet established: part of that gap is
  the AspNetDemo bridge rather than the transport, and a purely per-byte cost should not *widen* as a
  fraction of the total the way this one does between 16 KB and 256 KB. Three cheap measurements settle it
  before any design change - see `TODO.md`.
- **CPU cost per request.** Three attempts failed (confounder 9). Unmeasured, and it matters: equal
  throughput at a shared ceiling says nothing about equal efficiency, and the small-message table is
  entirely at that ceiling. Next attempt should measure at saturation with `Process.TotalProcessorTime`,
  not under a rate limit.
- How much of the remaining cost is Kestrel's pipeline rather than the transport? The bare SocketSet HTTP
  responder (`SmokeTest --http`) is the comparison, but the only figures taken for it are from the
  previous host. Re-measure both under identical conditions before repeating any "the pipeline dominates"
  claim.
- Shard counts above 12 on this host, and shards matched to *physical* rather than logical cores.
- Handshake cost, per the "not measured" section above.
