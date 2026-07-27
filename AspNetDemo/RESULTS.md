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

| leg | -c 64 | -c 128 | -c 256 |
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

| backend | bare | bridged | bridge cost |
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

**The prediction was wrong and the opposite is true.** The configuration that drops connections is the one
shipping today - 208 of them at 2048 connections - while every large-page configuration is clean at 0-1.

The mechanism, in hindsight: RIO holds exactly ONE write page per in-flight send, and at a 4 KB page a
256 KB response occupies that page across **64 sequential round trips**. At 64 KB it needs 4. Pool
*occupancy time* collapses, so a bigger page relieves pool pressure rather than adding to it. The original
reasoning counted buffers and ignored how long each is held.

So `64 KB page + 4 KB recv + 256 write buffers` is faster at every concurrency tested, has strictly better
error behaviour than the shipped default, and costs 144 MB across 12 shards.

**Still not changed as a default**, deliberately: these are Windows measurements at one payload shape on
loopback, `BufferPageSize` is shared with io_uring and epoll where it has not been swept, and the 208
errors on the current default are a pre-existing defect that wants fixing on its own terms (queue rather
than close) rather than being papered over by a page-size change. The knobs are now plumbed end to end -
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

## Linux: epoll vs io_uring vs kTLS (2026-07-26)

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
