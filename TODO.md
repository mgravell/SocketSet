# TODO

Engineering backlog — design calls and deferred work. Not user-facing (see `README.md` for that).

---

## SECURITY AUDIT 2026-08-04 (READ FIRST NEXT SESSION — full findings in `REVIEW.md`)

Marc asked for a full security audit of the codebase. **The findings, the reasoning and the gate status
all live in [`REVIEW.md`](REVIEW.md)**, which is new and is to security/correctness reviews what
`RESULTS.md` is to measurements. Do not re-derive any of it here; this section is only the backlog that
came out of it.

One-paragraph summary so you know whether to go read it: the unsafe code and the slot-lifetime
discipline came through clean (no wire-reachable memory-corruption bug found). The exposure was in
**defaults that fail open**, **absent liveness limits**, and **buffer reuse surfaced through the public
API**. Eight items were fixed this session; five are design calls left open, below.

**Fixed and gated (details in `REVIEW.md` F1-F8):** `Listen(IPEndPoint)` ignoring its address and
binding INADDR_ANY on all four native backends; IPv6 endpoints silently connecting to a truncated IPv4
address; an application-callback exception killing an entire shard (4096 connections plus 1/N of
capacity, permanently); `SSL_set1_host` unchecked on the kTLS path; `sockaddr_un` built per-UTF-16-char
and unbounded; unchecked `Advance` reading past a pool page onto the wire; unguarded free-list returns;
`HalfPipeWriter` draining a released `CycleBuffer`.

**New gate:** `bench/verify-bind-address.sh` (+ SmokeTest `--bind-probe`). Nothing could previously
distinguish a listener that honoured its bind address from one that ignored it, which is why that bug
survived; the smoke matrix binds `IPAddress.Any` and so is blind to it by construction. Wants a Windows
equivalent.

### The open items, in priority order (full reasoning in `REVIEW.md` D1-D6)

