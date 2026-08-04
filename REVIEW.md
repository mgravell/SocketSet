# REVIEW

Security and correctness audits of record. Same contract as `RESULTS.md` has for measurements: every
finding is written down with what it was, how it was established, and what was decided, including the
ones that turned out to be nothing. `TODO.md` carries the resulting backlog items; this file carries the
reasoning behind them.

---

## AUDIT 2026-08-04: full-codebase security review (Marc's request)

Scope: the whole repo. `src/SocketSet` (engine, all five backends, both TLS providers, the native
interop), `src/SocketSet.AspNetCore`, `src/SocketSet.StackExchange.Redis`, `src/SocketSet.Garnet`, the
demos and the bench rigs. Test certificates excluded by Marc: they are demo material and their being
readable is not a risk.

### The headline

Marc's steer was that the P/Invoke might be where the bodies are. It mostly was not. The unsafe code is
careful, and the defer-recycle plus per-slot generation discipline around slot reuse holds up under
adversarial reading; **no memory-corruption bug reachable from the wire was found**. The buffer-id and
completion handling in `IoUringShard` in particular does the right thing in every teardown path I could
construct.

The exposure is somewhere less glamorous and more dangerous:

1. **Defaults that fail open.** A bind address that was ignored, hostname verification that skipped
   itself when unconfigured, an IPv6 address that got truncated instead of refused.
2. **Absent liveness limits.** No handshake timeout, no idle timeout, no working backpressure. Nothing
   bounds what one peer can hold or make you buffer.
3. **Buffer reuse surfaced through the public API.** `RawBuffer` past `PayloadBytes` is another
   connection's data, in the TLS case its decrypted plaintext, and nothing says so.

Three of these are worse in this codebase than they would be elsewhere, because `SocketSet.AspNetCore`
terminates TLS *below* Kestrel. Every Kestrel defence that keys off the connection (`HandshakeTimeout`,
`MaxConcurrentConnections`, `RequestHeadersTimeout`) is bypassed by construction: Kestrel does not see
the connection until the handshake it never supervised has already completed. We took those defences
away and did not replace them.

### Method note

Findings are ordered by severity as assessed, not by discovery order. Where a claim is behavioural
rather than read-from-source I say which. Two things I checked and could NOT substantiate are recorded
in "Looked at, not a finding" at the end, because a review that only lists hits is not a review.

---

## FIXED IN THIS SESSION

### F1. `Listen(IPEndPoint)` ignored the address and bound INADDR_ANY on every native backend

**Severity: high.** `IoUringFactory.cs:101` read `sin_addr = 0, // INADDR_ANY TODO: use the actual IP`.
Same literal at `IocpShard.cs:869` and `WindowsRioShard.cs:655`; epoll binds through
`IoUringFactory.Bind` so it inherited it. Only the managed backend (`s.Bind(target)`) honoured the
address.

So `Listen(new IPEndPoint(IPAddress.Loopback, 5000))` listened on every interface, silently.
`AspNetDemo/Program.cs:83` makes exactly that call. The backend split is what turns a limitation into a
trap: the same call is loopback-only when tested on `Managed` and world-reachable on the backend you
actually ship.

**Fixed.** `LibC.ToSinAddr` / `Win32.ToSinAddr` write the requested address on all four native
backends. Both helpers use `MemoryMarshal.Read<uint>` rather than a `Read{Little,Big}Endian`, because
what is wanted is the `uint` whose in-memory bytes are those four unchanged (`sin_addr` is a raw
network-order word), on either endianness.

**Gated.** Nothing in the repo could see this, which is why it survived: the smoke matrix binds
`IPAddress.Any`, which is 0.0.0.0 whether the argument is honoured or not, and connects to 127.0.0.1,
which an Any-bound listener answers happily. A bind that ignores its argument and one that honours it
were byte-identical under every existing gate. Added `bench/verify-bind-address.sh` plus a
`--bind-probe` mode on SmokeTest. Two cells per backend, and the second is the point: asking for
0.0.0.0 must still yield 0.0.0.0, or a build with the opposite hard-coding (always loopback) would read
as correct. The assertion is read out of `ss` by pid rather than printed by the probe, because a probe
reporting its own opinion of the bind address would have passed before the fix too.

### F2. An IPv6 endpoint silently connected to a truncated IPv4 address

**Severity: high.** The connect paths hard-coded `AF_INET`, set `sin_family = AF_INET`, and copied the
**first four bytes** of `ip.Address.GetAddressBytes()`. For a 16-byte IPv6 address that is four bytes of
something else entirely: dialling `2001:db8::1` connects, successfully, to `32.1.13.184`.

io_uring (`IoUringShard.cs:373`), IOCP (`IocpShard.cs:826`) and RIO (`WindowsRioShard.cs:624`) all did
this. epoll happened to escape it by accident: `EpollShard.cs:495` also truncated, but kept
`sin_family = ip.AddressFamily`, so the kernel rejected the mismatch and the dial failed loudly.

The compounding case is the reason this is high and not medium. A TLS client with no `TargetHost`
(finding F6 below, the default) that lands on an unintended host will complete a handshake against it,
because the only check being made is that the certificate chains to a trusted root.

**Fixed.** `LibC.RequireIPv4` / `Win32.RequireIPv4` reject non-`InterNetwork` endpoints on all four
native backends, on the **caller's** thread so the throw is synchronous and the capacity reservation is
released rather than leaked. The message names the managed backend as the IPv6 route. Real IPv6
support is a `sockaddr_in6` path on every backend and is backlogged, not attempted here.

While doing this I also found that epoll's `Connect` threw `NotSupportedException` for an unsupported
endpoint type **without** releasing its reservation, where io_uring released first. Same for the new
IPv4 rejection path on IOCP. Both now release on every rejection.

### F3. An application-callback exception killed the entire shard

