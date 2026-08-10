# PRE-REGISTERED 2026-08-10, before the io_uring endpoint-tracking A/B was run

TODO item 0c: io_uring's endpoint declination is a cost choice whose cost claim has never been
measured. The change under test populates `RemoteAddress`/`LocalAddress` in `AdoptAccepted` and
`HandleConnect` via `NativeEndpoints.Populate` (getpeername + getsockname, two syscalls), gated on
`TrackEndpoints` exactly as every other backend is. The A/B is therefore the option itself, on the
SAME build: `TrackEndpoints=true` vs `false`, io_uring, plaintext, accept-churn (`SmokeTest --accept
--reset-close` shape: one round-trip per socket then reconnect, so accepts/sec IS the metric).

Why the load is accept-churn: the two syscalls are per-ACCEPT, so steady-state echo throughput
structurally cannot see them; a rig that showed "no cost" on a keep-alive workload would be measuring
nothing. House rule: confirm the path was TAKEN — the banner must say `endpoints=on` on the A leg and
`endpoints=off` on the B leg, and a `verify-endpoints` pass on the same build is the proof the
syscalls actually run.

- **P12 — the cost is AFFORDABLE at default-on: tracking-on accept-churn throughput is within noise
  of tracking-off** (overlapping min-max over six passes each, same session, same core pinning).
  Basis: the added work is ~2 cheap syscalls + a 24-byte parse against an accept path that already
  pays socket creation, a `setsockopt`, slot claim, and (the dominant term under churn) full teardown;
  every other backend pays the identical two syscalls by default and nobody has ever seen them in a
  churn number. **FALSIFIED IF** the on-leg's range sits disjointly below the off-leg by more than
  3%. If falsified, the consequence is pre-stated: the populate stays, but the io_uring default
  becomes a decision to re-open (per-demo opt-in rather than default-on), and the number goes in
  RESULTS.md either way.

- **P13 — the delta, if any, does not GROW with TLS.** The syscalls are per-accept and TLS adds a
  handshake per accept, so TLS-churn should DILUTE the relative cost, not amplify it. **FALSIFIED
  IF** the TLS-churn on/off delta is disjoint where the plaintext one was not. This is a coherence
  check on the mechanism: if it fails, whatever moved was not the two syscalls.

Not predicted, recorded as open: whether `connect/reports`-style outbound population shows any
measurable cost. No churn rig dials outbound at rate today, so the connect-path cost is asserted
affordable by construction (one populate per `Connect`, a user-initiated operation) and not measured.
