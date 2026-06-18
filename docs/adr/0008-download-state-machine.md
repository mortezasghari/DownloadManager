# ADR-0008: Download lifecycle state machine (pause vs cancel)

- Status: Accepted
- Date: 2026-06-18

## Context

Phase 3 adds lifecycle control (enqueue/pause/resume/cancel/retry) under
concurrency. The hard part is not the queue — it is that control operations race
each other and the worker. Pausing while a retry fires, cancelling during a
resume, cancelling mid-backoff: these must never deadlock, double-start a
download, leak a running segment, or land in an inconsistent state.

## Decision

### States and legal transitions

```
Queued    -> Running | Paused | Canceled
Running   -> Completed | Failed | Retrying | Paused | Canceled
Retrying  -> Running | Paused | Canceled        (Retrying = cancellable backoff)
Paused    -> Queued | Canceled
Failed    -> Queued | Canceled                  (Queued = manual retry)
Completed -> (terminal)
Canceled  -> (terminal)
```

Every transition goes through `DownloadHandle.TransitionTo`, which validates
against this table and **throws `InvalidDownloadTransitionException` on an
illegal transition** (resume a Completed, pause a Canceled, …). Control
operations validate their own legal source states and throw the same way.

### Pause and cancel are distinct intents, not one token

The handle holds two flags (`_pauseRequested`, `_cancelRequested`) and a single
per-run `CancellationTokenSource` linked to the scheduler shutdown token. Pause
and cancel both *stop* the active run by cancelling that source, but they set
different intents, and the worker reads the intent to choose the destination
state (Paused vs Canceled). They are never the same signal.

### One lock; worker owns active-state transitions

All state, flags, and the run CTS live behind one lock. The rule:

- **Queued / Paused / Failed** (no worker running): control ops transition
  directly under the lock (e.g. Queued→Paused, Failed→Queued).
- **Running / Retrying** (a worker owns it): control ops only set the intent and
  cancel the run token; the **worker** performs the terminal transition when the
  run unwinds.

This makes the races safe by construction:
- *pause while queued vs worker pickup*: the lock serializes Queued→Paused
  against Queued→Running; the loser is a no-op (worker's `TryBeginRun` returns
  false and skips).
- *cancel during resume*: whichever wins the lock first decides; the other sees a
  non-legal source and either throws (resume after cancel) or no-ops (worker
  skips a Canceled id).
- *duplicate queue entries* (from resume/retry): only one worker wins
  Queued→Running; stale ids are skipped. No double-start.

### Pause/cancel semantics

- **Pause** leaves a consistent, resumable on-disk checkpoint (the engine's §6c
  ordering guarantees it) and frees the worker/slot.
- **Cancel** is terminal and **discards** partial state (sidecars + partial
  target via `IDownloadEngine.Discard`). Chosen over "keep resumable" so a
  cancelled download is truly gone.
- **Completion wins** any racing pause/cancel: if the run finished successfully,
  the file is done and sidecars are gone, so the worker reports Completed.

## Consequences

- Illegal operations fail loudly and testably.
- Cancellation propagates through the linked token into the engine's in-flight
  `RandomAccess` writes and network stream, then the worker releases the slot.
- `DownloadHandle.WaitForStatusAsync` lets tests await transitions deterministically
  (no sleeps).