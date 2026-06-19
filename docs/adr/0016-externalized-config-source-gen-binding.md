# ADR-0016: Externalized configuration — OS-config-dir settings.json, source-gen binding, clamp-on-invalid

- Status: Accepted
- Date: 2026-06-19

## Context

Engine/scheduler tunables (max concurrent downloads, segments-per-download, small-file threshold, copy
buffer, checkpoint cadence, retry attempts/backoff, per-attempt timeout) lived as in-code defaults
(`EngineOptions`, `SchedulerOptions`, `RetryOptions`). Phase 7 makes them user-editable without a
rebuild. The hard constraint is Native AOT: binding must be reflection-free on all five RIDs.

## Decision

### Location — `{ApplicationData}/DownloadManager/settings.json`

Resolved via `Environment.SpecialFolder.ApplicationData`, the one special folder that is reliable
cross-platform (unlike MyVideos/Downloads): `~/.config/DownloadManager` on Linux (honoring
`$XDG_CONFIG_HOME`), `%APPDATA%\DownloadManager` on Windows, `~/Library/Application Support/DownloadManager`
on macOS. Loaded **once** at startup.

### Binding — System.Text.Json source generator, never the reflection binder

Config is bound through a `JsonSerializerContext` source generator (`AppSettingsJsonContext`), the exact
pattern already used for the `.dlmeta` sidecar (ADR-0003). `Microsoft.Extensions.Configuration`'s
reflection binder is **rejected**: it passes a local JIT run and then fails (or silently misbinds) under
AOT publish. The raw file shape (`AppSettings`) is deliberately plain JSON primitives (ints, seconds,
byte counts) so it is hand-editable; `SettingsStore` then validates and projects it into the existing
strongly-typed option records the engine already consumes. No new dependency.

### Failure handling — never crash on bad input

- **Missing file** → apply defaults **and write** a documented default `settings.json` (+ a
  `settings.README.md` sibling, since JSON has no comments) so the file is discoverable and editable.
- **Malformed JSON** → apply defaults, log a warning, and **leave the user's file untouched** — never
  destroy a broken hand-edit.
- **Out-of-range values** → **clamp to the nearest legal value** and log a warning, reusing the engine's
  own invariants (segment count [1..16], buffer floor, backoff coherence so `maxDelay >= baseDelay`).
  A value that would break an engine invariant can never reach the engine.

### Validation proven to reach the engine, not just to parse

Tests assert behaviour, not parsing alone: a loaded `maxConcurrentDownloads` actually gates the real
scheduler's concurrency, and loaded retry values drive `RetryPolicy`. Clamping and missing/malformed
fallbacks are each covered.

### The AOT risk is validated at runtime on every RID

The `--smoke` self-test now **writes and deserializes a real settings.json via the source-gen context**
before the preallocation check. Launch alone would not exercise binding; a reflection binder would pass
tests under JIT and fail only here. The CI matrix runs this on all five RIDs.

## Consequences

- Tunables are editable without a rebuild; the default file documents every key and its range.
- Binding stays 100% reflection-free and AOT-clean (0 trim/IL warnings).
- Bad edits degrade gracefully (clamp/fallback + warning), never a crash or a corrupted overwrite.
- New knobs are additive: a property on the raw settings + a clamp in `SettingsStore` + a projection
  into the option record. Routing config (ADR-0017) rides in the same file and loader.