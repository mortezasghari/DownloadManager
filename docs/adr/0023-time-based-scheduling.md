# ADR-0023: Time-based scheduling — opt-in schedule gate OR'd with the manual global pause

- Status: Accepted
- Date: 2026-06-21

## Context

Add a time-of-day schedule that pauses/resumes the queue, reusing the existing global Pause/Play
(ADR-0022) — no new scheduler mechanism, no engine change. It must be opt-in, DST-safe, and the user must
be able to tell *why* the queue isn't running.

## Decision

### Two independent pause gates, OR'd

```
paused = manualPause OR (scheduleEnabled AND outsideWindow)
```

- **Manual gate** — the existing user Pause/Play, unchanged.
- **Schedule gate** — asserts only when scheduling is enabled and now is outside the window.

The gates are **independent** — no override logic, no "who wins," no sticky state. Each is evaluated
fresh; the queue runs only when neither asserts. Clearing the manual pause while still outside the window
leaves the queue paused (the schedule still asserts), and vice versa — correct by construction. The
view-model holds the manual flag and the last-evaluated schedule flag; their OR is the effective state,
applied to the scheduler **exactly once per transition** via the existing global pause path.

### Opt-in (default off)

`scheduleEnabled` defaults **false**. When off the schedule gate never asserts and the time boundaries are
inert — a user who never enables it never experiences a time-based pause.

### Predicate model, not exact-time events (DST-safe)

The schedule gate is a **pure predicate** — "is now inside `[start, stop)`?" — re-evaluated every UI tick
against the injected `TimeProvider`'s local "now". There are no scheduled timer events to miss: a skipped
or doubled DST clock time is simply read correctly on the next tick. A single daily window applies every
day (multiple windows / per-day schedules are out of scope for v1).

- **Same-day** (`start <= stop`): inside = `start <= now < stop`.
- **Overnight wrap** (`start > stop`, e.g. 23:00–06:00): inside = `now >= start OR now < stop`.
- **Equal start/stop** is treated as an all-day window (always inside) so an accidental equal-time setting
  never pauses the queue forever.

### Boundary behavior

When the schedule gate transitions to asserting (window closes), the queue pauses via the **existing**
global pause: promotion is blocked and active downloads pause through the per-download pause (bytes
retained, resumable). A non-resumable (no range support) download caught at the boundary loses its
in-progress bytes and restarts next window — accepted, not special-cased. When the gate clears (window
opens) **and** the manual gate isn't asserting, the queue resumes via the existing global play.

### Exposing which gate asserts

The effective state exposes a `QueuePauseReason` flags value (`Manual`, `Schedule`, or both) and a
human-readable `PauseReasonText` ("Paused by you" / "Outside scheduled hours" / both), so the UI shows
*why* the queue isn't running rather than an opaque "Paused".

### Settings

`scheduleEnabled` (default false) and `start`/`stop` (`HH:mm`) live in `settings.json` via the existing
source-gen config context (AOT-safe, no reflection — no new config path). Validated like the other knobs:
an unparseable/missing time → scheduling is treated as **disabled**, never a crash. Surfaced in the
Phase-8 queue-settings panel (a checkbox + start/stop inputs) under its existing explicit-Save semantics;
Save mutates the shared `ScheduleOptions` singleton the view-model re-reads each tick.

## Consequences

- A small, opt-in schedule that composes with — never replaces — the manual pause; reuses the existing
  global pause/play path entirely (no new scheduler mechanism).
- DST/missing-time safe by construction (predicate, not events); overnight windows supported.
- The user can always tell which gate holds the queue.
- Pure BCL, reflection-free, AOT-clean; engine/durability/data-plane/SH-1 untouched; control plane still
  lock-based with no mailbox.