# bench — harnesses, and how to not fool yourself

The scripts carry their own detailed headers; this file is the part that is not in any one of them:
**how to get a number you can trust on this kind of machine.**

Read this before adding a harness or believing a result. It is written from a session in which eight
separate confounders each produced clean-looking, plausible, wrong numbers — none of them announced
itself as an error.

## Which harness

| script | question it answers |
|---|---|
| **`Run-SmokeMatrix.ps1`** | *"Is it still correct?"* **Windows: the correctness gate, and the first thing to run.** 48 cells (IOCP/RIO/managed × plaintext/TLS × out-of-band verify, echo callback + pipe, poke, churn), ~3 min, one PASS/FAIL line each. No `.sh` equivalent yet. |
| `Compare-Commits.ps1` | *"Did this change help?"* Two commits, isolated worktrees, **interleaved**. **Use this for any before/after claim.** `-Bridged` measures through Kestrel rather than the bare responder; `-ExtraArgs` passes demo flags (a change on an opt-in path such as `--byo` measures as nothing without it); **`-Upload` POSTs a body to `/echo` and scores the REQUEST** — added 2026-08-04, because until then the only rig that could isolate a change could not exercise the inbound path, and the only rig that exercised it (`Run-Upload.ps1`) could not isolate a change. Note the bridged legs are BLUNT: per-side spread 5-28% against the bare rig's 0.8-9% on the same host in the same hour, so do not point `-Bridged` at a single-digit effect. |
| `Run-Byo.ps1` | Windows counterpart of `run-byo.sh`, plus the legs that keep it honest: a `classic-seg64k` control separating pipe block size from zero-copy, and a same-session `kestrel` control. Gates every leg on the `SS_IOCP_STATS` counter, not just `/config`. |
| **`Run-Upload.ps1`** | *"What does the INBOUND path cost?"* POSTs large request bodies to `/echo`, scoring goodput on the REQUEST. Exists because **no other rig here has ever sent a body** — every one sends a small request and measures a large response, so the receive path had no number at all until 2026-07-31. |
| **`Soak-Churn.ps1`** | Long connection-churn soak across backends. Exists because **no benchmark here churns connections** — every one holds keep-alive and measures steady state, which is why item 0e hid for months. Watches for all three faces of a lifetime bug: crash, wedge, and a quiet accounting imbalance. |
| **`Repro-RioChurnCrash.ps1`** | Reproduces **TODO item 0e** — an intermittent access violation in RIO+TLS under churn, present on the default configuration. Judge a fix over 20+ reps across all its configs: pool depth moves the *rate* without removing the fault, so a lower rate looks exactly like a fix. |
| `Measure-PipeMemory.ps1` | *"What does the pipe block size COST?"* Windows: peak working set vs connection count for the bridge's pipe pool. Run it at **2048** connections — the effect is connections × block, so 64 measures nothing and reads as free. |
| `Run-TlsSizes.ps1` | Windows: how transports/TLS scale with payload size (and shard count). **Its `-Shards 16` DEFAULT is wrong for 256 KB plaintext on this host** — measured 2026-08-05, `iocp/s8` beats `iocp/s16` disjointly (10,943 vs 9,283 MiB/s) and the per-leg spread collapses 16.5% → 2.6%. Sweep shards, or state the one you used beside every number. |
| `Run-Matrix.ps1` | Windows: fixed-size transport × TLS matrix. |
| `run-matrix.sh`, `run-tls-sizes.sh` | Linux equivalents of the two above. Both take `SHARDS="4 8 12"`. |
| `run-bare-vs-bridged.sh` | *"Is this cost the transport or the Kestrel bridge?"* Bare responder at a MATCHED shard count, same session. |
| `run-byo.sh` | *"Does zero-copy send buy anything?"* classic vs `--byo`, `/config`-gated both ways. |
| `run-pipe-opts.sh` | *"Do the bridge's pipe options matter?"* Block size / pinned pool, byo-vs-byo. |
| `run-recv-slab.sh` | *"Does the receive slab scale with connections?"* Peak RSS vs connections x page. |
| `diagnose-sigint-hang.sh` | Reproduces TODO 0c and names the blocked thread. **Already answered** — see its header. |
| `cpu-split.sh`, `ktls-verify.sh` | Sourced by the Linux scripts; not run directly. See below. |

