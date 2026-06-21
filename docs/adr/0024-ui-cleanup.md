# ADR-0024: UI cleanup — adaptive settings layout, built-in icon buttons, queue/history polish

- Status: Accepted
- Date: 2026-06-21

## Context

A bounded UI/VM polish pass: make the settings panel responsive, replace text-label action buttons with
icons, and bring queue/history rows to visual parity with the settings section. No engine, scheduler,
durability, persistence, `_gate`, event-log/projection, or settings-schema change; no new dependency.

## Decision

### Adaptive settings layout

The settings groups (Concurrency, Segmentation, Retry & Timeout, Schedule) are fixed-width blocks
(`SettingsLayout.GroupWidth` = 380) in a `WrapPanel`: when the panel is wide enough for two blocks they
form two columns, otherwise they reflow to one. Pure Avalonia layout (no media queries, no VM/view
coupling). The breakpoint (`2 × GroupWidth + Gap`) is encoded in the pure, headless-testable
`SettingsLayout.ColumnsForWidth`, matching the WrapPanel's intrinsic reflow.

### Built-in icon buttons with hover tooltips — no icon-library dependency

Action buttons (queue: Postpone / Retry / Re-authorize / Stop; history: Open file / Open folder; global
Pause/Play) are `PathIcon`s drawn with **filled vector geometries in the path mini-language** — Material-
style silhouettes defined as `StreamGeometry` resources. **No NuGet icon library.** Each icon button carries
a `ToolTip.Tip` with the original text label, so meaning stays discoverable and accessible. The
state-machine enable/disable (`IsVisible`/`CanExecute`) is unchanged — icons disable exactly as the text
buttons did. The global Pause/Play swaps Play↔Pause geometry by `IsManuallyPaused`.

### Queue/history polish — bounded, not a redesign

Exactly three things, for both lists: (1) generous, consistent row padding/margins for parity with the
settings section; (2) the row shows the **filename, never the URL** — bound to the **existing SH-1
sanitized display name** (`DownloadItemViewModel.Name` / `HistoryItemViewModel.Name`, which apply the
bidi/control-char strip), so a right-to-left-override name can't render `exe.jpg` while the bytes are
`gpj.exe` (audit F3/F5); the raw URL is never parsed for display; (3) typography/spacing consistent with
settings. A structural queue/history redesign (new layout/columns/status indicators) is explicitly
**deferred** to a future pass.

### AOT render verification

`PathIcon`, `ToolTip`, and `WrapPanel` are templated controls. They publish clean under Native AOT +
compiled bindings (0 trim/IL/binding warnings) on all five RIDs, and — via the `--open-settings`
GUI-smoke flag, which now also seeds one display-only queue row — they actually **render** under AOT on the
Linux xvfb launch (settings WrapPanel + TimePicker + the row's PathIcon/ToolTip icon buttons), not just
compile. The demo row is added to the Running bucket only (never to `Downloads`), so the tick never logs it
— no lifecycle-log or recovery pollution.

## Consequences

- Settings reflow 1↔2 columns by width; action buttons are compact icons with discoverable tooltips; queue
  and history rows look as finished as settings and show safe sanitized filenames.
- Pure BCL + Avalonia, reflection-free, AOT/trim-safe on every RID; no new dependency; no settings-schema
  change. Engine/scheduler/durability/data-plane/SH-1 untouched.