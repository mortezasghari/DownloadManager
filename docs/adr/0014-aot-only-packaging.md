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

`linux-x64`, `linux-arm64`, `win-x64`, `osx-x64`, `osx-arm64`. Four run build + all tests + AOT publish
(0 trim/IL warnings) + `--smoke` on a native runner of that OS/arch.

`osx-x64` is added because the `fcntl(F_PREALLOCATE)` reservation path had only ever executed on arm64
macOS; x64-vs-arm64 is exactly where the variadic ABI / `struct fstore` layout could differ, so it must
be **exercised as a real x86_64 process**, not inferred from arm64.

The intent was a bare-metal Intel runner, but **GitHub-hosted Intel macOS would not allocate** — both
`macos-13` (queued ~1.5 h, no runner) and `macos-13-large` (queued ~7.8 h, no runner) failed to schedule
(Intel hosted capacity is retired/exhausted on this account). The accepted fallback is **Rosetta 2 on the
Apple Silicon image**: a dedicated job installs the x64 .NET SDK and runs the build, the **full test
suite**, the AOT publish, and `--smoke` all via `arch -x86_64`, so every step is a genuine x86_64 process
making x86_64 syscalls to the XNU kernel — which is precisely what the F_PREALLOCATE struct-layout /
marshalling concern needs. The `Full_preallocation_actually_runs_the_native_path_on_this_os` test is
**not** OS-gated (its fallback branch is arm64-only), so running as x64 it asserts the native path
executed; `--smoke` independently prints the proof line.

Caveat (recorded honestly): this is Apple's translation layer, **not bare-metal Intel silicon**. It
validates the x86_64 instruction stream and syscall ABI, not Intel-specific microarchitecture. If a real
Intel mac (self-hosted or future hosted capacity) becomes available, osx-x64 should move back to native
execution.

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
- The F_PREALLOCATE x64 path is gated by a real x86_64 CI run (under Rosetta), closing the
  inferred-only gap — with the documented caveat that it is translated, not bare-metal Intel.