**Severity: high.** `OnReceive`, `OnAccept`, `OnConnect` and `OnWrite` were dispatched bare on every
loop-driven backend. An exception unwound out of the completion loop, through `OnRun`, into
`SocketSetShard.Run`'s catch (`SocketSetShard.cs:136`), which logs and then falls into
`finally { OnShutdown(); }`. That runs `Cleanup()`: every fd on the shard closed, and the loop is never
restarted.

One unhandled exception in application code therefore cost up to `SocketsPerShard` (4096 by default)
live connections **and** removed 1/N of the set's capacity permanently. For a server that is a remote
denial of service with a one-request price, gated only on the app having a reachable parse bug.

`DispatchClosed` was already wrapped (`IoUringShard.cs:240`), so the pattern was established and had
simply not been applied to the hot four.

**Fixed.** All four now route through `SocketSet.DispatchAccept` / `DispatchConnect` / `DispatchWrite` /
`DispatchReceive`, which contain the fault to the one connection: close it (its protocol state is
unknowable after a partially-run handler), zero any half-written response so it is not transmitted, and
report through a new overridable `OnCallbackFaulted`.

Two deliberate choices worth recording. The wrappers return `void` and close via the existing
`Connection.Close()` rather than returning a keep/drop bool: `Close()` is already the thread-safe,
generation-guarded, marshal-onto-the-loop path that every backend implements and every application
uses, so containment introduces no new teardown route to get wrong, and no call site had to change
shape (the change is a pure rename at 38 sites). And `OnCallbackFaulted` swallows exceptions from its
own override, because an override that throws would defeat exactly the containment it exists to give.

### F4. `SSL_set1_host` return value dropped on the kTLS path

**Severity: medium.** `OpenSslTlsProvider.CreateClientFilter` throws when `SSL_set1_host` fails
(`:118`). `CreateKernelSsl` called it and discarded the result (`:239`). A failure there degraded that
connection to chain-only verification, accepting a valid certificate for any name, while the memory-BIO
path with byte-identical configuration would have refused to connect at all. One provider, one
configuration, two security postures, decided by which I/O path the backend happened to pick.

**Fixed.** Throws on both, and frees the `SSL*` on the throwing path.

### F5. `sockaddr_un` built one byte per UTF-16 char, with no bounds check

**Severity: medium.** `LibC.SockAddrUn.Init` and `Win32.SockAddrUn.Init` both did
`addr->sun_path[i] = (byte)path[i]`.

Two problems. First, **the address bound is not the address requested** for any non-ASCII path: U+0141
truncates to `'A'`, and any code unit ending in `00` truncates to NUL, which shortens the path, or at
index 0 flips a filesystem socket into the Linux abstract namespace. Distinct paths can alias to one
address.

Second, **nothing bounded the write** against the 108-byte `sun_path`. Two of the three call sites
(`IoUringFactory.cs:120`, `IocpShard.cs:882`) write into a **stack** `sockaddr_un`, so an over-long path
is a stack smash. The only thing preventing it is that `UnixDomainSocketEndPoint`'s own constructor
rejects paths over the native limit, and it validates the **UTF-8 byte count** while this wrote one byte
per char, so char count <= byte count and it happened to be safe. That is an invariant borrowed from
another type, holding by a margin nobody chose.

**Fixed.** Both are UTF-8 encoded and length-checked. Abstract-namespace lengths are byte-identical to
before, which matters because abstract addresses are length-delimited and any change would break the
existing `@abstract` smoke cells (checked: `@foo` still returns 6).

### F6. Unchecked `Advance` was a wire-visible over-read, not an exception

**Severity: medium.** `IoUringConnection.Advance` was `_curPos += count`. Over-advancing does not throw:
`_curPos` becomes the committed `Length` of an `OutSeg`, which becomes an `iovec`'s `iov_len`, and the
kernel then reads **past the pool page** and transmits whatever sits next in the shared pinned slab, ie.
another connection's buffered bytes, to this peer. `PooledBufferWriter.Advance` had the same shape, and
its array is handed out whole via `.Array`.

The `IBufferWriter` contract does say the caller must not over-advance, so this is a caller bug. It is
a caller bug whose consequence is a silent cross-connection disclosure rather than an exception, which
is not a reasonable thing to leave unchecked on a boundary this hot.

**Fixed.** Both validated.

### F7. `SlotAllocator.Free` and `PinnedWriteBufferPool.Release` were unguarded

**Severity: medium (latent).** `_holes[_top++] = slot` and `_free[_freeTop++] = index`, neither
validated. A double free does not merely risk an `IndexOutOfRangeException`: it puts the same index in
the free list **twice**, so two subsequent claims hand out the same slot and two live connections alias
one `Connection` object (shared `UserToken`, shared TLS filter, shared write state), or two in-flight
sends share one pinned page and each transmits a slice of the other's payload.

I could not construct a reachable double free in the current code. With roughly six teardown exit paths
per backend calling these, and the failure mode being silent cross-connection corruption rather than a
crash, it is worth failing loudly on.

**Fixed.** Both throw on an out-of-range index or an overflowing free list, naming double-free as the
likely cause.

### F8. `HalfPipeWriter.FlushAsync` drained after `Complete()`

**Severity: medium.** `Complete()` calls `_cb.Release()`, handing the `CycleBuffer`'s segments back to
the `MemoryPool`. `FlushAsync` guarded only on `_peerGone`, so a flush arriving after completion would
read recycled pool memory straight into `Connection.Send`. It already reported `_completed` in the
returned `FlushResult` and simply did not check it before draining.

Kestrel should not flush a completed `PipeWriter`, and `SocketSetConnection.DisposeAsync` calls
`Complete()` from a different thread than `FlushAsync` runs on, so the race is real if narrow. "Should
not" is not a reason to leave a use-after-recycle reachable.

