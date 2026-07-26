# TODO

Engineering backlog — design calls and deferred work. Not user-facing (see `README.md` for that).

---

## Factor the shared IOCP/RIO data path

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

**Why it matters:** every feature is currently written twice. TLS interception was (2026-07-25), and an
epoll backend (below) would make it a third copy of the send machinery. That is the argument — not
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

**Status:** proposed, not started. Sequence **after** the IOCP/RIO factoring, so it lands on a shared
base rather than adding a fourth copy of the send machinery.

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

**Do first:** measure io_uring vs managed on a **real Linux host** with `--latency` and `--bandwidth`.
If managed is within a few percent, the performance case is dead and the decision is purely
architectural. This cannot be measured under Docker Desktop (see below).

---

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
- **io_uring is unmeasurable under Docker Desktop.** Without `--privileged`, seccomp blocks it and the
  backend silently falls back to managed sockets; with `--privileged` io_uring is selected but multishot
  receive yields no completions on the WSL2 kernel. Needs a real Linux host or a proper WSL distro. Two
  commit messages (`925db23`, `1b48f8a`) claim "verified io_uring in a Linux container" — that was
  actually the managed backend, and the claim should be corrected.
