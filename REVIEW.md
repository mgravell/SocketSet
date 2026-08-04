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

### D1. `TlsClientOptions.TargetHost` is per-ENGINE, but hostname verification needs it per-CONNECTION

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

### D2. No handshake or idle timeout anywhere in the library

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

### D3. Backpressure is advisory in both bridges, so inbound is unbounded

`SocketSetConnection.WriteInbound` (`SocketSet.AspNetCore`) does `_ = w.FlushAsync()` and discards the
task, which means the `pauseWriterThreshold: 1 << 20` set two lines earlier does nothing: a client
uploading faster than the handler drains grows the pipe without bound. `PipeIoBridge` has the same
shape plus an unbounded `_staged` queue of `ArrayPool` rentals, filled from the loop thread.

Both are commented as known ("demo backpressure", "advisory"), and the proper fix is the receive
**parking** already recorded as item 7 (do not re-arm the receive until the flush completes). The point
worth adding to that item: it is not only an architecture/perf item, it is currently the DoS. Note that
`SocketSet.AspNetCore` is a published package, not the demo.

### D4. `ReceiveContext.RawBuffer` exposes other connections' bytes past `PayloadBytes`, by design

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

### D5. `SocketSet.snk` is a full RSA private key, committed

Verified rather than assumed: the file begins `0x07` (PRIVATEKEYBLOB) with `RSA2` magic, ie. a full key
pair, not a public key. `Directory.Build.props:47-50` has `SignAssembly=true` with `PublicSign=false`.

.NET Core does not verify strong names, so this is not an authentication bypass and the severity is
low. What it does mean, now that the packages are live on nuget.org, is that anyone with the repo can
build a binary-compatible assembly carrying our exact identity; and if the repo is ever public, that is
unrecoverable without rolling the key, which changes assembly identity for every consumer. The standard
answer is `PublicSign` with only the public key checked in. Marc's call because it touches published
packages.

### D6. Smaller items, recorded rather than fixed

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

Every Linux gate, run after all the fixes above were in:

| gate | result |
|---|---|
| full solution build | 0 errors, no new warnings (one pre-existing doc warning fixed in passing) |
| `bench/run-smoke-matrix.sh` | **60/60 PASS**, first run |
| `bench/verify-bind-address.sh` | **6/6 PASS** (new; see below) |
| `bench/verify-tls-floor.sh` | **8/8 PASS**, including both refusal cells |
| `bench/verify-aspnet.sh` | **18/18 PASS** |

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
- **The Windows half is UNRUN.** `IocpShard`, `WindowsRioShard` and `Win32.cs` were edited from Linux,
  which is exactly the situation `TODO.md`'s Windows catch-up section exists for. First thing on the
  next Windows session: `Run-SmokeMatrix.ps1` and `Verify-AspNet.ps1`, plus the discriminating check for
  F1, which is that a listener bound to `127.0.0.1` must NOT be reachable on the box's LAN address any
  more. "TLS still works" cannot distinguish a bind that took from one that did nothing, and neither can
  connecting over loopback. A Windows `verify-bind-address` equivalent (`netstat -ano` plus the pid) is
  wanted.