**Fixed.** One added condition.

---

## NOT FIXED: design calls, in priority order

These are not left out because they are small. They are left out because each one has a decision in it
that is Marc's to make, and guessing would bake the wrong answer into the API or into a gate.

### D1. RESOLVED 2026-08-04: authenticate callbacks, and the host is now mandatory

**Superseded by the implementation below.** The original writeup follows, because the diagnosis stands
even though the fix I proposed was the wrong shape.

**What I proposed was to move `TlsClientOptions` onto `Connect`. Marc's answer was better:** ask the
engine instead. `bool OnClientAuthenticate(ref TlsClientAuthenticateContext)` / `OnServerAuthenticate`
fire once per connection on the loop thread, immediately before the handshake, and answer *whether* TLS
and *how* in one place. That works where a `Connect` overload could not: the tunnel deliberately funnels
many endpoints through one engine, so no caller-supplied parameter can name the host at dial time, but a
callback CAN key off `Connection.UserToken` (which, for the tunnel, IS the transport that knows its own
endpoint). It is also lazy, and it collapses a decision previously spread across a constructor, an
options object and a call parameter.

Git history confirms this was a miss rather than an old design: `f55d6f8` (connect granularity) landed
2026-08-03 and `51a767f` (listen) 2026-08-04. The **provider** moved to per-connection; the options
object holding `TargetHost` did not.

Also relevant, and a genuine disjoint that justifies two context types rather than one: there is no
`TargetHost` on the server side at all. A server is TOLD a name by SNI rather than choosing one.

**The host is now mandatory, and `"*"` is the explicit opt-out.** Null, empty or whitespace is REFUSED;
`"*"` means no SNI and no name check. `"*"` cannot collide with a real target (not a legal DNS label,
not an IP literal, not legal as an SNI `server_name`), so it reads as a decision where null read as an
oversight. On the wire this MIRRORS the BCL: `SslClientAuthenticationOptions.TargetHost` unset sends no
SNI, and neither does `"*"`. It improves on the BCL only in that the *validation* opt-out is a named
value rather than something you express by writing a `return true` callback.

**Fails closed in all three directions.** A callback that throws, one that returns true without a
provider, and one that returns true without a host all yield `TlsResolution.Deny`, and the backend drops
the connection. None fall back to plaintext: a silent downgrade is the exact failure being removed.
`ResolveClientTls` on `SocketSet` is the single enforcement point, so all five backends inherit it.

**A CLAIM I MADE AND THEN FALSIFIED, recorded because it drove a design decision.** I stated confidently,
twice, that `SSL_set1_host("127.0.0.1")` matches dNSName only and would therefore FAIL against our demo
certificate's iPAddress SAN, and that this was "on the critical path" for the mandatory-host change.
Measured 2026-08-04 with a purpose-built pair of certificates: **false.** On OpenSSL 3.x,
`SSL_set1_host` accepts a certificate whose ONLY SAN is `IP:127.0.0.1` when dialling `127.0.0.1`, and
refuses it when dialling `127.0.0.2`. It is genuinely matching the address, not skipping the check. The
`X509_VERIFY_PARAM_set1_ip_asc` branch therefore fixes no observed break.

It is kept, on the narrower ground that it is the DOCUMENTED API (`X509_check_host` is specified over
dNSName; `X509_check_ip` is the address one), so the observed behaviour is undocumented and need not
hold on another build. That is a weaker justification than a measurement and is labelled as such in the
code — the evidence needed to delete it is already written down. The SNI half of the IP branch stands
independently: RFC 6066 §3 forbids an address literal in `server_name`.

**Gated** by `bench/verify-tlsname`, whose discriminating cells are the ones that must be REFUSED:
`wrong.example` (name check runs), `127.0.0.2` (it runs for addresses too, rather than being skipped for
anything IP-shaped), and `""` (fails closed at configuration time). The accept-cells alone would pass
just as happily against the pre-fix code.

**Known gap, raised by Marc:** "*" conflates two axes — announce (SNI) and verify. There is no way to say
"do not tell the server who I expect, but do check what comes back against name X". The machinery
already exists internally (SChannel carries `sniName`/`verifyName` separately, and the IP path already
announces nothing while still verifying); exposing it is a second field, not new plumbing. Recorded in
TODO rather than built, since the case is real but niche.

---

### D1 (original). `TlsClientOptions.TargetHost` is per-ENGINE, but hostname verification needs it per-CONNECTION

**This is the one to look at first.**

Both providers skip the hostname check entirely when the host is null or blank:
`OpenSslTlsProvider.cs:111` (`if (options.TargetHost is { Length: > 0 } host)` gates both the SNI
extension and `SSL_set1_host`) and `SChannelTlsProvider.cs:163`. It defaults to null. Since the
SChannel credential carries `SCH_CRED_MANUAL_CRED_VALIDATION` and the client context
`ISC_REQ_MANUAL_CRED_VALIDATION`, SChannel is doing no name check either, so a null host means
**nobody** checks.

The chain is still verified. What you get is "any certificate from a trusted CA, for any name", which
is the classic silent man-in-the-middle hole, described in exactly those words by the doc comment on
the option itself (`TlsOptions.cs:24-30`). The code fails open against its own documentation.

**Why this is not a one-line "throw if null".** `TargetHost` lives on `SocketSetOptions`, and every
shard reads the engine-level copy (`IoUringShard.cs:955` and `:1133`, `EpollShard.cs:873` and `:1081`,
`IocpShard.cs:1395`, `WindowsRioShard.cs:1036`). But `SocketSetTunnel` deliberately funnels **many**
endpoints through **one** engine: that is the anchor shape decided on 2026-08-03 and the whole point of
the design. So the SE.Redis tunnel, the primary testbed, structurally **cannot** set a correct
per-endpoint host today. The per-connect override that does exist takes a whole `TlsProvider`, not a
`TlsClientOptions`.

