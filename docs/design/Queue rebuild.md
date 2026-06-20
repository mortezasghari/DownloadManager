# Design — Queue Rebuild (event-log source of truth + projections)

Status: **Designed, ready to build.** This is the authoritative spec; the build prompt is written from this doc.

## Why

Today the scheduler's in-memory FIFO `Channel<DownloadId>` is the de-facto record of queue membership. That makes queue mutations (pause, stop, re-add) into in-memory operations whose crash-safety depends on careful ordering, and it scatters "what state is this download in" between the channel, the handle dictionary, and `ProcessAsync`'s sequencing — the split-ownership that ADR-0010 already flagged and that produced real bugs.

The rebuild makes a **durable append-only lifecycle-event log the single source of truth**, and turns the volatile structures into **projections** of it. This is the project's own durability principle applied to its own scheduler: the durable record is truth; the in-memory thing is derived and disposable.

## The five decisions (locked)

1. **Durable state is the source of truth.** A download's lifecycle state lives in a durable, append-only event log — not in the channel, not in memory.
2. **The channel is a projection.** The in-memory FIFO is a *work list* rebuilt from the log; it is never authoritative. Lost/cleared in-memory state is reconstructed from the log on startup.
3. **Mutations compose from primitives.** Pause-to-end, stop, re-add are built from `remove` / `park` / `enqueue-at-tail` — operations a plain FIFO already supports. **No ordered-queue/priority-queue replacement is needed** (this was the big deferred chunk; the decomposition routes around it).
4. **Append-only status log, tombstone deletes, compaction deferred.** Same pattern as the existing `.dllog` segment log: append events, replay latest-per-id on recovery, truncate torn tails, delete = append a tombstone, compact later. No in-place mutation anywhere (in-place writes are what reintroduce the crash gap).
5. **Lock-based control plane; mailbox deferred.** Keep the existing `System.Threading.Lock`-based mutations. **Do NOT build a hand-rolled single-owner mailbox.** Revisit the actor/mailbox model when Akka.NET 1.6 (AOT) ships — at which point it's a proven library, not a hand-rolled refactor (see ADR-0010 update).

## CQRS: one truth, two read views

- **The append-only lifecycle-event log** is the write side / source of truth. Events: `Queued`, `Started`, `Paused`, `Stopped`, `Completed`, `Failed`, `Deleted` (tombstone). Each event carries the download id + the data needed to reconstruct (URL, target path, etc.).
- **The active queue (channel)** is a read model: the projection of *non-terminal* state. Rebuilt from the log on startup.
- **`history.json` is a read model / cache**: the projection of *terminal* state (completed/stopped/failed), materialized newest-first for fast reads. **Not authoritative.** If lost/stale/corrupt, rebuild it by replaying the log. Treat it as a persisted cache of the projection, updated opportunistically, reconciled to the log on startup / after compaction.
- **Re-add-from-history = append a `Queued` event** for that id. The queue projection and the history view both reflect it automatically, because both derive from the same log. No copying between stores; no "is the history record rich enough to reconstruct" problem.

### Read-model rebuild policy
For this app's scale (thousands of entries, not millions), **replay the log into both projections on startup** is cheap and is the simplest correct option. `history.json` is a cold-start cache of that materialization. Incremental update of the cache on each terminal event is allowed as an optimization, but correctness rests on "rebuildable from the log," never on the cache being perfectly maintained.

## Accepted trade — crash-mid-mutation

A crash in the millisecond window during a multi-step mutation (e.g. between "removed from in-memory queue" and "append `Paused`") can leave the download's *queue membership* slightly wrong on restart (e.g. comes back active instead of paused, or needs a re-click to resume). **This is accepted, documented, and deliberate.**
- **Bytes are never lost** — file durability is the untouched `.dlmeta`/`.dllog` path.
- The append-only log already shrinks the window to "between two appends"; ordering the **non-destructive append first** (append the new state *before* removing from the disposable in-memory list) shrinks it further, since the in-memory list is rebuilt from the log anyway.
- The residual is a rare, recoverable inconvenience. Hardening it fully would require atomic multi-step mutation = the mailbox refactor = not worth it now (see decision 5).

## DO NOT TOUCH (hard boundary)

This is a **control-plane projection change only**. Leave entirely alone:
- The download engine algorithm, segmentation, parallel segment execution.
- `.dlmeta` / `.dllog` durability ordering (write → fsync data → append+fsync progress).
- Routing, config (`settings.json`), the history-store *internals* (this change makes history a projection, but does not alter the download/checksum/routing behavior).
- All SH-1 security-hardening fixes (router containment, ArgumentList launcher, URL redaction, prealloc clamp, bidi-strip) and their regression tests.
- The data plane: progress counters stay lock-free (`Interlocked`/`Volatile`); the durability append-lock stays.

## What actually changes
- A new append-only lifecycle-event log store (BCL, source-gen or fixed-record binary, AOT-safe, atomic-append + fsync, replay-latest-per-id, tombstones).
- A persisted lifecycle-state field per download, written as events to that log.
- Startup recovery extended to **rebuild the channel + history projection from the log**.
- Each queue mutation becomes **append-event-first, then reflect-in-projection**, on the existing lock model.
- ADR-0010 updated: mailbox deferred with the Akka-1.6/AOT trigger.

## Open questions — none
All design decisions are settled. The build prompt follows from this doc.

## Build discipline
Design-first (this doc). Gate on all five RIDs. Regression test the crash-safety property: a crash injected mid-mutation must recover to a consistent (if slightly-stale) state with no byte loss and no corruption — **red before the rebuild's recovery logic, green after**. Close out: merge → re-verify on master → tag → delete branch.