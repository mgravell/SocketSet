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
| `run-matrix.sh`, `run-tls-sizes.sh` | Linux equivalents of the two above. |

All of them fetch `bombardier` into `.tools/` on first run. Linux scripts need `jq curl taskset shuf`
(`gawk` for the nicer pivot).

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

## Environment checklist — confirm before measuring

- **Power state.** This is a laptop: battery vs mains is a large, *variable* difference in sustained
  power limit, not a few percent. Never compare a battery run with a mains run.
- **Firewall prompts.** A pending Windows Firewall dialog held every leg to ~95k rps (2.8x) with no
  errors and no other symptom. Check for one before starting.
- **io_uring under Docker.** The default seccomp profile blocks the io_uring syscalls, so the backend
  **silently falls back to managed sockets**. Pass `--security-opt seccomp=unconfined`, and trust
  `/config` rather than the flag you passed.
- **Idle sleep.** The Windows harnesses call `SetThreadExecutionState`; a host that sleeps mid-run
  produces a partial pass that reads as ordinary.
- **CPU pinning.** Server and load generator get disjoint halves, and both runtimes are told the true
  core count via `DOTNET_PROCESSOR_COUNT`/`GOMAXPROCS` — they size their ThreadPool and GC heaps at
  startup, *before* affinity is applied, and are otherwise oversubscribed against their own pinning.

## Two PowerShell traps

- A **BOM-less `.ps1` containing non-ASCII** is read as ANSI by Windows PowerShell 5.1, which turns an
  em-dash into a string delimiter and reports a syntax error hundreds of lines from the cause. **Keep
  these scripts ASCII-only.**
- `[IntPtr]0xFFFF0000` is silently **negative** — PowerShell types the literal as `Int32`. Compute
  affinity masks in `Int64`.

## Interpreting a result

Report a difference only if the per-side pass ranges are **disjoint**. `Run-Matrix.ps1` prints which leg
pairs actually separate; for everything else, compare the spread against the delta by eye. On this host,
anything under ~6% is unproven by default.
