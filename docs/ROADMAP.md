# Download Manager — Roadmap

A queue-first, cross-platform download manager. .NET 10, Avalonia 12, Native AOT (absolute constraint), pure-BCL / minimal-dependency, five RIDs (linux-x64, linux-arm64, win-x64, osx-x64, osx-arm64).

This file is the build order and status. Design detail for unbuilt work lives in `docs/design/`. Decisions live in `docs/adr/`.

---

## Status — done and green

Master is green and tagged. Latest restore point: **`security-hardening-1-green`** (commit `0028849`, CI run 27874228499, 221 tests, 5/5 RIDs).

| Phase | Scope | Status |
|---|---|---|
| 0 | AOT spike — solution, CPM, 4 projects, multi-RID AOT gate | Done |
| 1 | Durability-first single-stream — engine, durable persistence, crash-safe recovery, If-Range resume | Done |
| 2 | Segmentation — N parallel segments, range probing, native preallocation (P/Invoke) | Done |
| 3 | Scheduler & control — Channels queue, concurrency, pause/resume/cancel, retry+backoff | Done |
| 4 | Ingestion & network breadth — auth/cookies/proxy, encoding, checksum, URL import | Done |
| 5 | UI + tuning — MVVM, speed/ETA smoothing, perf measurement, logging | Done |
| 5.6 | Packaging — osx-x64 added (Rosetta-validated), single-binary + separated symbols, 5 RIDs | Done |
| 6 | Import dialog + queue delete (state-dispatched: Cancel-for-running, tombstone-for-queued) | Done |
| 7 | Externalized config (OS config dir, source-gen JSON, AOT-safe) + IDM-style extension routing | Done |
| 8 | Queue-first UI + inline queue-settings panel (explicit Save, per-knob apply semantics) | Done |
| 9 | Read-only download history + open/reveal actions (per-OS shell-out) | Done |
| SH-1 | Security hardening — router containment, launcher ArgumentList, URL redaction, prealloc clamp, bidi-strip | Done |

Security audit complete (report-only, then SH-1 fixed the actionable findings). No Critical/High; four Mediums fixed and locked with regression tests; Lows (F5-network, F7 symlink, F9 config-path, F3-remainder) accepted with rationale in ADR-0020.

---

## Build order — designed, not yet built

Dependency-ordered. Each phase: design-first, gate on all five RIDs, regression tests for any crash/concurrency/security property (red-before / green-after), close-out (merge → re-verify on master → tag → delete branch).

### NEXT — Queue rebuild (event-log source of truth + projections)
**Design:** `docs/design/queue-rebuild.md`.
Make a durable append-only lifecycle-event log the single source of truth. The in-memory FIFO channel becomes a *projection* of non-terminal state, rebuilt from the log on startup. `history.json` becomes a *read model / cache* (projection of terminal state), not authoritative. Mutations (pause-to-end, stop, re-add) compose from remove/park/re-enqueue on the existing **lock-based** control plane. **Mailbox/actor execution model is deferred** (see ADR-0010 update) until Akka.NET 1.6 (AOT) makes it cheap and robust. Crash-mid-mutation queue-membership inconsistency is **accepted** as a documented, recoverable trade (bytes are never lost — only queue position/state may be slightly wrong after a crash in a millisecond window; user re-clicks).
Foundation for everything queue-related below. **DO NOT TOUCH** engine algorithm, segmentation, `.dlmeta`/`.dllog` durability ordering, routing, config, history-store internals, or the SH-1 security fixes — this is a control-plane projection change only.

### Then — Queue features (compose onto the rebuilt base)
The four-action model on top of the event log: **queue pause/play** (global), **pause a download** (= reposition to end of queue, stays active), **stop** (= terminal, to history), **re-add from history** (= append a queued event). Small once the rebuild exists.

### Then — Time-based scheduling
A predicate model (not exact-time events): "is now within [start,stop] AND no manual override?" — handles overnight windows and DST by construction. Drives the global queue pause/play the rebuild provides. Set in queue settings. Thin layer once the queue rebuild + global-pause exist.

### Then — "Always gentle" resource discipline (bandwidth Tier 1)
Low-priority reassembly/checksum (below-normal thread/IO priority so the OS yields automatically), paced I/O, user-set bandwidth cap, manual throttle toggle. The robust, no-measurement-needed part that stops the app from freezing the machine during checksum/reassembly. Independent of the adaptive bandwidth controller — ship this well before that. Idle-aware speed (faster when user away, via last-input-time) folds in here or later as a refinement.

### Then — Update mechanism + single-instance port (one phase, security-critical)
**Design:** `docs/design/update-and-singleton.md`.
Browser-model update: notify (no auto-run), consent, **hash + internal-signature verify**, staged atomic swap, lock-coordinated restart. Single-instance via a **loopback-only** port, architected as a minimal local control endpoint the browser extension later extends. Unblocks shipping to users. The signature verification is the one non-negotiable (it's an RCE channel made safe).

### Then — Browser extension + secured localhost endpoint
**Design:** `docs/design/extension-endpoint.md`.
The extension (per-browser build, store submissions) + the secured localhost endpoint it talks to. Endpoint security: **loopback-only + Origin check + locally-paired/backend-brokered token (+ optional asymmetric signing)**. The extension is just another queue producer (URL → enqueue event). Concentrated security rigor here — it's the door to the outside.

### Last — Adaptive bandwidth controller (prototype-and-measure)
**Design:** `docs/design/bandwidth-controller.md`.
Token bucket + relative-baseline back-off + AIMD-style recovery + idle fast-path + RFC-1918 IP partitioning. **This is a prototype-and-measure feature, not a build-and-ship feature** — the constants are unknowns to tune against real contention, not values to ship. Needs distribution (update mechanism) and telemetry (its own privacy/consent decision) to gather measurements. Build instrumented; "it works" must be a measured claim.

---

## Parked / future (deliberate, not forgotten)
- Routing/folders settings page (own settings page; config is already file-editable now).
- OS code-signing / Apple notarization for installers (distribution polish; first-run "unknown developer" warning — not a security gap, since updates use internal signatures).
- Second-instance handoff (focus running app / pass URL) — v1 is "second instance exits cleanly"; handoff is the natural next step once the singleton port exists.
- Telemetry + consent channel (required to measure the bandwidth controller; its own privacy design).
- Log/history compaction (deferred, same as `.dllog`; append-only grows fine for a long time).
- Mailbox/actor control plane — revisit when Akka.NET 1.6 (AOT) ships (verify its AOT support is complete, not partial).