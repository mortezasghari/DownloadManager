# ADR-0013: Speed/ETA smoothing and the UI threading model

- Status: Accepted
- Date: 2026-06-18

## Context

Phase 5 adds the Avalonia UI. Two cross-cutting decisions shape it: how to derive a *useful* speed/ETA
from the engine's progress counters, and how the UI observes a concurrently-running engine without
blocking the UI thread or reaching into engine internals.

## Decision

### Progress source: a lock-free snapshot on the handle

The engine already reports `DownloadProgress` via an `IProgress<T>` hook that the scheduler previously
discarded. The scheduler now passes the `DownloadHandle` itself as that sink; the handle stores
completed/total bytes and phase in independently-`Volatile` fields and exposes a
`DownloadProgress Progress` snapshot on `IDownloadHandle`. This is additive — durability ordering, the
state machine, the transition table, and the engine algorithm are unchanged. Reads are lock-free and
safe to poll from the UI thread. (The reports are low-frequency — one per checkpoint — so there is no
boxing and no contention worth a lock; a reader at worst sees one field a tick stale, harmless for
display.)

### Speed: smoothed over a moving time window — never instantaneous

Instantaneous bytes/tick jitters uselessly on a bursty network. `SpeedSmoother` keeps recent
`(timestamp, cumulative-bytes)` samples within a window (default **5 s**) and reports the average rate
across the window: `(newestBytes − oldestBytes) / (newestTime − oldestTime)`. The clock is an injected
`TimeProvider`, so the calculation is fully deterministic under `FakeTimeProvider` and the engine stays
clock-free.

Rules:
- Speed is **null** (shown as "—") until there are ≥2 samples.
- A stalled transfer reports **0**, not a negative or a spike.
- **ETA** = remaining / smoothed-speed, shown as "—" whenever speed is null/0 or the total size is
  unknown — **never** "Infinity".

### UI threading: poll snapshots on a throttled UI-thread timer; everything else async

- A `DispatcherTimer` on the UI thread ticks ~3×/sec and calls `MainWindowViewModel.Tick()`, which
  calls `Refresh()` on each row. `Refresh()` only does lock-free reads of the handle snapshot + pure
  arithmetic — **no engine work, no blocking, no I/O** on the UI thread.
- Control commands (`Add`, `Import`, `Pause`, `Resume`, `Retry`, `Remove`, re-authorize) are
  `AsyncRelayCommand`s that `await` the scheduler's async API and never block the UI thread. A command
  is disabled while running and disabled when illegal for the current state (the UI reflects the state
  machine, it doesn't just reject) — `Can*` flags derived from `Status`/`NeedsCredentials`.
- The view-model is **Avalonia-free** (pure BCL + Core): the timer lives in the view, and dialogs
  (file picker, credential prompt) sit behind interfaces. This is what makes the VM headless-testable.

### NeedsCredentials resume

A `Failed` row with `NeedsCredentials` offers a re-authorize action: prompt for fresh credentials
(session-memory only, never persisted — ADR-0011), then re-enqueue the **same target** so the engine
resumes from the retained on-disk progress. The row is replaced in place; the old (Failed) handle is
left inert.

## Consequences

- Speed/ETA are stable and readable; no Infinity, no flicker.
- The UI never blocks on engine work and never touches engine internals — it consumes `IDownloadScheduler`
  / `IDownloadHandle` only.
- VM logic (smoothing, command enablement, import summary, re-auth flow) is unit-tested headless with a
  `FakeTimeProvider` and fake scheduler/handle — no Avalonia render in the test path.
- Polling at a fixed cadence (rather than event push) is intentional: it bounds UI work regardless of how
  fast bytes arrive, and matches the lock-free snapshot model.