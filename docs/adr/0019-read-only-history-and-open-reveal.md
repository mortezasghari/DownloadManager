# ADR-0019: Read-only download history + per-platform open / reveal

- Status: Accepted
- Date: 2026-06-20

## Context

Phase 9 adds a read-only download history: persist a record when a download reaches a terminal state,
show finished items newest-first, and let the user open the file or reveal it in its folder. No
re-download / re-add this phase. No engine, scheduler, durability, or `_gate` change — it sits on top of
the existing handle state and the Phase-7 config-dir/source-gen machinery.

## Decision

### Store — Phase-7-shaped, atomically written

`history.json` lives in the same OS config dir as `settings.json`
(`{ApplicationData}/DownloadManager/history.json`). Serialization is a source-generated
`JsonSerializerContext` (`HistoryJsonContext`) — reflection-free, mandatory under Native AOT (spec §1) —
with `UseStringEnumConverter` so the terminal state is a readable string. The file is written atomically
and durably via `AtomicFileWriter` (temp → fsync → atomic rename → dir fsync), the same hygiene as the
`.dlmeta` sidecar. The store keeps the records in memory (loaded once); each append mutates that list and
rewrites the whole file atomically.

### Record shape — minimal, flat, id-keyed

Each record is `{ id, name, size, state, savedPath }`. `savedPath` (the Phase-7 router's final path) is
**required** — the open/reveal actions depend on it. The on-disk shape is a **flat list** of records
(plus a schema `version`), appended chronologically. A list (not a dictionary) preserves append order
for the newest-first display sort; each record still carries its `id`, so future per-entry delete /
clear-all is trivial to add. Those mutations are **deliberately not implemented this phase** — the shape
just leaves them unblocked.

### Write trigger — terminal only, once per download

A record is written the first time a download is observed in a terminal state (Completed / Failed /
Cancelled), guarded by an id set so it is written exactly once. Progress does **not** write history (no
churn). Detection rides the existing UI poll (the same `Tick` that already reflects handle state), so no
engine/scheduler change is needed.

### Growth — unbounded by decision

History is **unbounded** for now: no cap, no pruning. This is a deliberate choice, recorded here so it is
a decision rather than an unnoticed leak. Capping / pruning is a future knob (it would pair naturally with
the clear-all the id-keyed shape already anticipates).

### Display — newest-first is a view sort, not a file rewrite

The store appends chronologically; the finished view shows records newest-first by reversing for display.
The file is never rewritten to reorder.

### Missing/malformed on load

A missing `history.json` loads as empty history (and is not created until a real append). A malformed file
loads as empty history, logs a warning, and is **not** overwritten — never destroy the user's file, never
crash.

### Open / reveal — per-platform shell-out behind a seam

The one genuinely per-OS code in this phase. Behind an injected `IFileLauncher` seam (so the view-model
stays Avalonia-free and headless-testable), with a pure command builder (`LaunchCommands`) separated from
execution so the exact per-OS invocation is unit-testable on every RID:

- **Linux**: open → `xdg-open <path>`; reveal → `xdg-open <dir>` (xdg-open has no select-file, so opening
  the containing directory is the portable behavior).
- **Windows**: open → shell-execute the path; reveal → `explorer.exe /select,"<path>"`.
- **macOS**: open → `open "<path>"`; reveal → `open -R "<path>"`.

**Missing file:** if `savedPath` no longer exists (moved/deleted since the download), the action surfaces
an error to the user. We attempt and report on failure — **no** live file-existence tracking, **no**
greying-out, **no** filesystem-watch. The launcher returns a `LaunchResult` (never throws); the view-model
surfaces the message.

## Consequences

- Finished downloads persist across sessions and are openable/revealable with two clicks.
- Serialization stays reflection-free and AOT-clean; `--smoke` now round-trips `history.json` under AOT on
  every RID (proving the new string-enum source-gen path), alongside the settings and preallocation checks.
- The store is read-only this phase but shaped (flat, id-keyed, unbounded-by-decision) so clear-all,
  delete-one, capping, and re-download are clean future additions.
- The only per-OS surface (open/reveal) is isolated behind a seam and verified per-RID by construction.
