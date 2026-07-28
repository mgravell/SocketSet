# bench — harnesses, and how to not fool yourself

Four harnesses live here. The scripts carry their own detailed headers; this file is the part that is not
in any one of them: **how to get a number you can trust on this kind of machine.**

Read this before adding a harness or believing a result. It is written from a session in which eight
separate confounders each produced clean-looking, plausible, wrong numbers — none of them announced
itself as an error.

## Which harness

| script | question it answers |
|---|---|
| `Compare-Commits.ps1` | *"Did this change help?"* Two commits, isolated worktrees, back to back. **Use this for any before/after claim.** |
| `Run-TlsSizes.ps1` | Windows: how transports/TLS scale with payload size (and shard count). |
| `Run-Matrix.ps1` | Windows: fixed-size transport × TLS matrix. |
| `run-matrix.sh`, `run-tls-sizes.sh` | Linux equivalents of the two above. Both take `SHARDS="4 8 12"`. |
| `cpu-split.sh`, `ktls-verify.sh` | Sourced by the Linux scripts; not run directly. See below. |

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
  no longer applies here, but the older figures in `AspNetDemo/RESULTS.md` were taken on a laptop.
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
  produces a partial pass that reads as ordinary.
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

**The noise floor is a property of the host, not of the project.** On the previous laptop it was ~6%, and
once 58%. On the current desktop per-leg spreads run 0.2-2.4%, so a 2% effect is detectable here and was
unprovable there. Re-establish it on any new machine - run the same leg several times before running
anything else - rather than inheriting a number from this file.