Both TLS client rigs currently depend on the fail-open behaviour: `bench/tunnel-selftest` and
`bench/mux-ab` construct `OpenSslTlsProvider(trustCertPem: ...)` with `verifyServer` defaulted true and
never set `TargetHost`. Worth noting that `mux-ab`'s SslStream **control** leg sets
`config.SslHost = "localhost"` and pins by thumbprint, so the two legs of that A/B are not running the
same verification posture, which makes it a slightly unfair comparison in our favour on handshake cost.

**And there is an IP-literal trap in the obvious fix.** Both rigs dial `IPAddress.Parse(target)`, ie.
`127.0.0.1`. `SSL_set1_host("127.0.0.1")` matches DNS SANs and the CN, not `iPAddress` SANs, so simply
requiring a host and passing it through would fail against the demo certificate even though that
certificate does carry the loopback IP in its SAN. Anyone fixing this naively will conclude the fix is
broken.

**Proposed shape, for Marc's call:**
- `TargetHost` (probably the whole `TlsClientOptions`) becomes a per-connect argument alongside the
  existing per-connect `TlsProvider` override, so the tunnel can pass the endpoint it is dialling.
- An IP literal routes to `X509_VERIFY_PARAM_set1_ip_asc` on OpenSSL and the IP branch of
  `MatchesHostname` on SChannel, instead of `set1_host`.
- `verifyServer: true` with no host becomes an error at filter-creation time rather than a silent skip.
  The genuine "no name check wanted" case gets an explicit, named, loud opt-out, which is what
  `TlsOptions.cs` already says the escape hatch should look like.

This is public API surface, so it interacts with the freeze proposal in `TODO.md`. Worth deciding
together.

### D2. RESOLVED 2026-08-04: handshake deadline on by default, idle deadline opt-in

The last of the three high-severity findings. Original writeup below.

**Two deadlines, and the asymmetry between them is the design.**
`SocketSetOptions.HandshakeTimeout` (default **10s**, ON) covers accept/connect until the application
SEES the connection open, which in practice is the TLS handshake budget. `IdleTimeout` (default **0**,
OFF) covers an established connection going quiet.

On by default for the first, off for the second, because a handshake that has not finished in ten
seconds is broken and has no legitimate case, whereas an idle ESTABLISHED connection is completely
normal for the workloads here: a SE.Redis multiplexer link or an HTTP keep-alive socket is legitimately
quiet for minutes, and reaping those would be a correctness bug wearing a security hat. The unfinished
handshake is also the actual DoS shape, and the one Kestrel used to defend against for us before
`SocketSet.AspNetCore` moved TLS below it.

**Mechanism.** The loops BLOCK when idle (`io_uring_enter`, `epoll_wait`, `GetQueuedCompletionStatusEx`)
which is exactly the state a half-open handshake leaves them in, so a deadline cannot be noticed
unaided. One timer per SET ticks at a quarter of the tightest deadline (clamped to 250ms..5s) and wakes
each shard through the doorbell it already has for `Stop`; the shard sweeps its own slot table on its own
thread, so the single-writer discipline is untouched. A set with no deadline configured starts no timer
and pays nothing. `SocketSet.Timeouts` counts the drops, because a deadline set too tight and an attack
look the same in a log but not in a counter.

The scan is a linear walk of the slot table: at the default 4096 sockets and a 2.5s interval that is
~1.6k checks/second/shard, riding along with a syscall the loop was making anyway. No measurable effect
on the smoke matrix (60/60, unchanged timings).

**Gated** by `bench/verify-timeouts`, 4 cells x 2 backends, and it is self-controlling rather than
merely positive:
- `handshake/reaped` — a TCP peer that connects to a TLS listener and says nothing IS dropped. The only
  cell that fails against the pre-fix code.
- `handshake/spared` — a peer that completes its handshake survives well past the same budget, so
  "reaped" cannot just mean "we drop everything".
- `idle/off-by-default` / `idle/reaped` — the same connection shape with `IdleTimeout` off and on. This
  pair is a controlled A/B: it proves the default really is off AND that the option is not inert, which
  neither cell could establish alone.

**A self-inflicted failure worth recording**, because it is the second of its kind this session. Adding
a net472-safe monotonic clock, I ran `sed` for `Environment.TickCount64` -> `Clock.Millis` across
`src/SocketSet` — including the file that DEFINES `Clock.Millis`, which duly became
`Millis => Clock.Millis`. Every backend stack-overflowed on the first accept. Same family as the
`pgrep -f` watcher that matched its own command line earlier: a mechanical rewrite applied to its own
definition. Caught immediately by the gates; the lesson is to exclude the definition site, or to write
the shim after the rewrite rather than before.

---

### D2 (original). No handshake or idle timeout anywhere in the library

Grepping `src/SocketSet` for timeouts returns `StartupTimeout` and P/Invoke signatures, nothing else. A
peer that connects and then sends nothing holds its slot forever. Nothing reaps it. At the default 4096
slots by 4 shards, 16k idle sockets exhaust the set, after which accepts are dropped with only
`PlacementFailures` moving to say so.

For the ASP.NET bridge this is a regression against the thing we replace, and that is the argument for
building it rather than documenting it. TLS terminates in the transport, below Kestrel, so Kestrel's
`HandshakeTimeout` (10s by default), `MaxConcurrentConnections` and `RequestHeadersTimeout` never see
the connection until the handshake is already done. Classic slowloris, against a stack that had a
defence before we removed it.

Cheapest credible shape: a coarse per-shard sweep over slots with `Opened == false` past a deadline
(the loop already wakes regularly, and `OnLoopDrain` already exists as a per-batch tick), plus an idle
deadline on established connections. Wants a measurement of what the sweep costs per loop iteration
before it goes in, since it touches the hottest loop in the repo.

