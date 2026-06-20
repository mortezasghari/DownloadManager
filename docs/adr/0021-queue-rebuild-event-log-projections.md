# ADR-0021: Queue rebuild — lifecycle-event log as source of truth, channel + history as projections

- Status: Accepted
- Date: 2026-06-20

## Context

The scheduler's in-memory FIFO `Channel<DownloadId>` was the de-facto record of queue membership, which
made queue mutations into in-memory operations whose crash-safety depended on careful ordering and scattered
"what state is this download in" across the channel, the handle dictionary, and `ProcessAsync`. This applies
the project's own durability principle to its own control plane: the durable record is truth; the in-memory
structure is derived and disposable. See `docs/design/Queue rebuild.md` (authoritative).

## Decision

### A durable append-only lifecycle-event log is the single source of truth

`queue.log` lives in the OS config dir beside `settings.json`/`history.json`, as newline-delimited
source-gen JSON (`LifecycleEvent`, AOT-safe, enum-as-string). It mirrors the `.dllog` discipline: each
append is a positioned write + fsync; replay keeps the latest event per id; a torn final record is
discarded on open (the write offset is set past the last complete record, so a partial tail is overwritten);
delete is a `Deleted` tombstone append; compaction is deferred. **No in-place mutation anywhere.** Events:
`Queued`, `Started`, `Paused`, `Stopped`, `Completed`, `Failed`, `Deleted`.

### The channel and `history.json` are projections (CQRS read models)

`QueueProjection.Reduce` deterministically reduces the log to two read models: the **active** queue
(downloads whose latest event is `Queued`/`Started`) and **history** (terminal: `Completed`/`Failed`/
`Stopped`), honoring tombstones. `QueueRecoveryService.Recover` runs on startup — it replays the log,
**rebuilds `history.json` from the terminal projection** (so a lost/corrupt history file is restored, never
trusted), and returns the active downloads to re-enqueue (their ids preserved, so the same logical download
survives restarts without duplication). For this app's scale, replay-into-both-projections on startup is the
simplest correct option; the history file is a cold-start cache, updated opportunistically but always
rebuildable from the log.

### Mutations compose from FIFO primitives, append-event-first

Pause/stop/re-add decompose to `remove`/`park`/`enqueue-at-tail` on the existing `System.Threading.Lock`
FIFO — **no ordered/priority-queue replacement**. Every mutation appends the lifecycle event **first** (the
durable, non-destructive step) and only then reflects it in the in-memory projection (the disposable step).
Re-add-from-history appends a fresh `Queued` event (new id, reconstructed from the original's logged
URL/target); the queue and history projections both update because both derive from the same log.

### Where emission lives — the orchestration layer, not the scheduler

Emission is in the view-model/orchestration layer: `Queued` is appended append-first at enqueue/re-add
(authoritative), and Started/Paused/terminal transitions are appended as observed. The download
**engine, the scheduler internals, the `.dlmeta`/`.dllog` durability ordering, and the lock-free data plane
are untouched** — this is a control-plane projection change only. URLs written to the log are userinfo-redacted
(SH-1 F4).

## Accepted trade — crash mid-mutation

A crash in the window of a multi-step mutation may leave a download's queue *membership* slightly wrong on
restart (e.g. a paused-then-crashed download comes back active, or a resume needs a re-click). **Accepted and
deliberate:** bytes are never lost (the file-durability path is untouched); append-first ordering shrinks the
window to "between two appends," and the in-memory list is rebuilt from the log anyway. Full atomic
multi-step mutation would require the single-owner mailbox — deferred (see ADR-0010). Transitions observed by
the UI poll (Started/Paused/terminal) are best-effort; the authoritative mutations (enqueue/re-add) are
append-first.

## Consequences

- Queue membership and terminal state have one durable source of truth; the channel and `history.json` can
  never disagree with it because both are reduced from it.
- A crash mid-enqueue recovers the download from the log (regression-tested red-against-naive-in-memory-first).
- `queue.log` round-trips under AOT on every RID (`--smoke` `LIFECYCLE OK`), alongside config/history/preallocation.
- Pure BCL, reflection-free, AOT/trim-safe, no new dependency; engine/durability/`_gate` unchanged.