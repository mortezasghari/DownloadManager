# ADR-0012: Post-completion checksum verification — streamed, cancellable, mismatch policy

- Status: Accepted
- Date: 2026-06-18

## Context

A download may carry an `ExpectedSha256`. When present, the finished file must be
verified before it is declared Completed. Targets can be very large (100 GB), so
verification cannot load the file into memory, and on a big file it is a
long-running operation the user should see progress for.

## Decision

### Streamed SHA-256 over a bounded buffer

`ChecksumVerifier` hashes the completed file through a single
`ArrayPool<byte>`-rented buffer (the engine's `CopyBufferSize`), using
`IncrementalHash` (SHA-256). The file is **never** buffered whole — memory is
bounded regardless of file size, so a 100 GB target costs one buffer. The
implementation is reflection-free and AOT/trim-safe (pure BCL).

### Cancellable, progress-reporting, TimeProvider-timed

The pass honours a `CancellationToken` (checked every chunk) and reports a
`DownloadProgress` carrying `DownloadPhase.Verifying`, throttled to ~100 reports
across the file (plus first/last) so a huge file does not flood `IProgress`. All
timing (duration logging) goes through the injected `TimeProvider`, keeping the
component clock-free and testable with `FakeTimeProvider`.

### Where it runs

Verification runs in the single download code path, **after** the bytes are
durable and the target handle is closed, and **before** the sidecars are deleted —
for both the segmented and unknown-size paths. The download's durability ordering
and streaming loop are untouched.

### Mismatch policy

- **Match** → delete sidecars, return `Completed`.
- **Mismatch** → return `Failed` (non-transient); **keep the file and metadata**
  so the user can re-download, and log expected vs. computed. A checksum-mismatched
  file is **never** marked Completed and is never silently dropped.

The expected value is matched case-insensitively and tolerates an optional
`sha256:` prefix.

## Consequences

- Verification scales to arbitrarily large files at constant memory cost.
- A long verification is visible (Verifying progress) and interruptible.
- A corrupt download fails loudly while retaining the bytes and metadata for
  inspection or re-download, rather than being presented as a good file.
- Verification adds a full sequential read of the file on completion when an
  `ExpectedSha256` is set; this is the accepted cost of integrity.