### D3. BOUNDED 2026-08-04 (not yet cured): inbound buffering now has a limit

**This is the bound, not the cure, and the distinction is deliberate.** Original writeup below.

`SocketSetConnection.WriteInbound` did `_ = w.FlushAsync()` and threw the task away, which made the
`pauseWriterThreshold: 1 << 20` configured two lines earlier completely inert: a client uploading faster
than the handler drained grew the pipe without bound. `PipeIoBridge` had the same shape plus an
unbounded `_staged` queue of pooled rentals. Memory exhaustion needing no protocol trickery at all.

**What landed:** both paths now TRACK how far ahead of the application they are running, and a connection
that exceeds `MaxInboundBufferBytes` (default 4 MiB, 0 disables) is dropped —
`SocketSetTransportMetrics.InboundOverflow` and `PipeIoBridge.OverflowCount` count it, because a cap set
too low and an abusive peer look identical in a log and different in a counter.

**Why that is only half the answer.** Dropping is right for abuse and blunt for a merely slow consumer.
The correct fix is receive PARKING — stop re-arming the receive until the flush completes, so the TCP
window slows the peer instead of the peer being killed — which is what Kestrel's own transport does and
what `pauseWriterThreshold` is supposed to mean.

I did not build parking here, and the reason is worth recording rather than hiding: it needs per-backend
pause/resume with the resume marshalled from a thread-pool continuation back onto the loop, and io_uring
is genuinely awkward because its receive is MULTISHOT — pausing means cancelling an armed op and
re-arming later, sharing the cancel path with teardown. A racy implementation there produces HANGS,
which is a worse failure than the bounded-growth it would replace. Bounding first is the low-risk step
that removes the unbounded case; parking is recorded in TODO with that difficulty spelled out.

The cap is deliberately generous (4 MiB) so that only a real mismatch reaches it, precisely because the
consequence is currently a drop rather than a slowdown.

---

### D3 (original). Backpressure is advisory in both bridges, so inbound is unbounded

`SocketSetConnection.WriteInbound` (`SocketSet.AspNetCore`) does `_ = w.FlushAsync()` and discards the
task, which means the `pauseWriterThreshold: 1 << 20` set two lines earlier does nothing: a client
uploading faster than the handler drains grows the pipe without bound. `PipeIoBridge` has the same
shape plus an unbounded `_staged` queue of `ArrayPool` rentals, filled from the loop thread.

Both are commented as known ("demo backpressure", "advisory"), and the proper fix is the receive
**parking** already recorded as item 7 (do not re-arm the receive until the flush completes). The point
worth adding to that item: it is not only an architecture/perf item, it is currently the DoS. Note that
`SocketSet.AspNetCore` is a published package, not the demo.

### D4. RESOLVED 2026-08-04 (same day): lazy tail wipe, with two triggers

**Superseded by the implementation below.** The original writeup is kept underneath because the
reasoning about *why* the buffer is shared still applies, and because one of the three options I
proposed turned out not to be implementable.

**Marc's proposal:** wipe the region past the payload lazily, the first time it is accessed. If it is
never accessed, zero cost.

**The hole he then found in my first implementation, which is the important part of this entry.** A
wipe hung off `RawBuffer` alone looks complete and is not. `ResponseBytes` can be set above
`PayloadBytes` **without ever touching `RawBuffer`**:

```csharp
protected override void OnReceive(ref ReceiveContext ctx) => ctx.ResponseBytes = frameLength;
```

That transmits the stale tail and sails straight past a wipe-on-first-access. It is also the *more*
likely accident of the two: a miscomputed frame length, rather than a handler that writes and then
over-reports. So there are two triggers, tracked through a single monotone `_wipedTo`:

| trigger | wipes | why that much |
|---|---|---|
| `RawBuffer` / `SendBuffer` read | the whole tail | once the span is out we cannot distinguish handler-written bytes from stale ones, and we do not yet know the reply length |
| `ResponseBytes` / `SendBytes` set | up to `value` only | clears exactly what is about to go on the wire, nothing more |

**The echo path pays nothing, by construction rather than by luck.** `_wipedTo` starts at
`PayloadBytes` and `WipeTo` early-returns on `end <= _wipedTo`, so `ctx.ResponseBytes =
ctx.PayloadBytes` (and any reply *smaller* than the request) does one integer compare and a predictable
not-taken branch. That answers "should a reply that does not exceed the payload pay the penalty?" with
"it does not" for the length-setting route.

