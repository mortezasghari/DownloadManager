# ADR-0018: Queue-first reframe + inline queue-settings panel (explicit Save, honest per-knob apply)

- Status: Accepted
- Date: 2026-06-19

## Context

Phase 8 reframes the existing per-download list as the home "queue" surface and adds an inline panel for
the five *queue* knobs. It is a UI/VM restructure on top of what already exists — the scheduler, the
handle dictionary, and the Phase-7 source-gen `settings.json` store. No engine, persistence, durability,
or `_gate` control-plane change.

## Decision

### Queue-first, running vs waiting split

The home list is the queue, partitioned into Running (running/retrying), Waiting (queued-not-started),
and Finished (paused/terminal) sections so a running download is visually separable from a waiting one
at a glance. The buckets are derived from the per-handle rows (which poll each `IDownloadHandle`), never
by draining the channel. A single master `Downloads` collection stays authoritative; rows are re-bucketed
only when their section actually changes. No tab shell yet — history earns sibling tabs in Phase 9;
building the shell now would build it half-empty.

### Inline panel, not a separate window

The panel is an expand-to-edit ribbon at the top of the window, not a modal/second window — it is a
quick, in-context tweak of the running queue, so it stays beside the thing it affects.

### Explicit Save, not instant-apply / debounce

Edits are local to the panel until **Save**; Cancel/Close discards. Instant-apply (or a debounce) was
rejected: these five knobs are a **coupled set**, and applying mid-edit would (a) push the engine through
transient bad states (e.g. a half-typed `maxDelay < baseDelay`), and (b) clamp half-typed values under
the user's cursor. Save validates the **whole set once** through the existing Phase-7 clamp/validate
path and writes the **clamped** result, so the UI can never persist a config the loader would reject.
Routing and the file-only advanced knobs in the file pass through untouched.

### Per-knob apply semantics — honest, because the read cadence already differs

Apply is not a uniform "applied!". Each knob is applied where its consumer already reads it, so the
timing is truthful and required almost no new mechanism — the values are the **shared singleton option
instances** the engine/scheduler already hold; Save mutates them (and resizes the gate):

- **Max concurrent → live.** Resizes the concurrency gate now via `IDownloadScheduler.SetMaxConcurrency`
  (the gate's supported control surface; run model/state-machine/durability/channel unchanged). Raising
  admits a waiting download immediately; lowering retires a worker only after it finishes its current
  download — a running download is never killed.
- **Segments per download / small-file threshold → newly started downloads only.** Segments is read by
  the view-model when building each request; the threshold is read by the engine at download start. An
  in-flight download keeps its parameters until it restarts. Surfaced as "Applies to new downloads."
- **Retry attempts / backoff / per-attempt timeout → the next attempt, including in-flight.** `RetryPolicy`
  reads its options per call and the engine reads the timeout per attempt, so mutating the shared instance
  takes effect on the next attempt. Surfaced as "Applies to the next attempt."

To make this live-update tear-free, only the live-tunable option properties became `set` (the records
stay records); they are single-word values (int / long / TimeSpan-over-long) written rarely from the UI
thread and read on worker threads that cross a memory barrier between downloads, on the 64-bit RIDs we
ship. No engine, `RetryPolicy`, or scheduler-run-model code changed — they read the same objects at the
same cadence as before.

### Deliberate exclusions

- **Routing / download folders** get their own settings page in a later phase — not crammed in here.
- **Copy-buffer size / checkpoint cadence** stay file-only. A bad checkpoint value measurably slows
  downloads (~3×); these are advanced footguns, not queue settings, and remain editable in `settings.json`
  for advanced users but are not surfaced in this panel.

## Consequences

- The app reads as "a queue I manage," with running/waiting separation and an in-context settings tweak.
- The UI cannot persist an illegal config (same clamp/validate as load), and apply-timing is presented
  honestly per knob rather than implying a running download changed underfoot.
- The VM layer stays Avalonia-free and headless-testable; compiled bindings publish clean under AOT.
- The one scheduler addition (`SetMaxConcurrency`) is the gate's control surface only; the ADR-0010
  single-owner mailbox refactor stays deferred.