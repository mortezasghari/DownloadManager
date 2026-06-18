# ADR-0006: Preallocation strategy and the Full → Sparse → None fallback chain

- Status: Accepted
- Date: 2026-06-17

## Context

Segments write directly into the final target at their offsets (ADR-0005). For
large multi-segment downloads it pays to reserve the file's space up front:
real block reservation avoids fragmentation and surfaces "disk full" before
gigabytes are streamed rather than after. But preallocation APIs are
platform-specific and not universally supported (filesystem, permissions), and
the spec (§5) forbids `DllImport` and any non-portable flush tricks.

## Decision

A `PreallocationMode` (`Full | Sparse | None`, default `Full`) selects the
strategy. The layout and total size are persisted to `*.dlmeta` (atomically)
**before** any preallocation or segment write, so recovery always knows the
intended size regardless of how allocation goes.

### Native full allocation (all via `LibraryImport`)

- **Linux**: `posix_fallocate(fd, 0, size)` — reserves real blocks and extends
  the file length in one call. Returns an errno directly (0 = success).
- **macOS**: `fcntl(fd, F_PREALLOCATE, &fstore)` trying `F_ALLOCATECONTIG` then
  `F_ALLOCATEALL`, followed by `ftruncate(fd, size)` (F_PREALLOCATE reserves
  blocks but does not move EOF).
- **Windows**: `SetFileInformationByHandle(FileAllocationInfo)` to reserve
  clusters, then a final-byte write to set EOF (the bytes are allocated, not
  sparse).

### Fallback chain — never abort the download

`NativePreallocator.TryAllocateFull` returns `false` (never throws) on any
failure — unsupported filesystem, `EOPNOTSUPP`, `ENOSPC`, etc. The factory then
degrades with a logged warning:

```
Full  --(native reservation failed)-->  Sparse  --(set-length failed)-->  None
```

- **Sparse** sets the logical length with a single end-byte write (sparse on
  NTFS/ext4/APFS).
- **None** does nothing; positioned writes extend the file as data arrives.

Preallocation is an optimization, not a correctness requirement, so a failure
here must never abort the download. Correctness still rests entirely on the §6c
durability ordering.

### Platform limitation: `F_PREALLOCATE` on arm64 macOS

`fcntl` is a **variadic** C function (`int fcntl(int, int, ...)`). On Apple Silicon
(arm64) the variadic argument is passed on the stack, not in a register, so a
`LibraryImport` declaration with a fixed third parameter passes the `fstore`
pointer in the wrong place and `F_PREALLOCATE` fails. The correct workaround is
`__arglist`, which requires `DllImport` — forbidden by spec §5. `LibraryImport`
does not support varargs. Therefore **real block reservation via `F_PREALLOCATE`
is not reachable on arm64 macOS** under our constraints.

Consequences and handling:
- The attempt is still made (it works on x64 macOS, where varargs use registers);
  on arm64 it fails cleanly and degrades to the sized (sparse, via `ftruncate`/
  end-byte) fallback. The download is unaffected — writes fill the file.
- This is **proven, not inferred**: the preallocation test and the `--smoke`
  self-test require the native path on Linux/Windows, and assert the documented
  fallback on arm64 macOS. If a future runtime makes the variadic call reachable,
  the tests will start requiring native there too.
- Durability is **not** affected: macOS `F_FULLFSYNC` (Phase 1) takes no third
  argument, so the variadic-ABI mismatch is harmless for fsync.

### Explicitly rejected

- `DllImport` (spec §5 mandates `LibraryImport`).
- `fallocate`/`sync_file_range` and other non-portable flush tricks — durability
  uses portable `fsync`/`F_FULLFSYNC`/`FlushFileBuffers` (ADR-0004). Flushing the
  whole file is fine: it only makes "durable ≥ recorded" more true.

## Consequences

- Resuming a file already at least the expected size is a no-op; existing bytes
  are never zeroed (verified by test).
- The native surface is the first real cross-platform P/Invoke beyond `fsync`;
  the `LibraryImport` source generator validates marshalling for every platform
  at build time, and the multi-RID AOT publish (CI) validates native linking.
- Tested: Full sets length, forced-failure falls back to Sparse, Sparse/None
  behaviours, and resume-preserves-contents. Per-OS native paths are exercised
  by the test on each CI runner.