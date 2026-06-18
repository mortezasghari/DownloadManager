# ADR-0010: Concurrency model — locking, cancellation, and deferred work

- Status: Proposed / Deferred (one item implemented in Phase 4; the rest deferred)
- Date: 2026-06-18

## Context

A structural/concurrency review examined the locking and cancellation patterns
across the engine, scheduler, and persistence layers. The
design is sound: a lock-free `ProgressAggregator` on the data plane, a single
`_gate` (`System.Threading.Lock`) guarding each control-plane state machine, and
per-attempt deadlines via linked `CancellationTokenSource`s. Two latent risks were
flagged. This ADR records the model and the disposition of each.

## Decision

### 1. Cancel/Pause must not run token callbacks under `_gate` — IMPLEMENTED (Phase 4)

`DownloadHandle.Pause()` and `Cancel()` previously called `_runCts.Cancel()` while
holding `_gate`. `CancellationTokenSource.Cancel()` invokes registered callbacks
**synchronously on the calling thread**, so a callback that re-entered the handle
(or blocked on any unrelated lock) could stall the entire control plane — a
reentrant-deadlock hazard.

Fix (now in code): capture the run CTS under `_gate`, release the lock, then trip
cancellation **outside** the lock via `CancellationTokenSource.CancelAfter(TimeSpan.Zero)`.
`CancelAfter` arms the cancellation on a pool thread and returns immediately, so
no registered callback ever runs on (or blocks) the pause/cancel caller. The
intent flags (`_pauseRequested` / `_cancelRequested`) are still set synchronously
under `_gate`, so the worker observes intent immediately; only the token signal is
decoupled. A disposed CTS (the run already ended) is treated as a benign no-op.

Regression coverage: `DeadlockTests` — cancel completes promptly even when a token
callback blocks on an external lock, and stays prompt for fast callbacks.

### 2. `BinaryProgressLog.Compact()` under the append gate — DEFERRED

`Compact()` performs an fsync + atomic-replace while holding the same `_gate` that
serializes `Append()`. This is **correct** (durability ordering is preserved) but
can spike append latency when compaction runs. The proposed mitigation — snapshot
and swap to a fresh writer under a short lock, then perform the heavy I/O off the
append hot-path — adds meaningful complexity and a second failure-handling path.

Because correctness is not at stake and compaction is infrequent under current
checkpoint cadence (ADR-0006/§6b), this is **deferred**.

**Revisit trigger:** implement the async-compaction handoff if *either* (a)
append-latency spikes from compaction show up in profiling or load testing, or
(b) the progress log grows large enough under real workloads that compaction runs
frequently while many segments are actively appending.

## Consequences

- The control plane can no longer deadlock on a cancellation callback that
  re-enters the handle or blocks on another lock.
- Pause/cancel signalling is now *eventually* (not synchronously) delivered to the
  run token — within a pool-thread hop. Intent flags remain synchronous, so
  scheduler observation is unaffected.
- Progress-log compaction retains its simple, provably-correct single-gate
  implementation until the revisit trigger fires.