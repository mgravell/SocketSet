# PRE-REGISTERED 2026-08-06, before `Run-TlsSizes.ps1 -Repetitions 7` was launched

Item (c): Windows baseline re-run at the CORRECTED (derived) shard default. The host is the 7900X
desktop, 24 logical, server half pinned to 12, so the derived default is **s12** against the old
hard-coded **s16**.

**These are all WITHIN-RUN claims on purpose.** House rule 1 forbids differencing against the
2026-08-05 table (different session; two identical builds have measured 6% apart here, once 58%), so
"the 256 KB plaintext number goes up" is NOT a testable prediction. What is testable is where each leg
sits against its own same-session controls.

- **P7 — at 256 KB PLAINTEXT, `iocp` reaches PARITY with the same-session `kestrel` control**
  (overlapping min-max), instead of the ~20% deficit the s16 table showed. Basis: s16 was measured as a
  bad operating point for this path (s8 10,943 [2.6%] vs s16 9,283 [16.5%], disjoint), and s12 measured
  10,817 [4.5%] in that same sweep. **FALSIFIED IF** kestrel leads iocp disjointly at 256 KB plaintext.

- **P8 — at 256 KB TLS, `iocp+tls` does NOT beat `kestrel+tls`.** s12 is the WRONG side of the
  path-dependent inversion for the TLS path, which wants MORE shards (s16 > s12 > s8, +26.9% s8→s16
  disjoint). So the corrected default should, if anything, cost the TLS row. **FALSIFIED IF** iocp+tls
  leads kestrel+tls disjointly at 256 KB. This one is a prediction that our own corrected default makes
  a headline WORSE, and it is recorded because that is the honest consequence of picking a defensible
  default over an optimal one.

- **P9 — at 256 KB plaintext, `rio` trails `iocp` disjointly** (the one-page-per-`RIOSend` cost; P5,
  confirmed at s8 as ~34%). **FALSIFIED IF** the ranges overlap or rio leads.

- **P10 — REPRODUCIBILITY CHECK, not a new claim: `httpsys` is LAST of the plaintext legs at 512 B and
  FIRST at 256 KB.** That was 2026-08-05's surprise (P6 falsified). A fresh baseline either reproduces
  it or tells us the earlier finding was session-specific. **FALSIFIED IF** either end fails to
  reproduce.

- **P11 — HONESTY CHECK on the instrument: our legs' per-leg spreads come in TIGHTER than the s16 run's
  5.8-16.3%.** The s16 spread inflation was attributed to oversubscription (12 CPUs, 16 loop threads).
  If s12 does not tighten them, that attribution is wrong and the noise has another source. This is the
  one I am least confident about, because the shard sweep's own s12 cells ran 4.5-9.4%.

Not predicted, recorded as open: the 512 B and 16 KB rows. Nothing in the shard work touched them and
the 2-byte sweep said more shards is monotonically better there, so s12 could plausibly cost the small
end. No direction claimed.
