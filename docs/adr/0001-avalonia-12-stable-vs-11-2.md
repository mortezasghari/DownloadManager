# ADR-0001: Avalonia 12 (stable) for the UI layer

- Status: Accepted
- Date: 2026-06-16

## Context

The spec (§1) was written while Avalonia 12 was in preview and mandated pinning
an exact 12-preview version, with **Avalonia 11.2.x stable as a documented
fallback** if a 12-preview blocker appeared. It also required keeping the UI
layer thin so the swap would be cheap.

As of 2026-06-16, NuGet shows Avalonia **12.0.0 through 12.0.4 released as
stable** (`12.0.0-preview1/2`, `rc1/2`, then GA). The premise behind the
fallback — "12 is an unproven preview" — no longer holds.

## Options

1. **Pin a 12.x preview** as literally written. Rejected: the previews are
   superseded by GA; pinning a withdrawn preview is strictly worse.
2. **Pin Avalonia 11.2.x stable** (the fallback). Rejected: 11.2 is the older
   line; 12 is now stable and is the version the spec actually wanted. Choosing
   11.2 now would be choosing the contingency over the goal.
3. **Pin Avalonia 12.0.4 (latest stable).** Chosen.

## Decision

Pin **`Avalonia* = 12.0.4`** in `Directory.Packages.props`, not floating. This
honours the spec's intent (Avalonia 12, compiled bindings on by default, exact
pin) while removing the single biggest Phase 0 risk the spec called out.

The "keep the UI thin / 11.2 fallback" guidance is retained as cheap insurance:
the UI project holds no engine logic (spec §10), so a forced downgrade would
remain a packages-only change.

## Consequences

- The Phase 0 AOT spike no longer gambles on preview stability; `dotnet publish
  -r linux-x64 /p:PublishAot=true` produces a fully native binary today
  (verified: 20.5 MB ELF, no managed DLLs).
- `AvaloniaUseCompiledBindingsByDefault=true` is the framework default in 12 and
  is set explicitly in the UI csproj.
- When 12.0.5+ ships, bumping is a one-line, deliberate change here — never a
  float.