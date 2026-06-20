# Design — Update Mechanism + Single-Instance Port

Status: **Designed. Security-critical. Build after the queue rebuild + ship-readiness.** This is the phase that puts code on users' machines at scale — it inherits the "don't be a weak link" bar.

## Update model (browser-style, not IDM-style)

Notify, don't auto-run. The Chrome/Firefox model, not IDM's download-and-install-for-you.

1. **No app installed** → user downloads + runs the normal one-click (has-UI) installer from GitHub Releases.
2. **App installed** → app checks a version source (GitHub Releases API is fine), **notifies** "update available." No auto-download, no auto-run.
3. **User consents** → download the update artifact **+ its signature** → **hash check** (integrity) → **internal-signature check** against a baked-in public key (authenticity) → only if BOTH pass, stage the verified new version completely.
4. **Ask to restart.** On consent → launch the zero-click updater → app exits → updater **waits for the lock/files to fully release** → atomically swaps the staged version into place → relaunches the new app → updater exits.
5. **Any verification or swap failure leaves the current working app intact** and reports the failure — never a half-updated state.

## The one non-negotiable: signature verification

The update path is, by definition, a remote-code-execution channel built into the app. Consent answers "does the user want to update"; it does NOT answer "is this actually my real update or a tampered one" — the user can't tell. **The app must verify a signature before running an update.**

- This is the app's **own internal signature** (app signs the artifact with a private key; app verifies with a baked-in public key) — NOT OS code-signing/notarization. You control both ends; you don't need Apple to vouch for an update you produced.
- Hash (integrity) + internal signature (authenticity) together = sufficient. `System.Security.Cryptography`, AOT-safe, pure-BCL.
- The private signing key is the one genuinely non-negotiable secret in the project.
- GitHub-Releases-over-HTTPS gives transport integrity (necessary) but NOT protection against a compromised release or push access (insufficient) — the signature covers that. HTTPS alone is not enough.
- OS code-signing/notarization is a *separate, parked* distribution-polish item (avoids first-run "unknown developer" OS warnings). Not required for update safety.

## Staged atomic swap (the fiddly core)
- **Windows can't overwrite a running `.exe`** (file locked). So the swap is a staged handoff, not a file copy: download fully → verify → stage alongside → app exits → updater waits for lock + files to release → atomic swap → relaunch.
- Replace the **whole app folder's** relevant contents, not just the `.exe` — the Avalonia native sidecars (`libSkiaSharp`, `libHarfBuzzSharp`, `libAvaloniaNative.dylib`, `av_libGLESv2.dll`) travel with the binary. Stage-verify-then-atomic-swap so an interrupted update can't brick the install (same durability discipline as the download engine).
- Linux/macOS are more forgiving (replace + re-exec); build to the Windows constraint.

## Single-instance via loopback port

One app instance per machine. Mechanism: **bind a TCP port on `127.0.0.1` only.**

Why a port over file-lock / named-mutex: genuinely cross-platform-uniform (`bind`/`EADDRINUSE` behaves the same everywhere — no "named mutex isn't system-wide on Linux" trap), and **crash-safe by construction** (OS reclaims the port on process death, clean or crash — no stale-lock-bricks-startup failure).

Hard requirements:
- **Loopback-only** (`127.0.0.1`). Binding `0.0.0.0` would expose a network service to the LAN — an attack surface and a Windows Firewall prompt. Loopback avoids both. (Verify on real Windows that loopback bind triggers no firewall prompt.)
- **Handshake on bind-failure** to distinguish "my other instance" from "an unrelated process on my port": on failure, connect to the port and exchange an app-specific token. Known response → genuine other instance. Unknown/refused → unrelated collision → fall back (next port), never silently refuse to start.
- The handshake **is** the future IPC channel — the same control endpoint the browser extension later extends, and the same channel for second-instance handoff (focus window / pass URL). Architect it as a minimal local control endpoint from the start.

Single-instance + updater-wait reuse the same primitive: the updater **polls trying to bind the port**; success = app is provably gone and files released = safe to swap (with a brief file-in-use retry on Windows, since lock-release and file-release can lag).

## v1 scope vs deferred
- v1: second instance **exits cleanly**. Handoff (focus/URL to running instance) is the natural next step once the port endpoint exists — deferred.
- The port's only jobs in this phase: "am I the only instance" + "updater, is the app gone." The URL-ingestion command is the **secured extension endpoint** (separate doc/phase) — do NOT expose "add URL" to the world here.
- **Telemetry** (for measuring the bandwidth controller) is a separate later decision with its own privacy/consent design — not part of this phase.

## Build discipline
Security-critical → design-first, gate on five RIDs, and the failure-safety property (interrupted/failed update never breaks the working install) is a regression test. Close out: merge → re-verify on master → tag → delete branch.