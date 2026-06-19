# ADR-0015: Import dialog + queue-delete via state-dispatch (Cancel-for-running, tombstone-for-queued)

- Status: Accepted
- Date: 2026-06-19

## Context

Phase 6 adds two user capabilities — add-to-queue via an import-review dialog, and delete-from-queue —
deliberately simplified: imported URLs go straight into the normal queue and start like any other
download. **No Held state, no manual-start, no Start button, no release semantics.** This phase adds no
new download state and must not touch the scheduler run model or the `_gate`/control-plane area.

## Decision

### Delete dispatches on state — reusing the existing Cancel path, not new teardown

A delete must never tear down a running download outside the state machine. It dispatches on the
download's current state, both paths already implemented in Phase 3's `Cancel`:

- **Running** → the existing Cancel path: `Cancel()` sets the cancel intent and signals the run CTS; the
  worker unwinds through the state machine with durable teardown. Verbatim reuse — no new logic.
- **Queued but not started** → **tombstone**: `Cancel()` flips the handle straight to the terminal
  `Canceled` state. Its id is **left in the channel**. When a worker later dequeues that id,
  `TryBeginRun` sees a non-`Queued` status and **skips** it (the existing duplicate/stale-entry guard) —
  no run starts, no exception.

The scheduler's `CancelAsync` already does exactly this dispatch (`Cancel()` returns `true` for the
queued/terminal-now case → discard immediately; `false` for running → worker tears down). So Phase 6
required **no engine or scheduler change** — only UI and tests.

### The channel is kept as-is — tombstone instead of channel surgery

The queue stays the existing `Channel<DownloadId>`. We do **not** replace it, add a priority/ordered
structure, add reordering, or remove ids from it. A queued delete tombstones the handle and lets the
worker skip the stale id on dequeue. Rationale: `Channel<T>` has no random-remove, and draining/rebuilding
it to extract one id would be racy queue surgery against live producers/consumers for no user-visible
benefit (this phase has no reorder). Tombstone-and-skip is O(1), lock-free on the data plane, and reuses
a guard the worker already had.

### Import-review dialog

Raw text — pasted, or auto-pasted from the clipboard — is fed to the **unchanged** `UrlListImporter`.
Parsed http/https URLs become a ticked checklist; the importer's skip-with-reason summary is surfaced;
"Add to Queue" enqueues the **ticked** URLs through the normal add-path. Clipboard access sits behind an
injected `IClipboardTextSource` seam (Avalonia's `TopLevel.Clipboard` impl — note Avalonia 12 replaced
`IClipboard.GetTextAsync()` with `ClipboardExtensions.TryGetTextAsync()` — lives in the view layer), so
the view-model references no Avalonia clipboard types and is headless-testable.

### Queue view

The queue list is built from the view-model's per-handle rows (mirroring the scheduler's handle
dictionary), **not** by draining the channel. Each row has a Delete action that performs the
state-dispatch above. No reorder.

## Explicitly out of scope / unchanged

- **No new download state** (tombstone reuses `Canceled`).
- **The run-state machine, `_gate`, and the control plane are untouched.** Nothing in this phase can
  trigger the ADR-0010 single-owner mailbox refactor — **it stays deferred.**
- `UrlListImporter`, the Cancel path, and persistence/durability are unchanged.

## Consequences

- Delete is correct by construction: it can only act through the state machine (Cancel or tombstone),
  never bespoke teardown.
- A deleted queued download never starts; deleting one download does not affect others (tested).
- The data plane keeps its simple channel; no ordering/priority debt introduced.