# ADR-0009: Pause/resume via persistence rehydration, not in-memory hold

- Status: Accepted
- Date: 2026-06-18

## Context

A paused download must be resumable — including across an app crash or reboot,
not just within one process lifetime. The tempting shortcut is to keep the
download's in-flight progress (segment offsets, open handles, HTTP connection) in
memory while paused and resume from it.

## Decision

**Pause stops the work and is just a recovery checkpoint; resume rehydrates from
the persisted state.** There is no special in-memory "paused download" holding
hot progress.

- On pause, the worker cancels the run; the engine unwinds, having already made
  durable progress via the §6c ordering (data fsync → progress append → progress
  fsync). All handles, the HTTP response, and the CTS are disposed.
- On resume, the scheduler simply re-enqueues the download. A worker re-runs
  `IDownloadEngine.RunAsync`, which reloads metadata + the progress log from disk,
  reconciles per-segment durable offsets, and resumes **only the incomplete
  segments**, each with an `If-Range` precondition (Phase 1/2 recovery — spec
  §6d/§7). Completed segments are skipped.

Resume is therefore identical to crash recovery: the same code path, exercised
two ways. This is the single-recovery-path principle (spec §0.5) applied to the
lifecycle.

## Rejected: in-memory hold

- It would be a *second* resume path that diverges from crash recovery — exactly
  what the spec forbids, and the place subtle "resumed-from-stale-memory" bugs
  live.
- It would pin a `SafeFileHandle`, an HTTP connection, and buffers for the entire
  pause duration (possibly hours) — a resource leak by design and a violation of
  the resource-hygiene requirement.
- It could not survive a crash, so a separate persisted path would be needed
  anyway. One path is strictly better.

## Consequences

- A pause→resume round-trip provably goes through persistence and resumes only
  incomplete segments (tested with the fake HTTP server asserting `If-Range` and
  per-segment offsets, no in-memory shortcut).
- Resume costs one re-probe/revalidation and a log scan — negligible against a
  multi-gigabyte download, and the price of correctness.
- Memory held by a paused download is essentially just its `DownloadHandle`.