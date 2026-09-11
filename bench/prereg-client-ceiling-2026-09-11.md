# PRE-REGISTERED 2026-09-11, before the proxy rig's generator capacity was touched

TODO (2026-09-11 queue, item 1): at `-P 1` the proxy A/B cannot separate us from Envoy because BOTH
legs sit at 90% of a CLIENT-limited ceiling (451,613 vs 451,584 ops/s against a `direct` ceiling of
499,929 — the same number twice). Until the generator can push past that, "parity" is a FLOOR and no
upper bound on Envoy is quotable from that column.

**This experiment does not measure the proxy at all.** It measures the GENERATOR, using `direct`
(redis-benchmark straight to garnet-server, no proxy in the path) as the instrument, and asks one
question: **is the ceiling per-PROCESS or per-CORE?** The answer decides the fix, and the two fixes have
very different costs:

- **per-process** → sum N `redis-benchmark` processes on the SAME client cores. Free: the three-way core
  split is untouched, so every existing number stays comparable.
- **per-core** → the client third (4 physical cores of 12) must grow, which means the proxy or backend
  third must shrink. That changes what is under test, so it is a different machine, not a purer
  measurement, and every absolute in the `-P 1` table would have to be re-measured.

## Configuration under test

All legs `direct`, `-t get`, `-P 1`, `-d 32`, same session, same shuffled order every pass, six scored
passes, garnet-server started ONCE and shared so the backend is a constant. Client processes are pinned
as a group to the existing client third (cores 0-3 → CPUs 0,1,2,3,12,13,14,15); the server third is
unchanged. **The proxy third sits idle in every leg**, exactly as it does for the `direct` leg of the real
rig — which is itself worth stating, because it means the recorded 499,929 ceiling was measured with a
third of the box doing nothing.

| leg | procs | `-c` each | total conns | `--threads` each |
|---|---:|---:|---:|---:|
| `1x64` (**CONTROL — the status quo**) | 1 | 64 | 64 | 6 |
| `1x64-t8` | 1 | 64 | 64 | 8 |
| `2x32` | 2 | 32 | 64 | 4 |
| `4x16` | 4 | 16 | 64 | 2 |
| `4x64` | 4 | 64 | 256 | 6 |

`2x32` and `4x16` hold TOTAL CONNECTIONS at 64 on purpose, so a rise there is attributable to the process
boundary and not to having more sockets. `4x64` then varies connections as well, which is what separates
"more processes" from "more connections".

## Predictions

- **P14 — the ceiling is PER-PROCESS: `4x16` beats `1x64` with disjoint min-max ranges.** Basis: at
  `-P 1` every connection is a strict round trip, so a generator process is bounded by how fast it turns
  syscalls around, and `--threads` shares one process's event-loop structures. This repo has already hit
  exactly this and solved it the same way — `Run-GarnetAb.ps1` runs **4 generator processes x 50
  connections, summed**, with "one process cannot saturate the server" written into its header as
  confounder 1. **FALSIFIED IF** `4x16`'s range overlaps `1x64`'s.

- **P15 — `--threads` alone does NOT fix it: `1x64-t8` overlaps `1x64`.** This is the cheap-fix check,
  and it is worth a leg precisely because if it FAILS the prediction the fix is a one-word change to the
  rig. **FALSIFIED IF** `1x64-t8` is disjointly ahead of `1x64`.

- **P16 — the backend is NOT the binding constraint at the higher rate.** garnet-server has 4 physical
  cores and the `direct` leg is supposed to prove headroom, not consume it. **FALSIFIED IF** the winning
  leg drives garnet-server above ~3.5 of its 4 cores, or if `4x64` fails to beat `4x16` *while* server
  CPU is pegged — either would mean the ceiling merely MOVED from client to backend, and the `-P 1`
  column would still be unable to separate two proxies.

## What would make the whole thing moot, stated now

If every leg lands inside one overlapping band, the ~500k is **neither** per-process nor per-core but a
property of the loopback path or of garnet-server itself. In that case the honest consequence is that
this box cannot resolve `-P 1` at all and the comparison needs a second machine — which is already on
record as the real-NIC dependency, and would be a finding rather than a failure.

## Caveat that applies to every number here

**Governor is `powersave` / `balance_performance`** (the box reboots to it and setting `performance`
needs root, which this session does not have). The 2026-08-02 proxy table was measured under
`performance`, so **nothing here may be compared against that table** — these legs are only ever compared
against EACH OTHER, in the same session, which is the case `RESULTS.md` has previously accepted under the
same caveat. The scored `-P 1` re-run that follows this diagnostic MUST be done under `performance`.

Also unchanged from the 2026-09-11 state refresh: the box is on **kernel 7.1.5**, not the 7.0.11 every
existing Linux throughput figure was measured on. That is a second reason the control leg matters — if
`1x64` does not land near the recorded 499,929, something other than process count has moved.
