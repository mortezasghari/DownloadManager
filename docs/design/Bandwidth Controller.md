# Design — Adaptive Bandwidth Controller (prototype-and-measure)

Status: **Designed and reviewed. NOT ready to build-and-ship — this is a prototype-and-measure feature.** The constants are unknowns to tune against real contention, not values to ship.

## Goal
The download manager is a silent background citizen: it yields bandwidth to whatever the user is actually doing, without trying to *classify* what that is. The principle: **don't classify the competitor — measure contention and get out of the way.**

## The blueprint (from external design session — sound core)

### A. Central token-bucket gatekeeper
All download streams read from sockets only by consuming tokens from a bucket. Throttle/unthrottle = change the bucket refill rate. One mechanism that also serves the manual cap and idle-aware speed. Build the bucket and you've built the chassis for every bandwidth feature.

### B. Black-box telemetry, ~500ms pulse
Ignore hardware specs (the "interface lie" — 10 Gbps NIC, 100 Mbps WAN). Treat the path as a black box.
- **Baseline calibration (C_max):** run unthrottled 3–5s to observe peak rate per connection.
- **Relative back-off:** each connection tracks drops *relative to its own C_max baseline* (not an absolute) — this separates "this server is slow/far" (stable) from "the path got congested" (rising delay / dropping throughput above baseline). A drop of X% → cut the bucket refill.

### C. Post-resolution IP classification (the genuinely clever part)
Split-horizon problem: fast intranet + slow internet behind indistinguishable hostnames, one connection's 10 Gbps blinding the other's congestion math. Solution: intercept via `SocketsHttpHandler.ConnectCallback` (real, AOT-safe) *after* DNS, classify the resolved IP against RFC-1918 private ranges, and route private→Intranet bucket (unthrottled, watch local hardware only) vs public→Internet bucket (the sensitive back-off loop). Speed-heuristic fallback for public-IP corporate proxies — **but** see caveat 4.

## The four caveats (my review — these are what the prototype MUST measure)

1. **False positives.** "Throughput drop = competing traffic" is wrong as often as right — server hiccups, TCP recovery, CDN rebalancing, jitter all look like congestion. As written it will twitchily over-throttle. Needs **hysteresis**: require a drop to persist across N pulses, distinguish sustained decline from a single-sample dip, rate-limit how often it can cut.
2. **2X over-correction can oscillate.** "Drop X% → cut 2X%" is a magic multiplier; proportional over-correction is where sawtooth oscillation comes from, especially against variable competitors (a video bitrate ladder). Loop stability must be *reasoned about and measured*, not assumed. Constants are first guesses, almost certainly wrong, must be tuned against real contention.
3. **The abstraction blindspot (named in the blueprint, then ignored).** HttpClient gives only coarse, noisy chunk-timing — no RTT, no per-packet signal. Worse: the token bucket *controls your read rate*, so measured "throughput" is partly a measurement of your own throttle, not the path — congestion signal and control action are entangled (this is why LEDBAT-family algorithms exist; you're approximating them at the app layer with a coarse signal). At low speed / small chunks, a pulse may have too few samples to compute a meaningful delta. **This is the piece most at risk of "doesn't work on this stack."**
4. **Speed-heuristic fallback is itself a footgun.** ">150 Mbps → upgrade to intranet" misclassifies fast residential fiber + fast CDN as intranet → runs unthrottled → stops respecting the user's bandwidth entirely (the opposite of the goal). Gate it hard (e.g. only when a private-range download is also active), or drop it.

## The recovery controller (the missing half — user-spotted gap)

The blueprint has aggressive **back-off** and only a timid "nudge up periodically" **recovery**. That asymmetry means it yields and never comes back — user ends their call, download crawls forever. Back-off and recovery are **not symmetric problems**: backing off is easy/safe (clear signal, low cost of over-reacting); recovering is hard/ambiguous (free capacity is only detectable by *probing* — using some and seeing if it's absorbed).

The fix — **AIMD** (additive-increase / multiplicative-decrease, what TCP itself uses):
- **Multiplicative decrease** (already present): cut hard the instant congestion is seen.
- **Additive increase** (missing): steady, persistent, confident upward probing while clear — so recovery climbs back to full speed over seconds, not never.
- **Idle fast-path:** if last-input-time shows the user is away, the back-off reason is likely gone → recover *aggressively*, skip the cautious probing.

**The prototype must measure recovery TIME** — seconds from "user stops competing" to "back at full speed." That number determines whether the feature feels responsive or broken. Back-off correctness and recovery responsiveness are separate measurements.

## What's off the table
**Activity classification** (detect Teams/Zoom/gaming). High native-interop cost, inherently brittle, fights the AOT/minimal-deps spine, and unnecessary — "yield to congestion without knowing its source" (Tier 3 here) is strictly better than "guess the source." The robust CPU/RAM/disk politeness (low-priority work) lives in the separate "always gentle" phase and needs none of this.

## Build framing
- **Tier 1 (separate phase, ship first):** always-gentle CPU/RAM/disk — low-priority reassembly/checksum, paced I/O, manual cap, throttle toggle. Robust, no measurement needed.
- **Tier 2:** idle-aware speed (last-input-time). Clean refinement.
- **Tier 3 (this doc):** the adaptive bandwidth controller. Build **instrumented**, measure against real contention, tune constants, be willing to fall back to Tier-1 manual-cap if the app-layer signal proves too noisy (caveat 3). Needs distribution + telemetry first. "It works" is a measured claim, never a design assertion.
- `ConnectCallback` IP classification: verify AOT-clean via publish+smoke on all five RIDs before trusting it.