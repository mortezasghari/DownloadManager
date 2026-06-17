# DownloadManager

Cross-platform desktop download manager. .NET 10 · Avalonia 12 · **Native AOT**.

Reliably downloads files from a few KB to 100+ GB on unstable networks,
surviving application crashes and system reboots. The authoritative spec lives
in the project prompt; architecture decisions are recorded in
[`docs/adr/`](docs/adr).

## Status

**Phase 1 — durability-first single-stream: complete and green.** The single
download code path (probe → resume/resolve → stream → durable checkpoint) runs
the 1-segment case end to end with crash-safe recovery and `If-Range` resume.
Source-gen JSON metadata + a CRC-protected binary progress log sit beside each
file; the §6c durability ordering (data fsync → progress append → progress fsync)
is enforced in one place. 37 tests cover offset math, resume (incl. `If-Range`
200-fallback and validator-missing), torn-tail/CRC recovery, and the durability
invariant. The whole stack AOT-publishes to a native binary with zero warnings.
See ADRs [0003](docs/adr/0003-persistence-source-gen-json-and-binary-log.md),
[0004](docs/adr/0004-durability-ordering-and-recovery-invariant.md),
[0005](docs/adr/0005-direct-offset-writes-and-shared-http-handler.md).

**Phase 0 — AOT spike: complete.** Avalonia 12 shell + libraries publish native.
See [ADR-0002](docs/adr/0002-solution-structure-and-aot-gate.md).

> Next: Phase 2 generalizes the (already segment-shaped) engine to N parallel
> segments with native preallocation — recovery and durability are unchanged.

## Layout

| Project | Role | AOT |
|---|---|---|
| `src/DownloadManager.Core` | Engine, scheduler, domain, recovery model. No UI. | `IsAotCompatible` |
| `src/DownloadManager.Persistence` | Metadata store + segment-progress log + durable I/O. | `IsAotCompatible` |
| `src/DownloadManager.UI` | Avalonia 12 Views/ViewModels. No download logic. | `PublishAot` (the executable) |
| `src/DownloadManager.Tests` | xUnit v3 over Core + Persistence. No Avalonia. | n/a |

Packages are pinned in `Directory.Packages.props` (Central Package Management);
nothing floats.

## Build, test, run

```bash
dotnet build DownloadManager.slnx -c Release
dotnet test  DownloadManager.slnx -c Release
dotnet run   --project src/DownloadManager.UI -c Release
```

## Publish a native binary

```bash
dotnet publish src/DownloadManager.UI/DownloadManager.UI.csproj \
  -c Release -r linux-x64 -o artifacts/publish/linux-x64
```

Target RIDs: `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`. The CI gate
(`.github/workflows/ci.yml`) builds, tests, AOT-publishes and smoke-runs on
every RID on each push/PR.

### Local Native AOT prerequisites (Linux)

The .NET 10 AOT compiler links through the system C toolchain. A working build
needs a C compiler plus `zlib` and `libstdc++` development files. On Debian/
Ubuntu: `sudo apt-get install clang zlib1g-dev`. On Arch the base `gcc` + glibc
were sufficient in testing.