# Design — Browser Extension + Secured Localhost Endpoint

Status: **Designed. Build after the update/singleton phase (it extends that port).** Concentrated security rigor — this is the door to the outside.

## What it is
A browser extension that hands download URLs to the running app, the standard way: the extension POSTs to a localhost endpoint the app listens on. The endpoint is the single-instance port (from the update/singleton phase) extended with an "add URL" command.

**Queue lens:** the extension is just another *queue producer*. It doesn't "start a download" — it appends a `Queued` event (via the queue-rebuild's event log), exactly like clipboard import, file import, and manual add. No new download logic; a network door into the existing ingestion pipeline.

## The threat (why this needs real security)
A loopback endpoint that accepts "download this URL" from anything that can POST to it is a juicy target. **Any website the user visits can run `fetch('http://127.0.0.1:port/add', ...)` in the background** — and if the endpoint naively accepts it, a random webpage just queued a download (potentially a malware payload to a routed folder) onto the user's machine. This is a known, exploited class (CSRF-against-localhost, DNS-rebinding). Loopback-only is necessary but NOT sufficient — a webpage's `fetch` originates from the user's own machine, so it IS loopback traffic.

## The auth model (corrected)

**Key principle: a secret must be generated/stored locally or brokered by a backend — NEVER embedded in shipped code.** Anything baked into the shipped extension is readable by anyone who unzips it, so it is not a secret. (A "bake the public key into the extension and require encrypted requests" scheme fails: the baked key ships to everyone, so a malicious page can extract it and encrypt identically.)

Defense stack (loopback + Origin + token, with optional signing):
1. **Loopback-only** binding (`127.0.0.1`). Necessary floor.
2. **Origin check** — the most valuable single check. The browser attaches an `Origin` header on cross-origin `fetch` that page JS *cannot forge*. Require the extension's specific origin; reject anything that looks like it came from a web page. Browser-enforced, free, can't be spoofed by page JS.
3. **Locally-paired / backend-brokered token** — a secret the webpage can't read. Either generated fresh on the user's machine by the app and stored in extension local-storage at pairing (a page can't read another extension's storage), OR brokered by the backend both ends can reach (the backend hands the extension and the app a matching token, vouches for each other). Every request carries it; requests without it are rejected. This is what stops arbitrary websites.
4. **Validate the URL** through the same hardened pipeline as every other input (SH-1 router containment etc. — an extension-supplied URL is exactly as untrusted as a clipboard one).
5. **Optional asymmetric signing (upgrade layer)** — app holds the *private* key (server side, hardest to extract; the side deciding whether to trust), extension gets the *public* key during pairing (NOT pre-baked). Either side signs, the other verifies. Defends against more sophisticated local tampering. This is the ~20% case — the token + Origin check already defeat the realistic (webpage) threat; signing is defense-in-depth, layer it on if the local-tamper threat is judged to matter. Do NOT build this first thinking it's what stops the webpage.
6. **Optional per-add user confirmation** ("Browser wants to add X to your queue. Allow?") at least until trust is established, so even a token leak doesn't mean silent downloads.

**Real stack:** loopback + Origin check + locally-paired/backend-brokered token, with optional signing + optional consent on top. The crypto is the last layer, not the first; the security comes from the secret being generated/stored locally or backend-brokered, never from embedding it in shipped code.

## Backend note
Both app and extension can reach a backend you control, so pairing can be backend-brokered (stronger than purely-local pairing). Caveat: a backend in the trust path becomes part of the attack surface and the privacy story (it may see pairings / URLs) — it graduates from convenience to infrastructure with its own security + consent design. A then-problem, flagged.

## Scope
- The extension itself: per-browser build (manifest, content script), Chrome Web Store / AMO submission — a real separate project, not a clause.
- The endpoint: extends the single-instance port's control protocol with a secured "add URL → append Queued event" command.

## Build discipline
Design-first, security-critical. Gate on five RIDs (app side). The "webpage can't add a download" property is a regression test (simulate a forged-origin / tokenless request → rejected). Close out: merge → re-verify on master → tag.