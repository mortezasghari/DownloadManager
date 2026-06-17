# ADR-0003: Persistence — source-gen JSON metadata + custom binary append-only log

- Status: Accepted
- Date: 2026-06-16

## Context

We must persist, beside each download, (a) cold metadata (URLs, validators, size,
segment layout) and (b) hot, high-frequency per-segment progress. Everything has
to be AOT-safe on every RID with no native bundle beyond what Avalonia/Skia
already pull in (spec §1/§6). Two distinct access patterns argue for two stores.

## Options considered

1. **Embedded database (SQLite / LiteDB / EF Core).** Rejected.
   - SQLite drags a native library per RID — exactly the native-bundle/trim
     hazard §1 forbids, and another cross-compile surface for `linux-arm64` etc.
   - EF Core / LiteDB lean on reflection and dynamic materialization that fight
     Native AOT. LiteDB is also reflection-heavy.
   - A transactional SQL engine is far more than "record N longs durably."
2. **One uniform binary file for both metadata and progress.** Rejected.
   - Conflates a rarely-written, schema-rich record with a hot fixed-size append
     stream. The metadata wants easy evolution; the log wants a trivial,
     scan-and-truncate recovery. One format serves neither well.
3. **Source-generated `System.Text.Json` for metadata + a custom fixed-size
   binary append-only log for progress.** Chosen.

## Decision

- **Metadata (`*.dlmeta`)**: `System.Text.Json` with a `JsonSerializerContext`
  source generator — no reflection, AOT-clean. Written via temp → fsync →
  atomic rename → directory fsync (§6a). Schema evolution is just adding
  `init` properties + a `Version` field. Human-readability is a free side
  effect, never a design driver (and never traded against AOT/durability).
- **Progress (`*.dllog`)**: a custom append-only log of fixed 24-byte records
  (segmentId · durableOffset · sequence · CRC32). Fixed size makes recovery a
  linear scan; a torn tail is detectable as a misaligned length or a failing
  CRC. Compaction rewrites the log from current per-segment maxima via the same
  atomic-replace primitive.
- **CRC-32** is hand-rolled (~30 lines, IEEE polynomial) rather than taking
  `System.IO.Hashing`, honouring the §1 "minimize dependencies" rule for
  something the BCL doesn't expose but is trivial.

## Consequences

- No native database binary; nothing to cross-compile or trim-root beyond the
  BCL. The whole persistence layer AOT-compiles with zero IL/trim warnings.
- Recovery is total and cheap: scan, keep the highest CRC-valid offset per
  segment, normalize the tail.
- We own the binary format, so its evolution is a deliberate `Version` bump
  (see `ProgressLogFormat`), not an opaque library concern.