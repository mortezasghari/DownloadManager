# ADR-0002: Solution structure, Central Package Management, and the AOT gate

- Status: Accepted
- Date: 2026-06-16

## Context

Spec §0/§1/§2/§14 demand: Native AOT on every RID as a per-phase CI gate,
minimal pinned dependencies, no reflection-based runtime behaviour, and a
four-project layout where the UI never touches engine internals.

## Decision

### Project layout (spec §2)

```
src/DownloadManager.Core         IsAotCompatible=true   (engine/domain; no Avalonia)
src/DownloadManager.Persistence  IsAotCompatible=true   (durable I/O; refs Core)
src/DownloadManager.UI           PublishAot=true        (Avalonia 12 executable)
src/DownloadManager.Tests        xUnit v3               (refs Core + Persistence)
```

- Libraries set **`IsAotCompatible=true`**, which switches on the AOT, trim and
  single-file analyzers and marks the assembly trimmable. This makes AOT
  hazards compile-time errors in the libraries, not link-time surprises in the
  exe.
- The executable sets **`PublishAot=true`**. It is the only project that emits a
  native image; the libraries are validated through it.
- The test project is **not** AOT-published (it hosts xunit.v3 and the
  Microsoft test SDK); it validates Core/Persistence behaviour on the CLR.

### Central Package Management (spec §1)

`Directory.Packages.props` pins every version with
`ManagePackageVersionsCentrally=true` and
`CentralPackageTransitivePinningEnabled=true`. No project declares a package
`Version`; nothing floats.

### Shared build settings

`Directory.Build.props` sets `net10.0`, nullable + implicit usings,
`InvariantGlobalization=true` (smaller, more deterministic AOT image),
`TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true` so trim/AOT
analyzer warnings fail the build.

### DI is explicit and reflection-free (spec §1)

`Microsoft.Extensions.DependencyInjection` is used with **hand-written
registrations only** — no assembly scanning, no auto-registration. The Avalonia
`App` receives its `IServiceProvider` through a constructor via the
`AppBuilder.Configure(() => new App(services))` factory overload, so there is no
static mutable service-locator global.

## The AOT gate

A phase is "done" only when, on **every** target RID
(`linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`):

1. `dotnet build` is clean (warnings = errors),
2. `dotnet test` passes,
3. `dotnet publish -r <rid> /p:PublishAot=true` succeeds and the image
   smoke-runs.

Enforced in CI (`.github/workflows/ci.yml`). Cross-RID AOT cross-compilation
from a single Linux host covers build+publish; launch smoke-tests run on
native runners per RID.

## Consequences

- Native AOT is validated continuously, never deferred (the spec's stated #1
  risk). Phase 0 already proves `linux-x64` produces a 20.5 MB native ELF with
  no managed DLLs.
- Adding a dependency requires a new `PackageVersion` line here plus the §1
  justification paragraph — friction by design.
- Local Linux Native AOT needs a C toolchain (the .NET 10 ILC links via the
  system compiler/`objwriter`). On this dev box gcc 16 + libc/libstdc++/zlib
  were sufficient; clang was **not** required. CI installs the documented
  prerequisites explicitly so the gate is reproducible.