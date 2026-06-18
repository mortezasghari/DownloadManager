# ADR-0010: Concurrency model — locking, cancellation, and deferred work

- Status: Proposed / Deferred-with-trigger (CTS-under-lock hazards fixed by
  point-patches; single-owner mailbox refactor deferred until this area is next
  touched — see the refactor trigger below)
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
callback blocks on an external lock (asserted via a `WaitAsync` timeout, not a
fixed sleep), and stays prompt for fast callbacks.

### 1b. No `CancellationTokenSource` op may run under `_gate` — IMPLEMENTED (3rd fix in this area)

The Pause/Cancel fix (#1) moved the `.Cancel()` calls out of the lock, but
`CancellationTokenSource.Dispose()` was still invoked under `_gate` at three sites:
`RecreateRunToken()` (via `TryBeginRun`/`BeginBackoff`/`BeginRetryRun`),
`DisposeRunCts()`, and `Dispose()`. `_runCts` is a *linked* token source, so its
`Dispose()` can block until an in-flight linked-cancellation callback finishes —
and the #1 fix (cancel on a pool thread via `CancelAfter`) *widened* the window in
which such a callback is in flight exactly when the worker recreates/disposes the
token. **Root severity: a bounded block, not a lock-ordering deadlock** — no
callback registered on `_runCts`, and nothing run synchronously during its
`Cancel`/`Dispose`, ever re-enters `_gate` (the engine holds no reference to the
handle; the worker's re-entry happens only on the async continuation). Still, no
blocking call belongs under the control-plane lock.

Fix (now in code): the same capture-then-act-outside-`_gate` pattern is applied
uniformly. Under `_gate` the code captures the `_runCts` reference and nulls the
field (so the superseded CTS is single-owner and can't be double-disposed or raced
by a concurrent recreate); `Dispose()` runs **after** the lock is released. After
this change **zero** `CancellationTokenSource.Cancel/.CancelAfter/.Dispose` calls
execute while `_gate` is held, at any site. (One `CancellationTokenRegistration.Dispose()`
in `SignalWaiters` remains under the lock; it is not a CTS op, and its callback is a
trivial `TrySetCanceled` on an async-continuation TCS that never touches `_gate` —
a negligible, accepted bounded wait.)

Regression coverage: `DeadlockTests` — `Dispose()`, `DisposeRunCts()`, and
`RecreateRunToken()` (via `BeginBackoff`) each driven 200× concurrently with an
in-flight `Cancel()`, asserting no stall and no `ObjectDisposedException` escapes.

### Lock model: kept by repeated patching — TRIGGER FOR REFACTOR

This is now the **third** fix in the `_gate`/CTS area (#1 Cancel/Pause callbacks,
#1b the three Dispose sites, after the original `_gate` design). The single-lock
control plane keeps surfacing CTS-under-lock hazards that we close one site at a
time. The design is being **sustained by repeated patching**, which is a smell.

**Explicit trigger:** the **next** time this area is touched for *any* reason — a
new lifecycle state, a transition-table change, or a further CTS/locking bug —
replace the lock-plus-CTS control plane with a **single-owner mailbox**: serialize
all control operations and worker transitions as messages processed by one owner,
so no shared lock guards cancellation at all. Do not add a fourth point-patch.

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

- No `CancellationTokenSource` operation runs under `_gate`, so neither a blocking
  token callback nor a concurrent dispose/recreate can stall the control plane.
- Pause/cancel signalling is now *eventually* (not synchronously) delivered to the
  run token — within a pool-thread hop. Intent flags remain synchronous, so
  scheduler observation is unaffected.
- Run-CTS disposal is single-owner (capture-and-null under the lock, dispose
  outside), so concurrent recreate/dispose paths cannot double-dispose; a
  stale-reference `CancelAfter` is guarded against `ObjectDisposedException`.
- The control-plane state machine and transition table are unchanged.
- The single-lock model is retained on borrowed time: the next change to this area
  triggers the mailbox refactor rather than a fourth patch.
- Progress-log compaction retains its simple, provably-correct single-gate
  implementation until its own revisit trigger fires.