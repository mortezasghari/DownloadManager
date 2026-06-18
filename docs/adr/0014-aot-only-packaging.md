# ADR-0014: AOT-only packaging — five RIDs, separated symbols, unavoidable native sidecars

- Status: Accepted
- Date: 2026-06-19

## Context

Phase 5.6 is packaging/CI only. The shipped form is a self-contained Native-AOT binary per RID. Two
questions: how to keep the published bundle clean (one runnable binary + only what must ship), and which
RIDs the CI gate covers.

## Decision

### Native AOT is the only publish model

No `PublishSingleFile` / `PublishTrimmed` (JIT) flavors are added. There is exactly one shipped form per
RID: the Native-AOT executable. `IsAotCompatible` / trim settings are unchanged, and warnings-as-errors
(incl. trim/IL/binding analyzers) stays on.

### Five RIDs, all full matrix entries

`linux-x64`, `linux-arm64`, `win-x64`, `osx-x64`, `osx-arm64`. Each runs build + all tests + AOT publish
(0 trim/IL warnings) + `--smoke` on a real runner of that OS/arch. `osx-x64` (macos-13, Intel) is added
because the `fcntl(F_PREALLOCATE)` reservation path had only ever executed on arm64 macOS; x64-vs-arm64
is exactly where the variadic ABI / struct layout could differ, so it must be **proven on a real Intel
mac**, not inferred. The `Full_preallocation_actually_runs_the_native_path_on_this_os` test is **not**
OS-gated (its arm64 fallback branch is arm64-only), so on osx-x64 it asserts the native path executed;
`--smoke` independently prints the proof line.

### Symbols separated, binary stripped

`StripSymbols=true` strips the shipped binary and emits native debug info to a separate file
(`*.dbg` / `*.pdb` / `*.dSYM`). CI moves that into a distinct `…-symbols` artifact — kept for
symbolicating crashes, **not** shipped beside the binary. Managed reference PDBs are kept out of the
bundle via `AllowedReferenceRelatedFileExtensions` (sentinel), with a CI `rm` as belt-and-suspenders.

### Unavoidable native sidecars (left correct, not forced)

Avalonia's rendering stack ships **prebuilt unmanaged** libraries — `libSkiaSharp`, `libHarfBuzzSharp`
(all RIDs), plus `libAvaloniaNative` (macOS) and `av_libGLESv2` (Windows). These are C/C++ libraries with
no static-link path under the published packages; collapsing them into the AOT image would mean building
Skia from source — out of scope, and not an AOT-cleanliness problem. They remain as required sidecars
beside the binary. Per spec, this is reported, not forced with a startup-harming workaround.

## Consequences

- Shipped bundle per RID = stripped AOT binary + the unmanaged Avalonia native libs; nothing else.
- Crash symbolication is possible from the separate symbols artifact.
- The F_PREALLOCATE x64 path is gated by a real Intel-mac CI run, closing the last inferred-only gap.