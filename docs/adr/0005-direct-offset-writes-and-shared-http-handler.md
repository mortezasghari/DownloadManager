# ADR-0005: Direct-offset writes (no merge) and a single shared HTTP handler

- Status: Accepted
- Date: 2026-06-16

## Context

Two engine-shaping choices from the spec need recording: how segment bytes reach
the file (§5), and how HTTP connections are managed (§4). Both bear on the
100+ GB / unstable-network target.

## Decision 1 — Direct-offset writes, no part files, no merge

Segments write straight into the final target at their assigned absolute offsets
via `System.IO.RandomAccess` positioned writes over a single shared
`SafeFileHandle` (`TargetFile`). Positioned writes don't share a cursor, so
distinct non-overlapping ranges are safe to write concurrently.

- **Rejected: part files + post-completion merge.** A 100 GB merge is a 100 GB
  re-read/write — unacceptable I/O and a second failure window. Direct writes
  cost nothing extra and the file is correct the instant the last byte lands.
- Preallocation (`Full | Sparse | None`) is configurable. Phase 1 sets the
  logical length via a sparse end-byte write; native block reservation
  (`posix_fallocate` / `fcntl(F_PREALLOCATE)` / `FILE_ALLOCATION_INFO`) is a
  Phase 2 upgrade behind the same `PreallocationMode` switch.

## Decision 2 — One shared `SocketsHttpHandler` for the whole app

A single `SocketsHttpHandler` + `HttpClient` (`SharedHttpClient`), never one per
download, configured per §4:

- `MaxConnectionsPerServer` explicit and configurable — the **per-host** cap is
  the real parallelism constraint (16 downloads × 8 segments mostly hit a few
  hosts), not the total request count.
- `AllowAutoRedirect = false` so range/validator semantics are re-established on
  the final URL (we follow redirects manually in `RangeProber` and persist the
  final URL).
- `AutomaticDecompression = None`; ranged requests send `Accept-Encoding:
  identity` so `Content-Length`/ranges refer to raw bytes and offset math stays
  correct (§3).
- `EnableMultipleHttp2Connections = true`; bounded
  `PooledConnectionLifetime`/`IdleTimeout` for long sessions.
- `HttpClient.Timeout = InfiniteTimeSpan`; per-attempt deadlines come from a
  linked `CancellationTokenSource` (TimeProvider-driven) so long large-file
  streams aren't aborted.

## Consequences

- The dominant performance lever (connection management) is centralized and
  testable; the copy loop uses an `ArrayPool<byte>` buffer and positioned writes
  with no seeking.
- `RandomAccess` has no managed `SetLength`/fsync, which is why `DurableIo` and
  the Phase-1 end-byte preallocation exist; true `Full` allocation is the only
  deferred piece, explicitly scoped to Phase 2.