**Trigger 1 was over-charging, and `GetWriteSpan(int sizeHint)` fixes it (Marc's second refinement).**
The shape the README suggests, `ctx.RawBuffer[i] = ...; ctx.ResponseBytes = ctx.PayloadBytes;`, tripped
trigger 1 and wiped the WHOLE tail even though the reply never exceeded the payload. Nothing can be
deferred once the full span is out: a later wipe would erase the handler's own writes.

The fix is not to defer the wipe but to stop handing out the whole buffer. `GetWriteSpan(sizeHint)`
deliberately does NOT promise `sizeHint` bytes (it is clamped, so callers check `.Length`, the
`IBufferWriter` convention and the same shape as `Connection.GetSpan`). That freedom is the entire
point: we hand out only what was asked for, so we only ever have to zero what was asked for. Granting
is a high-water mark on the same `_wipedTo`, so:

> **received 20, want to reply 25: we wipe 5 bytes, not 4000-minus-25.**

and asking twice never pays twice (`GetWriteSpan(22)` then `GetWriteSpan(25)` clears `[20,22)` then
`[22,25)`). A reply no larger than the request clears nothing at all, because `_wipedTo` already starts
at the payload end. `RawBuffer` survives as the "give me everything" form and keeps its pessimistic
whole-tail wipe, which is now a documented price for asking for everything rather than the only option.

This also removes the one case where the cost could have been ugly: on the TLS path the buffer is a
`PooledBufferWriter` array grown to the connection's high-water mark and never shrunk, so a connection
that once sent 4 MB had a 4 MB tail. Under `GetWriteSpan` that is irrelevant — the cost tracks the reply
size, not the buffer size.

**Measured (indicative, not a scored result).** `SmokeTest --io-uring -s -n 2 -c 8 -t 5 -z 512`, four
passes each, one session, same host. This workload exercises both sides: the server takes the free echo
path and the *client* pays both triggers against a 4 KB buffer with 512 B messages, ie. a ~3.5 KB memset
per operation, which is the pessimal shape.

| | round-trip bytes over 5s, four passes |
|---|---|
| without wipe (control) | 812.9M, 817.6M, 818.8M, 831.5M |
| with wipe | 813.9M, 816.6M, 816.9M, 819.8M |

The ranges overlap almost entirely, so per `bench/README.md` rule 5 **no delta is quoted**: even the
pessimistic whole-tail form was not distinguishable from noise at this size. `GetWriteSpan` was
therefore adopted on the strength of the argument rather than of a measured win, and that is worth
stating plainly: it makes the cost proportional to the reply instead of to the buffer, which is the
right shape whether or not this particular workload can see it.

**Gated** by `bench/verify-tailwipe`, four cells per backend: three disclosure vectors plus a
byte-for-byte assertion that a 20-byte request replying 25 clears exactly 5. It was falsified twice
before being believed; see the gate section at the end.

**Scope, deliberately.** This defends against accident, not against hostile in-process code. Anything
running in this process can reach the stale bytes trivially (`Unsafe.Add` off a reference, its own
pointer arithmetic, or the explicitly-named `RawBufferUnwiped` / `SendBufferUnwiped` accessors) and is
already inside every boundary the library has. Pretending otherwise would buy false confidence. The
thing worth stopping is an ordinary length bug quietly putting another connection's data on the wire.
Marc's call, recorded here so nobody "hardens" it later on a misunderstanding.

---

### D4 (original). `ReceiveContext.RawBuffer` exposes other connections' bytes past `PayloadBytes`, by design

`RawBuffer` spans the whole backing buffer and `ResponseBytes` may be set up to that full length
(`SocketSet.cs:625`, `:640`). The backing buffer is shared, recycled and never cleared:

- Plaintext io_uring: the provided-buffer page from the **per-shard** `ManagedBufferPool` slab, cycled
  across every connection on the shard.
- TLS: the per-slot `TlsPlain` / `KtlsRecv`, created with `??=` (`IoUringShard.cs:956`, `:1130`) and
  never disposed or zeroed in `TryFinalize`, so they survive slot re-tenancy holding the previous
  connection's **decrypted plaintext**. epoll is broader still: `_tlsPlain` there is shard-wide.
  `EpollShard.cs:363` documents the reuse explicitly ("kept for the next tenant").

So `RawBuffer[PayloadBytes..]` is the previous tenant's data, and a handler that pads a response or
miscomputes a length prefix transmits another client's traffic. Same for the `SendBuffer` handed to
`OnAccept` and `OnWrite`.

This is not so much a bug as an unstated contract. The reply-in-place design is the entire point of the
API, and clearing the tail on every receive would cost real throughput on the hottest path. But right
now nothing says it, and the README's own echo example sits one plausible edit away from leaking.
Options, increasing in cost:

(a) Document it on `RawBuffer` in those terms.
(b) Track a high-water mark of what the handler actually wrote, and validate `ResponseBytes` against
    that rather than against the buffer length.
(c) Clear on release.

(b) looks like the right trade and is cheap on the response path, but it is a semantic change to a
public contract, so it wants a decision rather than a guess.

> **CORRECTION (same day).** Option (b) as written is NOT implementable. There is no way to track "what
> the handler actually wrote": `RawBuffer` hands out a raw `Span<byte>` and writes through it are
> invisible to the library. Only the *declared* length (`ResponseBytes`) is observable. Marc's lazy-wipe
> is strictly better than what I proposed here, and is what shipped. Left in place because being wrong
> about an option is worth recording alongside the option that worked.

### D5. ACCEPTED RISK 2026-08-04 (Marc: "we're OK with this")

Marc's explicit call: the committed strong-name key is accepted. Recorded as a decision rather than an
oversight so it is not re-raised as a finding by the next audit. The reasoning below still describes what
is being accepted.

### D5 (original). `SocketSet.snk` is a full RSA private key, committed

Verified rather than assumed: the file begins `0x07` (PRIVATEKEYBLOB) with `RSA2` magic, ie. a full key
pair, not a public key. `Directory.Build.props:47-50` has `SignAssembly=true` with `PublicSign=false`.

.NET Core does not verify strong names, so this is not an authentication bypass and the severity is
low. What it does mean, now that the packages are live on nuget.org, is that anyone with the repo can
build a binary-compatible assembly carrying our exact identity; and if the repo is ever public, that is
unrecoverable without rolling the key, which changes assembly identity for every consumer. The standard
answer is `PublicSign` with only the public key checked in. Marc's call because it touches published
packages.

### D6. RESOLVED 2026-08-04 (Marc: "fine to pick up and fix, adding options as necessary")

All but one are now fixed. Where the behaviour was a POLICY choice rather than a bug, it became an
option with a secure default, per Marc's steer.

- **`SO_REUSEPORT` is now `SocketSetOptions.ReusePort`** (default true). It has to stay on by default:
  reuse-port multi-bind is how io_uring and epoll get one listener per shard. `CanMultiBind` now follows
  the option rather than asserting the capability independently, because with it off the second shard's
  bind would simply fail EADDRINUSE.
- **Unix-domain socket files are now chmod'd**, `SocketSetOptions.UnixSocketMode`, default **0600**, and
  applied BETWEEN bind and listen so there is no window where the socket both exists and is
  world-connectable. MEASURED, with a control in the same session: a UDS bound with no chmod on this box
  gets **0775** (group- and world-connectable, with no authentication anywhere in the stack behind it);
  with the change it is **0600**. No-op for the abstract namespace (no inode) and on Windows (directory
  ACLs). The chmod failing is a loud warning rather than a failed bind, because a bind that worked should
  not be undone by an exotic mount, but it must not be silent either.
- **Kernel-supplied buffer ids are masked**, and `HandleRecv` now DROPS a connection whose recv reports
  bytes without `IORING_CQE_F_BUFFER` rather than falling through to buffer id 0 — which would have
  delivered another connection's data. The kernel invariant holds; the mask is free and the cost of being
  wrong was an out-of-slab read.
- **TLS failures are visible in Release** — see the separate commit; `Debug.WriteLine` meant they did not
  exist at all outside a debug build.
- **Revocation is settable**: `SChannelTlsProvider(..., X509RevocationMode)`. Default stays `NoCheck`
  (matching `SslStream`) because changing it silently adds a network dependency to every handshake, but
  it is no longer hard-coded, and the docs say plainly that `Online` is the right answer for a client
  dialling a real off-box service.
- **Pooled buffers holding plaintext are cleared on return**, via `PooledBuffers.ReturnCleared(array,
  used)`. Deliberately NOT `Return(clearArray: true)`, per Marc: that clears the whole array, and these
  are routinely far larger than the part used (`ArrayPool` rounds to a power of two, and the TLS writers
  grow to a connection's high-water and stay). Clearing 64KB to retire 40 bytes of RESP is the wrong
  trade on a per-message path. Applied to the three that actually carry plaintext — SChannel's `_carry`
  (decryption is in place), `PooledBufferWriter` (the decrypt target), and `PipeIoBridge._staged` — each
  tracking a high-water mark so the clear costs the bytes actually touched. The other ~25 pool returns in
  the backends carry ciphertext or outbound copies and were left alone.

**Still open from D6:** `UnixSocketFile.PrepareForBind` still deletes whatever file is at the path,
unconditionally, with a `File.Exists`/`File.Delete` TOCTOU. The chmod above reduces the consequence but
not the primitive. Nothing reads `SO_PEERCRED` either, so a UDS peer is still unauthenticated beyond
filesystem permissions — which is now a real boundary rather than a nominal one.

### D6 (original). Smaller items, recorded rather than fixed

- **`SO_REUSEPORT` is set unconditionally** on every IP listener (`IoUringFactory.cs:91`). It is
  structural to multi-bind so it cannot simply come off, but it does mean any process running as the
  same uid can join the group and take a share of inbound connections. Worth a line wherever the
  security notes end up.
- **Filesystem UDS sockets have no access control.** Nothing chmods the socket file (so it is
  umask-dependent, usually connectable by any local user) and nothing reads `SO_PEERCRED`. Separately,
  `UnixSocketFile.PrepareForBind` deletes whatever file is at the path, unconditionally, with a
  `File.Exists` / `File.Delete` TOCTOU. The comment acknowledges the deletion but not that it is an
  arbitrary-file-delete primitive if the path is ever config-driven. The bench rigs use fixed `/tmp`
  paths (`run-proxy-ab.sh:110`), which is the symlink-attack shape on a shared host.
- **Kernel-supplied buffer ids are trusted unchecked.** `ManagedBufferPool.GetBufferAddress(bid)` is raw
  pointer arithmetic with no bound against `_entries`, and `HandleRecv` calls `DeliverReceive` on
  `res > 0` without checking `IORING_CQE_F_BUFFER` first. The kernel invariant holds today. A mask is
  free.
- **TLS failures are invisible in Release.** Both filters report through `Debug.WriteLine`
  (`OpenSslTlsFilter.cs:146`, `SChannelTlsFilter.cs:526`), so every handshake and decrypt failure
  vanishes in a release build. `OpenSslTlsFilter.BioWriteAll` silently drops the remainder on a BIO
  write failure, desynchronising the stream (it fails closed, but opaquely). The
  `SSL_CTX_ctrl(SET_MIN_PROTO_VERSION)` return is unchecked at `OpenSslTlsProvider.cs:59` and `:84`;
  behaviourally covered by `verify-tls-floor`, but the code itself would not notice.
- **`X509RevocationMode.NoCheck`** is hard-coded in `SChannelTlsProvider.ValidateRemote:148`. Already
  has its own TODO there; restated so one list has everything.
- **ArrayPool returns do not clear.** The TLS carry and plaintext buffers go back to the shared pool
  holding plaintext (`SChannelTlsFilter.cs:478`, `:496`, `PooledBufferWriter.cs:92`, `:99`) without
  `clearArray: true`.

---

## LOOKED AT, NOT A FINDING

Recorded so the next audit does not re-derive them.

- **`SelectAlpn`'s parsing of the client's ALPN list** (`OpenSslTlsProvider.cs:189`) reads
  attacker-controlled bytes in an `UnmanagedCallersOnly` callback. Bounds are correct: both loops check
  `c + 1 + m > clientLen` before the `SequenceEqual`, and the cursor increments cannot pass the length.
- **Unbounded growth of the OpenSSL read BIO during the handshake.** I suspected the shard writing every
  received byte into `rbio` with no cap was a memory-amplification DoS. I could not substantiate it:
  OpenSSL drains records as it needs them, and the record length field caps a single record at 64KB.
  Left as part of the general "no backpressure" item (D3) rather than claimed as its own.
- **`SChannelTlsFilter._carry` growth.** Bounded by the TLS record size, so `Append`'s doubling cannot
  run away.
- **The generation guard on cross-thread flush and close.** Checked against slot re-tenancy in
  io_uring and epoll; correct in both. `TryFinalize` genuinely does not publish the slot free until
  every in-flight op has been reaped.
- **`ZcJob` completion on every exit path.** Verified: success, send error, teardown, and the
  slot-went-away-before-drain path all call `Finish` exactly once. The pump cannot deadlock holding a
  `ReadResult`.

---

## GATE STATUS FOR THIS AUDIT

Every Linux gate, re-run after each of the seven commits this audit produced:

| gate | result |
|---|---|
| full solution build | 0 errors, no new warnings (one pre-existing doc warning fixed in passing) |
| `bench/run-smoke-matrix.sh` | **60/60 PASS**, first run, and again with the tail wipe in |
| `bench/verify-tailwipe` | **12/12 PASS** (new; 4 cells x 3 backends) |
| `bench/verify-bind-address.sh` | **6/6 PASS** (new; see below) |
| `bench/verify-tls-floor.sh` | **8/8 PASS**, including both refusal cells |
| `bench/verify-aspnet.sh` | **18/18 PASS** |
| `bench/verify-timeouts` | **8/8 PASS** (new; 4 cells x 2 backends) |
| `bench/verify-tlsname` | **6/6 PASS** (new; 3 of them refusal cells) |

**Windows: ALL OF IT UNRUN**, across all seven commits. `bench/Run-SecurityGates.ps1` (new) runs the lot
in one command, starting with a FULL multi-target build — the single-target habit shipped a net472 break
during this very session. `bench/Verify-BindAddress.ps1` is the Windows twin of the Linux bind gate; the
three `verify-*` .NET rigs are cross-platform and pick this OS's backends and TLS provider via
`bench/GateBackends.cs`. The discriminating check for the bind fix is deliberately NOT automated and is
called out at the end of the run: a listener on `127.0.0.1` must not be reachable on the LAN address.

Caveat, stated so it is not discovered as a surprise: the two new `.ps1` files were authored on Linux,
which has no PowerShell, so they have never been parsed. The .NET rigs they drive do build and pass
here. Treat a first-run failure in the scripts as a harness bug before suspecting the library.

One real break was caught here and is worth recording: `--bind-probe` used `Environment.ProcessId`,
which is net5+, and this repo multi-targets net472. A `-f net10.0` build passes happily; the FULL
solution build is what failed. It went out in the first commit because I built only the one framework
after adding the probe. Build the solution, not a target.

### The new gate was itself falsified before it was believed

Two things worth recording about `verify-bind-address.sh`, both instances of the house rules earning
their keep.

**Its first run reported a FAIL against correct code**, and the cell that caught it was the control:
`io-uring/any` claimed the kernel bound 127.0.0.1 when asked for 0.0.0.0. Checked rather than assumed,
and it was the rig: the pid filter was a no-op (`|| 1` matched every row) and `ss -ltnH` has no pid
column without `-p`, so with `SO_REUSEPORT` letting a new bind succeed alongside a not-yet-reaped one,
`head -1` was reading the *previous* cell's socket. Fixed with a real pid match plus an
await-port-clear between cells. Had the control cell not existed, the run would have been 3/3 PASS and
the rig defect would have shipped inside the gate.

**Then the gate was checked against the bug it exists to catch**, by temporarily restoring
`sin_addr = 0` and re-running. The result was the exact expected pattern rather than merely "some
failure": `io-uring/loopback` and `epoll/loopback` FAIL (epoll binds through the same
`IoUringFactory.Bind`), `managed/loopback` still passes (it always honoured the address, so it is the
built-in cross-check), and both `any` controls are unaffected. A gate that has never been shown to fail
is not a gate.

### `verify-tailwipe` needed the same treatment, and needed it twice

`bench/verify-tailwipe` (D4) went through two rounds of the same lesson, both worth recording because
both produced a green run that meant nothing:

1. **The first version passed against a knowingly-broken build.** It connected a fresh client per probe
   and expected to see the previous tenant's bytes. io_uring hands out provided buffers by bid from a
   per-shard ring, so a new connection rarely lands on the buffer that was dirtied. Fixed by forcing the
   collision: one connection, a 16-entry pool, 64 rounds. Only then did removing the `ResponseBytes`
   trigger produce the expected failure (B fails 64/64 on epoll and managed, 4/64 on io_uring, while A
   still passes) — which is what established that the cell tests what it claims to.
2. **The exact-delta cell then failed against CORRECT code**, reporting 4076 bytes cleared on io_uring.
   Also the rig: that receive had landed on a fresh bid whose tail was already zero (the slab is
   `MAP_ANONYMOUS`), so counting non-marker bytes counted the whole tail. The count is only meaningful
   on a confirmed-dirty buffer, so the cell now checks the marker is still present immediately after the
   growth region and reports "not measurable" otherwise. io_uring measures on ~4 of 64 rounds; epoll and
   managed on all 64. All three then report `cleared=5` exactly.

Both failures were rig defects presenting as code defects, which is the failure mode that costs the most
time. Checking a gate in both directions, against a known-good and a known-broken build, is what
separated them.
- **The Windows half is UNRUN.** `IocpShard`, `WindowsRioShard` and `Win32.cs` were edited from Linux,
  which is exactly the situation `TODO.md`'s Windows catch-up section exists for. First thing on the
  next Windows session: `Run-SmokeMatrix.ps1` and `Verify-AspNet.ps1`, plus the discriminating check for
  F1, which is that a listener bound to `127.0.0.1` must NOT be reachable on the box's LAN address any
  more. "TLS still works" cannot distinguish a bind that took from one that did nothing, and neither can
  connecting over loopback. A Windows `verify-bind-address` equivalent (`netstat -ano` plus the pid) is
  wanted.