The five `run-*.sh` rigs added on 2026-07-28/29 are Linux-only as written (they use `taskset` and the
Linux `/proc` interfaces). Their *headers* carry the pre-registered predictions and what would falsify
them, which is the part worth reading before re-running one — and in at least three cases the
falsification was the finding.

**A rig is not neutral about what it can see.** Two of the day's results came from adding a leg rather
than running one: `Run-Byo.ps1`'s `classic-seg64k` control is the only reason +117% could be attributed
to zero-copy rather than split with the pipe block size, and `Compare-Commits.ps1` measured the bare
responder only, so a bridged-path question could not have been asked of it at all. When a result matters,
check what the harness is *unable* to distinguish before trusting it.

**Every backend now has a counter, and all three are off by default (a `static readonly bool` read once,
so the default build pays a never-taken branch). They exist because rule 2 below cannot be satisfied by
reading code:**

| variable | backend | what it settles |
|---|---|---|
| `SS_URING_STATS=1` | io_uring | send SQEs by kind, iovec segments, pooled/pinned/zero-copy segment counts |
| `SS_IOCP_STATS=1` | IOCP | zero-copy sends **taken vs declined by cause**, and the true segment count at a fragmentation decline. This is what turned "zero-copy buys nothing at 256KB" into "it declined every 256KB response at 65 segments against a cap of 64". |
| `SS_RIO_STATS=1` | RIO | sends and bytes/send, per-direction commits, notify re-arms, port wakes, CQ drains, out-of-band flushes. Added for item 0d; its first job was proving the loop was **idle**, which killed several candidates at once. |
| `SS_BRIDGE_STATS=1` | pipe bridge (all backends) | inbound receives, sync vs async flushes, the STAGED second copy, and (2026-08-04) **`PARKED=`**. This is the one that makes a receive-parking result interpretable: `STAGED` 3,141 → 0 with `PARKED` equal to async flushes is what "parking works" looks like from outside. |

**A counter that cannot see the path reads exactly like a path that did not run.** `SocketSetTransportMetrics.ReceiveParks` lives in `SocketSet.AspNetCore` and only the classic/half-pipe path can reach it; in BYO mode the library's own `PipeIoBridge` drives the pipe, so it reports **0** however hard that connection parks. It did exactly that on 2026-08-04 — 0 against a real ~4,300 parks in six seconds — and "BYO never parks" was one sentence from being recorded as a finding. When a counter reads zero, establish that it is WIRED to the path before concluding the path is idle.

**If you are picking this up on Windows after the Linux work:** the catch-up was done on 2026-07-29/30 —
read the top of `../TODO.md` for what it found. Run `Run-SmokeMatrix.ps1` before believing anything.

**A confounder the other nine did not cover: a harness bug that makes every cell AGREE.** The first run
of `Bisect-RioChurnCrash.ps1` reported all eight variants identical — including two controls that were
known to behave differently. That is not a finding, it is a broken rig, and it happened because a
function parameter was named `$args`, a PowerShell automatic variable, so every variant launched with an
empty argument list and printed usage. It produced a clean, symmetric, entirely wrong table.

The general rule this earns: **put a control in the matrix whose answer you already know, and read it
FIRST.** Uniformity across cells that should differ is a harness failure until proven otherwise. That rig
now throws outright if a run exits cleanly without ever entering the scenario, rather than scoring it.

All of them fetch `bombardier` into `.tools/` on first run. Linux scripts need `jq curl taskset shuf`
(`gawk` for the nicer pivot, `lscpu` for the CPU split — without it the split falls back and warns).

## The rules, and why each exists

**1. Never compare across runs. Use `Compare-Commits.ps1`.**
The same binary measured in two different runs varied by up to **6%** on this host, and once by **58%**
(2,454 vs 3,843 MiB/s). A cross-run comparison once understated a real effect by 3x, and another time
reported a 4% "regression" that was two identical builds. If before and after were not measured minutes
apart in one session, the number is not evidence.

**2. Look at the per-pass values, not just the median.**
`Compare-Commits.ps1` prints every scored pass. A run disrupted midway looks like this:

```
after 120.5, 119.5, 46.4      <- power was lost during pass 3
```

The median silently absorbs that into a believable ~30% regression. The spread is the only thing that
shows it.

**3. Discard the first pass.** A cold machine runs at boost clocks and the transient spans an entire
pass, not a request — one run opened at ~258k rps and decayed through itself to ~115k while later passes
sat flat at ~111k. Per-*request* warmup does not fix this.

**4. Reshuffle leg order every pass.** In a fixed order, anything that accumulates is indistinguishable
from a property of whichever leg runs late.

**5. Verify what actually loaded.** Every harness hits `/config` and refuses to record a leg whose
reported backend or TLS mode is not what was asked for. Silent fallback is the failure mode here — see
the io_uring note below.

**6. A harness must not touch the repository it measures.**
`git checkout <sha> -- <paths>` updates the **index**, not just the working tree. An earlier A/B did that
in the background; an unrelated `git add <one-file>; git commit` in the same checkout then committed the
staged reverts, because `git commit` writes the whole index. It reverted the change under test, and the
A/B measured the old code as its own "after". Use worktrees.

**6b. Sweep SHARD COUNT before believing a transport comparison, and do not assume one answer covers the
payload range.** Measured 2026-08-05 on this host: at a 2-byte payload more shards is monotonically
better (`s4 → s8 → s16` is +69% on IOCP), while at 256 KB plaintext it REVERSES (`s8` beats `s16`
disjointly, +17.9%) — and at 256 KB *TLS* it reverses back (`s16` beats `s8` by +26.9%). One default
cannot serve all three. This cost a headline number: the 256 KB plaintext row of the Windows baseline was
first measured at the rig's default `s16` and read as a 20% deficit against Kestrel; at `s8` the same
comparison is parity. The 2-byte sweep was run FIRST and appeared to settle the question in the opposite
direction, which is the trap — a shard-count answer is only valid at the payload it was measured at.

**7. Sweep concurrency before believing two transports are equal.**
A saturated operating point flattens everything that can reach the ceiling. On the current host (12C/24T
desktop) `-c 128` is **past the knee** for small messages: throughput moves less than the run-to-run
spread across `-c 64/128/256` while p99 rises in proportion, and eight legs converge inside 1.3% looking
exactly like parity. Both pinned halves saturate together (90-98%), so it is the box, not one side. Run
`Run-Matrix.ps1 -Filter <leg> -Connections 64,128,256` before trusting a tie. This one has bitten before
from the other direction: a ~100k "generator ceiling" was once concluded from what turned out to be a
firewall dialog, so establish a ceiling by sweeping concurrency, never by inference from a flat table.

**8. Do not quote p99 below ~2ms on Windows.**
It is quantised at roughly 500µs by Go-client timer granularity - observed values cluster on 1,005 /
1,188 / 1,503 / 2,000µs. Sixteen unrelated legs all reporting an identical 1,503µs is the instrument, not
the transports. Larger values (the multi-millisecond figures in the payload sweep) are above the quantum
and usable.

**9. Do not measure CPU cost per request under a rate limit.**
Three attempts produced per-leg spreads of 38-174% and TLS legs *cheaper* than their own plaintext
controls. The first two sampled `\Processor(N)\% Processor Time` at 1Hz - the wrong instrument. Switching
to exact, kernel-accounted `Process.TotalProcessorTime` deltas **did not fix it**: one leg swung 63.55 ->
38.24 core-µs/req between passes. The operating point is the real confound - at a fixed sub-saturation
rate the server has idle gaps, and threads that wake, find nothing and spin before sleeping charge that
time to the next request. A slower path (TLS) leaves fewer gaps to spin in and so measures *cheaper* while
doing more work. **Measure cost per request at saturation**, where there are no idle gaps, and gate on
"every TLS leg must cost more than its own plaintext control".

## Environment checklist — confirm before measuring

- **Power state.** On a laptop, battery vs mains is a large and *variable* difference in sustained power
  limit, not a few percent - never compare across the two. The current host is a desktop on mains, so this
  no longer applies here, but the older figures in `RESULTS.md` were taken on a laptop.
