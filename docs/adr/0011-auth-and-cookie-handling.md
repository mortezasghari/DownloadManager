# ADR-0011: Per-download auth & cookies — scope, redirect safety, no plaintext persistence

- Status: Accepted
- Date: 2026-06-18

## Context

Phase 4 adds authenticated downloads: a request may carry `Authorization`
header(s) and `Cookie`s. Credentials are sensitive, and the engine already
follows redirects **manually** (the shared handler has `AllowAutoRedirect=false`),
which means credential forwarding is our responsibility — exactly the place where
credential-leak bugs live.

## Decision

### Credentials are per-download, carried on the request

`DownloadRequest.Credentials` (`DownloadCredentials`: `AuthorizationHeaders` +
`Cookies`) is attached at enqueue time, not configured globally. Cookies are
joined into a single RFC 6265 `Cookie` header; each Authorization value is sent
verbatim.

### Cross-host redirect stripping (hard default)

Credentials are **bound to the origin** (scheme + host + port) of the request URL.
A single `RequestAuthorizer` attaches them to an outgoing request *iff* the
request's target URI is same-origin with that binding. Because every request —
the probe, each manual redirect hop, the resume revalidation, and every segment /
single-stream GET — flows through the authorizer, a redirect to a different
scheme/host/port automatically travels with **no** `Authorization` and **no**
`Cookie`. Same-origin redirects retain them. This prevents leaking credentials to
a redirect target.

### No plaintext secret persistence

`Authorization` values, tokens, and cookies are **never** written to `*.dlmeta` or
any on-disk state. They live only on the in-memory `DownloadRequest`
(session memory). After a restart they must be re-supplied. OS keystore
integration (DPAPI / Keychain / libsecret) is an explicit **non-goal**: it would
drag native interop into an AOT-absolute build for marginal benefit.

### "Needs credentials" is a reason, not a new state

A resume (or probe) that returns **401/403** is classified by `HttpErrorClassifier`
as a distinct `NeedsCredentialsException` — neither transient (don't retry-loop)
nor permanent (fresh credentials may fix it). The engine surfaces it as
`DownloadOutcome.NeedsCredentials`. On a resume, the revalidation path raises it
**before** any state is discarded, so downloaded progress is preserved. The
scheduler maps it onto the existing `Failed` state with a `NeedsCredentials`
reason flag on the handle — **no new state-machine state** (the control plane is
unchanged this phase). The user can supply fresh credentials and retry, resuming
from the retained partial download.

## Consequences

- Credentials reach the origin and survive same-origin redirects, but can never
  leak across a host/scheme/port boundary.
- A torn or stolen `*.dlmeta` contains no secrets (asserted on the on-disk JSON).
- Stale credentials on resume neither discard progress nor spin in a retry loop;
  they terminate as Failed-with-`NeedsCredentials`, ready for re-auth.
- Secrets do not survive process restart by design; the UI must re-collect them.