1. ~~**`TlsClientOptions.TargetHost` is per-ENGINE but hostname verification needs it per-CONNECTION.**~~
   **RESOLVED 2026-08-04** via Marc's `OnClientAuthenticate`/`OnServerAuthenticate` callbacks: the engine
   is ASKED per connection rather than carrying config through `Connect`, which is the only shape that
   works for a tunnel funnelling many endpoints through one engine. Host is now mandatory, `"*"` is the
   explicit opt-out (and, like the BCL, sends no SNI). Gated by `bench/verify-tlsname`. Full writeup,
   including a confident claim of mine that measurement falsified, in `REVIEW.md` D1.

   **Follow-ups it leaves:**
   - **Split announce from verify** (Marc): `"*"` currently means both "no SNI" and "no name check",
     so "do not tell the server who I expect, but DO check what comes back" is inexpressible. The
     machinery exists (SChannel already carries `sniName`/`verifyName` separately; the IP path already
     announces nothing while verifying) — it is a second field, not new plumbing.
   - **Remove the now-redundant per-connect/per-listen `TlsProvider?` parameters** on
     `Listen`/`Connect`/`ConnectShard`/`ListenHandle` and the `Connection.TlsOverride` they feed. The
     callbacks subsume them and nothing outside the library passes one; leaving both means two
     mechanisms with precedence rules between them. Kept for now only so the callback change could land
     without touching every signature at once.
   - **SNI-based server certificate selection** is still open, and is NOT solved by the callback: SNI
     and ALPN both ride in the ClientHello, and `OnServerAuthenticate` fires before the receive is even
     armed. What DID land is the read-only half: `Connection.RequestedServerName` surfaces the name the
     client asked for, from OnAccept onwards (OpenSSL only; SChannel returns null, see below).
     Three routes to the selection half, in increasing order of ambition:
     - **(a) OpenSSL native hook.** `SSL_CTX_set_client_hello_cb` (3.x; sees the raw ClientHello, can
       read any extension, and can swap the whole context with `SSL_set_SSL_CTX`), or the older
       `SSL_CTX_set_tlsext_servername_callback`. Precedent exists and is proven: `SelectAlpn` is already
       an `UnmanagedCallersOnly` cdecl callback invoked from inside `SSL_do_handshake`, so the shape and
       its risks are known. OpenSSL-only, which breaks the provider parity deliberately established in
       bd3c31b.
     - **(b) SChannel.** No server-side SNI hook exists. .NET's own `SslStream` solves this by PARSING
       the ClientHello in managed code before handing bytes to SChannel. We are well placed to copy that:
       `SChannelTlsFilter` already buffers the first flight in `_carry` before the first
       `AcceptSecurityContext`.
     - **(c) Peek in the shard, uniform across providers.** Parse SNI out of the first ClientHello
       ourselves, THEN call `OnServerAuthenticate` with it populated. This is the shape that makes
       Marc's original question ("can the server context surface the instructed SNI?") answer YES, and
       it gives one answer on both providers and all five backends. Costs: the callback's timing
       contract changes from "at accept" to "on first bytes"; the first flight must be buffered before a
       provider is chosen; and it needs our own ClientHello parser, which is ATTACKER-FACING input from
       an unauthenticated peer and therefore wants fuzzing, not just care.
     Recommendation if this is wanted: (c), with (a) as the fallback if uniformity turns out not to
     matter. (b) is really a component of (c) rather than an alternative to it.

   ORIGINAL NOTE (kept for context):
   **`TlsClientOptions.TargetHost` is per-ENGINE but hostname verification needs it per-CONNECTION.**
   Null host means NO name check on either provider, and it defaults to null, so the posture is "any
   certificate from a trusted CA, for any name". The tunnel funnels many endpoints through one engine
   by design (2026-08-03's anchor shape), so it structurally cannot set this today, and both TLS client
   rigs currently depend on the fail-open behaviour. There is an IP-literal trap in the obvious fix:
   `SSL_set1_host("127.0.0.1")` does not match `iPAddress` SANs, so a naive change fails against our own
   demo certificate. Proposed shape is in `REVIEW.md`; it is public API surface, so it wants deciding
   alongside the freeze proposal further down.
2. **No handshake or idle timeout anywhere.** A peer that connects and sends nothing holds its slot
   forever. This is a regression against what we replace on the ASP.NET bridge specifically, because TLS
   terminates below Kestrel and its `HandshakeTimeout`/`MaxConcurrentConnections` never see the
   connection. Wants a cost measurement on the sweep before it goes in the hot loop.
3. **Backpressure is advisory in both bridges, so inbound is unbounded.** Same fix as the recorded
   receive-PARKING item (7) — worth re-tagging that item as a robustness fix, not only an architecture
   one, because right now it is the DoS. Note `SocketSet.AspNetCore` is a published package.
4. ~~**`ReceiveContext.RawBuffer` past `PayloadBytes` is another connection's data**~~ **RESOLVED
   2026-08-04, same day** (Marc's design, two refinements): a lazy tail wipe with two triggers, plus
   `GetWriteSpan(int sizeHint)` so the cost tracks the REPLY size rather than the buffer size —
   receive 20, reply 25, and 5 bytes are cleared. The vector worth remembering is the one Marc caught in
   my first cut: `ResponseBytes` above `PayloadBytes` without ever touching `RawBuffer` bypasses a
   wipe-on-first-access entirely, and is the more likely accident. Gated by `bench/verify-tailwipe`.
   Full writeup and the measurement in `REVIEW.md` D4.
5. ~~**`SocketSet.snk` is a full RSA private key, committed**~~ **ACCEPTED RISK** (Marc, 2026-08-04:
   "we're OK with this"). Recorded as a decision so the next audit does not re-raise it.

5b. ~~**D6 smaller items**~~ **DONE 2026-08-04**, with options where the behaviour was policy:
   `SocketSetOptions.ReusePort`, `SocketSetOptions.UnixSocketMode` (default 0600 -- measured: a UDS with
   no chmod gets 0775 on this box), buffer-id masking + an `IORING_CQE_F_BUFFER` guard, TLS faults
   visible in Release, settable revocation mode, and used-portion clears on the pooled buffers that
   carry plaintext. Details in `REVIEW.md` D6. STILL OPEN from it: `PrepareForBind`'s unconditional
   delete + TOCTOU, and no `SO_PEERCRED` peer check on UDS.

6. **MAKE THE DEFENSIVE WIPES OPT-OUT** (Marc, 2026-08-04). What shipped is fair as a DEFAULT — on by
   default is the right posture, and `GetWriteSpan` already makes the common cases free or
   proportional. But it should be electively turn-off-able for someone who controls the entire
   scenario: a closed system where every handler is known to write exactly what it reports, and where
   the buffer is never shared with anything the operator does not own, is paying for a guarantee it
   does not need.

   Shape to decide:
   - **Granularity.** A `SocketSetOptions` flag is the obvious one, but note the wipe lives on the
     `ref struct` contexts, which do not currently see options — they get a pointer and a length from
     the backend. Threading a bool in costs a field per context (cheap) or a sentinel in the length
     (nasty). Per-connection would be finer but harder still; per-listener is probably the useful unit,
     matching how TLS providers are already scoped.
   - **It must be LOUD.** Same rule as the TLS `verifyServer` escape hatch: name it for what it gives
     up, and surface it in the `ToString()` banner / `/config` line so a rig can gate on it. A silent
     "wipes off" would be indistinguishable from the pre-2026-08-04 behaviour that the audit found, and
     that is exactly the class of silent degradation `AGENTS.md` says to make say so.
   - **The per-call `*Unwiped` accessors already exist** (`RawBufferUnwiped` / `SendBufferUnwiped`) and
     cover the "one hot handler knows what it is doing" case today. The global switch is for "the whole
     deployment is closed", which is a different and broader claim — worth keeping the two distinct
     rather than collapsing them.
   - **Gate it.** `bench/verify-tailwipe` currently asserts the wipes happen; with the option it needs a
     control cell asserting they DO NOT when it is off, or the flag is untested in the direction that
     matters (see the bind-address gate for why a control cell is not optional).

## SESSION CLOSE 2026-08-03 (READ FIRST NEXT SESSION — the day SE.Redis ran on SocketSet)

One very long day. The short version: **the multiplexer now runs on SocketSet end-to-end** (Tunnel →
transport mode → push-feed, no socket/Stream/reader thread), it was measured honestly (falsified first,
fixed, then won where it matters), and everything is pushed. The long version, in order:

1. **The SER009 seam is merged upstream**: SE.Redis #3152 (Tunnel.ConnectTransportAsync + the
   [Experimental] DuplexTransport/TransportReceiver contract — transport IS the IBufferWriter, GetSpan
   virtual, receiver a separate abstract class) and #3153 (BufferedStreamWriter unified into
   RESPite.Streams) are BOTH on SE.Redis main, merged by Marc. Branch rule stands: SE.Redis main is
   NEVER pushed directly; marc/* branches only.
2. **Transport mode** lives on SE.Redis `marc/transport-push-feed` (unmerged): PhysicalConnection grows
   a transport path — no socket/SslStream/reader thread; TransportWriter : BufferedStreamWriter keeps
   every _output call site; TransportFeed pushes into CycleBuffer+CommitAndParseFrames on the loop
   thread; config.Ssl + transport tunnel throws. The SIBLING CHECKOUT must sit on this branch for
   SocketSet.StackExchange.Redis to build.
3. **The anchor shape** (SocketSet main): SocketSetTunnel holds ONE SocketSetClientEngine; transports
   are thin per-connection routers (UserToken dispatch, touched-this-batch OnBatchEnd). Engine-per-
   connection survives only as a documented convenience overload.
4. **The A/B arc (root RESULTS.md, the whole story): falsified → fixed → reframed.** v1 lost ALL EIGHT
   cells (~7x at depth). Counters located it (SQEs≈ops, 99.8% queued-behind-inflight) → io_uring
   DrainNext now coalesces the queued-chain run into one writev (5ec2b65; smoke 60/60 first). Post-fix:
   SET-depth disjoint wins, and the investigation Marc asked for ("why isn't it better") found: (a)
   BOTH legs sit at ~46% of the wire ceiling — SE.Redis machinery above the seam is the cap; (b) p999
   is 3-12x better on the tunnel in every single-mux run — the client-seat headline; (c) the 32B cell
   was the LEAST favourable point: +22-28% at 512B, +77-82% at 4KB (receive copy EXONERATED, into-
   caller-buffer demoted); (d) aggregate +20-27% at m=8 when MUXAB_SHARDS matches the core budget
   (shards=1 loses — the anchor one-armed); (e) epoll null (no conveyor); (f) theft audit closed
   (RunContinuationsAsynchronously default). Open: d1 +2.7µs and 32B-GET −5% — wake accounting next
   (eventfd wakes/op; doorbell coalescing the candidate fix).
5. **Also landed**: !@abstract in SE.Redis (`marc/uds-abstract-config`, tests + live pass, unmerged);
   alpha packages 0.1.196-alpha in ~/code/packages (Marc's nuget push pending); RESULTS.md moved to
   repo root; every table now states units; SocketSet.ToString diagnostic line (57ddaee); garnet
   discussion #2012 posted by Marc (0 replies yet; embedded-abstract gap PR offered).
6. **Marc-only pending**: nuget push; PR/merge calls on marc/transport-push-feed + marc/uds-abstract-
   config; redis PRs #15572/#15575 shepherding (8.12 bucket, quiet); #2012 watch.

## READ FIRST IF YOU ARE ON WINDOWS (written 2026-08-01, switching back from Linux)

> **THIS CATCH-UP IS DONE (2026-08-01 Windows session). Items 1-4 of the priority list below are all
> closed; what remains is item 5 (RIO page-quantization) and item 6 (the baseline re-measurement).** In one
> paragraph: the shared changes are correctness-clean on Windows (**48/48** smoke cells, including
> `rio+tls/verify-oob-4m` in 0.3s where it was a 15.2s FAILURE, and `rio+tls/churn` 5/5 clean with no sign
> of the item-0e access violation); the bridge got its first runtime gate, **`bench/Verify-AspNet.ps1`**
> (18 cells, backend x bridge-mode x TLS), which found **`--half-pipe` byte-exact on IOCP and RIO** and
> showed the **`SocketSet.AspNetCore` extraction to be behaviour-IDENTICAL to main on all 18 cells** — so
> it is verified and **MERGED**; and **SChannel reached TLS parity with OpenSSL** (1.3 floor by default +
> server-side renegotiation refusal), verified against controls by a new `bench/Verify-TlsFloor.ps1` and an
> `openssl s_client` A/B. **The TLS backlog is now empty on both providers.**
>
> **UPDATE (later 2026-08-01): the throughput half is done too, and it moved three conclusions.** A full
> 8-leg re-baseline (`Run-TlsSizes.ps1`, 6 scored passes, one session, zero errors) now supersedes the
> Windows tables in `RESULTS.md`. (a) Our **plaintext reached parity with Kestrel** at 512 B / 256 KB /
> 1 MB, losing only a disjoint −2.6% at 16 KB. (b) Our **TLS collapses at large payloads** — `iocp+tls` is
> LAST of eight legs at 256 KB and 1 MB. (c) An **http.sys baseline** was added (no elevation needed for
> plaintext; `bench/Enable-HttpSysTls.ps1` for TLS) and it **crosses over** rather than dominating: last at
> 512 B and 16 KB, first by 2x at 1 MB. The pre-registered "http.sys will kick us into orbit" prediction is
> therefore **half falsified**, and the half that held is on a workload (keep-alive, c64) that excludes
> accept — where a kernel stack should win most. Items 7 and 8 in the list below are what came out of it.
>
> **The gaps this session leaves:** `Verify-AspNet.ps1` has no Linux equivalent yet — the extraction is
> runtime-verified on IOCP/RIO/managed but not on io_uring/epoll, which is the first thing to do on the
> next Linux session. And accept cost is unmeasured on every stack (item 8).
>
> *The original cold-start plan follows, unchanged.*

Windows last ran **2026-07-29**; **shared code and the AspNet bridge changed underneath it** across two
Linux sessions (2026-07-31, 2026-08-01). Correctness first: run **`bench/Run-SmokeMatrix.ps1`** (48 cells,
IOCP/RIO/managed) before anything else — it is the gate, and it is how a shared-code change that broke a
Windows backend announces itself.

### What changed since Windows last ran

**Shared `src/SocketSet` (touches IOCP/RIO directly):**
- **Stale-completion detectors** added on IOCP and RIO (defensive — make the next lifetime bug announce
  itself). **Dynamic shard growth** (MinShards→MaxShards) for the single-listener path; capacity exhaustion
  is now visible instead of silently dropping connections. If you touch either, verify on IOCP/RIO.
- OpenSSL TLS gained a `MinProtocol` floor (defaults TLS 1.3) — Linux-only; **the SChannel provider has no
  equivalent floor/renegotiation parity yet — that is the open Windows TLS item.**
- Full detail: the Linux READ-FIRST below + `git log` since 2026-07-29.

**AspNet bridge — the 2026-08-01 session (AspNetDemo + a NEW library + `vendor/`, NOT `src/SocketSet`):**
- **Half-pipe (`--half-pipe`), MERGED to main.** A CycleBuffer-backed outbound `PipeWriter` that drains to
  `Connection.Send` on Kestrel's flush thread. Uses ONLY cross-platform `Connection.Send`, so it SHOULD
  work on IOCP/RIO — but is UNTESTED there. Off by default, so it cannot affect the default IOCP/RIO paths.
- **`SocketSet.AspNetCore` library extraction — branch `package-aspnetcore-lib` (pushed, NOT merged).** The
  reusable bridge is now a real library (`builder.UseSocketSet(...)`); the demo just maps flags. Builds 0/0
  but was never RUNTIME-verified (the Linux box stopped starting servers mid-session — an environment
  failure, not a code one). A clean Windows run validates the extraction cross-platform AND is the green
  light to merge it to main.
- Also unmerged, non-blocking: `halfpipe-followups` (an `SS_HALF_DRAIN=pool` p99 experiment, built but
  unverified) and the dotnet/aspnetcore#68148 spike on the user's `mgravell/aspnetcore` fork.

### BRANCH DISPOSITION as of 2026-08-01 (audited on Windows; do not re-derive this)

| branch | state | action |
|---|---|---|
| `package-aspnetcore-lib` | runtime-verified 18/18, **MERGED to main** | delete when convenient |
| `cyclebuffer-halfpipe` | **fully superseded by main.** Tree-compared: the ONLY files it has that main lacks are `AspNetDemo/{HalfPipeWriter,PinnedBlockMemoryPool,SocketSetConnection,SocketSetTransport}.cs` — the OLD paths of files the extraction moved to `src/SocketSet.AspNetCore/`. Its vendored `CycleBuffer`, the `KestrelPipeWriterRepro` experiment and the half-pipe itself are all on main already | nothing to land; delete when convenient |
| `halfpipe-followups` | **partially landed.** `bench/run-halfpipe.sh`'s `TLS=ssl` knob cherry-picked to main. The `SS_HALF_DRAIN=pool` experiment was NOT taken | see below |
| `rio-scatter-gather` | 0 commits ahead of main (its finding is recorded in main) | delete when convenient |

**The one genuinely outstanding piece is `SS_HALF_DRAIN=pool` on `halfpipe-followups`, and it needs
TWO things, not one.** (1) It is BUILT, UNVERIFIED by its own commit message, and its claim is a p99
effect from moving `Send` off the Kestrel request thread — that is a Linux throughput measurement, so it
cannot be settled on the Windows box. (2) **It edits `AspNetDemo/HalfPipeWriter.cs`, which no longer
exists at that path** — the extraction moved it to `src/SocketSet.AspNetCore/HalfPipeWriter.cs`, so the
branch will conflict and needs a port before it can even be measured. Do the port on Linux, then measure,
then decide. Do not merge it as-is.

*Branch deletions are left to the repo owner — nothing above is deleted, only assessed.*

### Prime Windows opportunities (priority order)
1. ~~**`Run-SmokeMatrix.ps1`** — correctness gate for the shared-code changes. First, always.~~ **DONE
   2026-08-01: 48/48 PASS.**
2. ~~**Runtime-verify `package-aspnetcore-lib` on IOCP/RIO**~~ **DONE 2026-08-01 and MERGED.** Verified with
   a new rig (`bench/Verify-AspNet.ps1`) rather than by hand, and the rig was run on main FIRST so the
   extraction could be shown behaviour-IDENTICAL (18/18, zero cell differences) rather than merely working.
3. ~~**Byte-exact `--half-pipe` on IOCP and RIO**~~ **DONE 2026-08-01 — it is byte-exact on both,
   plaintext and TLS, 1B-8MB.** The "uses only cross-platform `Connection.Send`, so it SHOULD work" claim
   is now measured. Correctness only: its throughput crossover is still Linux-only data.
4. ~~**SChannel min-protocol / renegotiation parity**~~ **DONE 2026-08-01 — see the TLS section below.**
   The TLS backlog is now empty on both providers.
5. **RIO send page-quantization (item 0 below, NOT fixed)** — IOCP is fixed, RIO isn't; the standing RIO
   perf item. Re-measured 2026-08-01 and still real: `rio/s12` trails `iocp/s12` disjointly by **−33% at
   256 KB and −47% at 1 MB** on plaintext. **No longer the TOP item — see the new item 7.**
6. ~~Re-measure the Windows baseline~~ **DONE 2026-08-01, then RE-DONE the same evening after the flush
   fix invalidated it.** The CURRENT table is at the top of `RESULTS.md`; the morning one is marked
   superseded. Headline now:
   - **We BEAT vanilla Kestrel on TLS at 16 KB on both backends, disjoint** (`iocp+tls` +6.6%,
     `rio+tls` +13.7%) — the morning run had this as overlapping.
   - The large-payload TLS deficit **halved at 256 KB and went +100% → +19% at 1 MB**. Still a real
     structural loss (encrypted bytes cannot use zero-copy send), no longer a collapse.
   - **Plaintext is at parity with Kestrel** at 256 KB and 1 MB; Kestrel keeps a disjoint ~2.6% at 16 KB
     and ~3% at 512 B — and item 9 shows that residue is the thread hops.
   - **http.sys CROSSES OVER** rather than dominating: last at 512 B (−18%) and 16 KB (−30%), first at
     256 KB and 1 MB (+112%). Unchanged by our work, and it reproduced across both sessions.
   - Method note worth keeping: the four control legs reproduced across the two sessions to within
     **1.6%** on all 12 cells, which is what made a morning-vs-evening comparison legitimate at all.
7. ~~**`rio+tls` beats `iocp+tls` at every size ≥16 KB**~~ **RESOLVED 2026-08-01 (evening): it was mostly
   the flush bug, and three of its four data points have evaporated.** Re-measured on current `main`, one
   session, 6 scored passes:

   | payload | `rio+tls` | `iocp+tls` | verdict |
   |---|---:|---:|---|
   | 16 KB | 3541-3568 | 3314-3385 | RIO still ahead, **+6.6% disjoint** |
   | 256 KB | 4707-5811 | 4407-4753 | *overlapping* — **gap GONE** (was +38%) |
   | 1 MB | 2988-3374 | 3686-3744 | **INVERTED — IOCP ahead +14% disjoint** (was RIO +17%) |

   The anomaly was largest where the `PooledBufferWriter` re-growth cost most, so most of it was that bug.
   **What survives is a 6.6% RIO lead at 16 KB only** — no longer the top Windows item, and no longer
   worth a profiling session on its own. The page hypothesis remains falsified either way. REPLICATED in a second independent session (+8% / +38%
   / +17% at 16 KB / 256 KB / 1 MB, all disjoint), while RIO trails IOCP badly on plaintext at the same
   sizes. So it is a real property of the two TLS send paths.

   ~~Pre-registered hypothesis: it is the page size, via the geometry sentinel.~~ **WRONG, and a bigger
   page is not even a workaround.** `--iocp --tls --page 65536`, banner-gated so the flag is confirmed
   TAKEN: 256 KB moved 3,774 → 4,023 (disjoint by **1.9 MiB/s**, i.e. a rounding error dressed as a
   result) against a pre-registered bar of "toward RIO's ~5,100+" — it closes ~17% of the gap. 16 KB
   overlaps. **1 MB is a disjoint −9.5% REGRESSION.** Full table in `RESULTS.md`.

   **The null result is trustworthy because the controls worked:** the plaintext companion (`iocp-p64k`)
   overlapped at all four sizes exactly as pre-registered (zero-copy send bypasses the page path), so the
   flag is not inert everywhere; and the page was verified to be the ONLY variable — `SmokeTest`'s
   `--page` rescales three pool depths, but `AspNetDemo`'s does not (`/config` geometry reads
   `writebufs=1024 oobwritebufs=256 readpages=256` at both page sizes). **Do not carry the SmokeTest
   pool-rescaling caveat across to the demo; they differ.**

   ~~**NEXT STEP — instrument, do not hypothesise again.**~~ **DONE 2026-08-01, and it makes the
   falsification MECHANICAL rather than statistical.** `SS_IOCP_STATS=1` / `SS_RIO_STATS=1` under identical
   256 KB TLS load:

   | | WSABUFs/response | WSASends/response | rps |
   |---|---:|---:|---:|
   | `iocp+tls` page 4096 | 65.0 | 2.0 | 12,115 |
   | `iocp+tls` page 65536 | 5.0 | 1.0 | 13,013 |

   The page flag did exactly what it was designed to do — **13x fewer buffers, half the send syscalls — and
   it bought ~7%.** A hypothesis whose mechanism works perfectly and delivers nothing is dead. And RIO
   issues **5.0 RIOSends per response against IOCP's 1.0 while being ~22% faster**, so send-call count is
   not the constraint in either direction. **Neither buffer count nor syscall count is the bottleneck;
   something per-BYTE is.**

   **Settled outright:** `declined: tls=96,906` with `zero-copy sends=0` — the IOCP TLS path declines
   zero-copy on **100%** of responses, by design (the record layer must produce ciphertext, so there is no
   caller buffer to send from). Measured now, not inferred. It is also why plaintext and TLS diverge so
   sharply: plaintext IOCP wins by NOT copying, and TLS cannot take that option.

   **NEW HYPOTHESIS, written down with its test rather than acted on** (the last one was plausible,
   mechanical and wrong): both backends copy exactly once, so the difference is what the send call does
   with the bytes — `WSASend` probes and page-locks user buffers on EVERY call, while RIO sends from
   buffers registered once via `RIORegisterBuffer`. That predicts a cost that is per-byte-locked:
   insensitive to buffer count (matches 65→5), insensitive to syscall count (matches RIO's 5 vs 1), and
   growing with payload (matches 16 KB → 256 KB → 1 MB).

8. **THE ACTUAL FIX THIS IMPLIES, and it is a design item rather than a knob: make TLS able to zero-copy
   on IOCP.** Today the TLS record layer frames ciphertext into its own accumulator, which is why
   `TrySendZeroCopy` declines 100% of TLS responses. If SChannel framed ciphertext DIRECTLY into the
   pinned send buffers (`EncryptMessage` already writes header/data/trailer into one caller-supplied span —
   see `SChannelTlsFilter`'s "outbound is zero-copy framing" note), the existing IOCP zero-copy path would
   become reachable for TLS, converting a 100% decline into the same fast path plaintext already uses.
   That is the highest-value Windows TLS work, and it plausibly also explains part of the Linux
   large-payload TLS loss, since the same structural shape appears there.
9. **THE READ-SIDE THREAD HOP — half of the "thread hops" term has never been measured, on any OS
   (raised 2026-08-01).** `SS_PIPE_SCHED=inline` has existed for a while and reads as though the hop
   question were settled (Linux io_uring: **−28%**). It is not: that knob only ever moved the **OUTBOUND**
   reader, the SocketSet pump. The **INBOUND** reader — the one that resumes *Kestrel's request pipeline*
   when data arrives — was hard-wired to `PipeScheduler.ThreadPool`. So every "thread hop" number on file
   is about the WRITE side, and the read side is untouched ground.

   Now testable: `SS_PIPE_SCHED=inline-read` / `inline-both`, reported in `/config` as `pipesched=<mode>`
   so a rig can gate on it. `bench/Run-PipeSched.ps1` is the interleaved A/B (same binary both sides,
   modes reshuffled per pass, banner-gated, ranges not medians).

   **It is an EXPERIMENT, not a candidate default, and that is the point.** An inline inbound reader runs
   Kestrel's whole request pipeline on the transport's loop thread, blocking that loop for every backend
   that owns one (all but managed) — Kestrel runs its own IO queues for exactly this reason. So a win is
   not shippable as-is. Its value is that it **UPPER-BOUNDS what removing the read hop could ever be
   worth**, which is precisely what decides whether an **inbound half-pipe** (a real fix: the loop drains
   on its own timeline, no pipeline on the loop thread) is worth building. **A null result deprioritises
   the read half-pipe outright**, which is just as useful an answer.

   *Pre-registered:* the read hop is a per-REQUEST cost, not per-byte — one resumption per request
   whatever the body size. So the gain should be largest at SMALL payloads (highest request rate =
   highest hop rate) and fade toward nothing at 1 MB. **If the gain instead GROWS with payload, the
   mechanism is not the hop** and the rig has found something else. Small payloads are also exactly where
   vanilla Kestrel still beats us (Windows 16 KB −2.6% disjoint), which is why that is where to look.

   **MEASURED 2026-08-01, THE PREDICTION HELD, AND IT IS THE BIGGEST FINDING OF THE SESSION.** With a
   vanilla-Kestrel control in the same passes:

   | payload | default vs Kestrel | inline-both vs Kestrel |
   |---|---|---|
   | 512 B | **−2.8%**, disjoint | *overlapping* — **parity** |
   | 4 KB | **−3.2%**, disjoint | *overlapping* — **parity** |
   | 16 KB | **−2.9%**, disjoint | **+1.7%, DISJOINT — ahead** |
   | 256 KB | *(every mode overlaps — no effect)* | |

   **The ~3% disjoint small-payload deficit to Kestrel IS the thread hops, essentially in full** — the
   first mechanism ever found for it; copies, pool pinning and segment counts were each measured and did
   not explain it. And the falsifier did not fire: disjoint at 512 B/4 KB/16 KB, nothing at 256 KB, i.e.
   per-request exactly as predicted.

   **CONSEQUENCE — the inbound half-pipe is now justified by a number instead of an argument, and this
   should be the next substantial piece of work on the ASP.NET path.** Calibrate it honestly though:
   the ceiling this measurement supports is ~2-4% at small payloads, and a real half-pipe will likely
   capture somewhat less than `Inline` does (Inline also skips work a correct implementation must still
   do). It is not a step change; it is the difference between losing to Kestrel by 3% and matching or
   beating it. The outbound half is already built and merged (off by default); this is the other half of
   the "two half-pipes" proposal, and its case is now measured.

10. **Accept-cost rig (http.sys is untested where it should win).** The 2026-08-01 table is **keep-alive
   only at c64**, so connection accept — the thing a kernel-mode HTTP stack should win most decisively —
   is entirely out of scope, and NO claim about accept cost in either direction is currently supported.
   **The obstacle is TIME_WAIT, and it is why this is a rig rather than a flag:** `bench/README.md`'s
   ephemeral-port gate exists because omitting it once manufactured a fake "208 dropped connections"
   defect, and a `Connection: close` sweep would measure port exhaustion rather than accept.
   Three options, assessed 2026-08-01:
   - *Widen the budget* — `netsh int ipv4 set dynamicport tcp start=10000 num=55000` plus
     `TcpTimedWaitDelay=30`. Machine-wide, needs elevation, buys ~1,800 conn/s. Moves the ceiling; does
     not remove the problem.
   - **RST-close the client (`SO_LINGER` 0) — recommended.** Sidesteps TIME_WAIT entirely rather than
     budgeting around it, and the repo already proves the pattern: `SmokeTest --churn --reset-close` is
     exactly this, which is how the churn cells do thousands of connections in 10s with no port gate.
     bombardier cannot do it, so this needs a small purpose-built HTTP client.
   - *Change the measurement shape* — a bounded burst (open N, time to first byte on all N, cool down,
     repeat) reporting **conn/s and accept p99** rather than MiB/s. Stays under any port budget by
     construction, and conn/s is the number that actually separates a kernel stack from a user-mode one.
   Recommended: the last two together. **Constraint to respect:** http.sys and Kestrel would be comparable
   to each other, but neither to `SmokeTest --churn`, which accepts without HTTP. Same layer or nothing.

---

## SESSION CLOSE 2026-08-02 (READ FIRST NEXT SESSION — one very long Linux day, everything below is current)

**Where everything landed, in one block.** Four consumers measured, all same-day where new:
- **RESP proxy** (SE.Redis `marc/proxy-socketset`): Envoy PARITY at `-P 1`, **2.7x Envoy at `-P 16`**
  (L3 shard-affine + callback-granularity flushing); UDS sidecar hop **+30% / ~half the tail** vs TCP;
  abstract==pathname free. Tail bisected: GC exonerated, shard-oversubscription was rig config, the
  remainder is an SMT trade (shards ≤ physical cores → Envoy-level p99 at −22% throughput).
- **Client shape** (`bench/run-client-shape.sh`): one loop thread carries ~1.15M ops/s per connection at
  47-103µs p99 THROUGH a full proxy hop; extrapolates ~2M+ for real client mode.
- **Garnet** (`src/SocketSet.Garnet` + `GarnetDemo`, PackageReference, no fork): plaintext
  parity-with-better-tails (one +7.7% disjoint win, p99 lower all cells); **TLS A/B all four cells
  DISJOINT ahead, +9.5% to +24.2%, p99 halved-to-thirded — TLS costs ours ~17% off plaintext vs stock's
  ~27%.** Gates 13/13 both legs.
- **redis fork** (`mgravell/redis`): `marc/uds-abstract-sockets` (hiredis @abstract for cli+benchmark) and
  `marc/uds-abstract-server` (server-side @abstract, `unit/networking` green incl. two new tests) — the
  server PR is DRAFT at Marc's choice; client-side PR not yet opened.

**Tooling debts paid:** confounder ledger at #14 (all structurally fixed in rigs); `verify-proxy.cs` is
13 cells and RESP3-literate; TLS-capable `redis-benchmark` exists (fork + `~/.local/openssl`, note the
lib→lib64 symlink); shared TLS certs in `bench/.tools/tls-demo`.

**Next, by who:** Marc — un-draft/open the redis PRs, lab note, API-freeze sign-off. Windows session —
smoke matrix + first IOCP run of the affine design (doc-comment fixed: IOCP loop threads DO support it).
Design steer needed — SE.Redis client-mode integration point (`PhysicalConnection` vs RESPite layer).
Parked with baselines: scanner tune, Envoy-over-UDS leg, `TlsMode` build, Envoy-SMT profiling,
per-client RESP3, receive-copy removal in SocketSet.Garnet.

## DIRECTION CHANGE (2026-08-02): the consumers are RESP, not ASP.NET — and one of them is SE.Redis itself

**Read this before planning work.** It reorders the backlog more than any measurement in this file, and it
reverses one conclusion reached earlier the same day.

**The context that decides priority:** Marc is *paid* for Redis work; he previously worked on ASP.NET
Core and does not any more. So "can we beat Kestrel" is unfunded, competes with a team he has left,
and is structurally confounded anyway — the Kestrel bridge costs 24-40%, so every bridged number measures
the bridge as much as the transport, and the "control" leg is a different APPLICATION path rather than
just a different transport.

**Two real consumers now exist, and neither is ASP.NET:**

1. **`RESPite.Proxy`** — an Envoy-style RESP proxy on `StackExchange/StackExchange.Redis`, branch
   `marc/proxy-spike2` (newest of `marc/proxy*`), under `toys/`. It already went Kestrel → hand-rolled
   `WorkerPool`/SAEA, which is precisely the work SocketSet exists to make unnecessary. It exposes
   `RunClientAsync(IDuplexPipe)` — the seam SocketSet plugs into — and `ProxyClient` is untouched by the
   swap, so the transport is the only variable. **That is the comparison AspNetDemo structurally cannot
   make.** The real target is beating **Envoy** running adjacent, with `redis-benchmark` driving
   {direct | Envoy | our proxy} against one backend. See `RESULTS.md` for first numbers.

2. **SE.Redis itself, as its IO core, in ordinary CLIENT mode.** This is not academic and it changes the
   engineering constraints more than the proxy does:

   - **IT PUTS WINDOWS BACK TO FIRST-CLASS, reversing the "Windows is an instrument, not a product"
     call made earlier today.** That call rested on nobody hosting web servers on Windows. SE.Redis is a
     CLIENT library with an enormous Windows install base (dev machines, Windows services, App Service).
     IOCP quality becomes a production concern again, and **item 8 (TLS zero-copy on IOCP) is relevant
     again** — SE.Redis clients do TLS to Azure Redis constantly.
   - **The managed backend and net472 stop being ignorable.** The managed path is the fallback wherever
     io_uring/IOCP specialisation is unavailable, and SE.Redis supports down-level targets. It appears in
     NO throughput table here, and `ReceiveBufferSize` does not even reach it (see "Known gaps"). Fine
     for a proxy; not fine for a client core.
   - **THE CLIENT REGIME IS NOT THE ONE ANYTHING HERE MEASURES.** Every rig is many-connections,
     server-side-accept. SE.Redis is ~1-2 connections per endpoint, multiplexed, deeply pipelined, with
     TAIL LATENCY as the product because an app blocks on the call. A 12-shard design spreads many
     connections over loops; with ONE connection the question is "what does a single loop cost", which is
     unmeasured. **A single-connection, deep-pipeline, p99-scored rig is the missing measurement.**
   - **Thread-theft returns, relocated.** The proxy escapes Kestrel's constraint because the handler is
     bounded and non-blocking. In client mode that is only half true: completing a `TaskCompletionSource`
     can run arbitrary USER continuations, and doing that on the loop thread reinvents exactly the problem
     Kestrel's IO queues exist to prevent. The 2026-08-02 `inline-both` result — both readers on the loop,
     consistently WORSE than inlining neither, on both Linux backends — is the live warning.
   - **API STABILITY BECOMES A CONSTRAINT.** `AGENTS.md` says public API and defaults can change freely
     because there are no users. That stops being true the moment SE.Redis depends on this. The
     `Connection` / `AcceptContext` / `UsePipe` / `SocketSetOptions` surface should be settled BEFORE that
     dependency is taken, not after. Note the single-writer-until-Flush contract on the `IBufferWriter`
     surface is a real interface requirement for a multiplexed client, not an implementation detail —
     SE.Redis can satisfy it (it serialises through its own backlog) but it must be designed against.

**What this demotes:** the inbound half-pipe (aspnetcore-specific engineering to recover something the
proxy gets by construction, and now measured at ~4% on io_uring ONLY — see the pipesched section);
"beat Kestrel" as a scoreboard; and RIO performance work. **What it does NOT demote:** the Windows
CORRECTNESS gates, which are now more important, not less.

## THE RESP PROXY WORK — STATE, AND THE LEVEL-2 CLIENT (2026-08-02; READ IF THE SESSION DIED)

**Where it lives.** `StackExchange/StackExchange.Redis`, branch **`marc/proxy-socketset`** (pushed),
based on `marc/proxy-spike2`. Three files under `toys/RESPite.Proxy/`: `SocketSetProxyServer.cs` (new),
`Program.cs` (transport switch + banner), `RESPite.Proxy.csproj` (conditional ProjectReference to a
SIBLING SocketSet checkout at `../../../SocketSet`, guarded by `Exists()` so CI without it still builds).
Nothing outside `toys/` is touched; the full solution builds.

**Rigs in THIS repo:** `bench/verify-proxy.cs` (correctness gate, `dotnet run --file` — plain
`dotnet run <file>` from inside another repo resolves THAT repo's project and fails confusingly),
`bench/run-proxy-ab.sh` (the A/B, incl. Envoy), `bench/envoy-redis.yaml`. Tools fetched into
`bench/.tools/`: `redis-benchmark` (built from source, no sudo), `envoy` (static binary, no sudo).
Backend is `garnet-server` (a dotnet tool, multi-threaded — chosen because single-threaded Redis would
saturate one core and make every proxy leg report the same number).

**What is measured (full tables in `RESULTS.md`):** vs the hand-rolled SAEA `WorkerPool` we
are +15-28% at both depths. **vs ENVOY we LOSE ~2x at `-P 1` and WIN ~1.5x at `-P 16`.** The
`% of ceiling` column localises it: Envoy is at 90% of the no-proxy ceiling unpipelined (we are at 46%),
and drops to 23% pipelined (we reach 35%). **Our per-request overhead is poor; our parse throughput is
good.** The SAEA path shows the same shape, so both halves belong to the .NET path, not to SocketSet.

### LEVEL 2 — the next piece of work, and it is aimed at a measured defect

**Definition.** Level 1 (built) bridges SocketSet through `PipeIoBridge` into two `Pipe`s, into
`PipeProxyClient`, which reads via `pipeReader.AsStream()`. Per request that is: a pipe write, a
ThreadPool hop to wake the reader, a `Stream` wrapper, the reply into a second pipe, another hop, and a
pump task. **Level 2 replaces all of it**: frame with `RespReader` directly off the span
`OnReceive(ref ReceiveContext ctx)` hands you ON THE LOOP THREAD, and reply via `Connection.Send`. No
pipe, no pump, no hop, no stream. That is the shape SocketSet was designed for, and it is legal here —
unlike in Kestrel — because we own the handler and RESP framing is bounded, non-blocking work.

**Why it is not speculative:** it attacks precisely the per-request cost that `-P 1` isolates, which is
where we lose to Envoy. `level2 - level1` is also the pipe-bridge tax measured somewhere nobody can blame
Kestrel for it.

**The structure already exists to copy.** `SocketProxyClient` (the hand-rolled leg) is ALREADY a push
model — `WorkerSocketAsyncEventArgs` callbacks plus `ICycleBufferCallback` — rather than the pull model
`PipeProxyClient` uses. So level 2 is "drive that same push structure from `OnReceive` instead of from
SAEA completions", not a new framing engine.

**Four hazards, each with an existing guard:**
1. **Partial frames** — a command can span reads; bytes must carry over between callbacks. RESPite's
   `CycleBuffer` is built for exactly this. **Use RESPite's real one** (the proxy already references
   RESPite), NOT SocketSet's `vendor/` copy — that copy exists only so SocketSet's own half-pipe can work
   standalone, and it is currently byte-identical, which is worth keeping true.
2. **`ctx.Payload` is transport-owned** and valid only for the callback. Anything retained must be copied.
3. **Reply ordering.** Locally-answered commands (PING/SELECT/ECHO) must sequence BEHIND in-flight
   upstream replies or they land in the wrong slot. The proxy already fixed the general form of this
   (`dbd4ad4d`, "fix local responses being out-of-band"), and `verify-proxy.cs`'s **mixed-pipeline** cell
   is the standing guard. This is also why the transport's instant-reply path (`SendBuffer`/`SendBytes`)
   is only safe when the connection has NO queued work.
4. **Never block the loop thread on upstream I/O.** Clients are round-robined onto 5 STICKY upstream legs,
   so one stall blocks that leg's whole cohort. Thread-theft is gone (we own the handler); self-inflicted
   head-of-line blocking is not. Expect it to show in p99 before throughput.

**Measurement plan when it exists:** re-run `bench/run-proxy-ab.sh` with a `socketset-l2` leg. **Get more
client capacity first** for the `-P 1` half — Envoy currently sits at 90% of a client-limited ceiling
there, so that gap is a FLOOR and we could otherwise "improve" into a ceiling we cannot see past and not
know whether we closed the gap or just hit the generator.

**Known gap, unrelated but found here:** the proxy does not accept INLINE commands (`redis-benchmark -t
ping` sends literal `PING\r\n`; `RespReader` rejects the `P`). Redis and Garnet accept them. Decision
taken: leave inline on the backlog, but a fault must FAIL LOUDLY rather than hang — that is now fixed.

### BACKLOG: TLS upstream legs — where kTLS might actually pay (raised by Marc 2026-08-02)

In most real deployments the proxy's UPSTREAM leg is TLS. **We support kTLS; Envoy terminates TLS in
userspace BoringSSL.** So there is a *possibility* (his word, and the right one — not a guarantee) of a
structural advantage on exactly the leg the affinity work just made hop-free. Split it by what each
environment can actually answer:

**Measurable on THIS box, today:** our in-transport OpenSSL upstream TLS vs Envoy's BoringSSL upstream
TLS — both userspace, so loopback is fair. Garnet does TLS, both proxies can originate it, and we already
beat `SslStream` broadly (+13-35%); vs BoringSSL is genuinely unknown. This is the groundwork for the
"TLS-originating RESP proxy" claim, and it needs no new hardware.

**PRE-REGISTERED for any single-box kTLS leg: expect kTLS to LOSE to our own userspace TLS here.** The
record path costs ~9-20% on this box at small messages (measured twice, both directions offloaded), and
loopback structurally cannot show `TlsTxDevice` — there is no NIC. A losing kTLS number on this box is
the KNOWN regime, not a finding, and must not be written up as "kTLS is bad". Gate the leg on
`/proc/net/tls_stat` movement as always.

**What needs a lab (external peers with appropriate hardware — see the handover-note idea):** whether `TlsTxDevice`
engages on a TLS-offload NIC, and what inline offload does to proxy CPU-per-byte at line rate. That is
where "Envoy pays AES per forwarded byte on its worker thread; our upstream leg sends at plaintext cost"
becomes testable. It is also the first scenario where the kTLS investment is visible AT ALL.

**Speculative but worth one line: bulk-payload splice.** A RESP proxy must parse frames, so full-stream
`splice()` is off the table — but large bulk-string BODIES are opaque bytes, and in principle could move
downstream-socket → kTLS-upstream-socket without entering userspace, kernel-encrypting on the way. No
userspace TLS stack can do that. Unbuilt, unmeasured, and the framing/streaming complexity is real — but
it is the kind of mechanism that makes the lab session worth scheduling.

**Integration scoping item — SCOPED 2026-08-02, and it is small:** TLS engages at exactly two sites per
backend (`opts.Tls is not null` at the accept path → `CreateServerFilter`, and at the connect path →
`CreateClientFilter`; see `IoUringShard.StartTls` and its callers at accept/connect). So the right knob is
**per-DIRECTION, not per-connection**: a `TlsMode { Accept, Connect, Both }` (default `Both`) on
`SocketSetOptions`, gating each site. That covers BOTH proxy shapes — TLS-terminating (TLS accepts +
plaintext connects) and TLS-originating (plaintext accepts + TLS connects) — with ~5 backends x 2 sites
of mechanical change and no behavioural change at the default. Not built; build it when the TLS upstream
A/B is next.

### ~~PROXY CORRECTNESS: HELLO must be intercepted~~ RETRACTED — it always was; item closed with -NOPROTO (2026-08-02)

**The "poisoning" was confounder #13** (a `timeout`+`head -c` harness that prints nothing for any reply
shorter than requested — see RESULTS), compounded by a dead backend in the original run. Re-measured with
a fixed harness: HELLO was ALWAYS intercepted locally (`-ERR unknown command`), and is now answered with
the protocol-correct **`-NOPROTO`** (matching Envoy 1.39 and RESP2-era Redis; clients downgrade
gracefully on it). Gate cell `hello-local-error` added to `verify-proxy.cs`; 13/13. What REMAINS open
here is only the differentiator: per-client RESP3 (answer `HELLO 3` locally, translate on the shared
leg), which nobody has built — Envoy 1.40's `protocol_version` is all-or-nothing per listener. AUTH is
still unimplemented and must likewise be per-client when built (forwarding it would authenticate the
shared leg). The original item text follows for the record.

### PROXY CORRECTNESS: HELLO must be intercepted, never forwarded (found 2026-08-02, empirically)

`HELLO 3` through the proxy is FORWARDED to the shared upstream leg, which then flips to RESP3 (or
desyncs) **for every multiplexed client on it** — measured: after one client sends `HELLO 3`, a plain
RESP2 SET/GET on a DIFFERENT connection gets no replies at all. Same class as the SELECT bug the proxy
already fixed (`dbd4ad4d`): per-client protocol state cannot ride a shared leg. Envoy 1.39.0's answer is to not
support HELLO at all (verified: it swallows the handshake, no reply). **Main-branch Envoy (1.40-track)
adds `RedisProxy.protocol_version` (RESP2 default / RESP3)** — verified absent from our 1.39.0 binary via
`--mode validate` — and its design is ALL-OR-NOTHING PER LISTENER: RESP3 mode demands downstream `HELLO 3`
before any data command (`-NOPROTO` otherwise) and negotiates `HELLO 3` on each upstream pool connection.
So the cheap correct v1 for us is Envoy's shape (one protocol per listener); PER-CLIENT protocol
adaptation on a multiplexed leg is the thing nobody has built and the real differentiator. When 1.40
ships, a RESP3-negotiated A/B (`redis-benchmark -3`) is possible — blocked on our fix first. Ours needs
to intercept HELLO like SELECT: answer locally (RESP2 downstream at minimum, translation later) and keep
the upstream leg's protocol fixed. `verify-proxy.cs` should gain a HELLO cell once the intended behaviour
is decided. Relevant context from Marc (whose day job is Redis client libraries): real clients DEFAULT to RESP3
now, for smart-handoff and client-side-caching — so "reject HELLO" is a real compatibility cost, and
handling it properly is a differentiator Envoy has declined to build.

### NEXT CODE WORK: callback-granularity flush in the level-2/3 clients (the −P 16 collapse)

The definitive run measured L2/L3 at depth for the first time: **they collapse** (L3 1.12M vs L1's 2.46M
at `-P 16` GET) while winning `-P 1` outright (Envoy parity). Mechanism: `SendRawSynchronized` does
`Connection.Send` — stage+flush — **per reply frame**, and at depth that is 16 flushes per receive
callback on the loop thread, where L1's pump coalesces. Fix: stage via `GetSpan`/`Advance` during
`Feed`, ONE `Flush` at end of the receive callback; same deferral for the upstream leg's `_outBuffer`.
Envoy batches at event-loop-iteration granularity for the same reason. **Pre-registered: restores most of
the depth loss without moving `-P 1`** (a `-P 1` callback holds one command either way). If `-P 1`
regresses, the batching is wrong; if depth does not recover, the collapse is not the flush count.

### GARNET RECEIVE-COPY: GREENLIT for exploration (Marc, 2026-08-03) — "may make it an easier sell"

The v1 extra receive copy (transport span → handler buffer) is worth removing: needs a
receive-into-caller-buffer shape on SocketSet. Explore alongside the Tunnel-shape design — the two want
the same primitive (letting the consumer own the receive target), so one design should serve both.

### GARNET AS THE FOURTH CONSUMER — BUILT, GATED 13/13 BOTH LEGS, AND MEASURED (2026-08-02, same day)

**TLS A/B DONE TOO (same day): in-transport OpenSSL beats Garnet's SslStream path in ALL FOUR cells,
disjoint — +9.5% to +24.2%, p99 halved-to-thirded; TLS costs ours ~17% off plaintext vs stock's ~27%.**
The HTTP-era TLS advantage transfers to small-record RESP. Full table in RESULTS.
**Done: `src/SocketSet.Garnet` + `GarnetDemo` (with `--stock` for the one-flag A/B). Result:
parity-to-ahead on day one — never behind on throughput, +7.7% DISJOINT on `-P 16` SET, and p99 LOWER in
every cell (depth-SET tail nearly halved) — despite a v1 extra receive copy.** Full table in RESULTS.
Remaining on this item: nothing blocking; known first lever if more is wanted is removing the receive
copy; `ActiveClusterSessions` yields nothing (RespServerSession is internal — needs an upstream accessor
if cluster mode is ever hosted). The original scoping follows.

### GARNET AS THE FOURTH CONSUMER (Marc's item, 2026-08-02): host Garnet on SocketSet

Garnet has a pluggable network server API and runs on managed SAEA sockets. Scoped against a checkout
(`~/code/garnet`): the seam is **`IGarnetServer` / `GarnetServerBase` + `NetworkHandler` (abstract) +
`INetworkSender`**, with `TcpNetworkHandlerBase` driving a `SocketAsyncEventArgs` receive loop
(`GarnetSaeaBuffer`) that PUSHES bytes into the RESP parser (`IMessageConsumer`) — i.e. the same push
shape as our `OnReceive → Feed`, and the same SAEA baseline the proxy beat by 15-28%. A
`SocketSetGarnetServer` maps naturally: `OnAccept` → session, `OnReceive` → the handler's receive push,
`INetworkSender` → `Connection` writer surface with CALLBACK-GRANULARITY flushing (the deferred-flush
lesson applies verbatim — Garnet replies per command too).

**Why this one is special among the consumers:**
- **It is a SERVER — the many-connections/accept shape SocketSet's shard design was originally built
  for**, before the client-mode work bent it the other way. The proxy tested both halves; this tests the
  original thesis at full scale.
- **Garnet is the `direct` ceiling in every proxy table from 2026-08-02.** Hosting it on SocketSet means
  raising our own rigs' reference line — the fourth application-held-constant A/B (proxy, client
  prototype, benchmark tool, now a full server), stock-SAEA-Garnet vs SocketSet-Garnet, same
  `redis-benchmark` methodology.
- **Garnet's TLS is stream-layer/userspace, so the server-side TLS story lands on their hottest path**:
  in-transport OpenSSL/SChannel (+13-35% over `SslStream`, our one broad structural win) and eventually
  kTLS. A TLS-enabled Garnet A/B is where that number stops being an HTTP-demo result.
- Windows matters to Garnet (Microsoft project) — IOCP/RIO become load-bearing, aligned with the
  client-core direction that already re-elevated them.

**Scope sketch (refined 2026-08-02 against the tree — do not trust interface names from memory; the
full set is `IGarnetServer`, `INetworkHandler`, `INetworkSender`, `IMessageConsumer`, `IServerHook`, and
`INetworkServer` does NOT exist):** the real seam is
**`IServerHook.TryCreateMessageConsumer(Span<byte> bytesReceived, INetworkSender, out IMessageConsumer)`**
— it takes the FIRST received bytes because Garnet sniffs the wire format from the opening packet. So:
`OnAccept` → hold; first `OnReceive` → `TryCreateMessageConsumer(payload, ourSender, out session)`;
subsequent `OnReceive` → push into the `IMessageConsumer` (the same push shape as `Feed`). Implement
`INetworkSender` over `Connection` (GetSpan/Advance/Flush, callback-granularity batching — the
deferred-flush lesson verbatim) and a `GarnetServerBase` subclass for lifecycle.
**PACKAGING (Marc's call, 2026-08-02): an IN-REPO `src/SocketSet.Garnet` library, the
`SocketSet.AspNetCore` pattern — NOT a spike branch in the garnet checkout.** Verified viable against
the tree: the embedding ctor is `GarnetServer(GarnetServerOptions, ILoggerFactory, IGarnetServer[]
servers = null, ...)`, documented "If none is provided, will use a GarnetServerTcp" — custom servers are
a first-class parameter, so this is a pure `PackageReference` to `Microsoft.Garnet` with no fork of
their repo. A small demo host (embedded Garnet + our transport) plays the role AspNetDemo plays for
Kestrel, and the same public interfaces (`IGarnetServer`/`INetworkSender`/`IServerHook`) are the entire
surface consumed — which also feeds the API-freeze list. **Their abstract
`NetworkHandler` is NOT needed and is deliberately bypassed** — it is receive-buffer plumbing plus
`SslStream` TLS, and SocketSet terminates TLS in-transport, handing the consumer plaintext. That bypass
IS the TLS experiment: a TLS-enabled Garnet A/B becomes purely their-TLS-vs-ours on identical server
logic. Gate with `verify-proxy.cs` pointed at it (it is a RESP server) plus Garnet's own tests; A/B via
`run-proxy-ab.sh`'s `direct` leg pointed at each build.
**Relationship note (corrected from an earlier over-cautious framing):** Marc has friends and long-time
peers in this project's community and likes helping them — so upstream contribution here is a POSITIVE
motivation, not a hazard to manage; any employment nuance is ordinary professional judgment and his to
exercise. Even short of code landing, the A/B DATA is itself a gift: "here is what your network layer
costs against a measured alternative, with the rig to reproduce it" is exactly what peers value.

### UDS / ABSTRACT-SOCKET BENCHMARKING (Marc's item, built 2026-08-02; push blocked on SAML)

**Premise corrected then built:** redis-benchmark already has `-s <socket>` (pathname UDS) — what was
missing is ABSTRACT sockets, because hiredis's `redisContextConnectUnix` strncpy's the path and a
leading NUL cannot ride that. Patched on a local redis clone, branch **`marc/uds-abstract-sockets`**
(`/home/marc/code/redis`): `@name` → abstract namespace (the socat/systemd convention), Linux-gated,
with the exact-addrlen subtlety documented (padding NULs become part of an abstract name). One hiredis
function, so **redis-cli gains it too**; both `-s` help texts updated. The proxy gained
`--listen-uds /path-or-@abstract` (banner `listen=`), which is the SIDECAR deployment shape.

**Verified with the stock binary as the control:** stock `-s @name` fails "No such file or directory";
patched connects and runs (abstract: 680k PING / 515k GET, p50 47µs through the affine proxy; pathname:
806k / 415k). **Teaser, properly un-measured:** the UDS hop's p50 looked meaningfully better than the
TCP-loopback hop — a real UDS-vs-TCP A/B (add a uds leg to `run-proxy-ab.sh`) is now unlocked and also
retires the ephemeral-port/TIME_WAIT confounder class for local benches.

**~~BLOCKED~~ PUSHED 2026-08-02: `mgravell/redis` existed already (Marc synced it to current), and the
branch is up — `marc/uds-abstract-sockets`, one PR-shaped commit.** Remaining: open the upstream PR when
Marc wants to make the pitch. Original note for the record:
**BLOCKED ON THE FORK EXISTING.** `gh repo fork redis/redis` is refused (the redis org is SAML-gated and
the fork API is gated by the SOURCE org) — and per Marc, org-level policy disables tokens, so SSO-
authorizing the token is NOT a route. **Fork old-school via the GitHub web UI** (his account carries the
org binding, but a web fork into the personal namespace sidesteps the API gate). Once
`mgravell/redis` exists: `cd ~/code/redis && git remote add fork git@github.com:mgravell/redis.git &&
git push fork marc/uds-abstract-sockets` — pushing to the personal fork needs no org SSO. The branch is
one commit, PR-shaped. Upstream pitch when ready: "support the established @ convention for abstract
sockets in -s" — small, one function, benefits cli and benchmark alike.

### ~~THE DEPTH-TLS SEND AMPLIFICATION~~ FIXED same day: OnLoopDrain, tax 28% → 8.4% (see RESULTS)

Built as pre-registered: `SocketSet.OnLoopDrain` (batch-end hook, io_uring + epoll; managed never fires
it), proxy drains per batch instead of per callback. SQEs 3x → below plaintext; TLS depth tax −28% →
−8.4% disjoint; TLS `-P 1` p99 improved ~25% as a bonus. The ~8% residue is the true encrypt-path cost —
a future lead, much smaller than it looked. Original diagnosis follows.

### THE DEPTH-TLS SEND AMPLIFICATION (diagnosed 2026-08-03; the ~28% headroom has a mechanism)

The TLS-origination showdown left a lead: depth TLS costs us ~28% off our own plaintext while Envoy's
depth numbers barely move. Instrumented (`SS_URING_STATS`, same 10M-op depth workload, plaintext vs TLS
upstream): **TLS issues 3x the send SQEs for identical work** (1,226,824 vs 406,135, ~1 iovec each).

**Mechanism, read from the code rather than guessed:** our decrypt path is NOT per-record
(`TlsData` decrypts the whole recv buffer, one `DispatchReceive`) — the fan-out is the PEER's write
granularity. An SslStream-style upstream writes per TLS record, so one logical reply burst arrives as
several TCP segments → several recv completions → and the proxy's CALLBACK-granularity deferred flush
(the -P 16 fix) faithfully turns each completion into its own downstream/upstream send. Peer
segmentation x per-callback drain = 3x sends. Envoy does not amplify because its event loop flushes per
ITERATION, not per readiness event.

**Candidate fix, well-scoped:** a LOOP-ITERATION flush point. io_uring already processes CQEs in
batches; expose a transport callback at batch end (an `OnLoopDrain`/batch-end hook on SocketSet), and
move the proxy's `DrainDeferred` from per-`OnReceive` to per-batch. Same latency envelope at -P 1 (a
batch with one completion drains identically), collapses the amplification at depth. Pre-register: the
send-SQE count under the TLS depth workload should fall from ~3x toward ~1x plaintext, recovering a
meaningful slice of the 28%; if SQEs fall but throughput does not move, the syscall count was not the
binding cost and the lead moves to the encrypt path itself.

### THE ~8% TLS ENCRYPT RESIDUE, DECOMPOSED (2026-08-03): one avoidable ciphertext copy

With send amplification gone, the remaining depth-TLS tax is −8.4% (disjoint, n=60M). Read from the
io_uring path: `TlsEncryptSend` → `ProcessOutbound` writes ciphertext into `TlsOut` (inherent), then
`TlsSend` **copies the ciphertext AGAIN** into OOB pool pages for the scatter-gather chain. TLS pays two
post-encrypt copies where plaintext pays one, plus the crypto itself — so the honest split of the 8.4%
is roughly {extra memcpy, AES-GCM, record overhead}, and only the first is avoidable.

**Fix shape — the io_uring twin of the Windows "owned staging" idea** (`StageOutboundOwned`, built
IOCP-only in the HTTP era): send FROM the encrypt scratch, holding the buffer until the send completes,
instead of re-copying into pages. Needs a lifetime design (per-connection `TlsOut` is reusable scratch;
sending from it means it cannot Reset until completion — double-buffer or hand-off), which is why this
is recorded rather than rushed. Ceiling honestly stated: some fraction of 8.4%, likely half or less —
worthwhile only bundled with other TLS-path work, not as a standalone session.

### BACKLOG: tune the frame scanner (raised by Marc 2026-08-02)

`RespReader`'s frame scanning is worth a pass — plausibly a few more percent. **Aim it correctly, because
the two pipeline regimes load it completely differently:**

- **At `-P 16` the scanner is on the critical path.** Many commands arrive per read, so per-byte scan cost
  dominates and per-request overhead has amortised away. This is where scanner work will show up — and it
  is where we already BEAT Envoy by ~50%, so this is extending a lead rather than closing a gap.
- **At `-P 1` it is nearly irrelevant.** One 32-byte command per read: the scan is rounding error against
  the per-request costs that actually decide that regime. Do NOT expect scanner work to move the Envoy
  deficit, which lives entirely at `-P 1`.

**The strategic reason it is worth more than the percentage suggests: `RespReader` is RESPite's, so it is
SHARED WITH SE.Redis.** Every gain lands in the client library as well as the proxy — and SE.Redis
pipelines heavily, i.e. it lives in exactly the `-P 16` regime where the scanner dominates. That makes
this the rare item that pays on both consumers at once, and it is aligned with the funded work.

**Fairness note for any Envoy comparison (empirical, 2026-08-02):** `RespReader` carries the full RESP3
prefix space on every scan; Envoy cannot even negotiate RESP3 (`HELLO 3` through it gets NO reply, while
Garnet direct answers a `%8` map). So the parse-cost comparison is structurally tilted toward Envoy, and
that is FAIR TO US to state: per Marc (whose day job is Redis client libraries), real clients default to RESP3.

**Measure it in isolation first.** The proxy A/B cannot attribute a scanner change: too much else is in
the path. A micro-benchmark over representative RESP frames (mixed inline-array commands, varied bulk
sizes, the multi-segment boundary case) is the right instrument, with the proxy A/B only as confirmation
that a micro win survives integration. `experiments/BufferBench` is the precedent for that shape.

## ALPHA PACKAGING: BUILT AND VALIDATED 2026-08-03 (Marc: all three together, alpha; publish pending)

Marc's call: stabilise in-flight work (done — all repos clean, gates green), then package `SocketSet` +
`SocketSet.AspNetCore` + `SocketSet.Garnet` **together, as alpha**. State:

- `version.json` → `0.1-alpha`; NBGV keeps git height, so packs come out `0.1.195-alpha` etc.
- Both satellites are now `IsPackable`. Garnet pins `Microsoft.Garnet [2.1.1]` EXACT (rides
  public-but-obscure surface). AspNetCore folds the vendored `RESPite.Vendored.dll` into `lib/` via
  `PrivateAssets="all"` + `TargetsForTfmSpecificBuildOutput` (no phantom package dependency), and pins
  plural `TargetFrameworks=net10.0` (repo default `net10.0;net472` was overriding its singular form,
  and net472 cannot reference the net10.0 vendored project).
- **Validated from packages alone** (scratch consumers outside the repo, local feed): Kestrel app via
  `builder.UseSocketSet()` serves HTTP with the `[socketset transport] resolved:` banner (fast path
  TAKEN, not just referenced), and an embedded Garnet on `SocketSetGarnetServer` answers `+PONG`.
  Scratch-consumer gotchas worth remembering: /tmp has no `global.json` (a preview SDK 11 got picked
  up) and no implicit usings.
- **PUBLISHED 2026-08-03 (Marc):** all three live on nuget.org at `0.1.196-alpha`, verified from the
  public feed (flatcontainer lists them; a scratch consumer restored `SocketSet.AspNetCore` cold and
  served HTTP with the transport banner). Remaining Marc-only: the `SocketSet.*` reserved-prefix
  application, and (optional) delisting the ancient `SocketSet 0.1.1`/`0.1.3` versions the ID carries.
  The garnet discussion's reproduction path can now be `dotnet add package SocketSet.Garnet
  --prerelease` instead of clone-and-build — Marc's edit to make if wanted.
- `SocketSet.StackExchange.Redis` is deliberately NOT packaged: welded to the cross-repo sibling
  checkout and the SER009 experimental contract.

## TLS AT LISTEN/CONNECT GRANULARITY (2026-08-03, from Marc — CONNECT HALF BUILT same day)

**Status: BOTH HALVES DONE and gated (per-LISTEN completed 2026-08-04).** `Listen(ep, userToken,
tls: provider)` / `ListenHandle(...)` across all five backends: the listener carries the provider,
accepted connections inherit it (multi-bind growth replay and cross-shard accept bounce both carry it),
`ResolveServerTls` mirrors the client rule (explicit provider wins outright), and server-side
kTLS/StartTls/BeginTls take the resolved provider everywhere. Proof extended to the strong
discriminator: ONE engine with NO engine-level TLS hosting cert-A, cert-B and plaintext listeners —
a verify-ON trust-B client succeeds on B, is REFUSED on A, and plaintext stays plaintext. Full gate
suite on the combined feature: smoke 60/60, verify-aspnet 18/18, verify-tls-floor 8/8, tunnel-selftest
5/5. Remaining: TlsClient options (TargetHost) still engine-level; Windows both halves
compile-only-unverified (first Windows session: smoke matrix + Verify-TlsFloor + Verify-AspNet).

Original connect-half status note follows.

**Status: the per-CONNECT half was DONE and gated** (Marc's motivating example verbatim): `Connect(ep,
userToken, tls: provider)` / `ConnectShard(...)` across ALL FIVE backends — per-connection override on
`Connection.TlsOverride` (nulled at every slot-recycle/init point, seeded only by an explicit connect),
one resolution rule (`ResolveClientTls`: explicit provider WINS OUTRIGHT and is itself the direction
signal — TlsMode does not gate it; null = engine options exactly as before), provider-parameterised
StartTls/BeginTls everywhere, kTLS eligibility resolved from the per-connect provider on Linux. Gates:
smoke 60/60, tunnel-selftest 5/5, verify-tls-floor 8/8, and a dedicated proof in the proxy shape — ONE
engine terminating with cert A downstream (TlsMode.Accept) while originating with trust B upstream via
the per-connect provider, with the no-override dial staying plaintext. **Windows backends compile and
follow the identical pattern but are UNVERIFIED — first Windows session must run the smoke matrix +
Verify-TlsFloor.** Remaining half: per-LISTEN granularity (listener carries provider, accepted conns
inherit) — the same plumbing pattern, deferred; and TlsClient options (TargetHost etc.) are still
engine-level, so per-connect providers to DIFFERENT hostnames share one TargetHost — recorded gap.

The original design note follows.


**The ask:** TLS config should live at the listen/connect level, not (only) the engine level. Today one
`TlsProvider` + one `TlsMode` per `SocketSetOptions` covers the whole engine; `TlsMode` gives
directionality but both directions share ONE provider. Marc's example is the sharpest: the proxy has no
structural need for separate SocketSets for upstream and downstream — it needs different TLS CONTEXTS
(terminate with cert A downstream, originate with trust B upstream), and today that forces two engines.
The tunnel documents the same wall ("one engine = one TLS posture; mixed targets want two tunnels") —
this item dissolves both.

**Sketch:** per-call optional TLS: `Listen(endpoint, tls: provider?)` / `Connect(endpoint, tls:
provider?)`, engine options as the default. A listener carries its provider; accepted connections
inherit it; outbound connections take the per-call one; `Connection` stores the resolved provider. The
~11 `TlsEnabled(isClient)` engagement sites across the 5 backends become connection-level reads instead
of `Parent.Options.Tls`. kTLS probes are already provider-scoped. `TlsMode` may collapse entirely —
presence of a provider AT THE CALL is the direction signal, which is cleaner than the flags enum.
Consequences to carry: `ToString`'s `tls=` becomes a per-listener summary; `SocketSetTunnel` can offer
per-endpoint TLS selection; multi-cert listens (SNI-adjacent) become representable later. Pre-alpha:
no compat constraint, but this touches every backend's accept/connect path — gate with the full smoke
matrix AND both TLS narrow gates on both OSes.

## SE.REDIS SIDE-QUESTS (2026-08-03, from Marc)

- **UDS incl. @abstract in SE.Redis itself** — "we should stop being hypocritical": we are shipping
  abstract-socket support to redis-server/redis-cli (PRs against upstream) while SE.Redis, from memory,
  supports `!foo.sock` UDS config syntax but almost certainly not `!@foo` abstract names. Verify what
  `!` parsing produces today, then support `@` → abstract (`\0`-prefixed) on Linux in BOTH paths: the
  classic managed-socket connect AND the SocketSet tunnel (the tunnel side already maps `@`; the config
  parse and `UnixDomainSocketEndPoint` construction are the likely gaps). Same `@` convention as
  socat/systemd and our upstream PRs. Test cell: in-proc or real server on an abstract name, connect via
  config string.
- **BufferedStreamWriter unification: DONE 2026-08-03** (Marc's go: "duplicated code is asking for
  trouble") — branch `marc/respite-buffered-stream-writer` (f1965d12, pushed): the trio moved to
  `RESPite.Streams` as the single copy (replace/move; SE.Redis files gone), factory decoupled to
  `(WriteMode, Stream, MemoryPool<byte>?, ct)` with the pub/sub-never-sync POLICY moved to the one call
  site in `PhysicalConnection.InitOutput`; RESPite gains the conditional System.IO.Pipelines reference
  (in-box net10.0+) so PipeStreamWriter stays with its family. Solution 0 errors, 129 writer/round-trip/
  in-proc tests pass. **Merged to main as #3153 (2026-08-03)**; `marc/proxy-socketset` has main merged
  in and its drifted copies deleted (49125952) — the duplication is dead everywhere. Sibling checkout
  back on main; tunnel gate re-smoked ALL PASS against the merge.

## THE SE.REDIS SEAM: DECIDED 2026-08-03 — via the Tunnel API (Marc's call)

**The integration point for SocketSet as SE.Redis's IO core is the `Tunnel` API, not `PhysicalConnection`
surgery.** Shape: add a new virtual to `Tunnel` that returns **null for every existing implementation**;
the new API returns whatever shape suits OUR abstraction — deliberately NOT constrained to `Stream` or
pipes — and SocketSet ships a `Tunnel` implementation. Consequences Marc called out explicitly:
- Null-default virtual = zero behaviour change for every current caller; SE.Redis core barely moves.
- **The concrete implementation of the new shape stays in SocketSet code** (as a Tunnel implementation),
  which largely SIDESTEPS the API-freeze pressure — SE.Redis depends on the Tunnel contract, not on
  SocketSet's evolving internals.
- Historical fit (Marc): `Tunnel` is ALREADY the connect-hijack seam — it is how the in-proc server
  connects without sockets — so the new virtual widens an existing hijack point rather than inventing one.
- **The SE.Redis-side surface is ONLY the new Tunnel virtual, and it ships `[Experimental]`** (Marc,
  same day) — consumption of the new API is free to evolve; no freeze anywhere.
- Design task therefore splits: (1) the new `Tunnel` virtual + the transport shape it returns (design it
  from what the level-2/3 work actually needed: push receive with transport-owned spans, any-thread
  copying send, batch-end flush point, completion-scoped lifetimes); (2) the SocketSet-side
  implementation of that shape.

## TUNNEL TRANSPORT SHAPE — ANSWERED AND BUILT ON BOTH SIDES (2026-08-03)

**Marc's answers:** abstract class ✓; free hand on `PhysicalConnection.Read` ("don't object to shaking
things up") ✓; **shape lives in RESPite** ✓. Receive-into-caller-buffer defers to exactly the
abstract-class mechanism (add later with a safe default).

**Built:** SE.Redis branch **`marc/tunnel-transport`** (off main, pushed, solution 0 errors) —
`RESPite.Transports.DuplexTransport`/`TransportReceiver` as `[Experimental]` **SER009** abstract classes
(registered in Experiments, `docs/exp/SER009.md`, PublicAPI entries with the `[SER009]` prefix in both
projects) plus `Tunnel.ConnectTransportAsync` (null-default virtual). SocketSet side:
`src/SocketSet.StackExchange.Redis` holds `SocketSetClientTransport` over a provisional shape copy,
**gated ALL PASS across plaintext/TLS/@abstract** (`bench/tunnel-selftest.cs` — the 1000-command burst
lands in six batch-end callbacks, the coalescing visible in the gate).

**SHAPE REVISION (2026-08-03, Marc's call after the collapse thought-experiment):** the separate
`Output` property was a lie — `Flush` lived on the transport, so the "output object" never truly
described output. Resolution: **the transport IS the writer** (`DuplexTransport : IBufferWriter<byte>`,
abstract `GetMemory`/`GetSpan`/`Advance` directly on it; `Output` deleted), and **the receiver stays a
separate abstract class** (consumer-implemented; abstract-class evolvability matters on RESPite's
net461/netstandard2.0 targets, where interfaces cannot grow — no DIMs). Passing the transport *as*
`IBufferWriter<byte>` deliberately grants stage-only access: the holder composes, the owner flushes at
its batch boundary — the batching contract expressed in the type system. Costs accepted: writer can
never differ from transport (framing multiplexers belong a layer up), and the SocketSet impl now
forwards `GetSpan`/`Advance` to the `Connection` instead of handing it out (one null-check +
delegation per call). Also settled permanently: a shared transport *interface* between SocketSet and
RESPite is dead — no structural identity for interfaces, and a third both-agree assembly won't happen;
containment (has-a) is the pattern. Revised on both sides, re-gated **ALL PASS × plaintext/TLS/@abstract**.

**Next steps in order:** (1) ~~retarget the SocketSet impl to the real RESPite types + the actual Tunnel
subclass~~ **DONE 2026-08-03**: provisional copy deleted (replace/move, not duplicate), cross-repo
sibling references to RESPite + StackExchange.Redis (marc/tunnel-transport checkout required — csproj
errors clearly if absent), `SocketSetTunnel : Tunnel` overriding `ConnectTransportAsync`, and the
has-a restructure the abstract-class decision forced (`SocketSetClientTransport : DuplexTransport`
contains a nested `Engine : SocketSet`; single inheritance forbids the old is-a). The gate had to stop
being a file-based app (`dotnet run --file` leaks `#:property` TFM overrides as globals into the
cross-repo restore graph → NETSDK1005 in the sibling's eng/ project) — it is now
`bench/tunnel-selftest/`, a real csproj, re-gated **ALL PASS × plaintext/TLS/@abstract** against the
real types. (2) ~~the `PhysicalConnection.Read` push-feed integration~~ **DONE 2026-08-03** with (3)'s gate in the
same stroke: SE.Redis branch `marc/transport-push-feed` (5324d703, off main, pushed) adds a transport
MODE to PhysicalConnection — transport acquired BEFORE socket creation, no socket/Stream/SslStream/
reader thread; outbound via `TransportWriter : BufferedStreamWriter` (every `_output` call site
untouched); inbound PUSH into the existing `CycleBuffer`+`CommitAndParseFrames` on the transport's
threads (one copy, zero hops). `config.Ssl` + transport tunnel throws (tunnel owns TLS). Gate:
`bench/tunnel-selftest` grew `mux` and `mux-tls` cells — a REAL ConnectionMultiplexer over
`SocketSetTunnel` against Garnet: connect/PING/SET/GET/500-op burst, plaintext AND transport-TLS,
**ALL PASS first run**; 129-test SE.Redis battery still green. Sibling checkout sits on this branch.
(3) remaining half: MEASURE it — SE.Redis-over-SocketSet vs SE.Redis-classic A/B (rig exists in
run-client-shape.sh's pattern; needs the interleaved-legs discipline and a control). **In progress
2026-08-03: `bench/mux-ab` + `bench/run-mux-ab.sh`** (identical generator both legs, counting-tunnel
engagement gate, stock-Garnet server, pre-registered P1/P2/P3 in the rig header).

**(4) NEXT REV, from Marc's question 2026-08-03 ("does the Tunnel anchor the shared socket-set?"):
today NO — v1 is deliberately engine-per-connection** (each ConnectTransportAsync builds a transport
whose nested Engine is a private SocketSet; cheap and clean at SE.Redis's 1-2 connections/endpoint,
wrong shape at cluster scale: 3 nodes x 2 connections = 6 engines). The intended end-state is Marc's
assumption: **the Tunnel concrete lazily owns ONE SocketSet; ConnectTransportAsync dials a Connection
on it; per-connection transports become thin routers.** Known work: per-connection dispatch for
OnReceive/OnClosed (userToken plumbing exists), per-connection OnBatchEnd (batch-end is per shard
loop — use the proxy's touched-this-batch t_pending pattern), and the payoffs are amortized pinned
pools + the L3 shard-affinity games across a cluster's connections. Do AFTER the v1 A/B is recorded —
the shared anchor changes engine COUNT, not single-connection mechanics.

The original proposal follows for the record (it shows the pre-revision shape with `Output`).

## TUNNEL TRANSPORT SHAPE — DESIGN PROPOSAL (2026-08-03, for Marc's review before any code)

**The virtual** (on `Tunnel`, `[Experimental]`, null-default — the same move as `BeforeAuthenticateAsync`
one level deeper: instead of yielding a `Stream`, yield the transport itself):

```csharp
public virtual ValueTask<IDuplexTransport?> ConnectTransportAsync(
    EndPoint endpoint, ConnectionType connectionType, CancellationToken cancellationToken) => default;
// null → the existing socket/stream path, untouched, for every current Tunnel
```

**The shape — each member derived from a measured lesson, not taste:**

```csharp
[Experimental]
public interface IDuplexTransport : IAsyncDisposable
{
    // OUTBOUND — "callable from any thread; bytes copied on the call" is the contract that made
    // level-2/3 thread-safety tractable (local replies on the loop thread, upstream replies on another,
    // one lock). Write/Flush split rather than Send-only: batching is the single biggest lever this
    // project measured (callback- then batch-granularity flushing; the 3x amplification fix).
    IBufferWriter<byte> Output { get; }      // stage: any thread, copies
    bool Flush();                            // wire everything staged as one send; false = closed

    // INBOUND — PUSH, because that is where the wins were: level 2's frame-on-the-loop beat the
    // pipe-bridge pull by the whole -P 1 gap, and every pull adapter we measured (Level 1) cost 24-40%.
    void Start(ITransportReceiver receiver); // begin delivery; exactly one receiver, set once
}

[Experimental]
public interface ITransportReceiver
{
    // payload is TRANSPORT-OWNED, valid only for the call (the level-2 contract). Return false to
    // request close. Runs on the transport's thread: the receiver must be bounded and non-blocking —
    // acceptable for SE.Redis because response dispatch completes TCS's with RunContinuationsAsync.
    bool OnReceived(ReadOnlySpan<byte> payload);

    // batch-end (SocketSet's OnLoopDrain surfaced): flush replies staged during a burst ONCE. The 3x
    // send-amplification fix depends on this existing in the contract.
    void OnBatchEnd();

    void OnClosed(Exception? fault);
}
```

**Open questions needing Marc (ranked):**
1. **Interface vs abstract class** for the shape — `[Experimental]` lets either evolve; abstract class
   gives null-default member addition forever (the Tunnel precedent), interfaces need DIMs. Lean: match
   the Tunnel house style (abstract class) unless double-inheritance at implementers matters.
2. **Consumption side**: `PhysicalConnection.Read` today PULLS from `_ioStream` into a `CycleBuffer`
   with `ReadStatus` phases — the machinery is already RESPite-cousin-shaped. Proposal: a push feed
   path on the read side (the `Feed`-into-`CycleBuffer` move the proxy handler made), NOT a pull adapter
   over the push shape (that is the pipe bridge again, the measured 24-40%). How invasive may the Read
   partial's integration be?
3. **Receive-into-caller-buffer** (the Garnet copy-removal want): v1 of the shape, or a v2 member?
   Sketch: `bool TryGetReceiveBuffer(int sizeHint, out Memory<byte>)` on the receiver, letting the
   transport land bytes directly in consumer memory. Cheap to add behind [Experimental] later; including
   it now serves both consumers from one design.
4. ~~Naming, and where the concrete implementation lives~~ **DECIDED (Marc, 2026-08-03): the concrete
   Tunnel implementation ships as `SocketSet.StackExchange.Redis`** — an in-repo library in the
   `.AspNetCore`/`.Garnet` pattern, the fifth consumer — **with an acknowledged corresponding SE.Redis
   rev adding the `[Experimental]` virtual + shape.** Still open within this: whether the SHAPE type
   itself sits beside `Tunnel` in `StackExchange.Redis` or in RESPite (RESPite placement lets
   Garnet/proxy share it without an SE.Redis reference; SE.Redis placement keeps the rev
   self-contained).

**Validated 2026-08-03: the shape needed NO surface-specific members across all three client-relevant
surfaces** — `tunnel-selftest` runs the identical battery (push round-trip, 1000-command burst,
batch-end coalescing, close-once) over plaintext TCP, TLS (real handshake, pinned trust, verification
on — the SE.Redis-to-managed-Redis shape), and `@abstract` UDS (the sidecar shape). ALL PASS, all
surfaces selected purely by `SocketSetOptions` + endpoint, invisible to the contract.

**What is deliberately absent:** backpressure signalling (SE.Redis reads never pause in practice; add
later behind [Experimental] if real), sync Read anything (push only), Stream/pipe compatibility members
(the point of the new shape is not being those).

## API-SURFACE FREEZE PROPOSAL (drafted 2026-08-02; decide BEFORE SE.Redis takes the dependency —
**urgency REDUCED by the Tunnel-seam decision above**, which keeps SocketSet's surface private to its
own Tunnel implementation)

`AGENTS.md`'s "public API and defaults can change freely" expires the moment SE.Redis references this.
Proposal, based on what the proxy integration ACTUALLY consumed — which is a good proxy (sorry) for what
SE.Redis client mode will consume:

**Freeze candidates (the surface the level-2/3 work used, all of it):**
- `SocketSet`: `Listen`, `Connect`, `ConnectShard`, `Dispose`; the callback set `OnAccept` / `OnConnect` /
  `OnReceive` / `OnClosed` and their contexts (`ReceiveContext.Payload` span semantics — transport-owned,
  callback-scoped — are a CONTRACT, not an implementation detail).
- `Connection`: `Send(span/sequence)`, the `IBufferWriter` surface (`GetSpan`/`GetMemory`/`Advance`/
  `Flush`) **with the single-writer-until-Flush rule stated in doc-comments as normative**, `Close()`
  (documented abortive), `UserToken`.
- `SocketSetShard.CurrentShardIndex` (with its -1-off-loop semantics).
- `SocketSetOptions`: `Factory`, `Shards`, `Tls`/`TlsClient`/`TlsServer`, `PinWorkerThreads` — plus
  `TlsMode` when built.

**Stays experimental (mark or hide):** `UsePipe`/the pipe bridge (ASP.NET-specific; level 2 proved the
callback path is the product), buffer-geometry knobs (`BufferPageSize` etc — backend-chooses sentinels are
still evolving), every `SS_*` env var (rig instrumentation, never API), `SendBuffer`/`SendBytes`
instant-reply (correct only with an empty queue; the proxy deliberately does not use it).

**Process:** on the first SE.Redis `ProjectReference`, changes to the freeze list get a deprecation note
in TODO rather than silent change; everything else stays pre-alpha. Revisit the list when the client-mode
prototype (single-connection rig) has run — it may consume surface the proxy did not.

## READ FIRST IF YOU ARE ON LINUX (2026-08-01 addendum — SHARED CODE CHANGED UNDER YOU AGAIN)

> **Written at the end of the 2026-08-01 Windows session. Linux has not run since 2026-08-01 morning and
> two SHARED changes landed after that, one of them on the hottest path. Correctness first:
> `bench/run-smoke-matrix.sh` (60 cells) before anything else.**
>
> **STATUS 2026-08-02 (Linux session): the gate is GREEN and item 1 is CLOSED.**
> `bench/run-smoke-matrix.sh` **60/60 PASS** — the Windows session's shared changes are correctness-clean
> on io_uring, epoll and managed, plaintext and TLS, including the kTLS and abstract-UDS cells. Item 1
> (the flush fix) is now verified on Linux, and it corrected its own handover claim — see below.
> **Item 3 (`SS_PIPE_SCHED=inline-read`/`inline-both` on io_uring/epoll) is the next high-value item and
> is NOT done.** Item 4 (a Linux `Verify-AspNet.ps1` equivalent) also remains open.
> **Bench-host note: the CPU governor is `performance` as of this session** (Marc set it; an agent cannot,
> as `sudo` needs a password). The 2026-08-01 Linux headline table was measured under `powersave`, so do
> not compare absolute MiB/s across that boundary.
>
> **1. `PooledBufferWriter` hand-off pessimisation — FIXED.** ~~and this is the one that matters to you~~
> **VERIFIED ON LINUX 2026-08-02: the prediction HELD on epoll, and the claim about WHICH BACKENDS REACH IT
> WAS WRONG.** See `RESULTS.md` "The flush fix, VERIFIED ON LINUX". Summary and correction inline below.
>
> `TakeArray()` left the writer empty, so the next use re-rented at the first size hint and grew by
> DOUBLING, with a `Buffer.BlockCopy` per doubling. `OutboundConnection.Flush` calls `TakeArray` on EVERY
> out-of-band flush ~~on EVERY backend — **io_uring, epoll and managed all reach it**~~ — **CORRECTION: on
> Linux ONLY EPOLL reaches it.** `OutboundConnection` is derived from by `WindowsConnection` (IOCP/RIO) and
> `EpollConnection` alone; `IoUringConnection` and `ManagedConnection` derive from `Connection` directly
> and have their own send paths, and `TakeArray` has exactly two call sites (`OutboundConnection.Flush`,
> `WindowsShardBase`). io_uring's TLS writers are reusable scratch that never detach, so io_uring **never
> paid this cost and had nothing to gain.** Fixed by remembering high-water capacity.
> Measured on Windows/IOCP, interleaved, disjoint: **classic plaintext 1 MB +33.3%; TLS 256 KB +18.8%;
> TLS 1 MB +58.6%.** Nothing at 16 KB (few doublings).
> **PRE-REGISTERED FOR LINUX: expect the same shape — a win that GROWS with payload, largest on TLS and on
> `--classic`, ~nothing at small payloads, and ~nothing on plaintext `--byo` (zero-copy send skips `Flush`
> entirely).** ~~If Linux shows a win at SMALL payloads, or on plaintext `--byo`, the mechanism is not the
> one described here and that is a finding.~~ **OBSERVED on epoll, exactly: 16 KB +2.0% overlapping,
> 256 KB +17.4% DISJOINT, 1 MB +29.9% DISJOINT, and plaintext `--byo` flat at all three sizes.** The
> falsifier did not fire. 256 KB matches Windows almost exactly; 1 MB is ~half the Windows figure.
>
> **THE COST OF THE WRONG CLAIM, recorded because it is the transferable part:** the first Linux run used
> `BACKEND=io-uring` on the strength of the struck-through sentence above and produced a clean, tight,
> entirely meaningless null (+0.4% at 1 MB, ranges too tight to hide a 58% effect). It reads precisely like
> "the fix does nothing on Linux". **An identical-binary guard cannot catch this** — the binaries differ;
> it is REACHABILITY that fails. House rule 2 ("confirm the fast path was TAKEN") applies to the BACKEND,
> not only to the flag, and the way to confirm it is to read the type hierarchy before spending 20 minutes.
> Rig: **`bench/compare-commits.sh`** — written for this, the Linux port of `Compare-Commits.ps1`'s
> interleaved two-worktree shape. Its header now carries this trap.
>
> **2. IOCP-only "owned staging" for TLS ciphertext** (`StageOutboundOwned` / `SupportsOwnedStaging`).
> Opt-in per backend and **false everywhere except IOCP**, so Linux behaviour is unchanged by construction.
> Measured NEUTRAL on Windows; it is a simplification, not a win. If you want it on io_uring/epoll their
> drains must first tolerate a staged segment LARGER than one page.
>
> **3. `SS_PIPE_SCHED` gained `inline-read` / `inline-both`** (the INBOUND reader; the old `inline` only
> ever moved the outbound one). Off by default, reported in `/config` as `pipesched=`. On Windows this
> found that **the ~3% small-payload deficit to vanilla Kestrel IS the thread hops** — see item 9 and
> RESULTS. ~~**Re-running `bench/Run-PipeSched.ps1`'s equivalent on io_uring/epoll is high value**~~
> **DONE 2026-08-02 on io_uring** (`bench/run-pipesched.sh`, new): **the prediction HELD and the Windows
> finding REPLICATED.** `inline-read`'s gain over `off` is +4.5% / +0.7% / −1.5% / −0.1% at
> 512 B / 4 KB / 16 KB / 256 KB — monotonic to nothing, i.e. per-REQUEST exactly as registered — and at
> 512 B it converts a disjoint −5.5% deficit into PARITY with Kestrel. Narrower than Windows (parity at
> 512 B only, not through 16 KB), so the Linux ceiling is ~4% at the smallest payload.
> **Two claims fell out, both recorded in RESULTS:** (a) the old Linux `inline` **−28% does NOT reproduce
> at any size** — it is positive throughout on today's defaults, so it can no longer be quoted as current;
> (b) **`inline-both` is worse than either knob alone at every size**, because both readers serialise on
> the io_uring loop thread — so "off the ThreadPool" is not monotonically good.
> ~~**STILL TO DO: the same run on epoll.**~~ **DONE the same day, AND IT FALSIFIED THE GENERALISATION.**
> Not reading it across was the right call: on epoll `inline-read` gains **+0.9% / −1.4% / −11.5% / +0.9%**
> at 512 B / 4 KB / 16 KB / 256 KB — it never meaningfully helps and at 16 KB it is a disjoint −11.5%
> catastrophe. **The read hop owns the small-payload deficit on Windows/IOCP and io_uring; it does NOT own
> it on epoll**, where `off` trails Kestrel −4.8% at 512 B and no scheduler mode recovers it.
> *Hypothesis, not result:* the two backends where it matters are the ones whose completion model hands
> you finished work; epoll's readiness loop must do the `recv` itself.
> **Consequence for the inbound half-pipe: the case SURVIVES but NARROWS** — io_uring is the Linux default
> so ~4.5% at 512 B is on the default path, but it is one backend at one size, not a general bridge win,
> and epoll would gain nothing. Cost it at ~4% at the smallest payload on io_uring only.
>
> **Also still open from the morning:** `Verify-AspNet.ps1` has no Linux equivalent, so the
> `SocketSet.AspNetCore` extraction is runtime-verified on IOCP/RIO/managed but NOT on io_uring/epoll.

## READ FIRST IF YOU ARE ON LINUX (rewritten 2026-07-31, after three days of Windows work)

> **THIS CATCH-UP IS DONE (end of 2026-07-31 Linux session). What follows is kept for its reasoning; the
> per-item status is inline.** In one paragraph: the shared changes are correctness-clean on Linux (new
> `bench/run-smoke-matrix.sh`, **60/60** including kTLS and prefix-send cells), the size-sweep flat-check found the
> transport throughput-neutral (the one mover was the BYO-default flip, not a regression), reuse-port shard
> growth now works on io_uring + epoll (two gaps, not the one named), the io_uring TLS out-of-band stall
> was a writev/IOV_MAX bug (fixed, NOT a geometry problem — so io_uring keeps `BufferGeometry.Default`),
> and **epoll gained a real kTLS path (item 3c)** — whose pre-registered throughput prediction then
> FALSIFIED itself (epoll+ktls trails epoll+tls ~9%, so the kTLS small-message penalty is the record path,
> not multishot forfeiture), **io_uring got zero-copy prefix sends** (§3 item 3 — measured that an 8MB
> response was 100% copy, fixed it to 100% zero-copy), and the **TLS renegotiation audit** landed
> (server refuses client-initiated renegotiation via `SSL_OP_NO_RENEGOTIATION`; client keeps allowing
> server-initiated; TLS 1.3 KeyUpdate untouched). All committed and pushed to `main`. **Active TLS
> follow-ups in progress:** a `TlsOptions.MinProtocol` defaulting to TLS 1.3 (make 1.2 opt-in), then an
> active KeyUpdate injection test. **Deferred (environment-gated):** kTLS multishot with RX offload (items
> 4/4b — need OpenSSL 3.2+ and real hardware), SChannel renegotiation parity (Windows), and the
> real-hardware session (item 5 — the only place kTLS NIC offload is visible). The largest remaining
> Linux code lever is the Kestrel-bridge cost (24-42% at 256KB).
>
> **UPDATE (later 2026-07-31):** `TlsOptions`/`OpenSslTlsProvider` now take a `minProtocol` floor,
> **defaulting to TLS 1.3** (1.2 is opt-in via `--tls-min12`) — this retires the whole 1.2 surface on the
> default path, so the renegotiation flag is now belt-and-suspenders. KeyUpdate is now test-verified too
> (`bench/verify-tls-keyupdate.sh`, both backends). **The Linux TLS backlog is now empty** — the only
> remaining TLS item is SChannel renegotiation/floor parity, which is Windows-only.
>
> **The Kestrel-bridge investigation found a concrete win, not just structure:** a same-session
> bare-vs-bridged isolation showed bridged EPOLL 41% below its (fastest-measured) bare transport at 256KB
> because epoll lacked BYO zero-copy send and copied the whole response on the pipe path. Implemented
> epoll `writev` zero-copy send — **bridged epoll 256KB 7,732 → 10,894 MiB/s (+41%)**, now level with
> io_uring.
>
> **CORRECTION: the residual "Kestrel wins at 256KB" gap was a POOL-DEFAULT confounder, not the thread
> model.** An inline-scheduler experiment made io_uring WORSE (−28%), killing the pump-hop hypothesis; the
> real cause was per-segment pinning — Kestrel's pipes use a `PinnedBlockMemoryPool` by default, ours used
> `MemoryPool.Shared`, so our zero-copy send paid ~64 GCHandle pins per 256KB response. With matched
> (pinned) pools, io_uring EDGES ahead at 256KB (12,610-12,965 vs Kestrel 12,239-12,529, disjoint), epoll ≈
> parity. Fixed by making the demo's pipe pool pinned by DEFAULT (`--pipe-unpinned` opts out); the bridge's
> structural pump-hop is NOT the 256KB bottleneck. **BUT the definitive 6-pass sweep (2026-08-01) corrected
> my broader over-claim: on `/payload` plaintext Kestrel actually LEADS 512B-16KB (disjoint +2-6%), 64KB is
> a wash, and we only edge ahead at 256KB — see the headline table in RESULTS. Where we DO win clearly is
> TLS: +13-35% disjoint from 512B through 64KB (in-transport OpenSSL vs SslStream), losing only large-payload
> TLS at 256KB (structural). And the "+5.6% small plaintext" is the 2-byte `/plaintext` endpoint, not `/payload`.**
> The one plaintext mystery left — we degrade MORE than Kestrel as concurrency rises (92%→80% c64→c128) —
> and the one correctness gap (advisory inbound backpressure) are both targeted by the **"two half-pipes"**
> proposal (see its section below): expose real reader/writer to Kestrel, drain directly on the loop side,
> kill the per-connection pump task. That is the most promising forward item for the ASP.NET path.
>
> **Zero-copy RECEIVE (item 7) was then built on epoll as a springboard and RESOLVED by measurement:** it
> engages 100% and is byte-exact, but a same-session A/B (`SS_NO_ZC_RECV`) shows NO throughput win across
> three workloads — Kestrel `/drain` upload, bare symmetric pipe echo, and a pure receive-flood
> (`SmokeTest --sink`, receiver ≫ sender). `recv()`'s kernel→user copy is unavoidable and dominates; the
> transport copy isn't the constraint. **So io_uring's much-harder zero-copy receive is not worth
> building.** The original handover text follows.

Linux has not been run since 2026-07-29 and **shared code changed underneath it**. Correctness first,
measurement second — the same discipline the Windows switch needed, for the same reason.

### 1. What changed in SHARED code, i.e. what can actually affect epoll and io_uring

Ordered by how likely it is to bite. Everything here is unverified on Linux.

| change | risk to Linux |
|---|---|
| **`PipeIoBridge` outbound pump rewritten** for prefix sends — it no longer assumes `AdvanceTo(buffer.End)`, and no longer exits on `IsCompleted` until the buffer is drained | **HIGHEST.** This is the shared bridge; epoll and io_uring both run it. A bug here is a stall or a lost tail on the pipe path. |
| **`Connection.TrySendZeroCopy` returns `long` (bytes accepted)**, not `bool` | io_uring was updated to all-or-nothing (`data.Length` or 0), so its behaviour should be **identical**. Verify, do not assume. |
| **`BufferPageSize` + 3 pool depths are `0` = "backend chooses"**, resolved once via `SocketSetFactory.DefaultGeometry` | epoll/io_uring inherit `BufferGeometry.Default` = the old hard-coded values, so nothing should move. |
| **`SocketSet.Listen`/`Connect` validate the endpoint** and reject `@abstract` names off-Linux | On Linux the guard must NOT fire — abstract sockets are a real Linux feature and io_uring maps a leading `@`. **Check an `@name` still works.** |
| **Dynamic shard growth** (`Options.MaxShards`, default 0 = off) | Off by default, so no behaviour change — but see the gap in §3. |
| `SocketSet.PlacementFailures`, `SS_BRIDGE_STATS`, stale-completion detectors | Additive counters. IOCP/RIO only for the detectors. |

### 2. What to check, in order

**ALL FOUR STEPS DONE 2026-07-31.** Results inline below.

1. ~~**Build both targets.**~~ **DONE** — `net10.0` and `net472` both build clean, 0 errors (7 pre-existing
   XML-doc warnings only).
2. ~~**The banner, not the flag.**~~ **DONE** — io_uring, epoll and managed all report
   `page=4096 recvbuf=4096 writebufs=1024 oobwritebufs=256 readpages=256` via `SmokeTest --http`. No `0`
   anywhere; no read site missed. io_uring confirmed as the auto-detected default (`IoUringFactory`).
3. ~~**The smoke matrix, by hand**~~ **DONE, and the `.sh` runner is now written**: `bench/run-smoke-matrix.sh`
   (io_uring / epoll / managed x plaintext / `--tls-ssl` x verify / echo-cb / echo-pipe / poke / churn,
   plus `@abstract` UDS echo-pipe on the two native backends, plus `+ktls` verify/echo cells on io_uring
   and epoll). Started at **51/52**, now **60/60** after a bug fix, the epoll+kTLS work, and prefix-send cells. The pipe path is
   clean (every echo-pipe cell passes), the abstract-UDS guard correctly does NOT
   fire. The one initial FAIL (`iouring+tls/verify-oob-4m`) LOOKED like item 0d but was NOT a geometry
   problem — it was an **unbounded writev** (IOV_MAX), now fixed. See §3 item 2 for the full diagnosis; the
   short version is the TLS out-of-band send issued a single `writev` of ~1024 page-segments for a 4MB
   response and the kernel rejected it with -EINVAL. `--page 65536` masked it by cutting the segment count.
4. ~~**A size sweep against the recorded numbers**, which should be flat.~~ **DONE 2026-07-31 — flat, and
   the one mover was a default flip, not a shared-code regression.** `bench/run-tls-sizes.sh` reproduces
   the payload-sweep table within ~3% everywhere (Kestrel control 12,450.9 vs recorded 12,450.5 = 0.0%)
   EXCEPT io_uring plaintext 256KB: **7,882.6 → 11,586.6 (+47%)**. That is `29da643` making the BYO bridge
   the default — the recorded row was the classic copy path (BYO was opt-in), the leg now measures
   zero-copy (confirmed taken: `zero-copy=2,602` segments, 0 pooled/managed; same-session A/B byo vs
   `--classic` = +71%). epoll is unchanged (no zero-copy send), which is why only io_uring moved. So the
   shared PipeIoBridge/Flush changes are throughput-neutral on Linux. Full write-up in
   `RESULTS.md` "Linux flat-check".

### 3. Then the Linux-only work, in priority order

1. ~~**Multi-bind listener replay for shard growth.**~~ **DONE 2026-07-31 — and it was two gaps, not the
   one the handover named.** Reuse-port growth now works end-to-end on both io_uring and epoll (each grows
   2→12 under load; `bench/run-shard-growth.sh`). See the dynamic-shard-growth section below for Gap A
   (listener replay) and Gap B (io_uring's silent local-accept drop) in full. Verified no regression: the
   smoke-matrix churn cells stay clean on both backends.
2. ~~**Decide whether Linux wants a different `DefaultGeometry`.**~~ **ANSWERED 2026-07-31: NO, io_uring
   stays on `BufferGeometry.Default`.** Two things had to be true to justify a bigger page, and neither is:

   - *Correctness.* The `iouring+tls/verify-oob-4m` stall looked like RIO's item 0d but is a different
     mechanism. The TLS out-of-band send (`TlsSend`) chunks ciphertext into page-sized segments and issued
     them as ONE `IORING_OP_WRITEV`; a ~4MB response at a 4KB page is ~1024 segments, which hits `IOV_MAX`
     and the kernel rejects the whole send with -EINVAL. It is SEGMENT COUNT, not page-vs-record:
     discriminating tests at page 4096 show 3MB (~768 segs) PASS and 5MB (~1280 segs) FAIL, and page 8192
     + 4MB (~512 segs) PASSES — and the 64KB/1MB TLS cells always passed at a 4KB page despite 7000-byte
     records. The plaintext OOB path already split at `IovMax` (`PumpFlush`); the TLS path bypassed it.
     **Fixed** by routing both through one `DispatchChainSplit` (`appData` rides only the last sub-chain so
     OnWrite still fires once). TLS verify now passes at 4MB/5MB/**16MB** at the default 4KB page. A bigger
     page would only have masked this — page 65536 still fails at 64MB.
   - *Throughput.* `bench/run-page-sizes.sh` (bare responder, pages 4/16/64KB) confirms the pre-registered
     prediction: io_uring is page-INSENSITIVE. 256KB medians 12,644 (p4096) vs 11,600 (p65536) — the 64KB
     page is if anything slightly slower. Unlike RIO (no scatter-gather, so page size is worth 4.68x),
     io_uring dispatches one writev over a segment chain, so the page is not a throughput lever.

   Net: RIO needed 64KB for both reasons; io_uring needs it for neither. Leave the default alone.
3. ~~**Prefix sends on io_uring.**~~ **DONE 2026-07-31 — measured first, then built.** The measurement
   settled "is the cliff real?": at 256KB the pipe path is 100% zero-copy (`zero-copy=1,952` segs, 0
   copied), but at **8MB it went 100% COPY** (`zero-copy=2` of ~61k segments, overflowing into
   `pinned-managed=53,790` per-response pinned allocations) — because 8MB / 4KB blocks = 2048 segments >
   `IovMax` 1024, so `TrySendZeroCopy` declined wholesale and the bridge copied the entire response. Far
   (needs >4MB) but a hard cliff. **Built:** `IoUringShard.TrySendZeroCopy` now returns BYTES ACCEPTED and
   caps the iovecs at `IovMax`, sending the first 1024 segments as a prefix; `PipeIoBridge` already
   re-presents the remainder (it did for IOCP), so a large sequence streams as several zero-copy writevs.
   After: 8MB is **100% zero-copy** (`zero-copy=61,472`, 0 copied). Byte-exact under concurrency (23 x 8MB
   downloads, all `x`, exact length) and a new `echo-pipe-8m-deep` smoke cell (8MB, window 2048) guards the
   prefix boundary math on io_uring and epoll.
4. **kTLS / epoll+kTLS.** ~~item 3c (epoll+kTLS pump)~~ **DONE 2026-07-31 — epoll runs real kTLS**, smoke
   matrix has `+ktls` cells (60/60). Its throughput comparison is DONE too and falsified the pre-registered
   prediction (epoll+ktls trails epoll+tls ~9%, so the kTLS penalty is the record path, not multishot
   forfeiture — see item 3c below).
   **items 4 / 4b (kTLS multishot receive)** remain open and need OpenSSL 3.2+ (RX offload) and real
   hardware to be worth it — deliberately deprioritised, see "START HERE".

### 4. What NOT to spend Linux time on

- **Anything RIO or IOCP.** Windows-only backends.
- **Item 1c (read depth)** — the premise was measured on Windows and the case for it may have the sign
  backwards; see that entry before touching the io_uring side.
- **Item 7 (zero-copy receive / receive parking)** — deprioritised on measurement, not opinion: the
  inbound gap to Kestrel is ≤5% and the second copy it removes is 0.011% of bytes.

---

## WINDOWS CATCH-UP: DONE 2026-07-29 (this section was the plan; this header is the result)

**All three jobs below were carried out on 2026-07-29 and the section is kept for its reasoning.** What
happened, in one paragraph each:

1. **The `Flush` hand-off risk (§1) is retired.** New `bench/Run-SmokeMatrix.ps1` runs the correctness
   gate as a script for the first time: IOCP / RIO / managed x plaintext / SChannel TLS x out-of-band
   verify at 64KB/1MB/4MB, echo-verify on the callback and pipe paths, poke, churn. **47/48 PASS with
   zero mismatches anywhere.** Run it on every backend you touch; it takes about three minutes.
2. **The one FAIL is a NEW finding and is not this change.** `rio+tls/verify-oob-4m` under-delivers, and
   bisecting to `dd8cdce^` in a worktree shows the same failure there, with a 5-pass interleaved A/B whose
   ranges overlap completely (pre 2.68-5.18s, post 2.68-5.20s). It is **RIO+TLS out-of-band send starved
   at the default 4KB page**: `--page 65536` gives 0.21-0.22s against 2.68-5.20s, fully disjoint, 15-25x.
   See the new item 0d below - it is a correctness-gate failure, not just a throughput number.
3. **The IOCP 65-segment experiment (§3) is ANSWERED, and the pre-registered prediction under-called it.**
   `SS_IOCP_STATS=1` (new, mirrors `SS_URING_STATS`) confirms the cap story on Windows directly: 40/40
   256KB responses declined at **mean 65.00 / max 65 segments against `MaxSendPages` = 64**. With
   `--pipe-segment 65536`, declines go to zero and IOCP gains **+117.3% at 256KB** (6 scored passes,
   disjoint ranges, `bench/Run-Byo.ps1`). The prediction asked for "something like io_uring's +45.1%".
   **And the control matters as much as the result:** pipe block size *alone* moves nothing on Windows
   (overlapping ranges), where on Linux it was independently worth +7.5% - so on IOCP the flag is purely
   the enabler that gets a response under the cap, and none of the +117% belongs to the block size.

4. **§2's premise was wrong, and the baseline re-measurement cannot answer what it was asked.** The plan
   said "compare against the 2026-07-27 numbers" to see what the copy removal bought Windows. It does
   re-measure cleanly - **both Kestrel controls reproduce within ~1%**, and IOCP is **+17.6% at 256KB** -
   but **six** commits touching Windows-reachable code sit in that window (`963143b`, `ff1a1c1`,
   `efcb1cc`, `30756c2`, `be09aed`, `dd8cdce`), and at least three of them can move a 256KB bridged
   number. A cross-day delta spanning six commits is a changelog, not an attribution. `Compare-Commits.ps1`
   gained a `-Bridged` switch (and interleaving) for the measurement that *can* attribute it.
   **Generalise the lesson:** "compare against the last recorded number" is only an attribution when
   nothing else landed in between, and this file's own commit list is the thing to check first.
5. **And the attributing A/B FALSIFIES §2's prediction.** §2 said the copy removal "should HELP Windows
   MORE than Linux" because the Windows path had 3-4 copies to Linux's 2. Isolated worktrees, one commit,
   interleaved, run twice: **+0.8% and -0.0%**, ranges overlapping, with an epoll-sized +16.3% excluded.
   So the +17.6% belongs to one of the other five commits and **which one is not established** - that is
   the one loose end this session leaves. The copy-count correlation in `RESULTS.md`'s matrix does not
   generalise to Windows, and this is now its second failed prediction (it also failed to explain why the
   bridge costs epoll twice what it costs io_uring).

6. **And item 2f's first option is DONE, so the flag is no longer required for most of the win.**
   Splitting the zero-copy segment cap from `MaxSendPages` gives **+61.1% at 256KB with no flag at all**,
   and with a same-session Kestrel control the tuned configuration is **-2.4% against vanilla Kestrel**,
   from -56.9% for the default bridge. It also costs p99 (15.3ms against 6.3ms) on the no-flag path, so it removes a
   silent cliff without replacing the recommendation. Details in item 2f.

**What that leaves open**, and it is now the top of the Windows list rather than the bottom:

- **Options 2/3 of item 2f.** The cliff moved rather than went: a 1MB response is 257 segments, a 4MB one
  1,025, so both still decline at a 256 cap - and the p99 result argues the PREFIX design would help tail
  latency as well as removing the cliff.
- **`--pipe-segment`'s memory bill is unmeasured on Windows.** On Linux it costs **2.7x RSS at 2048
  connections**, which is why it is a flag there. Nobody has measured that here, and a 117% result will
  tempt someone to default it. Measure before defaulting.
- **The Windows baseline re-measurement (§2) and where any of this sits against vanilla Kestrel** - see
  the results file; do not quote a Kestrel gap from a cross-day table.

*The original cold-start plan follows, unchanged.*

### 1. The one real risk: `OutboundConnection.Flush` changed, and IOCP/RIO inherit it

`dd8cdce` stopped `Flush` renting a snapshot and copying into it; it now hands over the accumulator's own
pooled array (`PooledBufferWriter.TakeArray`) and lets the writer re-rent. `EpollConnection` **and
`WindowsConnection`** both derive from `OutboundConnection`, so IOCP and RIO took this change sight-unseen.

Why it *should* be safe: the ownership contract is unchanged (the loop gets a pooled array it must return),
and both consumers slice by the `length` argument explicitly. The one behavioural difference is that **the
handed-over array can be much larger than `length`** (it grows by doubling) where the old snapshot was
`Rent(length)`. Anything inferring payload size from `data.Length` would break — nothing found in review
does, but that review was done on Linux.

**Do this first:** the smoke matrix on all three Windows backends — `--verify-echo`, `--verify`, `--churn`,
`--poke`, plaintext and TLS, IOCP + RIO (+ managed). If it passes, the change is good; if something
truncates or over-sends, this is the first suspect.

### 2. Then re-measure the Windows baseline — the copy removal should HELP Windows more than Linux

On Linux this was worth **+16.3%** at 256KB. The Windows path had *more* copies to start with
(accumulator → `Flush` snapshot → `StageOutbound` staging → write pages, i.e. 3-4), so removing one should
show at least as well. Compare against the 2026-07-27 numbers in `RESULTS.md`
(`iocp/s12` 4,483.4 and `rio/s12` 2,051.6 at 256KB). Use `Compare-Commits.ps1` for any before/after claim.

### 3. The highest-value Windows experiment, and it may need NO code change

IOCP's zero-copy send measured +3.5% at 16KB and nothing at 256KB, and 2026-07-29 found out why:
**a 256KB response through Kestrel's default ~4KB pipe blocks is exactly 65.00 segments**, measured, and
`IocpConnection.MaxSendPages` is **64**. So `TrySendZeroCopy` declined *every* 256KB response and silently
fell back to copying.

The demo now has `--pipe-segment <bytes>`, which sets the pipe block size. **At `--pipe-segment 65536` a
256KB response is 5.00 segments — comfortably under the 64 cap.** So the first experiment is a flag, not a
patch:

```
AspNetDemo --iocp --byo --pipe-segment 65536 --port 5080     # then bench/Run-TlsSizes.ps1 against it
```

*Pre-registered:* if the cap was the explanation, IOCP zero-copy should now engage at 256KB and gain
something like io_uring's **+45.1%**. If it engages and does NOT gain, the cap was a real decline but not
the cost, and `2b-result`'s "the bridge is structural" reading stands for IOCP. Either way, instrument
first: confirm the path is *taken* rather than assuming — on io_uring that meant a `zero-copy=` segment
counter, and IOCP has no equivalent yet, so add one or the run cannot be interpreted.

Only if the flag route works is the code change (raise `MaxSendPages`, or send a PREFIX and have
`TrySendZeroCopy` report bytes instead of a bool) worth making.

### 4. What is NEW on Windows since it last ran, in one list

- `OutboundConnection.Flush` hand-off (§1) — **untested on Windows**.
- `SocketSetOptions.ReceiveBufferSize` now honoured by epoll, io_uring and managed too. **IOCP/RIO
  behaviour is unchanged** — they always honoured it.
- `AspNetDemo --pipe-segment N` / `--pipe-pinned` (+ `PinnedBlockMemoryPool`), reported in `/config`.
  Cross-platform; on Linux the segment size is worth +6-8% at ≥16KB and costs **2.7x RSS at 2048
  connections**, so it is a knob, not a default.
- `SmokeTest --ktls-spike` gained `SS_KTLS_CLEAR_NO_RX` / `SS_KTLS_FORCE_TLS12`, and prints the loaded
  OpenSSL version. Linux-only in effect (Windows uses SChannel).
- `SmokeTest/StopSignals.cs` — SIGTERM shutdown. Unix-shaped; harmless on Windows.
- io_uring-only, so not a Windows concern but they explain the numbers: page chaining
  (`GetWriteSpan`), zero-copy send, the `zero-copy=` stat counter.

### 5. While you are there — and what NOT to spend Windows time on

**Worth doing on Windows, because it is Windows-shaped:** the page-size default (item 0). RIO wants a
64KB page badly (**4.68x at 256KB**, monotonic, no penalty at 512B) and IOCP is indifferent; the decision
has been blocked on *mechanism* rather than evidence for days — `BufferPageSize` is one global with a real
default of 4096, so there is no way to distinguish "user asked for 4096" from "user said nothing". Needs a
sentinel (0 = backend chooses) or a factory-supplied default, and that changes public option semantics.
Also unswept: anything **above** 64KB, where RIO was still improving at the top of the range.

**Do NOT spend Windows time on:**
- **kTLS / OpenSSL anything.** Windows uses SChannel; the entire kTLS thread (items 4, 4b, 3c) is
  Linux-only and does not apply.
- **Managed BYO send** (item 2e) — assessed and deliberately not built, for reasons that do not change
  by switching OS.
- **Re-deriving the Linux numbers.** They are in `RESULTS.md` with their caveats, and the two
  hosts/OSes must never be subtracted from each other.

**If you would rather stay on Linux instead of switching:** the honest alternative is zero-copy RECEIVE +
receive parking (order-of-work item 7) — the last structural term against vanilla Kestrel, which is
zero-copy in *both* directions while we still copy inbound. Much bigger job, and it does not retire the
correctness risk in §1.

### 6. Windows-specific traps that have already bitten

`bench/README.md` has the full list; the two that cost the most time were the **ephemeral-port gate**
(`Wait-Ports` in `Run-Matrix.ps1` — any harness opening thousands of connections per cell needs it, and
omitting it once produced a fake "208 dropped connections" defect) and a **pending Firewall dialog**
holding every leg to ~95k rps with no errors reported.

---

## START HERE (state as of 2026-07-28)

Orientation for picking this up cold.

**The agreed order of work — REVISED 2026-07-29 (end of day). If you are on Windows, read the section
above this one first; item 3 below is what you are here to do.**

1. ~~BYO-buffer phase 2, IOCP zero-copy (item 2b)~~ **DONE, and it under-delivered: +3.5% at 16KB and
   nothing elsewhere.** See `2b-result`. That is the single most useful negative result on this list.
2. ~~Write-pool exhaustion drops connections (item 0b)~~ **DONE**, with a wrong justification (the "208
   dropped connections" were my harness missing an ephemeral-port gate). The change is right anyway.
3. ~~Page-size defaults (item 0), blocked on the Linux sweep~~ ~~UNBLOCKED, blocked on MECHANISM~~
   **DONE on Windows 2026-07-29.** `BufferPageSize` and the three pool depths are now `0` = "backend
   chooses", filled from `SocketSetFactory.DefaultGeometry` once in the `SocketSet` constructor. Every
   backend keeps its exact previous geometry except **RIO**, which now asks for a 64KB page with a 4KB
   receive buffer — and that **fixes item 0d**: `rio+tls/verify-oob-4m` went from a 15.2s failure to
   passing in 0.2s. ~~**UNVERIFIED ON LINUX**~~ **VERIFIED ON LINUX 2026-07-31** — the geometry banner
   reads `page=4096 recvbuf=4096 writebufs=1024 oobwritebufs=256 readpages=256` on io_uring, epoll and
   managed (no `0`s, so no read site missed), and the smoke matrix passes; epoll/io_uring were indeed
   unchanged by the geometry mechanism, as constructed. (Separately, io_uring was CONSIDERED for a 64KB
   default like RIO's and DECLINED — see §3 item 2: its 0d-lookalike stall was a writev bug, not a page
   problem, and it is page-insensitive for throughput.)
4. ~~Item 1, the 64KB->256KB collapse~~ **ANSWERED: it is the bridge**, 2.0-2.4% at 64KB and 24.5-41.8% at
   256KB, with the instability the bridge's too. See item 1 for the isolation.
5. ~~The bridge's pipes are unconfigured (item 2d)~~ **MEASURED 2026-07-29.** Block size is worth
   **+7.5% at 256KB / +6.3% at 16KB** on io_uring (65.00 -> 5.00 iovec segments per response); pinning
   adds +0.7%, not separable. Costs **2.7x RSS at 2048 connections**, so both stay flags rather than
   defaults. `--pipe-segment` / `--pipe-pinned`, `bench/run-pipe-opts.sh`.
6. ~~Windows validation + the IOCP 65-segment experiment~~ **DONE 2026-07-29, and it is the largest win
   on this list: +117.3% at 256KB on IOCP.** The `Flush` hand-off is correctness-clean (47/48 cells), the
   65.00-segment decline is confirmed on Windows directly, and pipe block size alone is *not* the cause
   (overlapping ranges) - unlike Linux. **What it spawned, and both are now ahead of item 7:** item 2f
   (make zero-copy survive a fragmented sequence, so this is a default rather than a demo flag) and item
   0d (RIO+TLS out-of-band starvation, the one failing correctness cell).
7. ~~**Zero-copy RECEIVE + receive parking.** The last structural term against vanilla Kestrel~~
   **DEPRIORITISED 2026-07-31 — the premise was never measured, and it does not hold.** The inbound path
   had never been benchmarked at all (no rig POSTed a body until `Run-Upload.ps1`). Measured against a
   same-session Kestrel control, the inbound gap is **at most ~5%, and only disjoint at 4KB** — at 64KB
   and 1MB the ranges overlap outright. And `SS_BRIDGE_STATS` shows the second (`_staged`) copy is paid
   on **0.1% of receives / 0.011% of bytes** even at 1MB uploads, because flushes complete synchronously
   essentially always.
   **So neither half is a throughput item.** Zero-copy receive is chasing ≤5%, of which the copy is only
   a part; parking is chasing 0.011%. The ONLY surviving argument for parking is that inbound
   backpressure is **advisory** rather than real — a correctness gap, which should be scheduled as one.

   **CONFIRMED EMPIRICALLY 2026-07-31 by building the epoll springboard.** epoll is the cheap backend to
   test this on (readiness reads into pipe `GetMemory()` with no arm-ahead inversion). Built it
   (`PipeIoBridge.TryBeginReceive`/`CommitReceive`, epoll `PumpReceive`, new `/drain` endpoint,
   `SS_NO_ZC_RECV` A/B knob): it engages 100% (`zero-copy-recv=100.0%`, staged=0), byte-exact — **and a
   same-session ON-vs-OFF A/B at 256KB uploads shows NO difference** (~54.7k vs ~56.3k req/s, ranges fully
   overlapping). The inbound copy is rounding error against recv + pipe + Kestrel, exactly as estimated.
   **Triangulated across three workloads** after a challenge (was HTTP asymmetry / Kestrel overhead hiding
   it?): the Kestrel `/drain` upload, a BARE symmetric pipe echo (no Kestrel), and the discriminating PURE
   receive-flood (new `SmokeTest --sink` = a drain-only pipe server, flooded by a `--pipeline` client, so
   receiver ≫ sender and nothing dilutes the copy) — all three show ON/OFF overlapping (the flood at
   ~4,700 MiB/s inbound). The `recv()` kernel→user copy is unavoidable and dominates; the zero-copy path's
   own `GetMemory`+lock ~cancels the transport copy it removes. **So io_uring's much-harder zero-copy
   receive is NOT worth building** — the win it would chase does not exist here. (Kept the epoll
   implementation as it advances the both-directions-zero-copy goal; the backpressure/parking correctness
   gap is untouched — the MVP falls back to the copy path under a pending flush rather than parking. Standing
   caveats: real-NIC bandwidth saturation is invisible on loopback, and the callback path is already
   copy-free so this is moot for callback-based transports.)
8. **Dynamic shard growth.** Specified, untouched. Now the largest *unstarted* item.

**Deliberately deprioritised: kTLS multishot (items 4 / 4b), and a 2026-07-31 measurement weakened the
case further.** It is unblocked in principle (OpenSSL 3.2+ enables kTLS RX for TLS 1.3 — validated
2026-07-29), but: the system OpenSSL here is **3.0.13** (RX not offloaded, so multishot-over-plaintext
cannot even be attempted without a self-built OpenSSL), the work is substantial, and kTLS's actual payoff
(NIC offload) is *structurally invisible* on loopback. **AND the whole premise of items 4/4b — that
io_uring's kTLS cost is forfeiting multishot receive — was undercut by the item 3c measurement (2026-07-31):
epoll+ktls trails epoll+tls by ~9% while forfeiting NO multishot, so the kTLS small-message penalty is the
record path itself, and multishot-RX would recover at most the ~3% residual between io_uring's gap and
epoll's — inside the noise.** So items 4/4b now chase ~3%, need a self-built OpenSSL, and show nothing of
their real (NIC) value on loopback. Firmly a real-hardware session (item 5), not this box.

**Where the remaining performance is, on the evidence rather than on intuition.** Three independent
results now agree that the Kestrel bridge — not the transport, not copies, not allocation — is what costs
on the ASP.NET path: zero-copy send removed one copy for +3.5%; the bare-vs-bridged isolation puts the
bridge at 24-42% at 256KB while the bare transport does not decline at all; and the same ~42% appears on
Windows against tuned RIO. **The next lever is fewer pipes and thread hops, not fewer copies** — and the
strongest form of that is no bridge at all (Kestrel talking to the transport directly), which is out of
scope here. Anything aimed at this number should be justified against that.

### THE LINUX HOST IS NOW BARE METAL, AND THE FIRST JOB IS A BASELINE (state as of 2026-07-28)

**The environment changed underneath this plan.** Linux measurement moved off Docker-on-WSL2 onto bare
metal: Pop!_OS 24.04, kernel 7.0.11, the *same* Ryzen 9 7900X desktop as the Windows numbers. io_uring
selects natively with no seccomp workaround. Setup, tooling and the traps specific to this box are in
`bench/README.md` ("The Linux bench host") — read that before running anything; the short version is that
the CPU governor ships as `powersave`, the SMT split in the shell rigs was measuring server and client on
the same physical cores (fixed, `bench/cpu-split.sh`), and `perf` needs a non-obvious package.

**Consequence: there is no Linux baseline.** Every Linux figure on file predates *both* the host change
(2026-07-27) and the OS change (2026-07-28), and was taken in a container on a WSL2 kernel. The tables
below are kept for their reasoning, not their numbers. Nothing can be compared against them.

**The baseline is DONE (2026-07-28)** — see `RESULTS.md`, "Linux baseline on bare metal", which
is now THE Linux reference and the "before" for the catch-up work below. Four things from it that change
what is worth doing:

1. **The host is a usable instrument**: within-leg spreads 0.2-5.7% against 37.4% in the container. A 2%
   effect is detectable on Linux now, and was not before.
2. **SocketSet beats stock Kestrel on both plaintext (+5.6%) and TLS (+22%)**, separating on disjoint
   ranges. The container's "parity" verdict was a ceiling at one-eighth the rate.
3. **kTLS is 15-22% SLOWER than userspace TLS at small messages, with 3x the p99** - the expected shape
   (we pay the RX syscall-per-message cost and collect no TX benefit when crypto is rounding error), but
   now measured. Whether it crosses over at large payloads is the open question and the size sweep's job.
4. **A Linux shard default must not be copied from Windows.** `s8` beats `s12` on `iouring` plaintext and
   costs less latency everywhere; `s12` only wins on the TLS legs. Windows chose 12 as a core count, not
   as a measurement.

### What has NOT reached the Linux backends (verified by inspection 2026-07-28, not recalled)

| feature | IOCP | RIO | io_uring | epoll |
|---|---|---|---|---|
| kTLS | - | - | **yes** | **yes** (2026-07-31; TX offloaded, RX userspace < OpenSSL 3.2) |
| `ReceiveBufferSize` (send/recv split) | yes | yes | **yes** (2026-07-28) | **yes** (2026-07-28) |
| write-pool exhaustion: stage and retry | yes | yes | no | no |
| BYO-buffer zero-copy SEND | yes | n/a by design | **yes** (2026-07-30/31, prefix) | **yes** (2026-07-31, `writev`, prefix) |
| BYO-buffer zero-copy RECEIVE | **no** | **no** | **no** | **yes** (2026-07-31; measured to buy nothing — see item 7) |

Reading of that table:

- ~~**epoll has no kTLS path whatsoever**~~ **IMPLEMENTED 2026-07-31 (item 3c). `--epoll --ktls` now runs
  real kTLS** (TX kernel-offloaded, RX userspace on OpenSSL < 3.2), correctness-clean on the smoke matrix.
  The `~150-line readiness→SSL_read pump` estimate held (~130 lines). epoll being the backend where kTLS
  should look best — no multishot receive to forfeit — is now testable; the throughput comparison is the
  open follow-up (see item 3c).
- **kTLS is TX-only on the SYSTEM OpenSSL (3.0.13), and that is OpenSSL's limit, not ours** — measured
  2026-07-29: a self-built 3.5.7 gives RX=True on TLS 1.3 and moves `TlsRxSw` off zero (0 -> 8). On 3.0.13
  `TlsTxSw` moves and `TlsRxSw` stays at zero. Every kTLS number on file measures a
  half-offloaded path. That is what item 4 is about, and it now has direct evidence. **And per item 4b
  (2026-07-29) this is the ROOT of the "kTLS costs us multishot receive" story, not a side-effect of it:**
  multishot is unusable because RX is not offloaded, so OpenSSL must still see ciphertext and owns the
  reads. Turn RX on and multishot `IORING_OP_RECV` over the provided-buffer ring should return plaintext
  directly.
- **`ReceiveBufferSize` and stage-and-retry are Windows-only**, and both hypotheses about them have now
  been CHECKED rather than left as guesses (2026-07-28, by inspection):
  - *`ReceiveBufferSize`*: **epoll needs it, io_uring does not** — epoll's receive slab is per-SOCKET,
    exactly like Windows'. See item 0 step 2; this is what blocks the page default on Linux.
  - *Stage-and-retry*: **confirmed not needed on io_uring.** `IoUringConnection.EnsureRoom` falls back to a
    pinned-heap page when the pool is dry (`IoUringConnection.cs:226`) rather than closing, so pool depth
    costs allocation there, never connections. The Windows hazard genuinely does not exist on this path.
- **Zero-copy RECEIVE** (as of this 2026-07-28 note it existed on no backend; **epoll gained it 2026-07-31**
  — see item 7 — and measuring it confirmed the copy is not the constraint) **— the reason it was hard is
  the API shape, not the backlog.**
  `Connection` has a `TrySendZeroCopy` and no receive counterpart. Inbound always lands in the transport's
  own slab and `PipeIoBridge.OnReceived(ReadOnlySpan<byte>)` copies it into `pipe.Output.Write(data)` —
  on IOCP exactly as on epoll. Under backpressure it is worse than one copy: a `PipeWriter` permits only
  one outstanding flush, so a receive arriving during a pending flush is rented from `ArrayPool` and
  copied a SECOND time into `_staged`.

  Reversing it means asking the pipe for memory (`pipe.Output.GetMemory`) and pinning it *before* arming
  the receive, which inverts who owns the buffer at arm time and is why item 2b lists inbound zero-copy as
  needing receive-parking. Parking is also the only thing that would make inbound backpressure real rather
  than advisory — the same mechanism buys both, which is an argument for doing it as one piece of work.

  Note the asymmetry this creates between the two buffer models: on the **callback** path receive is
  already copy-free (the callback gets a span over transport-owned memory), so this cost is specific to
  the **pipe** path, i.e. specific to the ASP.NET-shaped caller. That is the reverse of the send side,
  where the pipe path is the one that got zero-copy first.

### Both buffer models are wanted, and they are not competing

Stated 2026-07-28, and worth recording because the file's history keeps re-deciding it: the BYO-buffer
work and the library-owned-buffer path are **both** the destination, for different callers.

- **Caller-supplied pipes** (`ctx.UsePipe`) fit ASP.NET Core exactly, because Kestrel's transport contract
  already is a pair of pipes. That is what phases 2a/2b are for.
- **Library-owned raw buffers** are what an echo server or a Redis-style client wants: it has no pipe, it
  wants the transport's memory handed to it, and the pipe model would only add a copy and a thread hop.

So the callback path is not a legacy path to be migrated off. Supporting both is the goal, and a change
that improves one at the expense of the other is a regression even if the benchmark it was aimed at moved.

### The page-size default (item 0) — still the blocked item, and here is the block

`BufferPageSize` is shared with io_uring and epoll and has been swept **only on Windows**. Everything else
about it is settled.

**1. Sweep page size on both Linux backends.** `bench/run-tls-sizes.sh` is the rig. Compare 4KB / 16KB /
64KB at 512B / 16KB / 256KB payloads, exactly as the Windows matrix in `RESULTS.md` does.

*Pre-registered expectation, so the result can falsify something:* **both should be roughly
page-INSENSITIVE**, unlike RIO. io_uring already dispatches one writev over an `OutChain` of segments —
the same scatter-gather shape that made IOCP page-insensitive — and epoll sends directly from the TLS
output buffer with no page copy at all. If either turns out page-SENSITIVE, that is a finding worth
chasing, not a tuning result. If both are insensitive, the shared default can move on the Windows
evidence alone, because Linux does not care.

**2. Check whether Linux needs the send/receive split. ANSWERED BY INSPECTION 2026-07-28: epoll needs it,
io_uring does not.** `SocketSetOptions.ReceiveBufferSize` is honoured only by IOCP and RIO. Both Linux
backends still take one size for everything (`EpollShard._bufSize`,
`IoUringShard._readPageSize`/`_writeBufSize` — all `options.BufferPageSize`).

- **io_uring is fine, as guessed.** Its read pool is per-SHARD — `ManagedBufferPool(entries:
  BufferPagesPerShard=256)` — so a 64KB page is ~16MB per shard.
- **epoll allocates the slab per-SOCKET, like Windows.** `EpollShard._recvBuffer` is
  `PinnedWriteBufferPool(_socketsPerShard, _bufSize)`, commented *"one per live connection"*, leased at
  accept and released at close: 4096 x 64KB = 256MB per shard at a 64KB page. **But the resident cost is
  workload-dependent, and that is the part worth knowing.**

**MEASURED 2026-07-28 (`bench/run-recv-slab.sh`), and my own prediction was falsified before it was
confirmed.** Predicted: RSS diverges as `connections x page`, ~123MB at 2048 connections. Measured at 2048
connections with small GETs: **2.1MB**. Wrong variable - resident memory is `connections x TOUCHED depth`,
and a ~100-byte GET touches ONE 4KB page of a 64KB buffer, so buffer size never enters. Re-run with 64KB
POST bodies at 512 connections, it lands exactly: **+30.8MB against 30.7MB predicted (0.2%)**, and
`--recv-buffer 4096` recovers all of it (78,952KB -> 48,288KB, against p4K's 48,176KB).

**The axis is NOT per-socket vs per-shard - both the old claim and my correction had that wrong.** The
Windows 3,163MB was measured at **`-c 64`**: 64 connections holding 3.0GB resident, so that slab is
resident whether touched or not, because RIO *registers* it (locked pages). epoll's is `calloc`'d and
faulted on touch. Same structure, different residency policy.

**Practical reading:** on epoll a big page is free for small-request workloads and costs
`connections x page` for large-request ones (uploads, proxies). Workload-dependent rather than
unconditional - weaker than "epoll has the Windows problem", stronger than "Linux does not have it".
`ReceiveBufferSize` makes it safe either way and both Linux backends now honour it. Full tables in
`RESULTS.md`.

**3. Pipe mode on Linux — CONFIRMED WORKING 2026-07-28, nothing to do.** `SmokeTest --verify-echo
1048576 --pipe -z 65536` round-trips byte-exact on **both** epoll and io_uring through the universal
fallback (`PipeIoBridge`), with `pipe=True` in the banner so the flag is known to have been honoured. The
64KB chunk size is the one that exposed the flush-concurrency fault on Windows, so this was the run worth
making. Zero-copy send remains IOCP-only; io_uring is the natural second driver, since its send is
already a writev over segments and pointing those segments at pinned pipe memory is a smaller change
there than it was on IOCP.

**4. While there, the standing Linux backlog:** item 1 (io_uring TLS large-payload, investigated but never
reproduced outside a container — and the container is now gone, so this is finally testable), item 2
(re-run the size sweep now the plaintext controls exist), item 4 (kTLS RECVMSG + cmsg receive arm, whose
premise is now confirmed by `TlsRxSw` staying at zero), item 5 (**mostly delivered by the host change** —
what remains is specifically a second machine with a real NIC, since loopback cannot show kTLS device
offload no matter how good the host is).

**Read `bench/README.md` first.** Nine confounders, and one of them was reproduced on 2026-07-28 by
someone who had already read it — see item 0b. Any harness opening thousands of connections per cell needs
the ephemeral-port gate from `Run-Matrix.ps1`.

**One correction worth carrying forward, because it was made twice.** "Copies are not the constraint"
(established by `fa97dd4`'s A/B and by page size moving RIO 4.68x without changing bytes copied) is TRUE,
and was wrongly used to de-prioritise BYO-buffer. BYO-buffer's target was never the per-byte copies in
isolation — it is the BRIDGE, which was 14-19% when measured against the untuned transport and is ~42%
against the tuned one. Both statements hold; they are about different things.

**The benchmark host changed on 2026-07-27** - from a laptop (16C/32T) to a desktop (Ryzen 9 7900X,
12C/24T, mains). Every Windows number recorded before that date is from the old machine and **cannot be
compared with anything measured since**. The current baseline is in `RESULTS.md` under the
2026-07-27 headings. Two practical consequences: shard sweeps run at **4/8/12** here (12 = the server
half's logical core count), not 4/8/16; and this host is roughly an order of magnitude more repeatable
than the old one, with per-leg spreads of 0.2-2.4% rather than up to 6%.

**Backends.** io_uring + epoll (Linux), IOCP + RIO (Windows), managed (portable fallback). All except
managed own one loop thread per shard; managed is callback-driven, which is why it alone needs a
per-connection TLS gate. TLS is wired into every backend: SChannel via raw SSPI on Windows, OpenSSL
(optionally kTLS) on Linux. ALPN works on both providers.

**Recently landed, in order:** SChannel TLS without SslStream; ALPN; TLS on IOCP/RIO; the epoll backend
(+ TLS); a fix that made io_uring work at all (multishot recv was submitted with a non-zero `len`, which
the kernel rejects with `-EINVAL`, so *every* recv failed silently); scatter-gather sends on IOCP.

**The one measured performance win:** IOCP sends were quantised to a single 4KB write page, so a 256KB
response left as 64 sequential `WSASend`s. Issuing one call with up to 64 `WSABUF`s gave **+133% at 16KB
and +162% at 256KB**, validated with `bench/Compare-Commits.ps1`. See `RESULTS.md`.

**Confirmed end to end (2026-07-27):** through the Kestrel bridge, `--page 65536 --recv-buffer 4096` takes
RIO from 2,023 to **6,348 MiB/s at 256KB (3.14x)** and 1,484 to **3,735 at 16KB (2.52x)**, closing the gap
to stock Kestrel from 82% to 43.6% and to within 5.8% at 16KB — and tuned RIO then beats tuned IOCP at both
sizes. No data-path code changed. It is still not the default: see items 0 and 0b for the pool-sizing and
connection-dropping caveats that have to be settled first.

**The biggest win now on the table: give RIO a bigger write page.** RIO trails IOCP 2.2-2.5x at >=16KB
because its send is quantised to one write page, and unlike IOCP it cannot scatter-gather (Windows caps
`maxSendDataBuffers` at 1 - attempted and refuted 2026-07-27). But page size alone recovers all of it and
more: at a 64KB page RIO goes **2,404 -> 10,969 MiB/s at 256KB (4.68x)** and **1,643 -> 4,449 at 16KB
(2.7x)**, with no penalty at 512B, and ends up faster than IOCP at *every* payload. Not yet a default
change - the blocker is pool sizing and the fact that pool exhaustion currently drops connections. See
items 0 and 0b.

**`fa97dd4` is now MEASURED and is the second-largest win in the project: +27.0% at 256KB**, +5.9% at
16KB, nothing at 512B (disjoint ranges; `Compare-Commits.ps1`, 2026-07-27). Pooling the out-of-band flush
snapshot matters because the old `ToArray()` allocated the whole response per flush, and past 85KB that
is a **Large Object Heap allocation on every response**.

Its pre-registered reading was "if it moves throughput, allocation was the cost; if it does not, copies
dominate". It moved throughput, so **allocation was the cost and per-byte copies are not the constraint**.
Note what that does and does not imply — see the correction at the top of this section: it rules out
chasing copies for their own sake, but not the caller-supplied-pipe work, whose target is the bridge.

**Before trusting any measurement, read `bench/README.md`.** It documents the eight confounders that each
produced clean-looking wrong numbers, and the noise floor (~6% between identical builds on this host).

**Direction of travel:** BYO-buffer, in measured steps — see 2a/2b. The API (`ctx.UsePipe`) and the
universal copying fallback landed 2026-07-27; what remains is the per-backend zero-copy path, IOCP first.
The end goal is that a caller supplies the memory (ideally pinned/registered) and we stop copying into it.
Accepting single-shot reads, or bypassing provided buffers, is an acceptable price for minimal copy. A
robust fallback is required for backends that cannot take foreign memory at all — RIO takes only
registered `BufferId`s, never addresses, which is exactly why the fallback is a permanent path and not a
stepping stone.

---

---

## Dynamic shard growth (MinShards -> MaxShards)

**IMPLEMENTED 2026-07-31 for the single-listener path (Windows IOCP/RIO, `ListenHandle`, AF_UNIX) and the
managed backend. Multi-bind / reuse-port (io_uring, epoll on IP) is NOT covered — see the gap below.
Default OFF: `MaxShards` 0 keeps a fixed shard count and the old behaviour exactly.**

`SocketSetOptions.MaxShards` is the growth cap, deliberately separate from `SocketSetFactory.MaxShards`
(a backend CAPABILITY cap — the managed backend wants exactly 1); both apply and the lower wins, verified
by the managed backend refusing to grow with `--max-shards 16`.

**Measured**, same tight table (`--sockets 8`, 4 shards) under 6s of churn on IOCP:

| | connections | capacity drops | shards grown |
|---|---:|---:|---:|
| growth off | 10,538 | **1,704** | 0 |
| growth on (cap 16) | **18,361** | **0** | 8 |

It moved **74% more connections** simply by not refusing them.

**Two things the survey got right and one it over-estimated.** `_shards` was already non-readonly and both
hot readers (`TryPlace`, `RoundRobin`) already snapshot it into a local — the copy-on-write-safe pattern —
so step 1 was nearly free rather than the work it was billed as. Teardown (step 4) also came free:
`Dispose` iterates the current array, so grown shards are stopped. Growth adds ONE shard per failed
placement rather than doubling, because each shard pins `SocketsPerShard` x the buffer sizes and there is
no shrink.

**REUSE-PORT PATH DONE 2026-07-31 (Linux, io_uring + epoll over IP), and it was TWO gaps, not one.** The
handover called listener replay "the only genuinely unfinished piece"; a reading of the accept path found
a second, and a rig (`bench/run-shard-growth.sh`, pure reuse-port server under client load, shard count
sampled from `/proc` worker-thread names — no server reporting code needed) proved both:

- **Gap A — a grown shard had no listener.** On reuse-port each shard binds its OWN listener and the kernel
  balances accepts; a shard grown after `Listen` had none, so the kernel never routed a single accept to
  it. Fixed: `SocketSet.Listen` records the multi-bind listens (under `_growLock`) and `TryGrow` replays
  them onto the new shard before publishing it. This alone made **epoll** grow end-to-end (2→12 under
  load), because epoll's `AcceptBurst` already routes every accept through `Parent.TryPlace` (which grows).
- **Gap B — io_uring never TRIGGERED growth on a pure server.** io_uring's reuse-port fast path adopts
  locally and, when its own slot table was full, **closed the accepted fd silently** — it never called
  `TryPlace`, so a pure server (accepts only, no connects) stayed pinned at its start count. Measured:
  io_uring `on` grew 0 shards until fixed, where epoll grew 10. Fixed: on a full local table, fall back to
  `Parent.TryPlace` + bounce (`EnqueueInbound`), mirroring the single-listener path — the bounce is only
  for the one connection that triggered the grow; subsequent accepts balance onto the grown shard's own
  replayed listener. The silent drop is now counted by `TryPlace` (`PlacementFailures`) too.

  After both: `run-shard-growth.sh` shows io_uring AND epoll grow 2→12 with growth on and hold at 2 with
  growth off; the smoke-matrix churn cells (which also exercise the accept path) stay clean on both.

*Original gaps entry follows.*

- ~~**Multi-bind listeners are not replayed** onto a grown shard~~ **DONE — see above (Gap A).**
- **Growth blocks the placing thread** while the new shard's startup gate is waited on (up to the 30s
  startup timeout). Acceptable for the accept path, which is already off the hot loop, but it is a
  serialisation point under a burst.
- **A shard being created concurrently with `Dispose`** can be missed, as the survey noted.
- **No shrink**, by design.

*Original entry follows.*


**Status: proposed, not started. Architecture surveyed 2026-07-27; the CURRENT failure behaviour finally
measured 2026-07-31, and it is two different failures rather than one.**

### What a full slot table does today (measured, not recalled)

The two callers of `TryPlace` fail differently, and one of them failed silently:

| path | behaviour when every shard is full |
|---|---|
| **Connect** | throws `InvalidOperationException`. Intended, but it killed an unguarded caller outright — `SmokeTest` filling its own table exits `0xE0434352`. |
| **Accept** | `closesocket` and **drop, with no callback, no log and no counter**. A server at capacity was indistinguishable from a healthy one. |

`SocketSet.PlacementFailures` now counts both, and SmokeTest prints it (including in the churn summary),
so the condition is at least visible: a tight table under churn reports **1,751 drops out of 10,570
connections**, where a roomy one reports 0. That number was previously invisible.

**This is the quantity growth would remove**, and the reason to build it is that it is non-zero under a
load you care about — not the architecture argument. It is still worth doing for the memory reason in the
next paragraph, but the trigger is now measurable rather than assumed.

*Original entry follows.*


Today `Options.Shards` is a fixed count materialised in the `SocketSet` constructor and never changed.
The intent is for it to become **MinShards**: start at a reasonable count, and when a connection cannot be
placed because every shard is full, spin up another (double-checked, synchronised) up to **MaxShards** and
place it there. Shards then ramp with load instead of being sized for the worst case up front.

**Why it matters beyond elasticity.** Per-shard memory is `SocketsPerShard x buffer sizes`, pre-allocated
and pinned. That product is what makes buffer sizing painful: at `SocketsPerShard` 4096 a 64KB receive
buffer costs 256MB *per shard* (measured 2026-07-27 - a 12-shard RIO server went 283MB -> 3,163MB
resident when the receive buffer followed a 64KB page). Growth lets `SocketsPerShard` be small, so each
shard's slabs are small and you only pay for shards that exist. It converts a worst-case allocation into
a demand-driven one, and it removes a hard failure: `TryPlace` currently returns null and the caller
drops or throws.

**The hook already exists** - `SocketSet.TryPlace` ends with
`return null; // every shard full (we may grow here later; for now the caller drops/throws)`. That is the
insertion point, and placement is already capacity-aware (it walks for a shard with room rather than
committing to one round-robin pick).

**Accept is NOT the obstacle it looks like, on Windows.** The concern is that accept only listens on
shards that existed at `Listen` time. That is true for exactly one of the two paths:

- *Multi-bind / reuse-port* (io_uring, IP): `Listen` binds a listener on EVERY shard and the kernel
  balances. A new shard needs the listen endpoints replayed onto it, so the set must remember what it was
  asked to listen on. This is the real work.
- *Single-listener* (Windows IOCP/RIO, `ListenHandle`, AF_UNIX): one shard owns the listener and bounces
  each accepted socket via `Parent.TryPlace()`, which re-reads `_shards` on every call
  (`IocpShard.cs:717`). **A new shard receives connections immediately with no listener changes.** So on
  the platform where the memory pressure actually bites, this part is free.

**What the work actually is:**

1. `_shards` becomes a swappable array (copy-on-write + `Volatile.Write`) so the hot readers - `TryPlace`,
   `RoundRobin` - stay lock-free and simply observe a longer array. Growth takes a lock; reads never do.
2. Starting a shard on a live set. The constructor's `CountdownEvent` startup gate assumes a fixed count
   and runs before anything is serving; growth needs an equivalent that does not stall the caller placing
   a connection. Note `UsesWorkerThreads` backends start a thread per shard and the managed backend
   initialises inline - two different paths.
3. Listen replay for the multi-bind path (remember endpoints + tokens; apply on new shards).
4. Teardown: `Dispose` must cover shards added after construction, including one being created
   concurrently with disposal.
5. Failure policy: if shard creation fails or `MaxShards` is reached, `TryPlace` still has to return null
   and the existing drop/throw path stands.
6. `MaxShards` currently exists on the FACTORY as a backend capability cap (the managed backend wants
   exactly 1) - a growth cap is a different thing and needs its own option; do not overload that name.

**Shrink is explicitly out of scope** unless asked for: idle shards holding pinned slabs is the mirror
problem, and reclaiming a shard means draining its connections first.

**Sequencing caveat, and it is the same one as everything else here:** this is a cross-cutting change
touching all four backends' startup/teardown. TLS was written twice, epoll made the send machinery a
third copy, `fa97dd4` paid an ownership rule out three times. Landing dynamic shards before the
IOCP/RIO factoring below means writing it N times too.

## TLS renegotiation requests

**AUDITED 2026-07-31, and the OpenSSL side is HARDENED. The decision was "reject, don't implement" for
renegotiation proper; KeyUpdate was already handled.**

What the audit found (OpenSSL backends, io_uring/epoll — probed with `openssl s_client` against a live
`SmokeTest -s --tls-ssl` server):
- **The default negotiates TLS 1.3** (`TLS_AES_256_GCM_SHA384`), which has NO renegotiation — its rekey
  mechanism is KeyUpdate, and that is already driven by the filter's `SSL_read` loop (post-handshake
  messages are pulled and any control-record reply is written back; see `OpenSslTlsFilter.ProcessInbound`).
  So on the default there was never a renegotiation-DoS exposure, and KeyUpdate needs no new code.
- **BUT TLS 1.2 is reachable** — there is no min-version pin, so a client offering only 1.2 negotiates
  `ECDHE-RSA-AES256-GCM-SHA384`, and the server advertised *"Secure Renegotiation IS supported"*. That is
  the live CVE-2011-1473 shape: a client forcing repeated expensive server handshakes.
- **kTLS** cannot renegotiate (keys are fixed in the kernel); it runs TLS 1.3 here anyway, so KeyUpdate is
  the only post-handshake event and OpenSSL services it.

Decision + change: **reject client-initiated renegotiation on the SERVER** by setting `SSL_OP_NO_RENEGOTIATION`
on the server OpenSSL context only (`OpenSslTlsProvider`). The CLIENT context deliberately does NOT set it:
that would refuse legitimate server-initiated renegotiation from servers we dial (a legacy TLS 1.2 pattern)
with no DoS upside for a client. Verified: a TLS 1.2 `R` (renegotiate) from s_client no longer
completes a second handshake and the server survives cleanly (no crash, no hang); TLS 1.3 and 1.2
handshakes + data are unaffected (full smoke matrix green, incl. TLS echo/verify on io_uring + epoll). The
flag does not touch TLS 1.3 KeyUpdate.

**DONE since the audit:**
- **Min-version floor, default TLS 1.3 (2026-07-31).** `OpenSslTlsProvider` gained a `minProtocol`
  parameter (`TlsProtocol.Tls13` by default; `CreateSelfSignedLoopback` too), setting
  `SSL_CTX_set_min_proto_version` on both contexts. This retires the whole TLS 1.2 surface on the default
  path — a TLS-1.2-only `s_client` is now rejected (`New, (NONE)`), TLS 1.3 works, echo byte-exact. 1.2 is
  opt-in (`SmokeTest --tls-min12`, verified to reconnect a 1.2 client). So the renegotiation flag is now
  belt-and-suspenders for the opt-in-1.2 case rather than load-bearing.

- **KeyUpdate is now TEST-verified (2026-07-31).** `bench/verify-tls-keyupdate.sh` drives a TLS 1.3
  KeyUpdate in BOTH directions mid-stream (`openssl s_client` `K` then `k`) against our echo server and
  confirms the echo keeps round-tripping byte-exact across the rekey, server clean — PASS on io_uring and
  epoll. (Not a SmokeTest matrix cell: it needs the external `openssl` binary. kTLS is out of scope — the
  kernel owns TX keys, so it can't do a userspace TX KeyUpdate.)

**~~Still open (smaller)~~ — SChannel parity DONE 2026-08-01, and the TLS backlog is now EMPTY on both
providers.** `SChannelTlsProvider` takes a `minProtocol` (defaulting to `TlsProtocol.Tls13`, mirroring
OpenSSL) which selects `SP_PROT_DISABLE_BELOW_TLS1_3`; `SChannelTlsFilter` refuses client-initiated TLS 1.2
renegotiation on the SERVER only, keeping the same deliberate asymmetry as OpenSSL (a client still accepts
server-initiated renegotiation — legitimate legacy 1.2 behaviour with no DoS upside to refusing).
`SmokeTest --tls-schannel` honours the existing `--tls-min12` opt-out.

Two things worth keeping:

- **The refusal is gated on the NEGOTIATED protocol, not on the configured floor.** They differ: at an
  opt-in 1.2 floor a connection may still land on 1.3, and treating its NewSessionTicket as an attack would
  break it. `SEC_I_RENEGOTIATE` is the ROUTINE TLS 1.3 path (NewSessionTicket, KeyUpdate), so getting this
  wrong breaks every TLS 1.3 connection at the first post-handshake message rather than failing visibly.
  Queried via `SECPKG_ATTR_CONNECTION_INFO`, and **fails closed** (query failure ⇒ "not 1.3" ⇒ refuse).
- **Both halves were verified against a control, and the renegotiation one changed my reading of the
  original entry.** The floor: `bench/Verify-TlsFloor.ps1` (new, 12 cells) — a TLS1.2-only client is
  REFUSED at the default floor and ACCEPTED under `--tls-min12` (reporting `Tls12`), with TLS1.3-only
  connecting in both. The `--tls-min12` leg is the control that proves the probe can see a 1.2 handshake at
  all; without it, a floor that refused *everything* would look identical. The renegotiation: driven with
  `openssl s_client -tls1_2 -state`, feeding `R`, same session, build with and without the change —

  | | after `RENEGOTIATING` |
  |---|---|
  | control (no refusal) | ServerHello → Certificate → **ServerKeyExchange** → ServerDone |
  | with refusal | connection closed, **no ServerHello** |

  So the pre-change server really did perform the full asymmetric handshake on demand — the entry above
  said it "*accepts* renegotiation", and that is now confirmed by observation rather than by reading the
  code. (Needs the external `openssl` binary, like the Linux KeyUpdate check, so it is a documented manual
  procedure rather than a SmokeTest matrix cell. `Verify-TlsFloor.ps1` IS scripted, and it is the more
  important of the two: at the default 1.3 floor renegotiation is not reachable at all.)

**One consequence to state plainly, because it is a real behaviour change and not just a hardening:** the
default now REFUSES any peer that cannot do TLS 1.3, and SChannel only speaks 1.3 on Windows 11 /
Server 2022 and later. On an older Windows the default disables every protocol the OS has and the handshake
fails outright rather than quietly settling on 1.2. That is the intended shape — a silent downgrade is the
failure mode a floor exists to prevent — but it is why the failure is loud, and `--tls-min12` /
`minProtocol: TlsProtocol.Tls12` is the opt-out. Pre-alpha, no back-compat obligation, so this is a default
change rather than a breaking one.

## Package `SocketSet.AspNetCore` as a consumable library (proposed 2026-08-01)

**Status: DONE, RUNTIME-VERIFIED ON WINDOWS, and MERGED TO MAIN (2026-08-01).** The runtime gap below is
closed: `bench/Verify-AspNet.ps1` (new) runs 18 cells — {iocp,rio,managed} x {byo,classic,half-pipe} x
{plaintext,SChannel TLS} — gating the `/config` banner (backend + mode + TLS named, geometry with no `0`),
byte-exact `/payload` at 13 sizes 1B-8MB, byte-exact POST `/echo` at 1B/4KB/1MB, and `/stats` accepts>0 /
writeFail==0. **18/18 PASS — and the same rig was run on main FIRST, with the two runs IDENTICAL on all 18
cells (same Result and same Detail: accepts, sendFalse, full geometry string, zero differences).** So the
extraction is measured as behaviour-PRESERVING, not merely working; a refactor that worked but shifted the
resolved geometry or moved a counter would pass a one-sided check and fail that one.

**The one honest gap: this is verified on Windows (IOCP/RIO/managed), not on Linux.** The extraction was
WRITTEN on Linux against io_uring/epoll, so its OS-independence is now evidenced from the opposite side —
but the Linux backends' 60/60 was against the PRE-extraction demo. **Next Linux session: port
`Verify-AspNet.ps1`'s cells to io_uring/epoll and run them.** That is a small job and it is the last thing
standing between this and "verified everywhere it runs".

*Original status follows.* The
extraction is complete: new project `src/SocketSet.AspNetCore/` holds the transport (`SocketSetConnection`,
`SocketSetTransport`/`Listener`, `HalfPipeWriter`, `PinnedBlockMemoryPool`, `ITransportTlsFeature` [now
public]) + a public `UseSocketSet(o => ...)` extension, `SocketSetTransportOptions` (with a
`SocketSetBridgeMode` enum: Classic/Byo/HalfPipe), and `SocketSetTransportMetrics` (a DI singleton with
`Interlocked` counters + `ResolvedGeometry` — the static `/stats` counters are GONE, as the plan required).
`AspNetDemo` now only maps its flags via `DemoConfig.ApplyTo(options, cert)` and drives the library. Library
+ demo + SmokeTest all build 0/0; `/config` gating strings are preserved (`DemoConfig.Describe()` unchanged,
geometry now from `metrics.ResolvedGeometry`); no rig gates on the console banner. README.md written for
external consumers. **Still needed before merge to main: a RUNTIME smoke** (the box could not start servers
this session) — confirm `/config`, a byte-exact `/payload`, and `/stats` counters work end-to-end on
io_uring/epoll, plaintext + TLS. Only then merge. (Original plan + design calls kept below for reference.)

---

**Original plan (2026-08-01):** The AspNet bridge currently lives entirely in `AspNetDemo/` — a demo
project — so the reusable part cannot be consumed by anyone else. Extract it into a real library project
`SocketSet.AspNetCore` with its own `README.md` written for a hypothetical external consumer, and reduce
`AspNetDemo` to what a demo actually is: **arg parsing, config, endpoints, and the banner** — driving the
library rather than *being* it.

**The split (what moves vs what stays):**
- **MOVE to `SocketSet.AspNetCore`** (the reusable transport bridge): `SocketSetConnection`,
  `SocketSetTransport` + `SocketSetConnectionListener` (the `IConnectionListenerFactory`/`IConnectionListener`),
  `HalfPipeWriter`, `PinnedBlockMemoryPool`, `ITransportTlsFeature`/`TransportTlsFeature`. Add a public
  registration extension — `builder.WebHost.UseSocketSet(options => ...)` (or an `IServiceCollection` one)
  — that wires the factory into Kestrel. That extension IS the public API; everything above stays internal.
- **STAY in `AspNetDemo`**: `DemoConfig` (the CLI A/B matrix — demo-only), `Program.cs` (endpoints, `/config`
  `/stats`, banner), the `--kestrel` vanilla control leg. `DemoConfig` MAPS its parsed args into the
  library's options type; it does not define the transport.

**Design calls to make (pre-registered concerns):**
1. **A real options type.** Introduce `SocketSetTransportOptions` in the library (backend/factory, TLS
   provider, shards, pin, page/recv/write sizes, pinned pool, bridge mode). It must be SEPARATE from
   `DemoConfig` (which is the demo's arg matrix, not a public API). The demo builds options from its flags.
2. **Bridge mode as an enum, not env vars.** Expose `classic | byo | half-pipe` as an option value. The
   experiment knobs (`SS_PIPE_SCHED`, `SS_HALF_DRAIN`) should NOT be public API — keep them as internal/env
   experiment toggles or drop them from the packaged surface.
3. **The static counters must go.** `SocketSetConnectionListener.Accepts/Closes/SendFalse/...` are `static`
   mutable fields feeding the demo's `/stats`. A library cannot ship process-global mutable counters —
   make them per-listener instance state (and expose via a metrics/`EventSource` or an options callback the
   demo reads). This is the one non-mechanical part of the move.
4. **Package metadata.** `net10.0`, `PackageId=SocketSet.AspNetCore`, framework-reference ASP.NET Core,
   ProjectReference the SocketSet core (+ the vendored `RESPite` for the half-pipe — decide whether to
   vendor into this package or keep the dependency explicit). Fill in description/authors/license.
5. **README for an outsider.** How to add the transport to a minimal Kestrel app, the options, the TLS
   story (transport-terminated OpenSSL/SChannel + kTLS), the bridge-mode tradeoffs (BYO for large, half-pipe
   for small-mid — link RESULTS.md numbers), and the platform matrix (io_uring/epoll/IOCP/RIO/managed).

**Done when:** `AspNetDemo` builds against `SocketSet.AspNetCore` with no transport implementation of its
own, the smoke/bench rigs still pass (they gate on `/config` — unchanged), and the library builds + packs
standalone. Nothing here needs a measurement; it is a refactor. Keep the `--kestrel` control working.

## Two half-pipes: replace the Kestrel bridge's two full `Pipe`s (proposed 2026-07-31)

**Status: OUTBOUND HALF BUILT AND CORRECT (2026-08-01, branch `cyclebuffer-halfpipe`). Inbound half not
started. A/B on the concurrency sweep is the next step — the hypothesis below is not yet measured.**

**Progress 2026-08-01 (branch `cyclebuffer-halfpipe`, NOT merged to main):**
- Vendored StackExchange.Redis `CycleBuffer` (segmented pooled producer/consumer buffer, MIT) under
  `vendor/`. Isolation micro-bench `experiments/BufferBench`: the write→commit→consume→discard CYCLE is
  **2.2–3.5× cheaper than `Pipe`** single-threaded, and **1.15–1.77× cheaper cross-thread** with a
  lock+condvar SPSC wrapper and 256KB backpressure (both zero-alloc). So the machinery win survives paying
  for coordination. Cross-thread is a conservative floor for the integration — see next.
- Built the **outbound half-pipe** (`--half-pipe`): `AspNetDemo/HalfPipeWriter.cs`, a `PipeWriter` Kestrel
  writes to that is backed directly by a `CycleBuffer` and **drains itself to `Connection.Send` on
  Kestrel's own flush thread**. No outbound `Pipe`, no pump `Task`, no ThreadPool hop, no async read loop.
  Key realization that made it lock-free: `Connection.Send` COPIES synchronously, so producer
  (GetMemory/Advance) and consumer (FlushAsync drain) are BOTH the single Kestrel write thread — the
  CycleBuffer is never touched cross-thread. So the SINGLE-thread bench numbers apply, not the cross-thread
  ones, AND the ThreadPool hop is deleted. Trades zero-copy send for a copy, so it targets small/mid +
  concurrency, NOT 256KB. Inbound stays a stock `Pipe`. Mutually exclusive with BYO. Banner: `half-pipe=1`.
- **Correctness gate PASSED** (byte-exact HTTP, io_uring AND epoll): `/payload?n` from 1B to 8MB exact;
  POST echo/drain at 500KB exact. (SmokeTest covers the raw transport, not the bridge — these HTTP checks
  ARE the half-pipe's gate.)
- Found + fixed TWO bugs from Kestrel's `PipeWriter` usage, both live in `HalfPipeWriter`: (1) it reads
  `PipeWriter.UnflushedBytes` (Http1OutputProducer + System.Text.Json) → must implement it
  (`_cb.GetCommittedLength()`), not leave it throwing; (2) Kestrel does `GetMemory` ONCE then `Advance`
  headers, then writes the BODY into the same retained buffer and `Advance`s again — a second Advance with
  no intervening GetMemory. This is an `IBufferWriter` contract violation (isolated repro:
  `experiments/KestrelPipeWriterRepro`, to be filed upstream). CycleBuffer's `Commit` (correctly) assumed
  a fresh lease and relocated bytes, corrupting the body; fixed defensively by re-leasing at the current
  end before each `Commit` since Kestrel writes contiguously.

**A/B DONE (2026-08-01, `bench/run-halfpipe.sh`, io_uring, 1 KB, RESULTS.md has the table):** half-pipe
wins throughput at every concurrency — +5.5/+3.2/+3.8% over classic at c64/c128/c256 (range-clean at
c64/c128, overlapping at c256), and +5.6–7.0% over BYO (zero-copy send isn't worth its overhead at 1 KB).
BUT the pre-registered pump-contention hypothesis (#1) is **NOT supported**: the lead is roughly FLAT with
concurrency, not growing — so the win is a per-request machinery saving (cheaper CycleBuffer cycle + no
pump task + no hop), not high-c contention relief. And it costs p99 (+16/+27/+56% at c64/128/256), because
the drain+Send runs on the Kestrel request thread. **Falsifier fired for the mechanism; the flat win is
the finding.**

**MERGE-READY as a runtime toggle (2026-08-01).** `--half-pipe` is off by default (`HalfPipe` defaults
false), mutually exclusive with BYO/classic, and the branch touches **zero `src/SocketSet` code** — the
transport core and every Windows backend are untouched. So squashing to main is additive and off-by-default;
it cannot affect the default or Windows paths. The only cost to main is a new opt-in flag + the vendored
`CycleBuffer` (`vendor/`, MIT). Windows caveat: `HalfPipeWriter` uses only `Connection.Send` (cross-platform),
so it SHOULD work on IOCP/RIO, but that is UNTESTED — Linux-only verified so far.

**Workaround for the Kestrel bug is IN (not blocked on upstream).** The `Advance`-relocation corruption is
[[aspnetcore-issue-68148]] (filed; not expected to land before .NET 12). `HalfPipeWriter.Advance` re-leases
before each `Commit`, which sidesteps it entirely — so the half-pipe does not wait on a framework fix.

**Crossover MEASURED (2026-08-01, `bench/run-halfpipe.sh` size sweep, RESULTS.md):** half-pipe wins
256 B–16 KB (+3–8.5%, range-clean), wash at 64 KB, and at 256 KB **BYO's zero-copy retakes decisively**
(half-pipe −36% vs BYO; the send-copy finally costs). So half-pipe is the small-to-mid path, BYO the large
path — both toggles, pick per workload. **Alloc MEASURED (`bench/run-halfpipe-alloc.sh`):** WASH vs classic
(gen0 193 vs 192, ~1350 B/req both) — so the win is CPU/scheduling, NOT GC; the "leaner allocations" framing
was wrong (corrected in RESULTS.md).

**FOLLOW-UP WORK STARTED (2026-08-01, branch `halfpipe-followups`, pushed, NOT merged):**
- **(a) p99 experiment — BUILT, UNVERIFIED.** `SS_HALF_DRAIN=pool` (banner `drain=inline|pool`): moves the
  Send off the Kestrel request thread — copy committed bytes to a pooled array + discard on the request
  thread (CycleBuffer stays lock-free), then Send on the ThreadPool, chained for order. Diagnostic for "is
  request-thread Send the p99 culprit?". Compiles 0/0, inline path unchanged, but **NOT byte-exact-verified
  and NOT A/B'd** — the box stopped launching AspNetDemo servers mid-session (see below). Verify + A/B
  before trusting.
- **(b) TLS crossover — knob added, UNRUN.** `run-halfpipe.sh` now takes `TLS=ssl`. Could not run: TLS
  server startup failed in that session (the pre-existing BYO+TLS path failed identically — environmental,
  not a code regression).
- **ENVIRONMENT NOTE:** after ~40 server start/kill cycles the box could no longer start AspNetDemo servers
  at all (even the proven plaintext form); `dotnet build` still worked. A fresh session clears it. Both (a)
  and (b) just need re-running there. Also: env-prefixed `bash run-*.sh` invocations were dropped by the
  harness intermittently — run the rigs plainly or `export` first.
- **(e-answer, 2026-08-04) Upstream asked on #68148: "is the Advance bug specific to chunked
  encoding?" — NO, verified against the source in the clone.** The violation is
  `BufferWriter<T>.Commit()` retaining `_span` across the transport `Advance`; it is reachable from
  (a) the **Content-Length** arm of `WriteDataWrittenBeforeHeaders` (`writer.Write(segment.Span);
  writer.Commit();` on the writer that just committed the headers — the ordinary buffered `Results.Bytes`
  shape, which is what the filed repro hits, no chunking involved) and (b) the chunked arm via
  `CommitChunkInternal` (same mechanism, also exposed). Chunked is sufficient but not necessary. Marc POSTED the
  answer on #68148 (2026-08-04). Context worth keeping: the question came from upstream maintenance
  ("we know that type of bug exists there, just hasn't been prioritized since no-one was replacing the
  backing PipeWriter... I think I have a branch with a possible fix somewhere"), so (a) the bug class
  was already suspected upstream and our repro is the first consumer to be bitten, and (b) THEY may have
  a fix branch; if theirs lands, our spike branch (fix/bufferwriter-advance-contract on the fork)
  becomes reference material rather than a PR. Watch the issue for their branch.
- **(e) aspnetcore fix — SPIKED.** [[aspnetcore-issue-68148]] root-caused to `BufferWriter<T>.Commit()`
  retaining `_span` (src/Shared/ServerInfrastructure/BufferWriter.cs) + `Http1OutputProducer` reusing the
  writer for headers-then-body. UNTESTED one-line candidate on branch `fix/bufferwriter-advance-contract`,
  pushed to `github.com/mgravell/aspnetcore` (clone at `~/code/aspnetcore`); `SPIKE-68148-NOTES.md` there.

**STILL TODO:** (c) Windows (IOCP/RIO) — should work via `Connection.Send`, untested. (d) the harder
inbound `PipeReader` half (real backpressure). Plus finish (a)/(b)/(e) above in a fresh environment.

---

**Original proposal (2026-07-31), for the full design + pre-registered evaluation:**

Today `SocketSetConnection` builds TWO full `System.IO.Pipelines.Pipe`s (`_inbound`, `_outbound`), each
with the complete reader+writer state machine, scheduler-dispatched continuations, and the
one-outstanding-flush constraint. Kestrel only ever touches ONE half of each; OUR side pays for the other
half it doesn't need. The idea: expose the REAL half Kestrel requires and go DIRECT on our side, because
our side is a loop thread that is already running — it can peek/push the shared buffer without the async
reader/writer machinery.

- **Outbound (app→socket):** Kestrel is the producer → expose a real `PipeWriter`. We are the consumer →
  the loop thread drains the buffer directly. **No `PipeReader`, and no per-connection `Task.Run` pump.**
- **Inbound (socket→app):** Kestrel is the consumer → expose a real `PipeReader`. We are the producer →
  the loop `recv`s straight into the buffer and publishes. No `PipeWriter`.

**What it targets (mapped to measured facts, not hope):**
1. **Eliminates the per-connection pump `Task`** — the leading suspect for the one thing measured but
   unexplained this session, now firmed up with a 3-pass ranged sweep (2026-08-01, RESULTS "Concurrency"):
   at c64 we LEAD (epoll disjoint above Kestrel), but by c128 Kestrel pulls ahead of both DISJOINT (+2-6%)
   and holds it at c256 — we degrade more under load. And **epoll degrades LESS than io_uring** (io_uring's
   single-issuer ring plausibly contends worse when many sends pile up). N ThreadPool pump tasks contending
   is the plausible cause; removing them is the clean test. **Strongest bet.**
2. **Real backpressure** — the custom inbound `PipeReader` lets the loop PARK `EPOLLIN` (stop reading the
   socket) when Kestrel falls behind, closing the "inbound backpressure is only advisory" correctness gap
   and removing the staged second copy (the one-flush constraint).
3. **Native zero-copy both ways** — loop `recv`s into the reader's buffer, sends from the writer's buffer;
   deletes the `TryBeginReceive`/`CommitReceive` dance + fallback.
4. **Less machinery / allocation** — one shared buffer + a poke per half vs two full `Pipe` state machines;
   should show on the small-message / GC / RSS axes (the regimes NOT stressed yet).

**Calibrate the expectation.** Do NOT expect it to move 256KB / c64 — we are already ≥ Kestrel there once
pools match, and the pump-hop was PROVEN not to be the bottleneck at that point (per-segment pinning was,
now fixed). Note the `SS_PIPE_SCHED=inline` result (−28% on io_uring) is NOT a refutation: inline ran the
pump CONTINUATION on Kestrel's thread and still marshaled; the half-pipe REMOVES the pump so the
already-running loop thread drains on its own timeline — a different mechanism. Bank the wins at
**concurrency / small-message / allocation-RSS / backpressure-correctness**, not the headline.

**The real cost is CORRECTNESS, not throughput.** Hand-rolling `PipeReader`/`PipeWriter` is the fiddly,
hot-path-dangerous part — `AdvanceTo`'s consumed-vs-examined, backpressure thresholds, `Complete` /
`CancelPendingRead`, the `FlushResult.IsCompleted/IsCanceled` edges. The current code uses stock `Pipe`
precisely to dodge all of that. **De-risk by doing the OUTBOUND half first** (a custom `PipeWriter` whose
`FlushAsync` just pokes the loop — no async reader on our side, the simpler half), prove the
pump-elimination + high-concurrency hypothesis, THEN do the inbound `PipeReader` (harder, but where real
backpressure lives).

**Evaluation, pre-registered** (against the current two-full-`Pipe` baseline, rigs already exist —
`SmokeTest --sink`, the demo, the concurrency sweep, `SS_BRIDGE_STATS`): (a) high-concurrency rps
(c128/c256) closes toward Kestrel; (b) small-message rps up; (c) GC gen-0 / allocations down; (d)
backpressure becomes real (staged-copy counter stays 0 AND recv parks under a slow reader). **If c256 does
NOT improve, the pump-task-contention hypothesis was wrong — and that is itself the finding.**

**Possible backing store:** the author has the core of a "ROS chunk allocator" — a `Pipe`-like
producer/consumer buffer with better semantics (multiple in-flight / real backpressure / pinned chunks
without the pinned-pool's ~2.7x RSS). Not on hand yet. The half-pipe is the natural place to slot it: our
direct side can use its native interface while the Kestrel side still sees a standard reader/writer. Worth
A/B-ing the allocator against stock `Pipe` on the same four axes when it lands.

## Factor the shared IOCP/RIO data path

**Status: PARTLY DONE (2026-07-27), and the remaining scope is much smaller than this entry assumed.**

Done:

- `WindowsConnection` + `IWindowsShard` - identity, teardown state, send serialization and the
  Close/IsClosed/SubmitOutbound trio, written once. (This entry claimed `WindowsOutboundConnection`
  already existed as a shared base; it did not.)
- `WindowsShardBase<TConn>` - the whole TLS block, outbound staging, the out-of-band flush pump, slot
  release, and the options/slot-table/pool/TLS-scratch fields both shards declared identically.

Net so far: the two shards went from 1331 + 1134 lines to 1154 + 957, against 269 lines of shared base -
roughly 350 lines of duplication removed, plus ~150 from the connection types.

**The generic-struct ops parameter is not needed.** The base asks for only `CloseClient` and
`StartPendingSend`, both per-EVENT; the per-operation primitives (issue-a-send, arm-a-receive,
drain-completions) never route through the base, so there is no virtual call on the hot path to
devirtualise. `TConn` is generic purely so the slot table stays typed.

**What must NOT be unified: the send state machine.** `CompleteWrite` and `StartPendingSend` differ by 39
and 25 lines because IOCP has a send page array and RIO cannot have one - Windows caps
`maxSendDataBuffers` at 1. This entry was written when the two were near-identical; they diverged when
IOCP got scatter-gather. Forcing them back together would mean giving RIO a shape the OS refuses.

**What is left**, all in the control plane, and all *near*-identical rather than identical - so each wants
reading before moving, not a mechanical lift:

| method | lines differing |
|---|---|
| `BeginTls`, `FailSend`, `SendResponse` | 2-3 |
| `SubmitSendBuffer`, `HandleSend` | 5-8 |
| `CloseClient`, `DrainCrossThread`, `TryFinalize` | 9-10 |
| `Poke` | 12 |
| listen/accept/connect (`Listen`, `ListenHandle`, `StartAccept`, `PostAccept`, `HandleAccept`, `StartConnect`, `HandleConnect`, `AdoptAccepted`) | not yet diffed |

The differences are mostly RIO's extra teardown bookkeeping (`Rq`, `Commit*`) and IOCP's inline-completion
path (`SkipOnSuccess`, `QueueInline`). Worth doing, but the easy 500 lines are already gone and what
remains needs judgement per method.

---

*Original analysis follows.*

**Status:** proposed, not started.

`IocpShard` and `WindowsRioShard` have evolved in parallel and are now largely the same code. Measured
2026-07-26: normalise the two type names and **646 lines are byte-identical** (~57% of the RIO file,
which is 1126 lines to IOCP's 1263); 38 methods share a name across both. Some are already
character-for-character identical (`QueueCipher`, `SendEncrypted`, `FireTlsOpen`, `DriveTlsHandshake`,
`StartPendingSend`).

The genuine differences cluster in only two places:

- the I/O primitive — `WSASend`/`WSARecv` + per-op `OVERLAPPED` vs `RIOSend`/`RIOReceive` + request
  queue, completion queue, and deferred-commit batching;
- teardown bookkeeping — RIO additionally carries `Rq`, `CommitPending`, `CommitRecv`, `CommitSend`.

**Why it matters:** every feature is currently written twice. TLS interception was (2026-07-25), and the
epoll backend (below) landed 2026-07-26 as a third copy of the send machinery — the thing this entry
predicted, now realised rather than hypothetical. `fa97dd4` then paid it again: one ownership rule,
written out three times in `IocpShard`, `WindowsRioShard` and `EpollShard`. That is the argument — not
tidiness.

**Shape:** a shared `WindowsShardBase<TConn>` owning slot lifecycle (`InitClient`/`FreeSlot`/
`CloseClient`/`TryFinalize`), cross-thread marshalling (`EnqueueInbound`/`SubmitClose`/`SubmitFlush`/
`DrainCrossThread`/`Poke`), listen + connect (`Listen`/`ListenHandle`/`Connect`/`StartConnect`/
`CreateListener`/`StartAccept`/`PostAccept`/`HandleAccept`), the send state machine (`Pending`,
`StageOutbound`, `StartPendingSend`, `CompleteWrite`, `SubmitSendBuffer`, `FailSend`, `HandleSend`), the
whole TLS block, and adoption (`AdoptAccepted`/`HandleConnect`). Each backend keeps only: arm-a-receive,
issue-a-send, the completion pump (`OnRun`), its setup/teardown, and RIO's commit-batching hook.

Half the pattern already exists: `WindowsOutboundConnection` is the shared base for the connection
types, and `IocpConnection`/`RioConnection` already declare identical `Socket`/`Generation`/`Closing`/
`RecvArmed`/`RecvBuf`/`SendBusy`/`SendBuf`/`SendSent`/`SendTotal`/`Pending`/`IsClient` fields. Those move
down; `SkipOnSuccess` stays on IOCP, `Rq`/`Commit*` on RIO.

**Decide up front:**

1. *Dispatch cost.* A plain abstract base puts a virtual call on `IssueSend`/`ArmRecv` — per operation,
   on the hottest path. Prefer a generic-struct ops parameter (`where TOps : struct, IShardOps`) so the
   JIT devirtualises.
2. *Test story.* No unit tests; this is the hottest code in the repo. The smoke matrix is now good enough
   to be a before/after gate — `--verify-echo`, `--verify`, `--churn`, `--poke`, plaintext and TLS — but
   it must be run across all three Windows backends on both sides of the change.

---

## epoll backend for Linux

**Status: DONE (2026-07-26)**, in `b1f4286`. `src/SocketSet/Epoll`, with TLS. Passes the smoke matrix —
echo, byte-exact verify, out-of-band send, poke, churn, AF_UNIX, ALPN. Measured at parity with io_uring
and stock Kestrel on small-message plaintext (102.5k vs 105.2k vs 102.8k rps, ~3% spread). What remains
is the real-hardware measurement described below.

**It landed BEFORE the IOCP/RIO factoring, against the sequencing plan in this entry.** The plan was to
factor first so epoll would land on a shared base; instead it is a fourth independent copy of the send
machinery, which is now an argument *for* that factoring rather than a cost avoided by it. `fa97dd4` is
the evidence: a four-line ownership change had to be written out separately in `IocpShard`,
`WindowsRioShard` and `EpollShard`.

The rest of this entry is the original rationale, kept because the *why* still holds and the cost
estimate is worth checking against what it actually took.

**Not for throughput.** Linux already falls back to `SocketSetFactory.Managed`, and .NET's managed socket
async path *is* epoll (`SocketAsyncEngine`). So this is not "add epoll", it is "replace .NET's epoll
engine with our own" — unlikely to win much on syscall efficiency, and it will never beat io_uring where
io_uring is available.

**The actual argument — threading-model uniformity.** `ManagedSocketShard` reports
`UsesWorkerThreads = false`: no loop thread, completions land on arbitrary thread-pool threads. That is
why it alone needs `conn.TlsGate`, a coarse per-connection lock held across encrypt→enqueue, and why it
cannot share shard-wide scratch the way io_uring/IOCP/RIO do. A native epoll backend with the
single-owner loop-thread model collapses that special case: one shape for all backends, no gate, features
written once.

**And the fallback matters more than it looks.** Discovered 2026-07-25: Docker's default seccomp profile
blocks io_uring outright, so a containerised Linux deployment silently lands on the managed backend
(confirmed — the container reports `ManagedSocketFactory` unless `--privileged`). GKE and others disable
it too. "Linux means io_uring" is not safe to assume, which makes the weakest backend the one that often
runs in production.

**Cost, honestly:** epoll is *readiness*, not completion — a different model from the other three. Needs a
readiness→completion adapter (on `EPOLLIN`, non-blocking `recv` into a pooled buffer and synthesise the
completion the shard API expects; on `EPOLLOUT`, drain the write queue), with `EAGAIN`/partial-write
handling moving into the loop and per-connection writable-interest registration (arm/disarm `EPOLLOUT`)
that no other backend needs. That last part is the usually-underestimated bit.

**Do first:** measure io_uring vs epoll vs managed with `--latency` and `--bandwidth`. If they land within
a few percent, the performance case is dead and the decision is purely architectural. This IS measurable
under Docker with `--security-opt seccomp=unconfined` (see below), though a loopback container shares all
the caveats in `RESULTS.md`. Partly done: the small-message parity numbers are in the status
above; the `--latency`/`--bandwidth` comparison on real hardware is still outstanding (item 5).

---

## Performance follow-ups (from the 2026-07-26 Linux size sweep)

Goodput MiB/s, median of 3 scored passes, Docker/loopback (`bench/run-tls-sizes.sh`):

| payload | iouring | epoll+tls | iouring+tls | iouring+ktls | kestrel+tls |
|---|---:|---:|---:|---:|---:|
| 512 B | 94.9 | 70.3 | 69.4 | 74.8 | 65.2 |
| 16 KB | 2,401 | 1,602 | 1,395 | 1,350 | 1,673 |
| 256 KB | 4,379 | 2,814 | **1,183** | 1,951 | **8,383** |

Unlike the small-message numbers these repeat tightly (~1-10% between passes), so the large-payload
shape is worth acting on.

### 0c. ~~io_uring does not always exit on SIGINT after sustained load~~ — CLOSED 2026-07-28: NOT A DEFECT, and not io_uring, and not load

**Diagnosed and closed the same day it was raised.** The process ignores SIGINT because it was *told* to:
a shell **without job control** — which means any non-interactive script, i.e. every rig in `bench/` —
starts background (`&`) children with SIGINT and SIGQUIT set to `SIG_IGN`. That is POSIX, so a Ctrl+C at
the terminal cannot kill a background job. .NET honours the inherited disposition and never raises
`CancelKeyPress`, so the process ignores SIGINT outright.

Read from the kernel rather than inferred. Hung process: `SigIgn: ...1006` (0x2 SIGINT + 0x4 SIGQUIT on
top of the usual 0x1000 SIGPIPE), `SigCgt: ...44f8` — SIGINT **not caught**. Same binary from an
interactive shell: `SigIgn: ...1000`, `SigCgt: ...44fe`, exits in **250ms**. Nine foreground trials across
shard counts and `taskset` all exited in 250ms; every scripted-background trial hung.

**So all three parts of the original framing were wrong:** not io_uring (the backend is irrelevant), not
"after sustained load" (idle hangs identically — the original idle-exits observation was an interactive
run), and not a teardown stall. `TryFinalize` was already refuted structurally; this closes it empirically
too. **There is no connection-state leak here** — the earlier note that one might still lurk was raised
against a symptom that has turned out to have an unrelated cause, so it is not evidence of anything.

**`PosixSignalRegistration` does not rescue SIGINT** — tried and measured; it also declines a signal
inherited as `SIG_IGN`, which is the correct convention. **Use SIGTERM from a harness**; every rig already
does (`kill $pid`).

**What DID change (`SmokeTest/StopSignals.cs`).** SIGTERM's default disposition killed the process
outright, so anything printed at shutdown was unreachable from a rig. It is now handled, shuts down
cleanly, and a `[uring-stats:shutdown]` line was captured from a scripted run for the first time. **That
retires the workaround this entry forced** — "do not build a measurement that can only be read at
shutdown". The reporter's 2s timer is still worth keeping for a hard-killed process.

*Original entry follows.*

### ~~0c. io_uring does not always exit on SIGINT after sustained load~~ — OBSERVED 2026-07-28, not diagnosed

Noticed while building the send instrumentation, so it is a side-observation rather than a hunted bug, but
it reproduced every time. `SmokeTest --http --io-uring -n 8` shuts down promptly on SIGINT when idle, and
**fails to exit within 80s** on SIGINT after serving ~200k requests over 64 keep-alive connections. The
banner is the only thing in the log; no shutdown report is ever printed.

**That suspicion is REFUTED by inspection (2026-07-28), and the refutation is structural.** The entry used
to read: "teardown waits on `RecvArmed`/`SendBusy`/`CancelPending` clearing per connection
(`TryFinalize`), and something is not clearing after load". **Nothing waits on that.** Shard pump threads
are created at exactly one site (`SocketSet.cs:49-61`) with `IsBackground = true`, and `SocketSet.Dispose`
only calls `shard.Stop()` — it never joins them. A background thread cannot hold a .NET process open, so a
slot that never finalizes cannot be the cause of the hang no matter how stuck it is.

Note what that does and does not clear. It does NOT clear a connection-state leak: if `RecvArmed`/
`SendBusy`/`CancelPending` really do fail to clear under load, that still strands a slot during normal
operation, which is the part that would matter in production. It only says that defect, if real, is not
this symptom's mechanism. **They are two separate investigations and the file had merged them.**

"No shutdown report is ever printed" fits loop threads not exiting — but by the above that is a *fellow
symptom*, not the cause. What can actually hold the process: the main thread after `httpStop.Wait()`
returns (`SmokeTest/Program.cs:284`, then `HttpBench` — a `SocketSet` — disposes), a finalizer at exit, or
the SIGINT handler never running at all.

**Cheapest thing that would settle it in one run**, and it needs no debugger (none is installed on this
host — no `gdb`, no `eu-stack`, no `dotnet-dump`): reproduce, then read
`/proc/<pid>/task/*/comm` alongside `/proc/<pid>/task/*/wchan`. That names every thread and what each is
blocked in, which separates "stuck in `io_uring_enter`" from "stuck in `futex`" from "stuck in `close()`"
without guessing. `dotnet-trace` and `dotnet-counters` are installed if more is needed.

Practical consequence right now: **do not build a measurement that can only be read at shutdown.** The
`SS_URING_STATS=1` reporter therefore dumps on a 2s timer as well as at shutdown; the shutdown-only
version silently produced no data at all under exactly the load that mattered.

### 1. io_uring+TLS large-payload behaviour — REFRAMED 2026-07-28: the scope was wrong twice over

**Status: the entry below is superseded. It is not io_uring-specific, not TLS-specific, and not
container-specific — and the component that owns it is still not identified.**

Measured on bare metal 2026-07-28 through AspNetDemo (`bench/run-tls-sizes.sh`), 64KB -> 256KB:

| leg | 64 KB | 256 KB | change |
|---|---:|---:|---:|
| epoll (PLAINTEXT) | 10,523.3 | 6,689.0 | **-36%** |
| iouring (PLAINTEXT) | 10,483.5 | 7,979.9 | **-24%** |
| epoll+tls | 9,200.7 | 4,343.3 | **-53%** |
| iouring+tls | 8,493.2 | 4,040.5 | **-52%** |
| kestrel | 10,057.2 | 12,469.7 | **+24%** |
| kestrel+tls | 6,764.6 | 7,819.0 | **+16%** |

Ranges disjoint. Both Kestrel controls RISE in the same reshuffled passes, which rules out the client,
the box and the payload shape. So the decline is real and it affects **every SocketSet leg including
plaintext epoll** — the original entry's framing as an io_uring+TLS peculiarity was wrong, and its
"could not reproduce" was a property of the container, not of the defect.

**But the component is NOT identified, and the obvious inference does not hold.** The bare responder
shows no collapse at all (see `RESULTS.md`), which looks like a clean indictment of the Kestrel
bridge — except that comparison is cross-run, cross-shard-count, and confounded by `HttpBench` funnelling
all sends through two threads. Bridged io_uring at 16KB measures FASTER than bare io_uring at 16KB, and a
bridge cannot cost negative time. **Next step is a clean bare-vs-bridged isolation in one session at a
matched shard count**, not another sweep.

**RE-MEASURED 2026-07-28 AT SIX PASSES ON THE FIXED TRANSPORT: THE TABLE ABOVE STANDS, AND THE FIX IS
IRRELEVANT TO IT.** 98 cells, zero errors. Every leg reproduces its pre-fix value within ~2%: epoll
-36.8%, iouring -26.0%, epoll+tls -52.8%, iouring+tls -53.9%, against kestrel **+24.9%** and kestrel+tls
**+20.2%**. Full table in `RESULTS.md`.

*Why the fix could not move it, and this is a general point about which callers the defect could reach:*
the allocating branch (`EnsureRoom`'s `want > pageSize`) needs a caller that writes **one large contiguous
span**. Both bridges send a `ReadOnlySequence` of ~4KB PIPE segments and `Connection.Send(in
ReadOnlySequence)` loops `WriteAll` per segment, so `want` is never above a 4KB page. **The bridged path
never had the defect**; the bare responder (which `Send`s the whole response as one span) did. So the fix
is real for callback-style callers and worth nothing to the ASP.NET-shaped one.

*What six passes added that three could not:* at 256KB the SocketSet legs spread 9-17% across passes while
both Kestrel controls hold ~2%, and at 64KB everything is tight. **The bridged path is unstable at 256KB,
not merely slow** - a defect signature, and a new one.

*So the percentages below are now earned rather than provisional*, and the paragraph following this one
(which told you not to trust them) is superseded. Kept for the reasoning.

**AND THE COMPONENT IS NOW IDENTIFIED: IT IS THE BRIDGE. This entry is answered (2026-07-28).**
`bench/run-bare-vs-bridged.sh` ran the bare responder at the SAME 12 shards, same load, same pinning, same
client, **in the same session** as the bridged sweep - the isolation this entry asked for by name instead
of the cross-run comparison it refused:

| backend | 64 KB | 256 KB | change |
|---|---:|---:|---:|
| bare epoll | 10,744.8 | 11,437.3 | **+6.4%** |
| bare io_uring | 10,832.8 | 10,349.4 | -4.5% (ranges overlap: flat) |
| bridged epoll | 10,532.0 | 6,655.5 | -36.8% |
| bridged io_uring | 10,568.0 | 7,817.6 | -26.0% |

**The bare transport does not collapse; only the bridged path does.** As the bridge's own cost that is
**2.0-2.4% at 64KB and 24.5-41.8% at 256KB** - and the 41.8% independently reproduces the ~42% measured
for the bridge on WINDOWS against tuned RIO (11,030 bare vs 6,348 bridged). Two OSes, two transports, one
number for one component.

*The validity check that voided the last attempt passes:* bare beats bridged at every cell, so there is no
"bridge costs negative time" anomaly and the tables may be subtracted.

*And the instability is the bridge as well:* bare 256KB spreads are 2.7-3.0% against 9-17% bridged. The
bridge charges a variable 24-42%, which is a defect signature rather than an overhead.

**Consequence for the backlog:** this converges with `2b-result` from the opposite direction. Zero-copy
send removed one copy and bought +3.5%; the bare-vs-bridged split says the remaining cost is not copies at
all. **The next lever is fewer thread hops / fewer pipes, not fewer copies** - and the strongest form of
that is "no bridge at all", i.e. Kestrel talking to the transport directly, which `2b-result` already
flagged as out of scope here. Anything else aimed at this 24-42% should be justified against that.

**A MECHANISM WAS FOUND AND FIXED (2026-07-28), AND THIS ENTRY MUST BE RE-MEASURED BEFORE IT IS TRUSTED.**
io_uring took a pinned GC allocation of the WHOLE response, once per response, whenever the response did
not fit one buffer page - a hard goodput cliff triggered by exactly "responses got large". See item 0; it
is fixed, and io_uring gained +58-65% at 256KB. (An earlier version of this paragraph blamed the
single-in-flight-send gate and a second completion round trip. That was wrong: sends/response measured
1.000 at every page size.)

Two consequences for this entry, and the first is uncomfortable:

- **The bare-vs-bridged comparison it calls for should be re-run on the FIXED transport**, because every
  number in the table above was taken against a transport that allocated once per response at every
  payload in the sweep. The decline may be smaller, gone, or unchanged - none of those can be assumed.
- **The related claim "epoll beats io_uring by 58% at 256KB because io_uring copies every outbound byte
  into write pages" is SUPERSEDED** - that gap was this defect, and the two now measure level. This
  entry's structural framing borrowed that reasoning, so it no longer stands on its own.

What is untouched: both Kestrel controls RISE while every SocketSet leg falls, which no allocation story
explains by itself. So there is still something here - it just cannot be characterised from pre-fix data.

**~~And it needs six scored passes, not three.~~ DONE — see the six-pass re-measurement at the top of this
entry.** The concern was right: the true per-cell spread at 256KB is 9-17% on the SocketSet legs while
three consecutive passes can span 1.2%. Re-run at six, the figures land within ~2% of the three-pass ones
anyway, so the direction *and* the magnitudes hold — and the spread itself turned out to carry a finding
(SocketSet unstable at 256KB, Kestrel not).

*Original entry follows.*

**Status: could not reproduce in a container. Do not treat the regression below as established.**

What was checked, so it is not repeated:

- *Pool-exhaustion hypothesis: REFUTED.* `TlsSend` copies ciphertext into `WritePageSize` (4KB) chunks
  leased from the out-of-band pool, falling back to a PINNED GC allocation per chunk when dry. One 256KB
  response needs 64 chunks against a 256-chunk-per-shard pool, so exhaustion looked likely. Measured with
  `SS_URING_STATS=1` across 512B-256KB x 8-64 connections: **0.0% miss in every combination.** Pages are
  released as sends complete; the pool is never the constraint. A tidy story, and wrong.
- *Throughput: did not reproduce.* A pure-transport comparison (SmokeTest, no Kestrel bridge) gave
  contradictory results run to run at 8 connections, and at 64 connections every backend converged on
  ~6.5 GB/s - a shared ceiling, so the test was bounded by the host rather than the transport. The
  original 16KB->256KB decline came from the ASP.NET sweep, where the bridge is also in the path (item 3).

**What remains, and is real regardless of timing:** io_uring copies EVERY ciphertext byte into 4KB chunks
and dispatches a writev of N segments (~1.6M chunk-copies/sec at load), where epoll sends directly from
the TLS output buffer with zero copies. That is a genuine structural difference in the send path. Whether
it costs measurable throughput needs a host that is not saturating elsewhere - i.e. real hardware.

Worth knowing before optimising it: the copy exists for a reason. `conn.TlsOut` is a reusable
`PooledBufferWriter`, so its buffer cannot be handed to an in-flight io_uring send. `PooledBufferWriter`
already has `TakeArray()` for exactly this hand-off pattern; the obstacle is that io_uring needs a stable
(pinned) address for the duration, and ArrayPool arrays are not pinned.

### ~~1b. Original observation (superseded by the above)~~

1,395 -> 1,183 MiB/s while every other leg rises. Goodput falling as payload grows is a defect signature,
not a tuning problem. Prime suspect is the outbound path: `OutChain`/writev plus write-page chunking
interacting badly with 16KB TLS records. Note io_uring+TLS is also the *slowest* leg at 256KB, below both
epoll+TLS (2.4x) and kTLS (1.6x) - it should not be.

Method that worked for the last io_uring bug: `SS_URING_TRACE=1` to see completions (failures are a
negative `cqe.res`, never a syscall error), and bisect against a minimal raw probe before blaming the
kernel or the environment.

**Highest expected value of anything on this list.**

### 1c. Configurable read depth (multiple outstanding receives) - IOCP and RIO

**Status: proposed, not started. Feasibility confirmed 2026-07-27. PREMISE MEASURED 2026-07-31 — the
window is real and large, but the entry's assumption that CLOSING it is a win may have the sign
backwards. Read that section before building this.**

### The re-arm window is real: up to 9.5 messages coalesce into one receive

The prerequisite this entry names — "Do first: build a receive-heavy benchmark… does not exist in
`bench/` yet" — now exists (`Run-Upload.ps1`, 2026-07-31). But the premise is observable without
implementing anything: if data piles up in the kernel buffer while nothing is posted, receives must
COALESCE, so `avg bytes/recv` should exceed the message size. Swept over pipeline depth, `--iocp -c 64`,
with `--recv-buffer 65536` so the measurement is not capped by the buffer:

| message | window=1 | window=8 | window=64 |
|---|---|---|---|
| 512 B | 1.00x | 2.16x | **3.87x** |
| 4 KB | 1.00x | 2.67x | **9.50x** |

**Confirmed: at depth, one receive drains up to 9.5 messages.** The window is hit constantly, exactly as
the entry predicted, and it scales with in-flight depth.

*(A first attempt at this measured 4KB messages against the default 4096-byte receive buffer, where `avg`
is capped by the buffer and can never show coalescing — the 4KB row read a flat 1.00x and looked like
evidence of no window at all. A `-z 16384` row is likewise void: `--size` is clamped to the page size, so
it silently measured 4KB messages.)*

### But coalescing is not obviously a COST, and that is the problem with the rationale

This entry argues the window is bad because data "is copied out on the next receive rather than going
straight into ours". What the measurement actually shows is **batching**: 9.5 messages retrieved per
completion instead of one. Fewer completion-port round trips per message is the same effect that made
scatter-gather sends worth **+133%** here — this codebase's biggest measured win came from doing exactly
this on the send side.

So read depth > 1 would *reduce* batching, and might cost rather than gain. That is not a prediction that
it will — it is a statement that the sign is unknown and the entry assumes it.

**Pre-registered, for whoever builds it:** if the window is a cost, `ReadDepth=4` should raise throughput
at small messages with deep pipelining (512B/window=64, where coalescing is 3.87x). If throughput is flat
or falls while `avg bytes/recv` drops toward 1x, the window was providing useful batching and this whole
line of work should stop.

**Weigh that against the price**, which is not small: the recv slab grows N x `SocketsPerShard`, RIO pays
non-paged pool per connection on top, and **ordering stops being structural** — TLS records are
sequence-numbered, so one out-of-order delivery kills the connection rather than degrading it. That is a
lot of risk for an effect whose sign has not been established.

*Original entry follows.*


Both Windows backends currently keep **exactly one receive outstanding per connection**: `RecvBuf` is a
single slab index leased for the connection lifetime and `RecvArmed` is a bool, so the cycle is strictly
complete -> deliver -> re-arm. RIO additionally hard-caps it, `RIOCreateRequestQueue(sock, 1, 1, ...)`.

io_uring does not have this limitation and never did - `IoUringShard.ArmRecv` uses
`IORING_RECV_MULTISHOT` against a provided-buffer ring, which is unbounded depth with no re-arm step. So
this is a Windows-only gap, and the backend that most wants fixing is the one whose entire design premise
is deep queues.

**Why it should help.** Between a completion and its re-arm there is a window with no buffer posted.
Data arriving then lands in the kernel socket buffer and is copied out on the next receive rather than
going straight into ours. At high message rates that window is hit constantly. It should also help the
fragmented-input case directly: successive segments land in successive pre-posted placeholders instead of
coalescing in the kernel buffer.

**Feasible on RIO** - `maxOutstandingReceive` accepts 4 and 16 (unlike `maxRecvDataBuffers`, capped at 1;
see item 0 for how that was established).

**Design points:**

- A `ReadDepth` option, defaulting to 1 so the change is opt-in until measured.
- `RecvBuf` becomes a ring of N leases per connection; the recv slab grows N x `SocketsPerShard`. That is
  the dominant cost.
- **Ordering becomes load-bearing.** Completions for one socket arrive in submission order and one loop
  thread drains them, so order holds - but it stops being structural. TLS is unforgiving here: records are
  sequence-numbered, so a single out-of-order delivery kills the connection rather than degrading it.
- RIO pays non-paged pool for `maxOutstandingReceive` per connection, on top of anything item 0 adds.

**Do first: build a receive-heavy benchmark.** None of the current rigs would show this. `/plaintext` is
ceiling-bound on this host, and both payload sweeps are response-heavy - large sends, tiny requests. Read
depth is a receive-side optimisation, so measuring it needs a large-request workload that does not exist
in `bench/` yet. `SmokeTest --verify-echo` is the closest thing in-tree and it is a correctness test.
Building the code first would repeat the pattern this file keeps warning about: a plausible mechanism
with no measurement able to confirm or refute it.

### 2. Re-run the size sweep now the plaintext controls are in

The first sweep had one plaintext control and it was the wrong one, so nothing at >=16KB is fully
interpretable yet. Plaintext `kestrel` and `epoll` legs added 2026-07-26; just needs a run.

### 0. Sends are page-quantised - **IOCP: FIXED. RIO: NOT FIXED, and now quantified.**

**Status (2026-07-27).**

- **IOCP: done.** `IocpShard.IssueSendPages` builds a `WSABUF` array and issues one `WSASend` with up to
  64 segments. Landed in `bb8007f`/`2be2663` (+133% at 16KB, +162% at 256KB, A/B controlled).
- **RIO: not done.** `WindowsRioShard.IssueSend` still posts `RIOSend(conn.Rq, &buf, 1, ...)` - buffer
  count 1 - and `CompleteWrite` still coalesces only "as many queued responses as fit into the write
  page". This entry claimed both backends were covered; only one was, and nothing recorded the difference
  until the 2026-07-27 sweep went looking for it.

**What RIO's page quantisation costs**, from `bench/Run-TlsSizes.ps1 -Shards 12` (goodput MiB/s, median
of 3):

| payload | rio/s12 | iocp/s12 | RIO vs IOCP |
|---|---:|---:|---:|
| 512 B | 142.7 | 138.0 | **+3.4%** |
| 16 KB | 1,521.1 | 3,741.1 | **-59%** (2.5x) |
| 256 KB | 2,051.6 | 4,483.4 | **-54%** (2.2x) |

RIO is *ahead* below one write page and 2.2-2.5x behind above it - the signature of the defect, isolated
to the one backend that still has it. Spreads 0.3-3.3%, so this is not close to noise. `rio+tls` tracks
plaintext `rio` (and at 256KB is marginally faster), confirming the constraint is the send path and not
the cipher.

#### RIO CANNOT do scatter-gather. Attempted 2026-07-27, refuted by the OS.

This entry used to end "RIOSend takes a buffer array exactly as WSASend does; copy
`IocpShard.IssueSendPages`". **That is wrong**, and it was wrong when first written. `RIOSend` takes an
array in its *signature*, but the buffer count is fixed at request-queue creation by
`RIOCreateRequestQueue`'s `maxSendDataBuffers`, and Windows accepts only **1**.

Measured directly - the full port of `IssueSendPages` was written, and every connection failed to
establish. Probing the parameter in isolation:

| `maxSendDataBuffers` | result |
|---|---|
| 1 | RQ created |
| 2, 3, 4, 8, 16, 64 | **WSAEINVAL (10022)** |

`maxRecvDataBuffers` is capped at 1 the same way. This matches Microsoft's own note on
`RIOCreateRequestQueue` that the Registered I/O extensions currently support a value of 1 for both. So
the IOCP fix has no RIO analogue: one `RIOSend` is one contiguous buffer, permanently.

#### What IS available: depth, not width

The *outstanding operation* counts are not capped that way. Probed on the same host, same call:

| parameter | 1 | 4 | 16 | 64 |
|---|---|---|---|---|
| `maxOutstandingSend` | ok | ok | ok | ok |
| `maxOutstandingReceive` | ok | ok | ok | - |

So the RIO-idiomatic fix for the same problem is **K single-buffer sends in flight** rather than one
send of K buffers: post each write page as its own `RIOSend` without waiting for the previous completion,
and let the RQ ring hold them. Completions for one RQ arrive in submission order, and the shard has one
loop thread draining the CQ, so stream ordering is preserved - but that ordering guarantee becomes
load-bearing rather than structural, which is the main risk to design around. This is a different change
from the IOCP one, not a port of it:

- `SendBusy` becomes a count (or a small ring), not a bool; `TryFinalize` must wait for all of them.
- Partial sends complete per-page, so the `SendSent`/`SendTotal` cursor becomes per-page bookkeeping.
- Teardown must reclaim every page still outstanding, not just page 0.
- `RIO_MSG_DEFER` already batches the kernel kick, so K submissions still cost one commit.

#### MEASURED 2026-07-27: page size fixes it, and the deep-queue spike is not needed

Page size is RIO's only lever, so it was swept against payload on the bare responder. Goodput MiB/s:

| payload | rio p4k | rio p16k | rio p64k | iocp p4k | iocp p64k |
|---|---:|---:|---:|---:|---:|
| 512 B | 154.1 | 153.9 | **154.5** | 152.4 | 149.0 |
| 16 KB | 1,642.9 | 2,967.6 | **4,448.9** | 4,357.0 | 4,255.6 |
| 256 KB | 2,404.1 | 6,948.8 | **10,968.8** | 5,495.5 | 5,873.4 |

**A 64KB page wins for RIO at every payload, monotonically, with no penalty at 512B** - 4.68x at 256KB
and 2.7x at 16KB. IOCP is page-insensitive (+-2%), which is exactly what scatter-gather buys it. And with
a large page RIO beats IOCP everywhere, inverting the standing: RIO was starved, not slow.

So the deep-send-queue rewrite described above is **not needed** to close this gap. Do not build it.

**But this is not yet a default change, and the blocker is not throughput.** Page size trades against
pool depth. The defaults are `SocketsPerShard` 4096 against `WriteBuffersPerShard` 1024 (4:1
oversubscribed already); holding pinned memory constant at a 64KB page means ~64 buffers per shard, i.e.
**64:1**. The sweep ran `-c 64` over 12 shards - about 5 connections per shard - so it never went near
pool pressure.

That used to be dangerous rather than merely slow, because write-pool exhaustion CLOSED the connection.
Fixed 2026-07-28 (item 0b): it now stages and retries. So a shallow pool costs latency, not connections.

**RESOLVED 2026-07-27, except the default itself.**

1. *The memory blocker is gone.* `_writeBufSize` and `_recvBufSize` were the same option, and receive
   buffers are one per SOCKET - so a 64KB page cost 3,163MB instead of 283MB, 97% of it receive slab that
   gains nothing from being large. `SocketSetOptions.ReceiveBufferSize` splits them (0 = follow
   `BufferPageSize`). A 64KB send page with a 4KB receive buffer gives the full 4.66x at **283MB, the same
   as today**.
2. *Pool pressure: the prediction was wrong, and instructively so.* A shallow pool at a big page was
   expected to starve. It does not: RIO holds one write page per in-flight send, and at 4KB a 256KB
   response holds it across 64 sequential round trips versus 4 at 64KB, so occupancy time collapses and a
   bigger page RELIEVES pool pressure. Counting buffers without counting holding time is what got it
   backwards. **Note the error counts that originally accompanied this were a harness artifact (no
   ephemeral-port gate) and are withdrawn — see item 0b.** The goodput comparison stands; nothing about
   connection drops does.
3. *Plumbed end to end.* `SmokeTest` and `AspNetDemo` both accept `--page` / `--recv-buffer` /
   `--write-buffers`; `/config` reports them so a harness can verify the setting took, and combining them
   with `--kestrel` is rejected.

**What is deliberately NOT done: changing the default.** `64KB page + 4KB recv + 256 write buffers` is
faster at every concurrency tested and has strictly better error behaviour than what the default does. It is still
not the default because these are Windows measurements at one payload shape on loopback, and
`BufferPageSize` is shared with io_uring and epoll where it has not been swept.

(An earlier version of this paragraph also cited "208 errors on the current default" as a blocking
defect. That was a harness artifact — see item 0b — and is not a reason for or against anything.)

**LINUX SWEPT 2026-07-28 — the memory objection is gone, and every backend now wants the same thing.**

`bench/run-page-sizes.sh` on the bare responder (full tables in `RESULTS.md`):

| backend | page sensitivity | RSS at 64KB page vs 4KB |
|---|---|---|
| RIO | wants 64KB badly (4.68x at 256KB) | was the blocker; solved by `ReceiveBufferSize` |
| IOCP | indifferent (+-2%) | n/a |
| epoll | **indifferent** at every payload | **flat** (72 -> 73 MB) |
| io_uring | **wants 64KB: 2.0x at a 16KB payload** | **37% CHEAPER** (122 -> 77 MB) |

Two things change here. First, the pre-registered "both Linux backends are page-insensitive" was **half
wrong** — io_uring is sensitive, so the shared default could not have been decided on Windows evidence
alone. Second, and more useful: **the memory argument against a larger default does not exist on Linux at
all.** It was the whole reason this was frightening, and it was a Windows-specific consequence of the
receive slab being per-SOCKET. On Linux a bigger page is free on epoll and actively cheaper on io_uring,
because collapsing buffer occupancy time reduces the fallback pinned allocations.

So every backend now benefits from or is indifferent to a 64KB page, and none of them pays for it in
memory once `ReceiveBufferSize` covers the Windows receive slab. **The case for raising the default is
much stronger than when this entry was written**, and what is left is mechanism, not evidence.

**Remaining:**

- Sweep past 64KB - RIO was still improving monotonically at the top of the range, so the peak is unknown.
- **"Does the response fit in ONE page" is CONFIRMED as the mechanism on io_uring (2026-07-28), and it
  turns item 0 from a tuning question into a defect.** Three pre-registered predictions, all held: only a
  page that FITS jumps (at a 256KB payload p256K buys nothing, p512K buys 1.48x); once it fits, more page
  buys nothing (p64K/p256K/p512K overlap at 16KB); and at a fixed p64K, walking the payload across the
  ~65.4KB boundary makes goodput fall off a cliff - 60,000 bytes at 11,341.6 MiB/s against 70,000 bytes at
  7,610.6, i.e. **17% more data for 33% less goodput**, ranges hugely disjoint. Full tables in
  `RESULTS.md`.

  **The cause is a PER-RESPONSE PINNED ALLOCATION, measured 2026-07-28 with `SS_URING_STATS=1`.** An
  earlier entry blamed the `SendBusy` single-in-flight-send gate and a second completion round trip; that
  was **wrong and is retracted**. Direct counts: every page size costs exactly **1.000 send SQE** carrying
  exactly **1.000 iovec segment**, with **zero** queued behind an in-flight send and **zero** partial
  resubmits. There is no extra round trip and the gate is never reached.

  What page size actually selects is where that one segment points: a response that FITS a pooled write
  page is sent from the pool; one that does not becomes **a pinned GC allocation of the whole response,
  one per response** (0.000 vs 1.000 pinned-alloc/resp either side of the boundary, coinciding exactly
  with the goodput cliff at a fixed page). At 256KB that is a pinned Large Object Heap allocation on every
  response - the same cost `fa97dd4` identified, arriving from another direction.

  **The fix is smaller and safer than the wrong diagnosis implied.** The machinery already exists and is
  simply unused here: `PumpFlush` sends up to `IovMax` (1024) segments in one `IORING_OP_WRITEV` and
  `HandleWriteV` already handles partial writes across a multi-segment chain. An oversized response should
  be assembled from **N pooled pages sent as one N-segment writev** instead of one big pinned allocation:
  same syscall count, same round trips, no allocation. **No change to ordering, `SendBusy`, `IO_LINK` or
  teardown is required** - so the risky option-B rewrite that the earlier diagnosis motivated is not
  needed for this.

  **So raising the default page only moves the boundary**, making the right default a function of expected
  payload - which a library cannot know, and which is worst for exactly the streaming workloads (downloads,
  video) where responses are unbounded and each would allocate its full size, pinned.

  **FIXED 2026-07-28.** `Connection.WriteAll` asked for `data.Length` as one contiguous span; it never
  needed contiguity (the loop always coped with a short span), but asking made `EnsureRoom` take its
  `want > pageSize` branch and allocate. io_uring now overrides `Connection.GetWriteSpan` to hand back its
  natural buffer, so an oversized write chains across pooled pages into one multi-segment `writev`.
  Per response at a 4KB page with a 16KB body: 1 pinned alloc -> **0**, 1 segment -> 5 pooled pages, send
  SQEs unchanged at 1.000. Goodput **+96% at 16KB** (4,363.7 -> 8,578.0) and **+58-65% at 256KB**
  (7,685.7 -> 12,706.8 at p4K). Byte-exact echo passes on both backends, callback and `--pipe`.

  **What this does to item 0: the io_uring half of the blocker is gone.** Page size no longer selects
  between two cost regimes on io_uring - every page now lands in the same band at 16KB and at 256KB - so
  the default no longer has to be chosen against expected payload for this backend's sake. RIO still wants
  a large page for its own (different, real) reason.

  **Deliberately opt-in per backend, and the follow-ups are the point:**
  - **RIO must NOT get this** without measurement: `maxSendDataBuffers` is capped at 1, so chaining would
    turn one send into N sequential sends - the same quantisation that already costs it 2.2-2.5x.
  - **IOCP probably should**: it scatter-gathers (up to 64 `WSABUF`s), which is the shape that benefits.
    Untested - this host is Linux.
  - **epoll needs nothing**: it does not share this defect at all. It goes through `OutboundConnection`,
    which accumulates into a pooled buffer writer and rents its flush snapshot from `ArrayPool` (the
    `fa97dd4` work), so a large response never becomes a pinned per-response allocation. That is why it
    was page-insensitive from the start. It keeps the contiguous default.
- ~~Confirm the page/pool-depth co-variation is inert on Linux.~~ **DONE 2026-07-28: it is inert, and the
  2.0x is the page.** With all pools pinned at 1024 the io_uring 16KB ratio is 1.97x against 1.98x
  uncontrolled. Two things had to be fixed to run it: the sweep rescales **three** pools, not one
  (`OutOfBandWriteBuffersPerShard` had no override at all - `--oob-write-buffers` added), and the old
  `FIXED_WRITE_BUFFERS` knob pinned only one of them, so it was not a control. `FIXED_POOL_DEPTH` pins
  all three and the rig now aborts a cell whose banner disagrees. Only io_uring ever needed this: epoll
  reads `BufferPageSize` and none of the pools, so `--page` moves one quantity for it.
- **The memory claim needs rewording, not retracting.** "A 64KB page is 37% cheaper" is mostly the pool
  rescale: at matched depth the page itself saves ~9MB of ~41MB. True of what the default does; not a property of
  the page. Reworded in `RESULTS.md`.
- Decide the mechanism for a per-backend default. The backends want opposite things (RIO large, IOCP
  small/indifferent) and `BufferPageSize` is one global constant with a real default of 4096, so there is
  no way to distinguish "user asked for 4096" from "user said nothing". Needs a sentinel (0 = backend
  chooses) or a factory-supplied default; that changes public option semantics and wants its own commit.

### 0e. ACCESS VIOLATION in RIO+TLS under connection churn — ON THE DEFAULTS (2026-07-29)

**Status: FIXED 2026-07-30 (a stale RIO request-queue handle), verified over 100 runs where ~50 crashes
were expected. The causal story is only PARTLY consistent with the bisection — read "what does not add
up" below before treating this as closed.**

**The bug.** `CloseClient` calls `closesocket`, which destroys the connection's RIO request queue. But
`conn.Rq` was only zeroed in `TryFinalize`, which **early-returns whenever an op is still in flight**
(`RecvArmed || SendBusy`) — the normal case under churn. `FlushCommits` guards on
`conn.Socket != 0 && conn.Rq != 0`, and `Socket` is *deliberately* held non-zero as the claimed marker
until finalize, so **both guards pass while the queue is already destroyed** and it posts
`RIO_MSG_COMMIT_ONLY` against a dead handle. The `Socket != 0` guard was clearly meant to prevent exactly
this and structurally cannot.

Not a data race — close and `FlushCommits` both run on the loop thread. A **sequencing** bug, which is
why it reproduces as often as it does.

**The fix** (`WindowsRioShard`): zero `conn.Rq` and the commit flags in `CloseClient`, immediately after
`closesocket`, because that is when the queue actually dies. `ArmReceive` and `IssueSend` gained
`Rq == 0` guards, since a completion arriving after close can still drive either of them and
`RIOReceive`/`RIOSend` against a zero handle faults rather than failing cleanly.

**Verification:** 4 configs x 25 reps = 100 runs, 0 crashes, against a pre-fix rate of ~4-5/8 per config
(so ~50 expected). Plus the full smoke matrix with churn cells repeated.

**WHAT DID NOT ADD UP, and what a soak then said about it.** The bisection says the crash needs a TIGHT
SLOT TABLE: `--sockets 4096` is 0/8 while the baseline is 4/8, and that variant does not reduce
concurrency at all — same clients, same churn rate — it only stops slots being recycled. A stale-`Rq`
window should not care whether the freed slot is immediately re-tenanted, so the worry was a **second
lifetime bug masked rather than removed**.

`bench/Soak-Churn.ps1` (new) went looking for it — 60s per case, watching all three faces a lifetime bug
can wear (crash, wedge, quiet accounting imbalance), including a case with the slot table at exactly
100% capacity (SmokeTest runs both ends in one process, so `-c 128` against `--sockets 64` x 4 shards is
full):

| case | connections churned | |
|---|---:|---|
| **rio+tls, table FULL** | **51,845** | clean |
| rio+tls, tight | 36,463 | clean |
| rio plaintext, tight | 244,992 | clean |
| iocp+tls, table full *(control)* | 49,779 | clean |
| iocp+tls, tight *(control)* | 37,751 | clean |

**~51,000 connections through the exact configuration that used to fault inside one second**, at maximum
reuse pressure. That is the strongest available evidence that slot reuse was shortening the stale-`Rq`
window rather than exposing a separate bug — but it is evidence, not proof: **0e was a ~1-in-2 fault and
still hid for months** behind a suite that ran its cell once. Treat "there is no second bug" as unproven.

**The structural hazard is now DETECTED on IOCP (2026-07-31), though not removed.** `IocpOp` carries the
connection generation it was armed for, and the completion dispatch drops any completion whose generation
does not match the slot's current tenant, counting it as `STALE COMPLETIONS` in `SS_IOCP_STATS`. It
should always read 0 — and does, through TCP and TLS churn — because the invariant below is what makes a
slot-only completion safe in the first place. The point is that if the invariant ever breaks, it now
announces itself instead of silently applying a dead connection's completion to whoever holds the slot,
which is how a lifetime bug becomes corruption rather than a log line. **RIO has the same detector**, and
it is the backend that most wanted one since 0e lived here: `RIORESULT.RequestContext` is a `ULONGLONG`
and the op-kind discriminator needs two bits of it, so the generation rides in the top half. Guarded on a
64-bit pointer, because the value is passed through `(void*)(nuint)` and a 32-bit build would truncate
the generation away and make **every** completion look stale.

The underlying hazard is unchanged: completions carry only the slot (`r.SocketContext`), *not* a
generation, so a stale completion landing on a re-tenanted slot is structurally possible; the
defer-recycle rule (`TryFinalize` refusing to free while `RecvArmed || SendBusy`) is the only thing
preventing it. Any path that clears those flags without the completion having drained reopens it.

*The original entry follows, including the pre-fix reproduction data.*

`SmokeTest --rio --tls-schannel -s -c 64 --churn 10 --close-after 4 --sockets 128 --reset-close` exits
with **0xC0000005 (access violation)**, intermittently, usually within the first second. Not a wedge, not
a managed exception - the process dies. `### UNHANDLED ###` never prints, so a managed handler never saw
it: this is a fault in unsafe/native-interop code, which on this path means the RIO submission or
completion machinery.

**Frequency — RE-MEASURED 2026-07-30 with a stuck Windows Firewall dialog cleared.** The first table
here was taken while one was pending against the binary under test, which is a documented 2.8x
confounder, and it **understated the rate roughly threefold**. Both builds, 8 reps per cell:

| page | write-buffers | pristine `e104568` | today's HEAD |
|---:|---:|---|---|
| **4096** | **1024** *(the DEFAULT)* | **5/8 crash** | **4/8 crash** |
| 4096 | 512 | 4/8 crash | 4/8 crash |
| 4096 | 128 | - | 2/8 crash |
| 4096 | 64 | - | **0 crash, 8/8 WEDGE** |
| 65536 | 512 | 3/8 crash | 3/8 crash |
| 65536 | 256 | 3/8 crash | 6/8 crash |

**Read the first row again: roughly one run in two, on the configuration the library ships.** And the
pristine build crashes at least as often as today's, which is what makes "this predates 2026-07-29"
solid rather than inferred.

**A correction to this entry's own earlier advice.** It previously said a 4KB page with 128 write buffers
showed 0/6, and drew from that "depth moves the rate, so a lower rate will look like a fix". The 0/6 was
the confounder plus luck: **128 crashes too** (2/8). The honest version is stronger and simpler - *every*
depth tested crashes, except the one shallow enough to wedge first (64 wedges 8/8 and never gets far
enough to crash, which is masking, not avoiding). Do not tune depth and call it fixed.

**Why it was never seen before**, and this is the transferable part: it is intermittent at roughly 1-in-6
on the defaults, and `rio+tls/churn` is ONE cell that had only ever been run a handful of times. Four
consecutive passes are entirely likely at that rate. **An intermittent fault in a suite you run once per
change is indistinguishable from a flaky harness** - which is exactly how it read the first time
(`Run-SmokeMatrix.ps1` reported "no churn result line", because the process died before printing one).
The harness now names crash exit codes explicitly rather than letting them fall through.

**What is established:**
- Pre-existing: reproduced 3/8 on `e104568`, and 5/8 on today's HEAD. Nothing from 2026-07-29 causes it.
- RIO-specific and TLS-specific: the same churn cell on `rio` plaintext, `iocp`, `iocp+tls` and both
  managed legs has never crashed.
- Churn-specific: the steady-state cells (`poke`, echo, out-of-band verify) do not crash at any size.
- Fast: it usually dies inside 1s, i.e. during ramp-up, when reconnects race in-flight teardowns.

**Where to start.** The churn cell exists to stress exactly what this is: reconnect `InitClient` on an
external thread racing the loop thread still reaping the previous tenant's in-flight ops, with a tight
`--sockets` table so slots recycle instantly. RIO's teardown carries extra state the IOCP path does not
(`Rq`, `CommitPending`/`CommitRecv`/`CommitSend`), and a request queue whose lifetime is tied to a socket
that churn is closing underneath it is the obvious suspect - a `RIOSend`/`RIOReceive` posted against an
`Rq` belonging to a slot that has just been re-tenanted. `_toCommit` holding a connection across the
close that frees its `Rq` would do it, and `FlushCommits` walks that list by `CollectionsMarshal.AsSpan`.
That is a hypothesis from reading, **not** a diagnosis - none of it is confirmed.

**Do not "fix" it by changing pool depths.** Depth moves the frequency around (128 at a 4KB page showed
0/6) and that is exactly the kind of change that looks like a fix and is a mask. The bug is a lifetime
race, and the evidence for that is that it happens at the default depth too.

**Repro rig:** `scratchpad/av-repro.ps1` in the session notes, or just loop the command above ~8 times
and watch for exit code -1073741819. It needs no special tooling; the exit code is the whole signal.

### 0d. RIO + TLS out-of-band send is starved at the default page — a CORRECTNESS-GATE failure (2026-07-29)

**Status: NOT diagnosed, but the search space is now small. Not fixed. The only red cell in the Windows
smoke matrix.**

**A mechanism was proposed and then REFUTED by its own confirming test, on the same day.** The proposal
was "the send page must hold a whole encrypted record", the record being the caller's write size plus
framing - which predicts the cliff moves with the write size. `--verify-seg` was added to test it, and
**both directions fail**: a ~1KB record that fits a 4KB page eight times over is still slow at page 4096
(2.34-5.07s), and a ~15KB record that cannot fit an 8KB page at all is already fast at page 8192
(0.18-0.53s). Record framing is not the mechanism. Do not re-derive it.

**What IS established - the cliff is the send page, between 4096 and 8192, and nothing else:**

| | pools 1024 | pools 512 |
|---|---|---|
| page 4096 | 2.06-5.50s | 3.03-5.20s |
| page 8192 | 0.51-0.53s | 0.18-0.56s |

(`--page` silently rescales all three pool depths to 4MB/page, so every earlier "page" result was also a
"pools" result. Crossed over, the effect follows the page and not the pools at all.)

Excluded, each by measurement:
- **Not pool depth**, in either direction - raising both write pools to 4096 at page 4096 changes
  nothing, and the crossover above settles it.
- **Not the TLS record size / caller write size** - the refutation above.
- **Not a busy or over-kicking loop** - `SS_RIO_STATS=1` (new; RIO had no instrumentation at all) shows
  0.17 port-wakes and 0.17 notify-rearms per send, 2.0 completions per send. It is waiting, not working.
- **Not the receive buffer** as the main term. `--page` moves it too (it follows the page unless
  `--recv-buffer` overrides); separated, the send page is ~25x and the receive buffer ~2.6x.
- **TLS-only** - plaintext RIO at the same page does 12.58MB in 0.15s.
- **RIO-only** - IOCP+TLS at page 4096 does the same in 0.24s.

**Where to start, for whoever picks this up:** the question is what a RIO send of 4096 bytes waits for
under TLS that a send of 8192 bytes does not, given the loop is idle and nothing is starved. That is a
much smaller question than this entry started with, and it wants a profiler or an ETW trace rather than
another flag sweep - four have now been run and each one only excluded something.

**What would dissolve it regardless of cause:** scatter-gather, which RIO cannot have
(`maxSendDataBuffers` capped at 1). So the practical fix is the page-size default (item 0), and this is
the strongest argument for it on file, because it is a failed correctness cell and not a throughput
number.

*Original entry follows.*

`bench/Run-SmokeMatrix.ps1` cell `rio+tls/verify-oob-4m` fails: 7,815,168 of 12,582,912 bytes inside the
harness's 15s deadline, **zero mismatches**. A rate problem, not a corruption one - but it fails a
correctness gate, which no other throughput finding on this list does.

**It is not the `Flush` hand-off.** Bisected to `dd8cdce^` in a worktree, which fails the same way
(11,747,328/12,582,912). Interleaved 5-pass A/B of the 1MB cell: pre 2.68-5.18s, post 2.68-5.20s -
**ranges overlap completely, so there is no delta to quote in either direction.**

**It is the page size.** One session, same host:

| leg | 3MB out-of-band verify | |
|---|---:|---|
| rio+tls, page 4096 (default) | 2.68-5.20s (5 passes) | ~0.6-1.1 MiB/s |
| **rio+tls, page 65536** | **0.21-0.22s** (3 passes) | disjoint, **15-25x** |
| rio, page 4096 *(control)* | 0.08s | TLS-specific |
| iocp+tls, page 4096 *(control)* | 0.22s | RIO-specific |

**What is explained and what is not.** `WindowsRioShard.IssueSend` posts `RIOSend(conn.Rq, &buf, 1, ...)`
- one write page per send, because Windows caps `maxSendDataBuffers` at 1 - so page size is RIO's only
lever, which is the documented 4.68x story. **That does not explain the ~60x gap between `rio` and
`rio+tls` at the same page size.** The implied ~5ms per send is far too slow to be syscall cost and
suggests something is *waiting* rather than working; RIO's deferred commit (`RIO_MSG_DEFER` +
`RIO_MSG_COMMIT_ONLY`) is the first place to look. **Do not fold this into the existing page-size story
until that is understood** - it is recorded as measured-but-undiagnosed on purpose.

**Why it matters to item 0:** this is a third independent argument for a backend-chosen page default, and
the first that is a failed correctness cell rather than a throughput number. RIO wanting 64KB is no
longer only a tuning preference.

### 0b. Write-pool exhaustion closes the connection instead of applying backpressure

**Status: DONE 2026-07-28. The justification I gave for it was wrong; the change is right anyway.**

*The wrong part.* This entry claimed the default configuration "drop 208 connections at `-c 2048`". That was
read off an error column without checking what the errors were. In isolation the same configuration
serves 73,852 requests with **zero** errors. The counts came from `Run-PoolPressure.ps1`, a harness
written the same day with **no ephemeral-port gate**, where `Run-Matrix.ps1` has three `Wait-Ports` calls
precisely because Windows has ~16k ephemeral ports with a multi-minute TIME_WAIT — and that run opens
about 74,000 connections. Client-side port pressure, i.e. confounder 2 of `RESULTS.md`,
reproduced by someone who had read the warning. **Any harness that opens thousands of connections per
cell needs the port gate; copy it from `Run-Matrix.ps1`.**

*The right part.* Closing a healthy connection because a write page was briefly unavailable is wrong on
its own terms, whatever the error counts said. Both Windows backends now stage the bytes into `Pending`
and queue the connection for retry (`WindowsShardBase.MarkAwaitingPage` / `DrainAwaitingPage`, drained
once per loop pass), instead of calling `CloseClient`. Retrying per pass rather than hooking every buffer
release is deliberate: pages are freed by whichever connection finishes a send, usually not one that is
waiting, so a per-release hook would fan out to every waiter anyway.

Verified directly rather than incidentally: a **4-buffer** write pool — which under the old code would
have torn connections down almost immediately — now round-trips 4MB byte-exact on both IOCP and RIO.

Still open: the drop path is invisible in Release (`Debug.WriteLine`), and there is no counter for how
often a connection had to wait. Worth adding before anyone tunes pool depth against it.

`WindowsRioShard.SendResponse` and `StartPendingSend` both do
`if (!_writeBuffer.TryLease(...)) { CloseClient(slot); return; }`, and `IocpShard` has the same shape. So
running out of write buffers is a **dropped connection**, not a slow one.

That is tolerable while pools are generously sized relative to sockets, and it becomes a live hazard the
moment page size goes up and buffer counts come down (item 0). It also makes pool sizing a
correctness-adjacent decision rather than a tuning knob, which is the wrong shape for something an
operator is expected to set.

Wanted: queue the send (the `Pending` machinery already exists for exactly this) or refuse the write and
let the caller retry, rather than tearing down a healthy connection because a buffer was briefly
unavailable. Note the drop is currently invisible in Release - `Debug.WriteLine` is compiled out.

---

*Original write-up follows, for the reasoning.* `BufferPageSize` defaults to 4096 and the Windows send path
puts at most ONE page in flight per connection, so a 256KB response goes out as **64 sequential 4KB
WSASends**, each with its own completion-port round trip. Kestrel's transport issues one `SendAsync` over
the whole buffer.

Measured on the BARE SocketSet HTTP responder (no Kestrel, no bridge), 256KB payload, `-c 64`, 16 shards,
median of 2 scored passes:

| page size | goodput | vs 4KB |
|---|---:|---:|
| 4KB (default) | 885 MiB/s | - |
| 16KB | 2,503 MiB/s | 2.8x |
| 64KB | 3,556 MiB/s | **4.0x** |

Passes were tight (878/885, 2474/2503, 3257/3556), so this is not noise.

**Do not just raise the default.** Page size is a trade-off, not a dial: at a 16KB payload the best page is
16KB (2,103 MiB/s) and a 64KB page is WORSE (1,273 MiB/s) because most of the page is wasted. A fixed page
is wrong at both ends.

The real fix is to stop quantising sends by page at all:

- `IocpShard.IssueSend` posts a single WSABUF (`WSASend(sock, &b, 1, ...)`), and `CompleteWrite` coalesces
  the pending queue into one write page. Build a WSABUF **array** from the pending chain and issue one
  WSASend with count > 1 - scatter/gather is exactly what the API is for.
- RIO has the same shape and `RIOSend` likewise takes a buffer array.
- **io_uring already does this**: `TlsSend` builds an `OutChain` of segments and dispatches one writev.
  So this is an IOCP/RIO gap, not a design gap - the shape to copy already exists in-tree.

This also reframes the shard-count result: throughput scaled near-linearly with shards (2->4->8->16, then
flat at 32 = the pinned core count) precisely BECAUSE each connection's sends are serialised. Parallelism
across connections was the only lever available.

### 2a. BYO-buffer, phase 1: `ctx.UsePipe(IDuplexPipe, pinned)` - LANDED 2026-07-27 (fallback path)

Opt-in per connection from OnAccept/OnConnect. The pipe handed in is the TRANSPORT-side endpoint
(Kestrel's `Application` half): the transport writes received bytes to `pipe.Output` and reads outbound
bytes from `pipe.Input`. A connection that never calls it is completely unaffected, so this cannot regress
the existing path - and the whole feature is `#if NET` (netfx does not reference System.IO.Pipelines).

**This is deliberately the BAD path.** `PipeIoBridge` is written entirely against the existing public
surface - `Connection.Send(in ReadOnlySequence<byte>)` outbound, the normal receive callback inbound - so
it works on EVERY backend today, including ones that can never do better (RIO takes registered buffer ids,
never foreign addresses; DPDK would be similar). It costs one copy per direction, which is precisely what
the per-backend fast paths are supposed to remove. Having it first means each backend's fast path has a
correctness reference and a number to beat rather than being designed blind.

Verified byte-exact (4MB echo) on IOCP, RIO, managed, and IOCP+SChannel TLS. Confirmed the path is
actually taken rather than silently ignored: with `--pipe`, receive completions HALVE (2,080,047 ->
990,511) because the server half no longer enters `OnReceive`, while round-trip bytes still balance.
Costs about 5% throughput versus the callback path at 512B ping/pong - the extra copy and the thread hops.

Known limitations, all for phase 2:

- **Inbound backpressure is advisory.** The receive callback runs on the loop thread and cannot block, so
  a flush that does not complete synchronously is observed asynchronously while writing continues into the
  PipeWriter's buffer. Honouring it means PARKING the receive, which needs backend cooperation.
- **`pinned` is recorded and unused.** Nothing in the fallback pins anything, because it copies.
- **Read depth and the instant-response RawBuffer path are incompatible by construction** - both hand out
  transport-owned memory whose lifetime does not match a pipe segment's.
- Applications must not mix pipe mode with direct `Connection.Send`/IBufferWriter on the same connection;
  the outbound pump owns ordering on that half.

### 2a-bis. The AspNetDemo BYO bridge (`--byo`) - LANDED 2026-07-27, and it is at PARITY as predicted

`AspNetDemo --byo` selects a parallel bridge: the same two pipes, but handed to the transport via
`ctx.UsePipe` instead of `SocketSetConnection` copying inbound and running its own outbound pump.
Reported in `/config` as `byo=pipe` so a harness refuses a leg where the flag was ignored.

Measured on IOCP, 12 shards, `-c 64`, median of 3 scored passes (goodput MiB/s):

| payload | classic | byo | delta | classic passes | byo passes |
|---|---:|---:|---:|---|---|
| 512 B | 134.0 | 135.8 | +1.3% | 134,134,134 | 136,132,137 |
| 16 KB | 3,475.2 | 3,434.6 | -1.2% | 3475,3510,3421 | 3435,3372,3544 |
| 256 KB | 4,410.7 | 4,430.1 | +0.4% | 4410,4428,4411 | 4267,4430,4621 |

**Every per-pass range overlaps: this is parity, and parity was the prediction.** Phase 1 relocates the
bridge into the library rather than removing it - `PipeIoBridge` performs the same inbound copy and the
same `Connection.Send` that `SocketSetConnection` did. An earlier note in this file claimed switching the
demo to `UsePipe` would recover the measured 14-19% bridge cost "with no zero-copy work at all"; that was
wrong, and this measurement is what corrects it.

The value of the leg is that it is the vehicle phase 2 needs, and a like-for-like baseline: the zero-copy
work must be measured against THIS, not against the callback path, or it will be credited with what pipe
mode already costs.

### 2b-result. IOCP zero-copy send: LANDED 2026-07-28, worth +3.5% at 16KB and nothing elsewhere

Measured against the `--byo` bridge (the like-for-like baseline), IOCP, 12 shards, `-c 64`, median of 3:

| payload | classic bridge | byo + zero-copy | delta | classic passes | byo passes |
|---|---:|---:|---:|---|---|
| 512 B | 141.7 | 138.5 | -2.3% | 140,144,142 | 140,139,138 |
| 16 KB | 3,615.4 | **3,741.7** | **+3.5%** | 3671,3615,3587 | 3742,3748,3741 |
| 256 KB | 4,271.9 | 4,094.7 | -4.1% | 4314,4272,4139 | 4018,4329,4095 |

Only the 16KB row has disjoint ranges, so **+3.5% is the only defensible claim**; the other two are noise.

**Against the ~42% bridge cost this was aimed at, that is a poor return, and the reason is the finding.**
The bridge's cost is not mostly copies — it is the two Pipes, the scheduling hops between them, and
Kestrel's own pipeline. Removing one of the two copies bought 3.5%. This is the third independent arrival
at the same conclusion (the others being `fa97dd4`'s A/B and page size moving RIO 4.68x without changing
bytes copied): **per-byte copying is not what costs on this path.**

Two things worth trying before concluding the approach is exhausted, in rough order of expected value:

1. **The 64-segment cap probably binds at 256KB.** Kestrel's pool hands out 4KB blocks, so a 256KB
   response is ~64 segments — right at `MaxSendPages`. Anything over declines and silently falls back to
   copying, which would explain why the largest payload gained nothing. Fix by sending a PREFIX of the
   sequence instead of declining: the pump advances only by what was actually sent, which needs
   `TrySendZeroCopy` to report bytes rather than a bare bool. Instrument the decline rate first — do not
   assume it binds.
2. **Inbound zero-copy** (receive straight into `pipe.Output.GetMemory()`) removes the OTHER copy and, more
   interestingly, removes the staging introduced for backpressure. It also needs receive-parking, which is
   the only mechanism that makes backpressure real rather than advisory.

If neither moves it, the honest conclusion is that the bridge cost is structural — pipes and thread hops —
and the way to recover it is to not have a bridge, i.e. for Kestrel to talk to the transport directly,
which is out of scope here.

### 2e. Managed backend BYO send — ASSESSED 2026-07-29 and deliberately NOT built

The managed backend is the closest of any to zero-copy and the furthest from being worth the risk today.
Recording the decision so it is a choice rather than an omission.

**Why it looks attractive.** It is already one copy, not two: `ManagedConnection` accumulates into an
`ArrayPool` buffer and `Flush` hands *that array* to `SendAsync` via `SetBuffer` - no staging copy. Only
the `WriteAll` accumulation stands between it and BYO, `SocketAsyncEventArgs.BufferList` takes
`ArraySegment`s (the shape of a `ReadOnlySequence`), and **no pinning is needed** - the SAEA handles it.
That is the same mechanism vanilla Kestrel's own transport uses.

**Why it was not built.** The send path is a state machine over one `byte[]` plus `SendOffset` /
`CurrentLength`, and `PumpSend` re-issues `SetBuffer(data, offset, remaining)` on every partial send.
`BufferList` cannot coexist with `SetBuffer`, so supporting it means a *second*, parallel representation
with its own partial-send cursor across segments - inside a lock-based path, in the backend that is the
portable fallback (i.e. what runs wherever io_uring is unavailable, including Docker's default seccomp
profile), and which appears in **no** benchmark in this repo. New concurrency-adjacent code with no
measurement to catch a regression is the wrong trade while larger, measured wins are open.

**What would change the decision:** a managed leg in the rigs (so a regression would be visible at all),
or evidence that the managed path is on someone's hot path. The io_uring result (+45.1% at 256KB) says the
mechanism pays where it can be measured, so this is a sequencing call, not a doubt about the idea.

### 2d. The bridge's pipes are OURS, and they are almost entirely unconfigured (raised 2026-07-29)

**Status: proposed, not started. Cheap levers first, custom pipe second.** This is aimed at the term the
evidence actually blames — the bare-vs-bridged isolation puts the bridge at 24.5-41.8% at 256KB with the
transport not declining at all, and `2b-result` reached the same place from the other side (zero-copy send
removed a copy for +3.5%). If the cost is pipes and thread hops, the pipes are the thing to attack.

`AspNetDemo/SocketSetConnection.cs:58-61` constructs both pipes with:

```
readerScheduler: PipeScheduler.ThreadPool, writerScheduler: PipeScheduler.ThreadPool,
useSynchronizationContext: false, pauseWriterThreshold: 1MB, resumeWriterThreshold: 512KB
```

Everything else is default. Three knobs are sitting untouched, and none needs new abstractions:

1. **`PipeScheduler`.** ThreadPool on *both* ends of *both* pipes means a hop per direction per exchange.
   That is the "thread hops" term, named and unmeasured. **Do not naively set `Inline` on both** — an
   inline *reader* scheduler runs Kestrel's request pipeline on the transport's loop thread, which blocks
   the IO loop for every backend that has one (all but managed). Kestrel runs its own IO queues for exactly
   this reason. The safe experiment is one side at a time, with the loop-thread hazard in mind.
2. **`minimumSegmentSize`** (default 4096). This is why a 256KB response is ~64 segments — which is what
   `2b-result` suspects silently defeated IOCP's zero-copy send (it caps at 64 `WSABUF`s), and it is 64
   iterations of `WriteAll` per response on every other backend. A 64KB segment makes that 4.
3. **`MemoryPool`** (default, unpinned). `ctx.UsePipe(pipe, pinned: true)` exists and is *recorded and
   unused*; a pinned-block pool is what would make it mean something, and io_uring needs exactly "a stable
   address" (see 2b).

**Do these before writing a custom pipe** — they are configuration, they are individually A/B-able against
the `--byo` leg, and one of them (segment size) plausibly explains an existing null result rather than just
adding speed.

**Then the custom `IDuplexPipe` itself**, which is the part worth exploring rather than assuming.
`System.IO.Pipelines`' `Pipe` is general-purpose: it locks, it schedules, it manages its own segment
lifetime, and it is written to let arbitrary producers and consumers meet. Our half is neither arbitrary
nor general — one producer, one consumer, a known owner thread, and a transport that already owns pooled
(often pinned) memory. A purpose-built duplex pipe could:

- hand the app **the transport's own buffers** rather than copying into pipe segments — which is BYO in
  the direction we currently cannot do at all (there is no receive-side zero-copy on any backend), and
  which is the same mechanism that would make inbound backpressure real rather than advisory;
- drop the locking for an SPSC ring, since the producer/consumer identities are fixed;
- decide scheduling per direction instead of taking `PipeScheduler`'s general answer.

**The obvious risk, stated up front:** Kestrel's transport contract is `IDuplexPipe`, and anything we hand
it must behave exactly like a `Pipe` under every access pattern Kestrel uses (`ReadAsync`/`AdvanceTo`
combinations, examined-vs-consumed, cancellation, completion with exceptions). That is a correctness
surface much larger than it looks, and `SmokeTest --pipe` is the only harness that would catch a mistake.
Weigh that against the measured prize: the whole bridge is 24-42% at 256KB and ~2% at 64KB, so this is a
large-payload play, not a general one.

### 2b-result-2. io_uring zero-copy send: +45.1% at 256KB, and IOCP's null result is EXPLAINED (2026-07-29)

**Status: DONE for io_uring send. The measurement that motivated de-prioritising this work was wrong for
a specific, now-measured reason.**

A/B against the same `--byo` bridge, 12 shards, `-c 64`, 6 scored passes:

| payload | classic | byo + zero-copy | change |
|---|---:|---:|---:|
| 64 KB | 10,423.2 | 10,613.0 | +1.8% (ranges nearly overlap) |
| 256 KB | 7,950.2 [7795-8158] | **11,536.1** [11520-11682] | **+45.1%** |

Verified taken rather than declined: `zero-copy=11,680,439` segments, `pooled-page=0`,
`pinned-managed=0`.

**Why IOCP measured nothing at 256KB, now measured rather than suspected.** That same counter gives
**exactly 65.0 segments per response** - Kestrel's 4KB blocks make a 256KB body 64 segments, plus one for
headers. `IocpConnection.MaxSendPages` is **64**, so IOCP declined *every* 256KB response and fell back to
copying. `2b-result` guessed this ("probably binds at 256KB... instrument the decline rate first"); it is
now a number. io_uring's `IovMax` is 1024 and never hits it.

**Follow-up for a Windows host, pre-registered:** raise `MaxSendPages` above 65, or implement the
send-a-PREFIX fix `2b-result` sketched (`TrySendZeroCopy` reporting bytes instead of a bool), and IOCP
should show a large-payload gain of the same shape. If it does not, the cap was not the explanation.

**RUN 2026-07-29, AND THE CAP WAS THE EXPLANATION — see item 2f.** The flag route (`--pipe-segment
65536`, no code change) was tried first and is worth **+117.3% at 256KB** on IOCP, with
`SS_IOCP_STATS=1` confirming the 65.00-segment decline on Windows directly rather than by inference from
this counter. So this entry's prediction is confirmed, and by 2.6x more than "the same shape" implied.

**Consequences for the rest of this file:**

- **"Per-byte copying is not the constraint" is now bounded, not general.** It held for allocations
  (`fa97dd4`), for page size, and at 16KB. At 256KB, removing copies is worth +16.3% (epoll's `Flush`
  snapshot) and +45.1% (io_uring zero-copy). The constraint is payload-dependent and the old wording
  over-generalised from small-message evidence.
- **The bare responder is no longer a ceiling.** Bridged byo (11,536) beats the bare responder (10,352)
  because they no longer run the same code - `HttpBench` still copies via the callback path. Do not
  compute "bridge cost = bare - bridged" for a byo leg.
- **The gap to vanilla Kestrel at 256KB is 7.3%, from 36%** (12,450.5 vs 11,536.1). Kestrel is zero-copy
  in BOTH directions and pays no bridge; the obvious next term is the receive side, where we still copy
  and it does not - which is the zero-copy-receive/receive-parking work that has never been started.
- **The pin cost did not bite.** 65 `GCHandle` pins + disposes per response and it still won by 45%, so a
  pinned-block pool (item 2d) is upside on top, not a precondition.

### 2f. Make IOCP's zero-copy send survive a fragmented sequence (raised 2026-07-29)

**Status: OPTIONS 1 AND 2 DONE. The cliff is gone rather than moved — a fragmented sequence of ANY size
now goes out zero-copy. Option 3 (coalescing small trailing segments) remains, and is now optional.**

**Option 2, done 2026-07-30.** `Connection.TrySendZeroCopy` returns **bytes accepted** instead of a
bool; IOCP sends the first `MaxZeroCopySegments` segments and reports how many bytes that was, and
`PipeIoBridge` advances its reader by exactly that much and re-offers the remainder on the next read.
io_uring kept all-or-nothing at first — its cap is `IovMax` 1024 against IOCP's 256, so its cliff is far
away rather than absent. **That follow-up is now DONE too (2026-07-31): io_uring adopts the same prefix
behaviour, after measuring that the cliff is real (an 8MB response was 100% copy before, 100% zero-copy
after). See §3 item 3 for the measurement and the change.**

*Measured, isolated worktrees, interleaved, 6 scored passes, `--byo`:*

| payload | before | after | |
|---|---:|---:|---|
| 256 KB | 7,346.8 [7168-7743] | 7,329.0 [7035-7545] | **-0.2%, ranges overlap** — no cost where the cap already fit |
| **1 MB** | **2,422.0** [2399-2503] | **4,374.1** [4365-4449] | **+80.6%, fully disjoint** |

*And the counter proves the mechanism rather than inferring it* — at the default ~4KB pipe blocks:

| payload | segments/response | before | after |
|---|---:|---|---|
| 256 KB | 65 | zero-copy | zero-copy, 0 prefixes |
| 1 MB | 257 | **declined, 200 WSASends copying** | 80 zero-copy sends, 40 prefixes, **copying path silent** |
| 4 MB | 1,025 | **declined, 680 WSASends copying** | 200 zero-copy sends, 160 prefixes, **copying path silent** |

**What this retires:** `--pipe-segment 65536` is no longer needed to get zero-copy to engage at all — it
is now purely a tuning knob for segments-per-send. The memory finding still stands, so if it IS used,
`--pipe-pinned` remains its required companion.

**AND THE COMBINED EFFECT, measured in one session across four payloads with a same-session Kestrel
control (2026-07-30):** the tuned configuration is now **+14.2% FASTER than vanilla Kestrel at 1MB**
(5,640.6 against 4,938.4, disjoint) and at **parity at 256KB**. That is the first time anything here has
beaten Kestrel at a large payload. `--pipe-segment` still earns +27.6% at 1MB and +37.9% at 256KB over
plain `--byo`, so segments-per-send remains a real second effect even with the cliff gone.

**~~The number that matters for anyone NOT opting in is unchanged and bad~~ — ANSWERED 2026-07-31 by
making BYO the DEFAULT.** The classic bridge was -60.3% against Kestrel at 256KB and -52.6% at 1MB, and
everything good was behind an opt-in flag. `DemoConfig.ByoPipe` now defaults to true; `--classic`
(alias `--no-byo`) opts out.

*Three things that were not one-liners, recorded because the sketch "just default the property to true"
misses them:*

- **`--kestrel` would have thrown on startup.** `Validate` rejected `ByoPipe && VanillaKestrel`, so the
  default would have failed every vanilla-Kestrel control leg — the leg every headline comparison is
  measured against. It now turns the default off silently for `--kestrel` and rejects only an EXPLICIT
  `--byo` alongside it, which is a real contradiction rather than a default that does not apply.
- **The banner said `byo=pipe` only when ON**, so "classic" was the ABSENCE of a string. Fine while byo
  was opt-in; useless as a default, and a harness gating on absence cannot tell classic from an older
  build that had no byo at all. It now always reports `byo=pipe` or `byo=off`.
- **Three rigs took the classic leg as the no-flag leg** (`Run-Byo.ps1` x2, `run-byo.sh`,
  `run-pipe-opts.sh`) and asserted it must NOT report `byo=pipe`. All now pass `--classic` explicitly and
  gate on `byo=off`.

**The classic path is kept, not deprecated:** it is the control every zero-copy claim is measured
against, and it is the only path available on backends that cannot do zero-copy send at all (RIO,
managed).

*Original entry (option 1) follows.*

**Status: OPTION 1 DONE 2026-07-29 (+61.1% with no flag, and a p99 bill). Options 2 and 3 still open,
and the p99 result is the argument for doing one of them.**

`MaxZeroCopySegments = 256`, split from `MaxSendPages`, with `ZcPtrs`/`ZcLens` allocated on first use so
the bigger cap costs *less* than the old eager 64 (a callback-path connection now allocates nothing
rather than 768 bytes). Smoke matrix re-run: 47/48, the same pre-existing `rio+tls` cell.

Measured, 6 scored passes, both untouched legs holding as controls:

- **`--byo` with the default ~4KB pipe blocks: 5,243.6 -> 8,447.9 MiB/s at 256KB (+61.1%, disjoint)**,
  declines 194,804 -> **zero**, no flag involved.
- **With a same-session vanilla-Kestrel control, `--byo --pipe-segment 65536` is -2.4% against Kestrel
  at 256KB**, where the default bridge is -56.9%.

**But p99 on the no-flag path nearly triples** - 15,255us against 6,291us classic and 4,509us at 64KB
blocks. 65 pins and one long send occupancy per response, against one send in flight per connection, is
head-of-line blocking. So the split removes a *silent cliff* (a caller one segment over lost 2.2x with
no way to see it); it does not make the flag unnecessary, and nobody should read it that way.

**What is left, and the pre-registered prediction now has evidence behind it:** the cliff moved rather
than went - a 1MB response is **257** segments and a 4MB response **1,025**, both measured, so both still
decline at 256. Option 2 (send a PREFIX; `TrySendZeroCopy` reports bytes rather than a bool) is the one
that removes it, and the p99 result says it should *also* help tail latency by capping how much one send
occupies the connection. Option 3 (coalesce small trailing segments) remains the best-of-both.

**New, and cheap, found while doing this:** `TrySendZeroCopy` allocates a `MemoryHandle[n]` **per
response** on the pump thread whenever the caller has not asserted pinned memory. This repo has already
measured +27% for removing one per-response allocation (`fa97dd4`), and the array can be pooled per
connection exactly as `ZcPtrs` now is. `--pipe-pinned` sidesteps it entirely (the handles array is null
when the pool is pinned), which is a second, independent reason to look at item 2d.

*Original entry follows.*

**Status: specified, not started. This is what converts a +117.3% flag into a default.**

`--pipe-segment 65536` buys +117.3% at 256KB on IOCP purely by getting the response under
`IocpConnection.MaxSendPages` = 64 (measured: the default configuration declines at **65.00** segments -
off by one). That is a *demo* flag configuring *Kestrel's* pipes. A library caller with its own pipes,
or any caller whose sequence happens to be fragmented, still silently falls back to copying and pays the
same 2.2x. The transport should not be one segment away from a cliff it cannot see.

Three options, in increasing order of how much they actually fix:

1. **Raise the cap.** One constant. `WSASend` accepts far more than 64 `WSABUF`s, and the arrays are
   per-connection (`ZcPtrs`/`ZcLens`/`SendPages`/`SendLens`, four arrays x `MaxSendPages` x connections),
   so the cost is memory per connection and a bigger `stackalloc` per send. Cheapest, and it moves the
   cliff rather than removing it.
2. **Send a PREFIX** - `TrySendZeroCopy` reports bytes accepted instead of a bool, sends the first 64
   segments zero-copy and lets the caller advance and re-offer the rest. Removes the cliff entirely and
   is the shape `2b-result` originally sketched. It changes the `PipeIoBridge` contract, because
   `AdvanceTo` must then consume a partial buffer.
3. **Coalesce small trailing segments** into one pooled page and send a mixed vector. Best of both and
   the most code.

**Do 1 first and measure**, because it is a one-line change against a now-instrumented path: with
`SS_IOCP_STATS=1` the decline count *is* the experiment, and it should reach zero without touching the
demo's pipe configuration. **Pre-registered:** if raising the cap to (say) 256 reproduces the +117%
without `--pipe-segment`, option 2's contract change is not worth making yet.

**The memory question is now MEASURED on Windows (2026-07-29), and it changed the recommendation.**
`bench/Measure-PipeMemory.ps1`, two independent runs at 2048 connections:

| leg @ 2048 conns | peak RSS | rps |
|---|---:|---:|
| classic | 404 / 386 MB | 208k / 206k |
| **byo + `--pipe-segment 65536`** | **1,282 / 1,285 MB** | **181k / 175k** |
| **the same + `--pipe-pinned`** | **388 / 346 MB** | 227k / 216k |

**`--pipe-segment 65536` on its own is the most expensive AND the slowest leg at 2048 connections** -
~3.2x the default bridge's memory and ~15% less throughput. **`--pipe-pinned` removes both**, landing at
or below `classic`. That reverses the Linux reading, where pinning measured +0.7%, "not separable", and
was filed as optional: on Windows it is `--pipe-segment`'s **required companion**, not a refinement.

At 64 connections the whole effect is invisible (0.99x) - the same connections-x-block trap the
receive-slab table fell into on 2026-07-28. Any memory claim here needs 2048.

**So the defensible configuration is `--byo --pipe-segment 65536 --pipe-pinned`, not `--pipe-segment`
alone** - and that gap is now closed too. `Run-Byo.ps1` gained a `byo-seg64k-pin` leg: at 256KB, 6 scored
passes, it measures **11,557.7 [11328-11777]** against `byo-seg64k`'s 11,394.6 [11274-11818] (overlapping
- **pinning costs nothing on throughput**) and against a same-session vanilla Kestrel of 11,715.7
(**also overlapping - parity**). The default `classic` leg is -55.0%.

**So there is no trade left to weigh on this pairing**: same throughput, parity with Kestrel, and
346-388MB instead of 1.28GB at 2048 connections. What remains before defaulting is a decision about
public behaviour, not another measurement - and note the memory figures were taken at a 4KB payload
while the throughput ones are at 256KB, so a single run covering both axes at once would still be the
most honest evidence to default on.

### 2b. BYO-buffer, phase 2: per-backend zero-copy, IOCP first

**Status: designed, not started. Now the highest-value item on this list, with a measured target.**

Phase 1 measured at parity (above), so everything below is the part that pays — and the end-to-end run on
2026-07-27 says how much. Bare tuned RIO does 11,030 MiB/s at 256KB; through the Kestrel bridge it does
6,348. **The bridge costs ~42% on the best configuration**, up from the 14-19% measured the same day on the
untuned one. The bridge did not get worse; the transport got fast enough to expose it. That 42% is what
zero-copy is aiming at, and it is measured rather than assumed.

IOCP is the right first driver because the shape already fits: its send
path takes a `WSABUF` ARRAY, and a `ReadOnlySequence<byte>` is exactly an array of segments. Outbound
becomes "build WSABUFs over the sequence's segments and issue one WSASend", with no staging copy at all.

- **Outbound:** replace the `Send(in ReadOnlySequence)` copy with WSABUFs pointing straight at pipe
  memory. The send must not complete-and-`AdvanceTo` until the WSASend completes, so the pump has to hold
  the `ReadResult` across the operation - this is the real design change, and it is what `pinned` is for
  (skip per-buffer pinning when the pool is already pinned; otherwise pin for the operation's duration).
- **Inbound:** `pipe.Output.GetMemory(hint)` -> pin -> WSARecv straight into it -> `Advance` -> flush,
  instead of receiving into the shard's slab and copying. This is where parking the receive on a pending
  flush becomes both possible and necessary.
- **RIO cannot follow** on the outbound half: `RIO_BUF` addresses a REGISTERED buffer by id, never a raw
  address, so foreign pipe memory is unusable unless the caller's pool is registered up front. RIO keeps
  the fallback, which is exactly why the fallback exists.
- Measure against the fallback's own numbers, not against the callback path, so the comparison isolates
  what zero-copy buys rather than what pipe mode costs.

### 2c. BYO-buffer: original notes on caller-supplied (pinned) pipes and the copies below them

> **DE-PRIORITISED 2026-07-27. The three measurements below were run, and they refute the premise.**
>
> The hypothesis was that the three copies dominate the large-payload gap. Two independent results say
> otherwise:
>
> - **Removing one ALLOCATION and zero copies** (`fa97dd4`) moved 256KB by **+27%**.
> - **Changing page size** moved RIO by **4.68x** at 256KB - and page size changes the number of segments,
>   not the bytes copied. 256KB is 256KB either way.
> - At a 64KB page RIO reaches 11,180 MiB/s against IOCP's best 6,083 while running an identical copy
>   path. If copies dominated, they would converge, not sit 84% apart.
>
> **Allocation and per-operation cost dominate; per-byte copying does not.** Tier 1's allocation half is
> already delivered by `fa97dd4`. Tier 2 buys the copies at the price of threading a completion signal
> through every backend's send path - aimed at a cost that is not binding.
>
> Not dead, but not next. Revisit if a real-NIC run (where memory bandwidth is contended, unlike loopback)
> shows a different shape, or if allocation/GC pressure turns out to matter for its own sake rather than
> for throughput. Also note the bridge measured at 14-19%, not the ~47% assumed below.

**Original reasoning, kept for the record.** The 2026-07-27 sweep puts IOCP at
4,483 MiB/s against Kestrel's 11,489 at 256KB - a 61% gap, and the largest open number in
`RESULTS.md`. The working hypothesis is that the three copies below dominate it. That is
plausible (the ratio, 2.56x, is about what 2-3 extra copies per byte would cost) but **not established**,
and two things argue for checking before committing to a design change that ripples into every backend's
send-completion path:

1. *Part of that 61% is the bridge, not the transport.* This sweep runs through AspNetDemo. The last
   controlled bare-vs-bridged comparison put the bridge at 23% at 256KB, and that was **before** IOCP got
   faster - the bridge's share was already noted as having grown from ~23% to ~47% of the remaining gap.
   Re-measure `SmokeTest --http` against the bridged transport on this host, same pinning, same payload.
   BYO-buffer cannot address the bridge's share.
2. *A per-byte cost should not widen as a fraction of the total, and this one does.* IOCP goes
   3,741 -> 4,483 MiB/s from 16KB to 256KB (+20%) while Kestrel goes 4,008 -> 11,489 (+187%). N fixed
   copies per byte would give a roughly constant relative penalty once fixed costs amortise. Something
   else also degrades with size - segment/page management, the `Pending` queue, or the bridge's per-write
   behaviour. Only the copies are in BYO-buffer's scope.

Cheapest discriminators, in order, all available today:

1. **Bare responder control** - separates bridge from transport. Nothing to build.
2. **The voided `fa97dd4` A/B, re-run at 256KB** - pre-registered: moves throughput => allocation was the
   cost; does not => copies dominate. Never yet run validly.
3. **Tier 1 below** - removes exactly one of the three copies, with no API change. If one copy of three
   buys roughly a third of the gap, the hypothesis is confirmed and Tier 2 is justified. If it buys
   nothing, BYO-buffer is aimed at the wrong target.

Only then Tier 2.

Proposal: let a connection optionally accept a caller-supplied `IDuplexPipe` plus a "this memory is
pinned" flag, and drive I/O straight off it - so a caller who hands us a pipe backed by a pinned-heap
pool (as Kestrel's own `PinnedBlockMemoryPool` does) pays no pinning and no copy.

Right target. Today the out-of-band path copies **three times** for one response:

1. `OutboundConnection.Flush` -> `w.WrittenSpan.ToArray()` - an UNPOOLED full-size allocation
2. `PumpFlush` -> `StageOutbound` copies that array again into pooled `Pending` segments
3. the drain copies those segments into write pages

Split it into two tiers, because only one of them is cheap.

**Tier 1 - no API change, do this first.** Rent in `Flush` instead of `ToArray`, and transfer ownership of
that array straight into `Pending` rather than copying it again. Removes one allocation and one full copy
per response. The only care needed is that `Pending` segments currently must not exceed a write page (the
packing invariant in `DrainPendingIntoPages`), so an over-page segment has to be split or handled. This is
the piece worth doing on its own merits - the bridge is now ~47% of the remaining gap to Kestrel.

**Tier 2 - the actual BYO-pipe. Not a no-brainer; two things bite.**

*Ownership, not pinning, is the blocker.* `Flush` snapshots via `ToArray` precisely so the writer may reuse
its buffer immediately. Sending directly from pipe memory means the pump must not `AdvanceTo` until the
send completes, so `Connection.Send`/`Flush` need a completion signal (`ValueTask` / `IValueTaskSource`)
rather than today's fire-and-forget bool. That is the real design change, and it ripples into every
backend's send-completion path.

*"Pinned" is not a sufficient capability, because RIO does not take addresses.* RIO wants
`RIO_BUF { BufferId, Offset, Length }` where `BufferId` comes from `RIORegisterBuffer` - so caller memory
is unusable to it unless that slab is registered up front. Meanwhile io_uring only needs a stable address
(pinned suffices) and epoll needs nothing at all, since `send()` is synchronous and copies before
returning. So the contract is not one `IsPinned` bool; it is a per-backend capability, something closer to
"can you accept foreign memory, and on what terms" - with RIO able to answer "only if I registered it".

A workable shape: the caller supplies the pool, the SET registers/pins it once at construction (RIO
registers the slab; io_uring/IOCP just require pinning; epoll ignores it), and connections then hand out
memory from it. That inverts the direction - we accept a POOL, not a pipe - and sidesteps the per-buffer
capability question entirely. Probably the better design.

### 3. The AspNetDemo bridge - real, but much smaller than first claimed

**Correction.** This was described as "the bottleneck at large payloads" on the strength of the Linux
sweep. A controlled Windows comparison - same client, same pinning, same payload, bare SocketSet HTTP
responder vs the same transport behind the Kestrel bridge - puts the bridge at **12% at 16KB and 23% at
256KB**, not the ~4x it was blamed for:

| | 16KB | 256KB |
|---|---:|---:|
| bare SocketSet responder | 1,655 | 2,454 |
| AspNetDemo iocp/s16 (bridged) | 1,455 | 1,899 |
| AspNetDemo kestrel (own transport) | ~3,740 | ~9,417 |

The bridge is worth fixing (the per-flush `ToArray()` in `OutboundConnection.Flush` allocates a full-size
array per response, unpooled, on the ThreadPool thread that also runs Kestrel), but item 0 above is the
larger prize and it is in the transport itself.

### 3b. Original bridge write-up (superseded by the measurement above)

kestrel+tls hit 8,383 MiB/s against plaintext io_uring's 4,379 - a TLS leg beating a plaintext leg 2:1,
which is only possible because they are limited by different things. The SocketSet legs go through the
demo's Kestrel bridge (two `Pipe`s plus a copy per write in `SocketSetConnection`), and at large payloads
that bridge, not the transport or the cipher, is what is being measured.

If plaintext kestrel also lands near 8 GB/s, the bridge is costing 2-4x on big responses - which matters
more to the "ASP.NET Core over SocketSet" story than any transport difference measured so far.

### 3c. epoll + kTLS behind a toggle (raised 2026-07-29) — and epoll is where kTLS should look BEST

**Status: IMPLEMENTED 2026-07-31, correctness-clean. The "85% already done" estimate held — the new code
is ~130 lines in `EpollShard.cs` (`StartKtls`/`KtlsPump`/`KtlsComplete`/`KtlsRead`/`ReportKtlsOnce` plus
`FireOpen`/`HandleConnection`/teardown wiring and three `EpollConnection` fields). As predicted, TX needed
nothing (plaintext `send()`, kernel encrypts, reusing `SendBytes`) and RX is the backend's own idiom
(`EPOLLIN → SSL_read`). `--epoll --ktls` reports `[ktls/epoll] tx=True rx=False` on this box's OpenSSL
3.0.13 (TX genuinely kernel-offloaded; RX userspace, as expected < 3.2) and passes the smoke matrix:
callback echo, pipe echo, out-of-band verify at 4MB, and ALPN (h2). New `*+ktls` cells in
`run-smoke-matrix.sh` cover io_uring and epoll.**

**MEASURED 2026-07-31, and the pre-registered prediction (below) is FALSIFIED.** Same-session
`bench/run-tls-sizes.sh` (SHARDS=12, `-c 64`, 4 scored passes, disjoint ranges), small-message rps:

| leg | 512 B | 4 KB |
|---|---:|---:|
| epoll+tls | ~592,000 | 2,034.7 MiB/s |
| **epoll+ktls** | **~537,000 (−9.3%)** | **1,785.2 MiB/s (−12.3%)** |
| iouring+tls (control) | ~585,000 | 1,900.8 MiB/s |
| iouring+ktls (control) | ~516,000 (−11.7%) | 1,761.7 MiB/s (−7.3%) |

**epoll+ktls does NOT reach epoll+tls — it trails by ~9-12%, comparable to io_uring's ~8-12%, ranges
disjoint (not noise).** The prediction said epoll should reach parity because it has no multishot receive
to forfeit; it did not, so **most of the kTLS small-message penalty is the kTLS record path itself, not
multishot forfeiture** — epoll pays ~9% while forfeiting nothing multishot-related. What differs between
the two epoll legs: kTLS moves TX encryption into the kernel (worth ~nothing at 512 B, where crypto is
rounding error) but RX still goes through `SSL_read` on a kTLS-enabled socket, whose per-read overhead is
what shows up. Any multishot-forfeiture cost is at most the small residual between io_uring's gap and
epoll's, and that residual is inside the cross-backend noise here.

**Scope caveat:** this is TX-only offload (RX userspace on OpenSSL 3.0.13, `[ktls/epoll] rx=False`). With
RX offloaded (OpenSSL 3.2+, item 4b) the RX `SSL_read` cost could change and io_uring could regain
multishot over provided buffers — so this falsification is about TX-only kTLS on loopback, and the
RX-offloaded picture is still unmeasured. NIC offload stays structurally invisible on loopback regardless.

*Original entry follows.*

**The estimate "85% of the work is already done" was checked and holds.**

Already shared, i.e. nothing to write: `OpenSslTlsProvider.CreateKernelSsl` (the `SSL*` bound to the fd
with kernel offload), `KtlsProbe` (capability detection), the `NativeOpenSsl` bindings
(`SSL_do_handshake`/`SSL_read`/`SSL_write`/`SSL_shutdown`/`SSL_get_error`), and
`OpenSslTlsFilter.GetAlpnSelected`. None of that is io_uring-specific.

What is io_uring-specific, and is what epoll needs (~150 lines, `IoUringShard.cs:1066-1180`):
`StartKtls` → `KtlsPump` (drive the handshake) → `KtlsComplete` → `KtlsRead`/`KtlsRespond`, plus teardown
(`SSL_shutdown` while idle, `SSL_free` in finalize) and a per-connection plaintext receive buffer.

**And the mapping is EASIER on epoll, not harder.** io_uring drives kTLS receive as `POLL` + `SSL_read` -
it has to synthesise readiness, because its native model is completion. epoll *is* readiness, so
"`EPOLLIN` → `SSL_read`" is the backend's own idiom rather than an adaptation. TX needs nothing at all: it
is a plaintext `send()` that the kernel encrypts, and epoll already sends plaintext directly from its
buffer.

**The non-obvious reason to want it, beyond parity.** Every kTLS number on file is io_uring's, and
io_uring pays a structural penalty for kTLS that epoll does not: kTLS forfeits `IORING_RECV_MULTISHOT` and
provided buffers, dropping io_uring to one syscall per message (that is what item 4 is about). **epoll has
no multishot to give up** - it is one `recv` per readiness event either way - so kTLS should cost epoll
nothing structurally. If that holds, `epoll+ktls` is the configuration where kTLS finally competes on this
codebase, and it is measurable on loopback because it is a comparison against `epoll+tls` on the same
backend rather than a claim about NIC offload.

*Pre-registered:* `epoll+ktls` should land at or above `epoll+tls` at small messages, where
`iouring+ktls` trails `iouring+tls` by ~15% (597,486 vs 702,829 rps). If it trails on epoll too, the
"multishot forfeiture" explanation is wrong and the cost is in the kTLS record path itself.

**→ RESULT (2026-07-31): it TRAILS on epoll too (−9.3% at 512 B, disjoint), so the second branch is the
one that fired — the cost is the kTLS record path, not multishot forfeiture. Full numbers at the top of
this item.**

**Factoring, since two backends will then want the same pump.** The Ktls\* methods are parameterised by
only two backend concerns - "tell me when the fd is readable" and "send these plaintext bytes" - so the
handshake drive and the read/respond loop can move to a shared helper with those as callbacks. Worth doing
as part of this rather than leaving a third copy of the send machinery, which is the mistake this file has
already recorded twice (TLS written twice, epoll's send path a third copy).

**Toggle:** ship it behind the existing `--ktls` selection path plus a capability check, and keep the
`bench/README.md` rule that `/config` must report what was actually negotiated - `tls_stat` verification
against the kernel counters is what caught "kTLS is TX-only" and must gate any epoll+ktls leg too.

### 4b. "kTLS costs us multishot RX" — the PREMISE IS WRONG, and the real blocker is that RX was never offloaded (2026-07-29)

Raised from an external, **unverified** claim: *io_uring can keep multishot RX with kTLS provided you use
TLS 1.3, because the kernel decrypts in place and plaintext lands in the provided-buffer ring; TLS 1.2
breaks it because the crypto layer's cmsg/header trick conflicts with `recvmsg_multishot`'s packed
layout.* Worth chasing, but the framing does not match what this codebase actually does, and the
correction matters more than the claim.

**What is verified here, not recalled:**

- We drive kTLS receive as io_uring `POLL` + `SSL_read` - one syscall per message, no multishot, no
  provided buffers (`IoUringShard.KtlsRead`).
- **That is not a sacrifice made FOR kTLS. It is forced because kTLS RX is not on.** `/proc/net/tls_stat`
  on this host reads `TlsTxSw = 3,612`, **`TlsRxSw = 0`** cumulative across every kTLS run ever made here.
  TX is offloaded; **receive has never been offloaded at all**, so OpenSSL still decrypts in userspace,
  still needs the ciphertext, and owns the fd (`SSL_set_fd`) - which is exactly why reads must go through
  `SSL_read` rather than through our own multishot recv.
- We never call `setsockopt(SOL_TLS)` ourselves. We set `SSL_OP_ENABLE_KTLS` and let OpenSSL decide, so
  whether RX engages is OpenSSL's choice, not ours.

**So the causal chain is the other way round from the claim.** Multishot is not lost *because* kTLS is on;
it is lost because kTLS RX is *off*, leaving userspace decryption in the path. **If RX offload were
active, plain `recv` returns plaintext, and `IORING_OP_RECV` + `IORING_RECV_MULTISHOT` over a provided
buffer ring should work unmodified** - that part of the external claim is mechanically sound, and it is
the prize.

**RESOLVED 2026-07-29: OpenSSL 3.0.13 declines kTLS RX for TLS 1.3; a self-built 3.5.7 grants it.**
Validated by building OpenSSL 3.5.7 with `enable-ktls` and running the same probe against it - same box,
same code, TLS 1.3 both times:

| OpenSSL | client | server | probe |
|---|---|---|---|
| 3.0.13 (system) | TX=True **RX=False** | TX=True **RX=False** | FAIL |
| **3.5.7 (self-built)** | TX=True **RX=True** | TX=True **RX=True** | **PASS** |

**Confirmed end to end in the real transport, not just the probe:** the io_uring kTLS echo against 3.5.7
moved `/proc/net/tls_stat`'s `TlsRxSw` off zero for the first time (**0 -> 8**) with the byte-exact echo
still passing. So **Path A works** and no protocol downgrade is needed: TLS 1.3 + kTLS RX + multishot is
available on OpenSSL 3.2+.

*(A first attempt using the flatpak runtime's 3.5.7 reported TX=False AND RX=False - kTLS not engaging at
all. That is not the same as declining RX, so it was discarded as inconclusive rather than quoted. The
probe now prints the loaded version and whether the build has kTLS, so this cannot be misread again.)*

**The backend now reports what it got**, once per process:
`[ktls] openssl=3.0.13 tx=True rx=False -- RX NOT offloaded: ... OpenSSL 3.2+ is required ...`. A silent
half-offload is what made every kTLS figure on file mean something other than it appeared to.

*How it was narrowed, kept for the method.* `SmokeTest --ktls-spike` takes two switches, so the candidates
could be separated in seconds:

| run | TLS version | client | server | probe |
|---|---|---|---|---|
| baseline | 1.3 | TX=True **RX=False** | TX=True **RX=False** | FAIL |
| `SS_KTLS_CLEAR_NO_RX=1` | 1.3 | TX=True **RX=False** | TX=True **RX=False** | FAIL - no change |
| **`SS_KTLS_FORCE_TLS12=1`** | **1.2** | TX=True **RX=True** | TX=True **RX=True** | **PASS** |
| both | 1.2 | TX=True RX=True | TX=True RX=True | PASS |

**So: OpenSSL 3.0.13 declines kTLS RX for TLS 1.3 and grants it for TLS 1.2.** The `SSL_MODE_NO_KTLS_RX`
theory is refuted (clearing it changes nothing). The "you never called `SSL_read`" and "you used a memory
BIO" theories were already excluded - this probe does neither, and still saw RX=False on 1.3.

**The common advice "enforce TLS 1.3 to keep multishot" is therefore backwards on this OpenSSL**: 1.3 is
exactly what keeps RX offload off. The honest options are (a) OpenSSL **3.2+**, which is *said* to support
TLS 1.3 kTLS RX - unverified here, this box has 3.0.13 - or (b) cap at TLS 1.2, which is a real security
and performance regression and is only sane as an experimental vehicle, not a shipping default.

**And the property multishot needs is directly confirmed.** In the passing runs the probe completes a
**plain `send` -> plain `recv` round-trip over the kTLS socket** - the kernel doing the crypto, no OpenSSL
in the data path. That is precisely what `IORING_OP_RECV` + `IORING_RECV_MULTISHOT` over a provided-buffer
ring requires, so the external claim's core mechanism holds; only its version advice was inverted.

#### The three ways out, and what each actually costs

**A. Require OpenSSL 3.2+ for kTLS RX.** The cleanest if it works. **Being validated** - the probe now
reports the loaded version and whether the build has kTLS at all (`OpenSSL_version_num` /
`OpenSSL_version(OPENSSL_CFLAGS)`), because a first attempt using the flatpak runtime's 3.5.7 returned
**TX=False RX=False** - kTLS not engaging *at all*, which is not the same as "declines RX" and must not be
read as evidence either way. Settling it needs a build configured `enable-ktls`.

**B. Extract the keys and call `setsockopt(SOL_TLS, TLS_RX)` ourselves, bypassing OpenSSL.** Possible, but
**the API usually named for this is the wrong one**: `SSL_export_keying_material` is the RFC 5705 / RFC
8446 §7.5 *exporter*. It derives fresh key material from the exporter master secret for other protocols
(channel binding, DTLS-SRTP); it does **not** return the record-protection traffic keys, and keys obtained
that way decrypt nothing. The actual route for TLS 1.3 is `SSL_CTX_set_keylog_callback` ->
`CLIENT_TRAFFIC_SECRET_0` / `SERVER_TRAFFIC_SECRET_0` -> HKDF-Expand-Label("key") and ("iv") per RFC 8446
§7.3 -> fill `tls12_crypto_info_aes_gcm_128/256`.

Three things make it harder than the snippet suggests, and all three fail *silently* (garbage plaintext or
a dead connection, not an error):
  - **The record sequence number must be exact at handoff.** `rec_seq` has to be OpenSSL's current read
    sequence, and the handoff must happen when OpenSSL holds no partially-consumed record - it may already
    have eaten post-handshake records (NewSessionTicket) and advanced past them.
  - **We inherit rekeying.** TLS 1.3 KeyUpdate arrives as a handshake record, so after handoff *we* must
    notice it (via the cmsg path) and push new keys. This kernel does support it - `/proc/net/tls_stat`
    here exposes `TlsRxRekeyOk` / `TlsRxRekeyError` / `TlsRxRekeyReceived` - but it becomes our job.
  - **It is a reimplementation of part of OpenSSL's key schedule**, in a place where a mistake is a
    security bug rather than a perf bug.
  So: a legitimate fallback for old OpenSSL, not the first thing to reach for.

**C. Cap at TLS 1.2 where kTLS RX matters.** Works today (measured above), needs no new code beyond a
version knob - and is a real security/performance regression. Defensible as an experimental vehicle for
measuring multishot+kTLS, not as a shipping default.

**Recommended order: validate A; if it holds, require 3.2+ for the kTLS-RX path and keep the current
TX-only behaviour as the fallback on older OpenSSL (which is what the code already does, correctly, by
discovering capability rather than assuming it). B only if A is unavailable and the win is proven.**

**If RX offload can be turned on, the follow-on work is small and the payoff is item 4's whole point:**

- Receive becomes `IORING_OP_RECV` multishot over the provided-buffer ring, returning plaintext - i.e. the
  same fast path as plaintext io_uring, which is the thing kTLS currently forfeits.
- **The one real complication is control records.** With kTLS RX, a non-`application_data` record (alert,
  KeyUpdate, close_notify) cannot be delivered through plain `recv`; the kernel fails the read and the
  record type is only retrievable via `recvmsg` + the `TLS_GET_RECORD_TYPE` cmsg. So the shape is:
  multishot `RECV` as the fast path, and on that specific failure fall back to a one-shot `RECVMSG` +
  cmsg, handle the record, and re-arm. `TlsContentType` already exists for exactly these values.
- The external note's `recvmsg_multishot` objection then does not apply, because the fast path is `RECV`
  multishot (no `io_uring_recvmsg_out` header), not `RECVMSG` multishot.

**Why it is worth doing:** kTLS currently trails userspace TLS on every leg (`iouring+ktls` 597,486 rps
against `iouring+tls` 702,829, -15%), and the standing explanation is precisely this forfeiture. This
would test that explanation rather than assume it - and item 3c predicts the same thing from the other
end, since epoll has no multishot to lose and should therefore not pay the penalty at all.

### 4. kTLS: implement the RECVMSG + cmsg receive arm

Only worth doing if kTLS matters. The current kTLS path drives receive as io_uring `POLL` + `SSL_read`,
one syscall per message, forfeiting multishot receive and provided buffers - the design seam described in
`TlsFilter`'s notes (`RECVMSG` + `TLS_GET_RECORD_TYPE` cmsg) is what would let it keep them. kTLS does
already pull ahead of userspace io_uring TLS at 256KB (+65%), so the crossover is real; this is what
would make it competitive rather than merely present.

Note loopback has no NIC, so kTLS's largest win - inline NIC offload - cannot appear in any of the
numbers above. Real hardware required before drawing conclusions about kTLS itself.

### 5. Real-hardware Linux run — PARTLY DONE 2026-07-28, and the rest has a route

The rigs are in-repo and runnable: `bench/run-matrix.sh` (transport matrix) and `bench/run-tls-sizes.sh`
(payload sweep).

**Done:** the container-on-WSL2 objection is gone. Linux now runs on bare metal (Pop!_OS 24.04, kernel
7.0.11) on the same desktop as the Windows numbers, with io_uring available natively. See
`bench/README.md` for the host and its setup.

**Not done, and not fixable here:** it is still ONE box over loopback. Two things stay structurally
invisible no matter how good the host is — kTLS inline NIC offload (`TlsTxDevice` is 0 and always will
be), and any effect where memory bandwidth is contended by a real NIC rather than shared by a client and
server on the same silicon. Every large-payload conclusion in this file carries that caveat.

**Route for the remainder (noted 2026-07-28):** a run on appropriate hardware is *planned to be
investigated later*, once the work is in a state worth testing. So this is a dependency with a plausible
path rather than a permanent ceiling — which is an argument for keeping the rigs and their
`/config` + `tls_stat` gates in a state someone else could run cold, and for writing results up to a
standard that survives being read by an expert audience.

## Check UDS on Windows

**Status: WAS BROKEN, FIXED 2026-07-31. And the 2026-07-27 "it WORKS" verdict below was an artifact of
the harness ignoring the flag — the very failure that entry congratulates itself on guarding against.**

### The verdict was wrong because the test never used UDS

`RunVerify` and `RunEchoVerify` in `SmokeTest/Program.cs` built `new IPEndPoint(Loopback, port)`
unconditionally. **They ignored `-u` entirely**, so every "UDS" result from them was a TCP run. The entry
below says it was "Checked deliberately, because 'the test passed' is not evidence when the flag might
have been ignored" — and the flag was being ignored, by the harness rather than by the transport. Rule 1
of `bench/README.md`, broken from the inside.

Fixed: both harnesses now take the endpoint, and their banner prints `transport=` and `endpoint=` so a
run says which transport it actually used.

### With `-u` honoured, UDS on IOCP did not work at all

`verify-echo` over UDS: **`roundtripped=0/262144`**. `verify`: **`FAIL (no connection accepted)`**. The
TCP control passed and — the decisive comparison — **the MANAGED backend over the same UDS path passed**,
so AF_UNIX works on Windows; it was the IOCP backend that did not.

**Cause:** `ConnectEx` requires an explicitly bound socket, and `IocpShard.Connect` bound only in the
`if (af == AF_INET)` branch. AF_UNIX fell through unbound, `ConnectEx` failed, `StartConnect` closed the
socket and freed the slot, and the connect silently never happened. The comment stating the requirement
was three lines above the branch that skipped it.

**Fix:** bind the AF_UNIX client socket to the UNNAMED address (family only, no path) before `ConnectEx`.
Unnamed rather than a temp path on purpose: the client end needs an address, not a name, and a named bind
would leave a file per outbound connection.

**Now passing over UDS on IOCP:** echo-verify (plaintext and SChannel TLS), out-of-band `--verify`
(plaintext and TLS), and churn. Stale socket files were already handled (`UnixSocketFile.PrepareForBind`
unlinks at bind), and an over-long path already fails with a clear .NET message naming the 108-char limit.

### `@abstract` names are now rejected before the socket layer is touched

`-u @name` on Windows used to create a FILE literally called `@name` in the working directory and carry
on. It now throws `PlatformNotSupportedException` from `SocketSet.Listen`/`Connect` — at the public API,
**before any socket is created**, so nothing has to be unwound. Validation lives there rather than in a
backend so every backend gets it: the managed path had the same hole.

### OPEN but MUCH milder than first recorded: a rare UDS churn leak — item 0f

**First recorded as "TCP 0/5, UDS 3/5", which was an unstable estimate from too few samples.** Measured
properly it is rare and, crucially, **it does not accumulate**:

| run | connections moved | leaked |
|---|---:|---:|
| first two batches, 9 reps total | ~12,000 each | **5 runs leaked `live=1`** |
| bisection baseline, 6 reps | ~12,000 each | 0 |
| soak, 3 x 6s + 3 x **60s** | **370,000 total** | **0** |

So: observed five times, always exactly one server-side connection, **never reproduced on demand since**,
and three 60-second runs moving 123,000 connections apiece came back clean. A per-connection leak would
have shown ~10x more at 10x the connections; it showed none. That makes it a rare teardown one-off
rather than the slot-exhaustion risk the first write-up implied.

Bisection (6 reps per variant) found the controls clean — **TCP 0/6 and the MANAGED backend over UDS
0/6** — so it remains UDS-and-IOCP-specific, but the baseline itself was 0/6 in that same run, which is
the honest reason no variant can be credited or excluded. `bench/Bisect-UdsChurnLeak.ps1` is kept for
whoever picks it up.

**Priority: low.** It is real, it is quiet, and it does not grow. Do not spend a session on it before
something with a measured cost.

*The original entry follows, and its verdict should be read as retracted.*

**Status: RAISED as suspected-broken, then TESTED and it WORKS (2026-07-27).**

Evidence, IOCP backend, filesystem path under `%TEMP%`:

- `SmokeTest --iocp -s -c 4 -t 6 -u <path>` reports `transport=uds`, listens on the path, creates the
  socket file, opens 4 client connections and exits 0.
- `SmokeTest --iocp --verify-echo 1048576 -u <path>` round-trips 1,048,576/1,048,576 bytes with zero
  mismatches on either leg.

Checked deliberately, because "the test passed" is not evidence when the flag might have been ignored and
the run silently fallen back to TCP - the banner and the socket file are what confirm it did not.

**Still untested on Windows**, so the item stays open with a narrower scope:

- TLS over UDS (SChannel), and the out-of-band `--verify` path over UDS.
- Churn/teardown over UDS: stale socket files at bind, and whether the shared cleanup helper is on the
  Windows path at all.
- That `@abstract` names are rejected with a clear error rather than silently creating a file called
  `@name` - the abstract namespace is Linux-only.
- Path-length limits (`sockaddr_un` is ~108 bytes; a long `%TEMP%` could overflow it).

Scope, so the test is not run against the wrong thing:

- **IOCP only.** RIO is TCP/UDP and cannot do AF_UNIX; the code already routes AF_UNIX to the IOCP
  backend, so `--rio` plus a UDS endpoint is out of scope rather than a bug.
- **Filesystem paths only.** The abstract namespace (`@name`) is a Linux extension - Windows AF_UNIX is
  filesystem-path only, and the interop already says so (`sockaddr_un (Windows AF_UNIX - filesystem path
  only, no abstract namespace)`). A Windows UDS test must use a real path.
- **Single listener.** AF_UNIX cannot multi-bind on any platform, so it takes the
  one-shard-listens-and-bounces path rather than reuse-port.

**Reproduce first:** `SmokeTest --iocp -s -c 8 -u C:\Users\<you>\AppData\Local\Temp\ss-uds.sock -t 10`,
then the same with `--verify` and `--verify-echo`. Record what actually happens - bind failure, accept
failure, silent hang, or success - rather than assuming.

**Things known to differ on Windows** and worth checking against whatever fails: `AcceptEx` /
`GetAcceptExSockaddrs` behaviour on AF_UNIX, `ConnectEx` needing an explicit prior bind, stale socket
files needing removal before bind (the shared helper does this for the Linux backends - confirm the
Windows path calls it), and path-length limits. Datagram AF_UNIX and `SO_PEERCRED` do not exist on
Windows at all.

If it turns out never to have worked, the honest options are to fix it or to say so in the README and
have the factory reject AF_UNIX on Windows loudly - what is not acceptable is the current state, where
nobody knows.

## Smaller / previously flagged

- **`close_notify` is never emitted on teardown**, on any backend. `TlsFilter.Shutdown` is implemented by
  both providers but no shard calls it; doing so means draining a final send before `closesocket`, which
  touches the teardown state machine. Deliberately deferred (2026-07-25) rather than changing teardown
  semantics on three backends inside a wiring change.
- **Negotiated cipher suite is not reported.** The AspNetDemo A/B pins the certificate so it is not a
  variable, but there is no way to confirm two legs landed on the same suite. ~40 lines:
  `SECPKG_ATTR_CONNECTION_INFO` for SChannel, `SSL_get_cipher`/`SSL_get_version` for OpenSSL, surfaced on
  `TlsFilter`. Worth having before publishing any numbers.
- **SChannel provider is `#if NET` only.** net472 would need substitutes for `X509ChainTrustMode`,
  `X509Certificate2.MatchesHostname` and `X509CertificateLoader`. The SSPI interop itself is fine there.
- **`Connection.Close()` is abortive** and truncates a queued send. Harmless on the client-close and
  abort paths, but a server-side graceful close with a pending write (or a Redis client closing with an
  unsent command) wants a flush-then-close primitive. Pre-existing; see `AspNetDemo/README.md`.
- **RESOLVED (2026-07-26): io_uring in Docker.** Two things were tangled together here, and the earlier
  entry got the conclusion wrong.
  1. Docker's default seccomp profile blocks the io_uring syscalls, so the backend silently falls back to
     managed sockets. Fix: `--security-opt seccomp=unconfined` (`--privileged` also works but changes far
     more than seccomp, which is what made this look kernel-related). Always check which backend was
     actually selected rather than assuming.
  2. With io_uring genuinely selected, every recv failed. That was *ours*, not the kernel's: multishot
     recv was submitted with a non-zero `len`, which the kernel rejects with `-EINVAL`. Fixed. io_uring
     now passes the full smoke matrix in a container, TLS included.

  Worth carrying forward: **io_uring reports operation failures as a negative `cqe.res`, not as a syscall
  error**, so a mis-built SQE presents as a healthy submit followed by silence - `strace` shows nothing
  wrong. `SS_URING_TRACE=1` now dumps every completion, which is what found it. Bisecting against a
  60-line raw-io_uring C probe (does NOP complete? does single-shot recv? multishot? with a buffer ring?)
  is what separated "kernel cannot" from "we asked wrongly".

  The claim in commits `925db23` / `1b48f8a` that io_uring was verified in a container was still wrong at
  the time - those runs were the managed backend, because seccomp had hidden io_uring from the probe.