- **Mismatched memory pools (an APPLES-TO-ORANGES default).** Comparing our ASP.NET path against vanilla
  Kestrel at 256KB looked like a clean −14 to −16% loss for weeks — but Kestrel backs its pipes with a
  `PinnedBlockMemoryPool` by default while AspNetDemo defaulted to `MemoryPool.Shared`, so OUR zero-copy
  send paid ~64 `GCHandle` pins per 256KB response that Kestrel does not. That WAS the whole gap: with
  matched (pinned) pools both backends reach parity/edge ahead. Fixed by pinning the demo pool by default
  (2026-07-31; `--pipe-unpinned` opts out). Lesson: when you benchmark against another stack, match its
  buffer/pool strategy, or you are measuring your own default's handicap, not the transport.
- **Firewall prompts.** A pending Windows Firewall dialog held every leg to ~95k rps (2.8x) with no errors
  and no other symptom - and was then misread as a generator ceiling, so it cost a wrong conclusion as
  well as a wrong number. Do not rely on spotting the dialog: allow-list the binaries **by path**, for all
  profiles and protocols, once. Remember a `Block` rule beats an `Allow` rule, so remove any stale rules
  for those paths first - dismissing a prompt can leave one behind.
- **Background host load.** Whatever else runs on the machine hits low shard counts hardest: measured
  2026-07-27, s4 legs moved 3.6-5.2% with background load while s12 legs moved under 0.5%. At low shard
  counts each loop thread is itself the bottleneck, so stolen cycles come straight off throughput. Quiesce
  the host, and never change what is running on it *during* a run - a mid-run change makes the passes
  before and after incomparable, which is the whole run.
- **io_uring under Docker.** The default seccomp profile blocks the io_uring syscalls, so the backend
  **silently falls back to managed sockets**. Pass `--security-opt seccomp=unconfined`, and trust
  `/config` rather than the flag you passed.
- **Idle sleep.** The Windows harnesses call `SetThreadExecutionState`; a host that sleeps mid-run
  produces a partial pass that reads as ordinary. **The `.sh` rigs have no equivalent** — see the Linux
  host section, where sleep is worse than a bad pass.
- **CPU pinning.** Server and load generator get disjoint halves, and both runtimes are told the true
  core count via `DOTNET_PROCESSOR_COUNT`/`GOMAXPROCS` — they size their ThreadPool and GC heaps at
  startup, *before* affinity is applied, and are otherwise oversubscribed against their own pinning.
  **"Disjoint" means disjoint physical CORES, and the obvious arithmetic does not give you that on
  Linux** — see `cpu-split.sh` and the host section below.

## The Linux bench host (from 2026-07-28)

Linux measurement moved from a Docker container on a WSL2 kernel to **bare metal**: Pop!_OS 24.04,
kernel 7.0.11-76070011, the same Ryzen 9 7900X desktop (12C/24T, 124 GB) the Windows numbers come from.
io_uring is available with no seccomp workaround. Two consequences, both load-bearing:

- **The Docker seccomp caveat does not apply here** — but it applied to *every Linux figure recorded
  before this date*, all of which are also from the older laptop. There is no Linux baseline on this host.
- **Two backends, one box, no NIC.** Still loopback, so kTLS's inline-offload win remains unmeasurable
  regardless of the OS change. That needs two machines and is the whole of TODO item 5's remainder.

### Setup this host needs, and why each one bites

- **CPU governor.** Ships as `amd-pstate-epp` / `powersave` with EPP `balance_performance`. Set both to
  `performance` (`/sys/devices/system/cpu/cpu*/cpufreq/{scaling_governor,energy_performance_preference}`)
  or the clock moves under you and the whole disjoint-ranges discipline is measuring the governor.
  **Does not survive a reboot** — re-check it rather than assuming.
- **Suspend, which on this box is a REBOOT.** GNOME default with a 30-minute idle suspend on AC, and this
  machine has never once resumed from it — a suspend means a hard reset, a lost session, and a run that
  ends with no partial results and no log of why. Disabled 2026-07-28 with
  `gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type 'nothing'` (user-level
  dconf, survives reboots, no sudo). Unlike the Windows rigs there is no `SetThreadExecutionState`
  equivalent here, so nothing in the harness will hold the box awake for you. If a long sweep vanished,
  check `uptime` before you look for a bug.
