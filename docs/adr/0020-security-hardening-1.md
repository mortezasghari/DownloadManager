# ADR-0020: Security hardening #1 — path containment, launcher argv, URL redaction, preallocation clamp, bidi-strip

- Status: Accepted
- Date: 2026-06-20

## Context

A report-only adversarial audit (threat model: server, URL, headers, filename, and bytes are all
hostile) found no Critical/High issues but several real Mediums and Lows. This ADR records the targeted,
surgical fixes applied for the ones that matter, and the explicitly accepted (un-fixed) findings. Each fix
ships with a permanent regression test derived from the audit's proven exploit. No engine-algorithm,
durability-ordering, or `_gate`/control-plane change.

## Decisions — fixes applied

### F1 + F8 — Router path containment, by design (not incidental)

`FileRouter` previously did `Path.Combine(folder, fileName)` with no sanitization or containment; it was
safe only because upstream callers happened to pass bare leaf names (the audit proved this, but also that
it was incidental). The protection is now **at the router**:

- A pure `SafeFileName.Sanitize` reduces any name to a safe leaf: collapse any directory component (both
  `/` and `\`, host-independently), drop separators / drive-or-ADS `:` / Windows-illegal chars / control
  and Unicode-format (bidi) chars, trim trailing dots and spaces, neutralize reserved device names
  (`CON`, `PRN`, `NUL`, `AUX`, `COM1`–`COM9`, `LPT1`–`LPT9`, with or without extension) by prefixing `_`,
  and fall back to `download` for an empty/all-dots result (folds in F8).
- The router then **canonicalizes** (`Path.GetFullPath`) and **verifies containment**
  (`StartsWith(folder + separator, Ordinal)`), rejecting with `PathContainmentException` on escape.
- The **`explicitPath` branch** (previously returned verbatim) gets the same treatment: canonicalized and
  required to resolve inside a configured destination folder, else rejected. It is only ever called with
  `null` today, but the chokepoint is now safe regardless of caller — notably if future
  `Content-Disposition` support feeds attacker-controlled names straight in.

The headless no-router fallback in the view-model also sanitizes, so the guarantee is not router-only.

### F2 — Launcher argument injection → `ArgumentList`

`LaunchCommands` built a `ProcessStartInfo.Arguments` **string** with a hand-rolled `Quote()` that did not
escape embedded quotes, so a filename containing `"` injected extra argv tokens/flags (audit proved it; it
is argument injection, **not** command injection — `UseShellExecute=false`, no shell). Fixed by emitting an
explicit **argument list**: `LaunchCommand` now carries `IReadOnlyList<string>` and `ProcessFileLauncher`
adds each token to `ProcessStartInfo.ArgumentList`, where .NET handles escaping. Windows reveal uses a
single `/select,<path>` token. The path is always absolute, which (as before) also keeps a leading-dash
filename from being parsed as an option.

### F4 — URL userinfo redaction at persist/log sites

A URL with userinfo (`https://user:pass@host/…`) was persisted to `.dlmeta` (`OriginalUrl`/`FinalUrl`) and
written to several logs in cleartext. A single `UrlRedaction.Redact` (using
`GetComponents(UriComponents.HttpRequestUrl, …)`) strips userinfo, applied at every persist and `{Url}`
log site (`DownloadEngine` metadata + start log, `RangeProber` probe/revalidate logs,
`MainWindowViewModel` enqueue log). The **on-the-wire** request still uses the full in-memory `Uri`
(userinfo isn't transmitted as auth by .NET anyway; credentials travel via `DownloadCredentials`); only
the persisted/logged representation is redacted. The source-gen JSON schema is unchanged — only the value.

### F6 — Preallocation size clamp

`TargetFile` reserved the full server-advertised size before any byte arrived, so a hostile "size that
fits" could reserve most of the disk then stall. A new `engine.maxFullPreallocationBytes` config knob
(default 16 GiB, clamped like the other knobs) caps **Full** reservation; the factory also refuses to Full
above 90% of the target volume's free space. Above either bound it degrades to **Sparse** (no real disk
reserved) via the existing Full→Sparse→None path. The download still proceeds; only the upfront
reservation is bounded.

### F5 — Bidi/control-char strip on displayed names

`SafeFileName.StripBidiControls` removes Unicode format (Cf) and control characters from the **displayed**
name in the queue and history rows, so a right-to-left override can't render `exe.jpg` while the bytes are
`gpj.exe`. (The on-disk path is already cleaned by the router sanitizer.)

## Decisions — explicitly accepted (not fixed), with rationale

- **F5-network (http accepted / SSRF-via-redirect):** blind side-effect GETs only — the attacker controls
  the origin and never sees the response (it goes to the user's disk); low value for a user-driven tool.
  `file://`/`ftp://` redirects are already blocked by `SocketsHttpHandler`. Optional backlog: flag plain
  http and deny loopback/link-local redirect targets.
- **F7 (symlink TOCTOU on target open):** requires a **local** attacker with write access to the user's
  Downloads dir — already inside the threat model; the app is not the weak link there. `O_NOFOLLOW`
  hardening is optional backlog.
- **F9 (routing-folder path in `settings.json`):** user-owned local config — a trust boundary the user
  controls.
- **F3-remainder (executable-open warnings):** opening a downloaded file with the OS default handler is
  inherent to the feature and user-initiated; a per-executable confirmation prompt would be noise. The
  bidi-strip (F5) addresses the disguise vector; the rest is accepted.

## Consequences

- The path chokepoint is safe by construction; the launcher cannot be argument-injected; URL secrets no
  longer land in `.dlmeta` or logs; a hostile `Content-Length` can no longer reserve the disk; disguised
  filenames render honestly.
- All five fixes are pure BCL, reflection-free, AOT/trim-safe, no new dependency. Each has a permanent
  regression test built from the audit's proven exploit.
- This is hardening against known threat models, not a guarantee of security; the accepted findings are
  explicit, deliberate risk decisions.