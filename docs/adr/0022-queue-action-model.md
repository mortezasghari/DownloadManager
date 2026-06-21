# ADR-0022: Four-action queue model — global Pause/Play, Postpone, Stop, Re-add

- Status: Accepted
- Date: 2026-06-21

## Context

Building on the queue rebuild (ADR-0021: lifecycle-event log is the source of truth; channel + history
are projections), this defines the complete queue-action vocabulary. A preceding adversarial validation
(Phase 1) confirmed the rebuild's load-bearing properties hold — replay determinism/idempotence,
tombstone + re-add ordering, torn-tail/corruption recovery, and projection-vs-truth reconciliation — so the
actions are built on a proven foundation. All actions are lifecycle-event appends (append-event-first, then
reflect in the projection); no new source of truth, no in-place queue mutation outside the model. The
control plane stays lock-based (no hand-rolled mailbox — ADR-0010).

## Decision — the four actions

### 1. Pause / Play — GLOBAL

One action for the whole queue. **There is no per-item pause.** Pause halts the running queue: the
scheduler blocks promotion (a worker **gate** the workers park on before consuming work, so queued entries
keep their position), and every active download is paused via the existing per-download pause (bytes
retained). Play un-blocks promotion and resumes the downloads we paused. While paused nothing downloads
regardless of per-item state. Pause/Play is **orthogonal to Postpone**: Postpone reorders within a running
queue; Pause halts the running queue. Global pause is a runtime control, not a per-download lifecycle
transition — it is not itself logged (the per-download Paused events it triggers are).

### 2. Postpone — PER-ITEM

Sends a download (running or merely queued — doesn't matter) to the **end of the queue** and stops its
active transfer if running. The freed slot promotes the next download; the postponed one **resumes
naturally** when the queue works back to it. It is **not** a parked/terminal state — there is **no
`Postponed` lifecycle state and no un-postpone action**; to send it further back, postpone again.
Implemented on the existing FIFO + lock model, no ordered/priority queue:

- **Running** → pause (retain bytes, free the slot), then re-enqueue at the tail when it has parked.
- **Queued** → append a fresh tail entry and record one *skip* so the worker discards the stale entry at
  the old position (the FIFO has no random-remove); the handle stays Queued — no state change, no
  duplicate early start. Multiple postpones record multiple skips, matched one-for-one with stale entries.

At the orchestration layer Postpone appends a re-queue `Queued` event first, then calls the scheduler.

### 3. Stop — PER-ITEM (terminal)

Append a `Stopped` event (append-first) and stop the download via the existing cancel path. It leaves the
active queue and appears in history through the **same terminal projection** as a completed download
(ADR-0021 / the completed-leaves-queue fix) — not via an ad-hoc removal. Partial bytes are handled per the
existing stop/cancel semantics.

### 4. Re-add from history — PER-ITEM

Append a `Queued` event (new id, reconstructed from the original's logged URL/target) → it appears at the
**tail** of the active queue and the original terminal record remains in history. Both projections derive
from the same log.

## Scheduler additions (control plane, lock-based)

`IDownloadScheduler` gains `PostponeAsync`, `PauseQueue`/`ResumeQueue`/`IsQueuePaused`. These are small,
lock-based control-plane additions: a `TaskCompletionSource` resume-gate the worker parks on (fast-path
skipped when open), and a per-id postpone-skip counter consumed in the worker loop before `TryBeginRun`.
The engine algorithm, segmentation, `.dlmeta`/`.dllog` durability ordering, the lock-free data plane, and
the SH-1 fixes are untouched; no actor/mailbox loop.

## Crash-safety (accepted trade, unchanged)

All mutations are append-event-first. A crash mid-mutation may leave queue membership/position slightly
stale on restart (recovered from the log); bytes are never lost. Documented and accepted — closing it
fully is the deferred mailbox (ADR-0010), not worth it now.

## Consequences

- A coherent four-verb queue model, all expressed on the one event log; the channel and history stay
  projections of it.
- Postpone needs no ordered/priority queue — tail-append + skip on the plain FIFO suffices; it resumes
  naturally with no benched state.
- Global pause halts the queue without per-item pause and without dropping or terminating anything.
- Verified end-to-end against the real scheduler (postpone running/queued/twice; global pause blocks
  promotion; interactions while paused) and at the VM layer (append-first ordering; Stop terminal →
  history; log replay confirms in-model behavior). AOT-clean on all RIDs.