- **SMT enumeration.** Linux numbers CPUs 0-11 as *one thread of each of the twelve physical cores* and
  12-23 as their siblings, so a lower/upper-half split gives the server and the load generator the SAME
  cores. Windows enumerates siblings adjacently, which is why the `.ps1` rigs get away with the same
  arithmetic. `cpu-split.sh` splits by core: `0-5,12-17` against `6-11,18-23`.
- **`DOTNET_ROOT`.** The SDK lives in `~/.dotnet`; the built apphosts look in `/usr/share/dotnet` and die
  with a *missing runtime* error that reads like a broken install. Exported from `.bashrc`, which is
  interactive-only — a script launched from a non-interactive context still needs it.
- **`tls` kernel module.** Not loaded by default. Without it kTLS cannot engage, and `/config` will still
  cheerfully report `tls=ktls`. `sudo modprobe tls`, persist via `/etc/modules-load.d/tls.conf`.
- **`perf`.** System76's `linux-tools-common` is a docs-only stub, so there is no `/usr/bin/perf` wrapper
  and the `command-not-found` suggestion is wrong. Install `linux-tools-generic-hwe-24.04` and symlink
  `/usr/lib/linux-tools/<ver>/perf`. For kernel frames you also need
  `kernel.perf_event_paranoid=1` and `kernel.kptr_restrict=0`, else perf silently degrades to `cycles:u`
  and you profile user space only. Add `DOTNET_PerfMapEnabled=1` so JIT frames resolve.

### `/config` proves configuration; `/proc/net/tls_stat` proves behaviour

The harnesses refuse a leg whose `/config` does not match what was asked for. That catches a backend that
silently fell back — but for kTLS it cannot catch a socket the kernel never took, because the demo would
report `tls=ktls (openssl + kernel offload)` either way and would serve HTTPS correctly in userspace at
userspace speed. `/proc/net/tls_stat` is the kernel's own accounting, and `ktls-verify.sh` gates on it.

**Measured 2026-07-28, and it changes how every kTLS figure should be read:** traffic through the kTLS leg
moves `TlsTxSw` and leaves `TlsRxSw` at **zero**. Transmit is offloaded into the kernel; receive is not
offloaded at all, because that path drives receive as io_uring `POLL` + `SSL_read` in userspace. So kTLS
here means **TX-only offload** — a property of our integration, not of kTLS. `TlsTxDevice` stays 0 and
always will on loopback.

## Two PowerShell traps

- A **BOM-less `.ps1` containing non-ASCII** is read as ANSI by Windows PowerShell 5.1, which turns an
  em-dash into a string delimiter and reports a syntax error hundreds of lines from the cause. **Keep
  these scripts ASCII-only.**
- `[IntPtr]0xFFFF0000` is silently **negative** — PowerShell types the literal as `Int32`. Compute
  affinity masks in `Int64`.

## Interpreting a result

Report a difference only if the per-side pass ranges are **disjoint**. `Run-Matrix.ps1` prints which leg
pairs actually separate; for everything else, compare the spread against the delta by eye.

**Disjointness is only as good as the pass count, and the required count grows with payload.** Measured
2026-07-28 on bare io_uring: at a **256KB** payload the true per-cell spread is **~8%**, but any three
consecutive passes can span as little as **1.2%**. Three passes there produce a falsely tight range, and
two such ranges can be disjoint while describing *identical* configurations - which is exactly what
happened when the same `p4096` cell was run in two sessions (8,016 [8007-8102] vs 7,846 [7759-7850]).
Small payloads do not have this problem: 16KB cells spread ~1.5% over the same three passes.

So: **at 256KB use six scored passes, not three**, and treat any existing low-single-digit 256KB claim
built on three passes as unproven. This bites hardest on cross-session comparisons, because a
same-session mistake at least shares a warm-up state.

**The noise floor is a property of the host, not of the project.** On the previous laptop it was ~6%, and
once 58%. On the current desktop per-leg spreads run 0.2-2.4%, so a 2% effect is detectable here and was
unprovable there. Re-establish it on any new machine - run the same leg several times before running
anything else - rather than inheriting a number from this file.
