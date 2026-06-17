# Roadmap & Progress

Status of the cross-platform download manager against the spec's six phases.
The two hardest, highest-risk pieces — Native AOT viability and durability /
recovery correctness — are already complete.

## Snapshot

| Phase | Scope | Status |
|---|---|---|
| **0 — AOT spike** | Solution, CPM, 4 projects, multi-RID AOT gate | ✅ Done |
| **1 — Durability-first single-stream** | Engine (1-segment), durable persistence, crash-safe recovery, `If-Range` resume | ✅ Done |
| **2 — Segmentation** | N parallel segments, range probing, native preallocation | ✅ Done |
| **3 — Scheduler & control** | `Channels` queue, concurrency, pause/resume/cancel, retry+backoff | ⬜ Not started (next) |
| **4 — Ingestion & network breadth** | URL import, auth/cookies/proxy, encoding, checksum | 🟡 ~20% (proxy + encoding done) |
| **5 — Polish & tuning** | Real UI, perf measurement, log completeness | 🟡 ~10% (Phase 0 shell only) |

**Roughly 55% of total effort complete.** Remaining phases are mostly breadth, not depth.

## Done (verified)

- 53 tests passing; build clean (0 warnings); AOT publishes to a native ELF on
  `linux-x64` with 0 IL/trim warnings (engine + native P/Invokes rooted via the
  UI composition root, so the publish actually validates them). CI gate covers
  all 4 RIDs.
- **Phase 2**: multi-segment downloads run in parallel (bounded by
  `MaxSegmentConcurrency`), segmenting only on confirmed `206` and above the
  small-file threshold, with the count clamped to `[1,16]`. Native full
  preallocation (`posix_fallocate` / `fcntl(F_PREALLOCATE)` / `FILE_ALLOCATION_INFO`)
  with a Full→Sparse→None fallback that never aborts the download. Mixed-state
  recovery resumes only incomplete segments, each with `If-Range`. ADR-0006/0007.
- The engine was already segment-shaped, so recovery and the §6c durability
  ordering generalized without a second code path.
- ADRs 0001–0007 recorded.

## Next steps

### ▶ Phase 3 — Scheduler & control (next)

1. `System.Threading.Channels` bounded queue; max-concurrent-downloads gate.
   (Per-download segment concurrency already exists via `MaxSegmentConcurrency`.)
2. Operations: enqueue / pause / resume / cancel / retry via `CancellationToken`.
3. Retry policy: exponential backoff + jitter (`TimeProvider`-driven), bounded
   attempts, honor `Retry-After`. The transient/permanent classifier
   (`HttpErrorClassifier`) already exists.
4. This is where the Phase-1 "queue" item lands properly.

### Phase 4 — Ingestion & network breadth

1. Per-download auth headers + cookies (plumb through the request).
2. URL import / list ingestion.
3. Optional post-completion SHA-256 verification (`ExpectedSha256` field already
   in metadata).

### Phase 5 — Polish & tuning

1. Real MVVM UI: per-download rows (name, status, progress, speed/ETA computed in
   the VM from `IProgress` + `TimeProvider`), Add / Import / Pause / Resume /
   Remove / Retry.
2. Perf measurement against §11 levers (fsync cadence, buffer size, connection caps).
3. Log-completeness pass.

## Cross-cutting tracker

- **Deferred & assigned**: native prealloc → P2 · queue/retry → P3 ·
  auth/cookies/checksum → P4 · speed/ETA/UI → P5.
- **Open product decision**: best-effort-resume opt-in for no-validator resources
  (§7) — currently restarts (safe default). Revisit in P4.
- **Standing gate**: every phase must keep build clean + tests green +
  AOT-publish on all RIDs.