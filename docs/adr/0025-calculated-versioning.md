# ADR-0025: Calculated versioning, auto-release, and a notify-only update check

- Status: Accepted
- Date: 2026-06-22

## Context

The app shipped as `1.0.0.0` with no release automation. We want a version that is **always calculated**
(never hand-typed), an automatic release on every master merge, and an in-app way to see the running
version and learn when a newer one exists — without building the security-critical auto-updater yet.

## Decision

### Calculated, label-driven version (`major.minor.build`)

The version is computed from history + a bump signal; no number is typed anywhere.

- **Bump intent = a PR label, only.** On merge to master: `major` → next major (reset minor+build);
  `minor` → next minor (reset build); **no/unknown label → build increments**. Commit-message keywords are
  deliberately **not** a signal — commit messages are written by many hands and trip too easily; a label is
  one deliberate, visible decision on the PR.
- **`build` is the auto component.** It is the running counter advanced from the latest release tag — an
  equivalent monotonic source to commit-height, and it resets whenever a `major`/`minor` label bumps.
- **The bump is a pure function** (`VersionBump.Next(latestTag, label)`), unit-tested to lock the rules,
  and invoked by CI through the app's `--next-version` mode (pure arithmetic on its arguments — the running
  binary's own version is irrelevant, so there is no chicken-and-egg with injection).
- **Build-time only, AOT-safe.** CI passes the calculated value as MSBuild `-p:Version=…`, which injects
  `AssemblyVersion` / `FileVersion` / `InformationalVersion`. No versioning tool ships in the runtime; the
  injected string is just data in the assembly. `IncludeSourceRevisionInInformationalVersion=false` keeps
  it a clean `major.minor.build`.

### Auto-build + auto-release on every master merge

Every push to master runs the existing five-RID matrix with the injected version; once all are green, a
`release` job tags the merge commit `vX.Y.Z` and publishes a GitHub Release with the per-RID **binary
bundles** attached (the artifacts the matrix already built — **no installers**; the installer phase is
parked). The new tag is what the next merge's calculation reads as "latest". The smoke step asserts the
published binary reports the injected version (a `VERSION OK x.y.z` line), not the `1.0.0` default — proving
injection flowed all the way into the Native-AOT image on every RID.

### App side — display version + NOTIFY-ONLY update check

- The app reads its **own** version from the assembly (`AppVersion`, BCL/AOT-safe) and shows a
  `Version x.y.z` line in the settings panel. Nothing is hardcoded, so the display cannot drift from the
  published binary.
- A manual **Check for updates** button (not a startup network call — unobtrusive, no blocking) queries the
  GitHub Releases API for the latest release, compares the tag to the running version, and if newer shows
  "you're on X — latest is Y" with a **View release** link. The check runs off the UI thread; any network/
  API error is a **silent no-op**, never a crash.
- **NOTIFY ONLY.** It fetches release *metadata* and compares two numbers — it never downloads, verifies,
  stages, or installs an artifact, so it has no RCE surface. The "view release" link is restricted to
  http/https. Actual update installation (download + signature verification + staged swap) is the separate,
  parked, security-critical updater phase.

## Consequences

- Versions are reproducible and intentional; releasing is merge-and-forget; the binary self-reports its
  real version on every RID.
- Reflection-free, AOT-clean (the GitHub metadata uses a source-gen JSON context); no new runtime
  dependency. Engine/durability/data-plane/SH-1/event-log/scheduler untouched; no settings-schema change.
- Every master merge produces a release by design; release-page curation is handled by branch discipline,
  out of scope here.