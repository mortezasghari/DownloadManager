# ADR-0007: Segment-count selection and the small-file threshold

- Status: Accepted
- Date: 2026-06-17

## Context

Segmentation trades extra connections, requests, and fsync checkpoints for
parallel throughput on large files over high-latency links. For small files,
or servers that don't truly support ranges, that trade is a net loss — more
round-trips and disk syncs for no gain, and more ways to fail.

## Decision

`DownloadEngine.ChooseSegmentCount(totalSize, acceptsRanges, requested)` decides
the effective segment count, in this order:

1. **Segment only when range support is real.** `acceptsRanges` is true only
   after the probe saw an actual `206` with a sane `Content-Range` (ADR/spec §3)
   — never from `Accept-Ranges` alone. Otherwise: 1 segment.
2. **Skip small files.** If `totalSize < SmallFileThresholdBytes` (default
   **8 MB**, configurable): 1 segment.
3. **Clamp the request.** Otherwise use `Clamp(requested, 1, MaxSegmentsPerDownload)`
   (default max **16**).

`SegmentLayout.Split` then further caps the count at `totalSize` so a zero-length
segment is never produced, and gives the division remainder to the last segment
so the segments tile `[0, totalSize-1]` exactly.

Parallelism within a download is bounded separately by `MaxSegmentConcurrency`
(default 8): the layout can have up to 16 segments while only N run at once.

## Consequences

- A non-resumable or unknown-size resource (`200`, no `Content-Range`) is always
  a single stream — the 1-segment case of the same code path (spec §0.5).
- The thresholds are `EngineOptions` (configurable, not magic numbers).
- Tested: tiny-file-no-segment, clamp-to-16 (request of 100 → 16 segments),
  last-segment remainder end-to-end, and parallel multi-segment completion.