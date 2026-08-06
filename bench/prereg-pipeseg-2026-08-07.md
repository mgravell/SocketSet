# PRE-REGISTERED 2026-08-07, before `Run-Byo.ps1` / `Measure-PipeMemory.ps1` were launched

Item (b): should `--pipe-segment 65536` become the **Windows default**? `RESULTS.md` (2026-08-05) says
the cost/benefit "now looks strongly favourable" but explicitly declines to change it, because the
+117.3% benefit and the 1.17x memory bill came from **different sessions and different rigs**, and a
defaults change of that reach wants a same-session A/B carrying BOTH. That is what this run is.

Host: 7900X, 24 logical, server half pinned to 12; both rigs default to `Shards 12`, which is the
corrected default measured on 2026-08-06.

**Every claim is WITHIN-RUN** (house rule 1). In particular, "1.17x reproduces" is NOT a testable claim
against the 2026-08-05 figure; what is testable is the ratio measured inside this session.

- **P12 — `byo-seg64k` beats `byo` at 256 KB, DISJOINTLY and by a lot.** This is the zero-copy send
  engaging: a 256 KB response through the default ~4 KB pipe blocks is **exactly 65 segments** against
  IOCP's 64-`WSABUF` cap, so the backend declines every response; at 64 KB blocks it is ~5 segments and
  is accepted. **FALSIFIED IF** the ranges overlap.

- **P13 — `classic-seg64k` vs `classic` shows NO disjoint difference at either size.** Pipe block size
  ALONE was previously found to move nothing on Windows; the win belongs to zero-copy engaging, not to
  bigger blocks. This control is what stops P12 being credited to the wrong mechanism. **FALSIFIED IF**
  `classic-seg64k` separates from `classic` at either size.

- **P14 — THE DISCRIMINATING ONE. At a 64 KB payload, `byo-seg64k` does NOT beat `byo` by anything like
  the 256 KB margin.** 64 KB / ~4 KB blocks is ~16 segments, comfortably UNDER the 64 cap, so zero-copy
  should ALREADY engage at the default block size and the bigger block should buy little or nothing.
  This is the prediction that tests the MECHANISM rather than the effect: if 64 KB shows a similarly
  large gain, the segment cap is not the story and the whole explanation in `TODO.md` is wrong.
  **FALSIFIED IF** the 64 KB gain approaches the 256 KB gain.

- **P15 — memory: `byo-seg64k` costs MORE working set than `byo` at 2048 connections, and the effect is
  invisible or inverted at 64 connections.** The 64-connection row is the rig's own documented trap (a
  fixed ~105 MB baseline swamps a per-connection effect, and it read 0.92x — better than free — last
  time). **FALSIFIED IF** 2048 shows no increase, which would mean the bigger block is genuinely free
  and the flag's original justification never applied here at all.

- **P16 — `byo-seg64k-pin` is not obviously better than `byo-seg64k` on THROUGHPUT.** The pinned pool
  exists for the memory side. Recorded because it is the configuration `RESULTS.md` says is "actually
  worth recommending", so if it costs throughput that matters to the decision.

**What would make me NOT change the default**, stated now so the decision is not reverse-engineered from
whatever the numbers turn out to be:
1. P13 falsified (bigger blocks help even without BYO) — that would mean the change helps for a reason
   nobody has modelled, and it should be understood before it becomes a default.
2. The 2048-connection memory cost coming in materially above ~1.2x.
3. Any disjoint throughput REGRESSION at 64 KB, which is the size most likely to be a common payload.
