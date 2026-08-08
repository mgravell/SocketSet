# Working in this repo

`README.md` is the user-facing doc. This file is for whoever picks the work up — human or agent — and
exists because the important context is *not* in the code.

This library makes extensive and unapologetic use of agent assistance under human guidance and oversight.

## Read these before doing anything

| file | why |
|---|---|
| **`TODO.md`** | The backlog, the design calls, and **why** each was made. Starts with a "READ FIRST IF YOU ARE ON WINDOWS" section — read it if that is you, because shared code changed under Windows while it was not running. |
| **`RESULTS.md`** | Every measurement of record, with its method and its caveats. Opens with "WHERE THINGS STAND" — the consolidated feature matrix and headline numbers. |
| **`REVIEW.md`** | Security/correctness audits of record: what was found, how it was established, what was decided, and what was deliberately left open. What `RESULTS.md` is to measurements, this is to reviews — including the things checked that turned out to be nothing, so nobody re-derives them. |
| **`IOCP-VS-RIO.md`** | Which Windows backend to use, and why — with the ~3.2µs RIO completion floor that decides it, and the SEVEN things tried that did not fix the depth-1 gap. Read before touching either Windows backend or proposing a fix for RIO. |
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
- **ONE COMMAND runs every security gate on Windows: `bench/Run-SecurityGates.ps1`.** Full multi-target
  build first (a `-f net10.0` build is blind to net472 breaks, which has bitten), then the gates cheapest
  first so a broken build does not cost you the smoke matrix before you find out. The Linux equivalent is
  still the individual rigs.
- **Those gate the TRANSPORT. The narrower gates cover things they cannot see:**
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
  - The three `bench/verify-*` .NET rigs below are CROSS-PLATFORM: they pick this OS's backends and TLS
    provider automatically (`bench/GateBackends.cs` — IOCP/RIO/Managed + SChannel on Windows,
    io_uring/epoll/Managed + OpenSSL on Linux), so `dotnet run --project bench/<rig>` is the whole
    invocation on either.
  - **`bench/verify-tailwipe`** (added 2026-08-04; cross-platform, `dotnet run --project`) — that the
    recycled receive/send buffers cannot put a previous tenant's bytes (another client's decrypted
    plaintext, under TLS) on the wire, AND that avoiding them is charged at cost: a 20-byte request
    replying 25 must clear exactly 5, not 0 and not the whole tail. Three of its four cells are
    disclosure vectors; the discriminating one is `ResponseBytes` set above `PayloadBytes` WITHOUT ever
    touching `RawBuffer`, which a wipe-on-first-access sails straight past. Run it on any change to the
    context types. **Since 2026-08-04 every cell runs TWICE**, with
    `SocketSetOptions.DangerousDisableBufferWipe` off and on, and the off-half asserts the INVERSE — the
    previous tenant's bytes MUST come back, and `cleared` must be exactly 0. That is the only direction
    in which the opt-out can be tested at all: an inert flag leaves the on-half green, and a flag that
    leaked into the default leaves the off-half green.
  - **`bench/verify-parking`** (added 2026-08-04; cross-platform) — that a slow consumer SLOWS THE PEER
    instead of getting the peer killed (receive parking, `REVIEW.md` D3). Parking's failure mode is a
    HANG, not a leak: a connection that parks and never resumes stays open, healthy-looking and silent
    forever, and nothing in the smoke matrix can see it because every cell there has a consumer that
    keeps up and so never parks at all. Hence `resume/completes`, whose only job is to release a stalled
    consumer and demand all 8 MiB back byte-exact. The discriminating cell is `stalled/peer-held`: the
    sender must STOP (two samples a second apart, so "slow" cannot pass as "stopped") while the
    connection stays ALIVE — against the pre-parking code the sender instead runs to the 4 MiB bound and
    the connection is dropped. `drain/control` runs the same volume past a consumer that does read, so
    the stall cannot be general slowness. On a backend that reports it cannot park (io_uring), the rig
    asserts the DOCUMENTED degradation rather than skipping, so a backend that quietly started or
    stopped parking fails. A fifth cell, `parked/peer-vanishes`, covers the state a CHURN SOAK cannot
    reach: parking leaves a live connection with NO RECEIVE OUTSTANDING, which is what IOCP/RIO
    defer-recycle reasons about, and a churn soak's echo consumer always keeps up so it never parks. Its
    diagnostic reports `noticed while parked: False` — a completion backend cannot see the peer leave
    while parked, because seeing it needs an armed receive.
  - **`bench/verify-tlsname`** (added 2026-08-04; cross-platform) — that hostname verification actually
    RUNS, and that the `"*"` opt-out actually opts out. Its meaning lives entirely in the cells that must
    be REFUSED (`wrong.example`, `127.0.0.2`, and an unset host); the accept-cells alone would pass just
    as happily against the pre-fix code, where a null `TargetHost` silently skipped the name check. It
    also asserts what the SERVER was told via SNI, which is what proves `"*"` and IP literals really do
    suppress the extension on the wire rather than merely being documented to. **Since 2026-08-05 it does
    not have to ask the server**: it opens a plain socket, reads the ClientHello and parses `server_name`
    out of it, so the announce half is asserted on BOTH providers — SChannel cannot report received SNI,
    which had left every announce cell skipped on Windows, i.e. exactly the half the
    `ServerNameIndication` split changes. When a gate's only discriminating assertion is unobservable on
    your OS, read the wire rather than skipping the cell.
  - **`bench/verify-timeouts`** (added 2026-08-04; cross-platform) — that a peer which connects and goes
    quiet is actually reaped. Self-controlling rather than merely positive: a COMPLETED handshake must
    survive the same budget (so "reaped" cannot mean "we drop everything"), and the idle-off/idle-on pair
    is a controlled A/B proving both that the default is off and that the option is not inert.
  - **`bench/verify-bind-address.sh`** (Linux) / **`bench/Verify-BindAddress.ps1`** (Windows), added
    2026-08-04 — that
    `Listen(IPEndPoint)` binds the address it was GIVEN. Every native backend used to hard-code
    INADDR_ANY, and nothing could see it: the smoke matrix binds `IPAddress.Any` and connects over
    loopback, which an Any-bound listener answers either way. The assertion is read out of `ss` /
    `Get-NetTCPConnection` by pid, not printed by the process, and the discriminating cell is the CONTROL
    (asking for 0.0.0.0 must still give 0.0.0.0) — without it, the opposite hard-coding would read as
    correct.
  - **`bench/Verify-BindReachability.ps1`** (Windows, added 2026-08-04; **no Linux equivalent yet**) —
    the NETWORK half of that same question, and until now the one check nothing automated. The rig above
    asks the kernel what address the socket carries; this asks whether anyone else can reach it. No second
    machine: connecting to this box's own LAN address still carries that address as the destination, so a
    127.0.0.1-bound socket does not match it. Its control is load-bearing — bound to `0.0.0.0` the LAN
    address MUST answer, or a host firewall would make every backend "pass" while proving nothing, so that
    case reports INCONCLUSIVE rather than green. `-SimulateBug` reproduces the INADDR_ANY bug at the same
    observation point with no library edit, and is how the gate was shown to fail before it was believed.
