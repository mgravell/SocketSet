# Working in this repo

`README.md` is the user-facing doc. This file is for whoever picks the work up — human or agent — and
exists because the important context is *not* in the code.

## Read these before doing anything

| file | why |
|---|---|
| **`TODO.md`** | The backlog, the design calls, and **why** each was made. Starts with a "READ FIRST IF YOU ARE ON WINDOWS" section — read it if that is you, because shared code changed under Windows while it was not running. |
| **`RESULTS.md`** | Every measurement of record, with its method and its caveats. Opens with "WHERE THINGS STAND" — the consolidated feature matrix and headline numbers. |
| **`bench/README.md`** | How to get a number you can trust on this kind of machine. Written from a session where **eight** separate confounders each produced clean-looking wrong numbers. |

`TODO.md` and `RESULTS.md` are long on purpose: they record retractions and falsified predictions as
prominently as results, because several conclusions here have been reversed by their own follow-up tests.
If you are about to "just quickly re-derive" something, it is probably already in there with a reason.

## The house rules for measurement

These are not style preferences; each one was learned by getting a wrong answer first.

1. **Never trust a flag — trust the banner.** Every rig gates on `/config` (or the `http-bench:` line)
   reporting what the process actually did. A flag that parses and is ignored measures identically to one
   that works. This has happened: `--recv-buffer` did nothing on Linux for weeks while the banner printed
   it back.
2. **Confirm a fast path was TAKEN, not just enabled.** A path that silently declines measures identically
   to one that ran and did not pay. IOCP's zero-copy send looked like "no benefit at 256KB" for a week; it
   was actually declining every response (65 segments against a 64-segment cap).
3. **Pre-register what would falsify you**, in the rig header or the commit message, before running it.
   Several predictions here have been falsified and the falsification was the finding.
4. **Six scored passes at 256KB, not three.** Three consecutive passes can span 1.2% when the true spread
   is 9–17%.
5. **Ranges, not medians alone.** If the min-max ranges overlap, say so and do not quote the delta.
6. **A control in the same session.** Cross-run and cross-machine comparisons have produced confident
   nonsense here more than once. Two hosts and two OSes appear in `RESULTS.md`; never subtract across them.

## This is pre-alpha, and it changes what you are allowed to do

There are **no users, no releases and no back-compat obligation**. Two consequences worth stating because
both have caused hesitation:

- **Public API and defaults can change freely**, given a measurement. Item 0 sat blocked for days partly
  on "that changes public option semantics" — which was not a real constraint. Change it, measure it,
  write down why.
- **Say "the default", not "shipped".** Nothing ships. "Shipped" implies released code with users, and
  makes a default change read as a breaking change needing care it does not need.

## Conventions

- **Commit messages carry the reasoning**, including what was *dis*proved and what remains unverified.
  They are the primary record; the diff is secondary.
- **Do not add AI/Co-Authored-By trailers to commits.**
- **Commit, do not push**, unless asked.
- `bench/results/` is gitignored — raw CSVs and logs live there.
- The `bench/run-*.sh` rigs are Linux-only as written (`taskset`, `/proc`); the `.ps1` rigs are Windows.

## Things that are true and non-obvious

- **This box cannot show everything.** It is one machine over loopback, so kTLS's NIC offload
  (`TlsTxDevice`) is structurally invisible and always will be. Large-payload numbers carry that caveat.
- **Capability is discovered, never assumed** — e.g. kTLS RX is probed via `BIO_get_ktls_recv`, so an old
  OpenSSL degrades to TX-only rather than breaking. Keep it that way, and make degradations *say so*: a
  silent one made a year of kTLS figures mean something other than they appeared to.
- **No unit tests.** `SmokeTest` is the correctness gate: `--verify-echo` (byte-exact round-trip),
  `--verify`, `--churn`, `--poke`, across backends, plaintext and TLS, callback and `--pipe`. Run it on
  every backend you touched — this is the hottest code in the repo and the only safety net.
  It is one command per OS now: **`bench/Run-SmokeMatrix.ps1`** on Windows (48 cells: IOCP/RIO/managed),
  and **`bench/run-smoke-matrix.sh`** on Linux (60 cells: io_uring/epoll/managed x plaintext/OpenSSL-TLS,
  plus `@abstract`-UDS and `+ktls` cells). Each reduces to one PASS/FAIL line per cell in ~3-6 minutes.
  Both exist because the gate was previously run by hand and so was skipped between OSes.
- **Those gate the TRANSPORT. Two narrower gates cover things they cannot see** (Windows only so far; both
  want a Linux equivalent):
  - **`bench/Verify-AspNet.ps1`** (Windows) / **`bench/verify-aspnet.sh`** (Linux, added 2026-08-03) —
    the ASP.NET BRIDGE, which nothing gated until 2026-08-01 (and nothing gated on Linux until
    2026-08-03). 18 cells each (backend x `byo`/`classic`/`half-pipe` x plaintext/TLS), ~15s-2min:
    `/config` banner, byte-exact `/payload` 1B-8MB and POST `/echo`, `/stats` counters. Run it on any
    bridge or `AspNetDemo` change. Run it on BOTH sides of a refactor — comparing two runs cell-by-cell
    is what turns "it works" into "it is behaviour-preserving".
  - **`bench/Verify-TlsFloor.ps1`** (Windows) / **`bench/verify-tls-floor.sh`** (Linux, added
    2026-08-03) — that the TLS min-version floor is APPLIED, not merely configured. The discriminating
    cell is one that must be REFUSED; "TLS still works" cannot distinguish a floor that took from one
    that did nothing. Both narrow gates now exist on both OSes.
