# Benchmark results - AspNetDemo transport matrix

Numbers produced by [`../bench/Run-Matrix.ps1`](../bench/Run-Matrix.ps1), which drives this project through
bombardier. Run it from the repo's `bench/` folder; raw CSV and per-leg logs land in `bench/results/`
(gitignored).

> Getting a trustworthy number out of a single-box loopback benchmark took six attempts. Every failed
> attempt produced clean-looking output - plausible magnitudes, zero failed requests - and none announced
> itself as an error. The confounders are documented at the bottom; they are the more transferable half of
> this document.

## Environment

| | |
|---|---|
| Host | Windows 11, 16 cores / 32 logical processors |
| Date | 2026-07-26 |
| Load generator | bombardier v1.2.6, fasthttp client, same box (loopback) |
| Server | `AspNetDemo`, Release, HTTP/1.1 pinned on every leg |
| TLS certificate | one shared RSA-2048/SHA-256 self-signed cert across **all** legs, Kestrel's included |
| Route | `/plaintext` - minimal work, so the transport is as visible as it can be |
| Pinning | server -> logical CPUs 0-15, generator -> 16-31; `DOTNET_PROCESSOR_COUNT`/`GOMAXPROCS` = 16 |
| Method | `-c 128 -d 10s`, 4 passes in reshuffled order, pass 1 discarded as host warm-up, median of 3 |

## Keep-alive, steady state

All legs zero errors. `SpreadPct` is each leg's own run-to-run range - the honesty check.

| leg | med rps | vs kestrel | spread | med p99 |
|---|---:|---:|---:|---:|
| managed | 128,148 | +1.5% | 1.8% | 2,382µs |
| **kestrel** *(control)* | 126,199 | - | 1.1% | 2,716µs |
| managed+tls | 121,665 | -3.6% | 0.7% | 2,527µs |
| rio-s16 | 118,052 | -6.5% | 0.4% | 3,178µs |
| kestrel+tls | 117,773 | -6.7% | 1.4% | 2,999µs |
| iocp-s16 | 116,516 | -7.7% | 0.9% | 3,001µs |
| rio-s16+tls | 112,374 | -10.9% | 0.8% | 3,519µs |
| iocp-s16+tls | 111,127 | -11.9% | 0.9% | 3,001µs |
| rio-s8 | 103,041 | -18.4% | 1.3% | 4,520µs |
| iocp-s8 | 99,930 | -20.8% | 4.5% | 6,219µs |
| iocp-s8+tls | 95,235 | -24.5% | 3.5% | 7,936µs |
| rio-s8+tls | 94,746 | -24.9% | 5.0% | 6,857µs |
| rio-s4 | 94,479 | -25.1% | 2.3% | 2,549µs |
| iocp-s4 | 89,521 | -29.1% | 1.6% | 2,721µs |
| rio-s4+tls | 88,581 | -29.8% | 0.7% | 2,521µs |
| iocp-s4+tls | 83,629 | -33.7% | 1.3% | 2,615µs |

Between-leg range 53.2%, worst within-leg spread 5.0% - the differences are larger than the noise.

### What it says

**Shard count is the dominant variable.** IOCP: 89.5k -> 99.9k -> 116.5k across 4/8/16 shards; RIO:
94.5k -> 103.0k -> 118.1k. Obvious in hindsight - the server is pinned to 16 logical CPUs, so 4 shards
leaves most of them idle. Anyone benchmarking these backends should match shards to available cores
before comparing anything else.

**The thread-pool backends win here.** `managed` (128.1k) and stock `kestrel` (126.2k) beat every sharded
configuration, including 16 shards (116-118k). They use the .NET thread pool and therefore all 16 CPUs
without the operator having to size anything. That the specialised backends do not beat stock Kestrel on
this workload is worth sitting with rather than explaining away - though see the caveats: this is a
minimal route on loopback, which is close to the best case for a general-purpose transport.

**RIO edges IOCP at every shard count** on throughput: +5.6% at s4, +3.1% at s8, +1.3% at s16. Its p99 is
better at s4 and s8 but slightly worse at s16.

**TLS cost, in-transport SChannel vs Kestrel's SslStream** - the question this exercise started from:

| stack | plaintext -> TLS | cost |
|---|---|---:|
| Kestrel + SslStream | 126,199 -> 117,773 | **-6.7%** |
| managed + SChannel | 128,148 -> 121,665 | **-5.1%** |
| iocp-s16 + SChannel | 116,516 -> 111,127 | **-4.6%** |
| rio-s16 + SChannel | 118,052 -> 112,374 | **-4.8%** |

Terminating TLS in the transport with raw SSPI costs roughly **4.6-5.1%**, against **6.7%** for Kestrel's
SslStream - about a third less relative overhead, consistently across three backends. This is
steady-state record-layer cost only; handshake cost is out of scope (see below).

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
- **Half the machine each.** Server and generator get 8 physical cores apiece. A 16-shard server on 8
  physical cores is oversubscribed, which may flatter lower shard counts.
- **Minimal route.** `/plaintext` is close to the best case for exposing transport differences; a real
  application would dilute them further.
- **RIO** trades bulk throughput for latency by design, so judge it on percentiles as much as on rps.

## Open

- How much of the remaining cost is Kestrel's pipeline rather than the transport? The bare SocketSet HTTP
  responder (`SmokeTest --http`) is the comparison, but the only figures taken for it predate the firewall
  fix and used different pinning, so they are not comparable. Re-measure both under identical conditions
  before repeating any "the pipeline dominates" claim.
- Shard counts above 16, and shards matched to *physical* rather than logical cores.
- Handshake cost, per the "not measured" section above.
