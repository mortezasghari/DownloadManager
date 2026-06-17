# ADR-0004: Durability ordering, the recovery invariant, and one recovery path

- Status: Accepted
- Date: 2026-06-16

## Context

A crash or power loss must never leave the manager believing it has more durable
bytes than the file actually contains (spec §0.4/§6c). Recovery is the hard part
and the spec demands it be designed once — single-stream as the 1-segment case —
not re-invented per phase (§0.5/§14).

## Decision

### The invariant

> Persisted progress must never exceed durable file bytes.

### The ordering (enforced in exactly one place)

`DownloadEngine.Checkpoint` performs, in this fixed order:

1. `ITargetFile.FlushToDisk()` — fsync the segment data to the device.
2. `IProgressLog.Append(checkpoint)` — record the new durable offset.
3. `IProgressLog.FlushToDisk()` — fsync the progress record.

Because data is fsynced *before* its offset is recorded, on crash the file always
holds **at least** the recorded bytes. Resume from a recorded offset can only
re-download bytes that are already durable — never overwrite-from-stale or skip a
hole. Losing the last (un-fsynced) progress record is safe: it only causes a
small re-download, the harmless direction.

`fsync` is real on every platform: `fsync` on Linux, `F_FULLFSYNC` on macOS
(plain `fsync` there does not flush the drive cache), `FlushFileBuffers` on
Windows — all via `LibraryImport` (`DurableIo`).

### One recovery path

A non-segmented download is the 1-segment case of `SegmentLayout`. Recovery
(`BinaryProgressLogStore.Open`) returns per-segment durable offsets the same way
for 1 or N segments; the engine seeds each segment's start from them identically.
Phase 2 parallelizes the segment loop without touching recovery or durability.

### Resource-change handling lives at resolve time

Rather than detect a changed resource mid-stream and try to repair, the engine
re-validates on resume with an `If-Range` probe (§7). A `200` (precondition
failed), a size mismatch, or a missing validator means *discard partial state and
restart* — so the streaming path always runs against a resource proven unchanged.
A belt-and-suspenders `Content-Range` check on each segment response still fails
loudly if reality diverges.

## Consequences

- The dangerous direction (recorded > durable) is structurally impossible.
- Over-fsyncing would wreck throughput, so the cadence is one fsync per
  ~8 MB checkpoint (configurable, §6b/§11) — not per write.
- Tested directly: `DurabilityOrderingTests` asserts the data-flush → append →
  log-flush triplet and that a recorded offset never exceeds flushed bytes;
  `EngineTests`/`ProgressLogTests` cover resume, If-Range 200 fallback,
  validator-missing restart, torn-tail truncation and CRC rejection.