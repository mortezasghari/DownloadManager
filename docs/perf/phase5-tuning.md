# Phase 5 — perf tuning & memory ceiling

Measured, not guessed. Harness: `DownloadManager.UI --bench` (`src/DownloadManager.UI/Bench.cs`) —
the real engine + scheduler + persistence stack driving downloads over a raw-socket loopback HTTP
origin that generates bytes on the fly (so the origin holds no large buffer and stays out of the
downloader's memory measurement). Local measurement only; **not** part of the CI gate.

## Method

- Throughput: one 128 MB download per configuration, wall-clock → MB/s; managed bytes allocated over
  the run via `GC.GetTotalAllocatedBytes()`.
- Memory ceiling: 8 concurrent × 64 MB downloads, then `Process.PeakWorkingSet64` and
  `GC.GetTotalMemory(forceFullCollection: true)`.
- Run with `dotnet run --project src/DownloadManager.UI -c Release -- --bench`.

## Environment

- Linux 6.19 (arch), 32 logical CPUs, .NET 10, Release JIT, loopback (no real network latency), SSD temp.

## Results (representative run)

```
# Checkpoint cadence (8 segments, 128 KB buffer, 128 MB)
  checkpoint=1 MB         1540.8 MB/s   83 ms   alloc 2.1 MB
  checkpoint=8 MB         4322.6 MB/s   29 ms   alloc 1.6 MB
  checkpoint=64 MB        4402.5 MB/s   29 ms   alloc 0.8 MB

# Copy-buffer size (8 segments, 8 MB checkpoint, 128 MB)
  buffer=64 KB            4966.6 MB/s   25 ms   alloc 1.4 MB
  buffer=128 KB           4546.5 MB/s   28 ms   alloc 0.8 MB
  buffer=1024 KB          3949.7 MB/s   32 ms   alloc 8.8 MB

# Segment count / per-host connections (128 KB buffer, 8 MB checkpoint, 128 MB)
  segments=1              4946.2 MB/s   25 ms
  segments=4              5518.3 MB/s   23 ms
  segments=8              4789.7 MB/s   26 ms
  segments=16            4496.2 MB/s   28 ms

# Memory ceiling: 8 concurrent x 64 MB
  peak working set 79.6 MB   managed heap 16.3 MB   (ceiling 200 MB)
  RESULT: UNDER 200 MB ceiling
```

## Reading the numbers (important caveat)

Loopback has **no network latency** and effectively infinite bandwidth, so absolute MB/s here reflect
disk + HTTP framing + checkpoint overhead, **not** real-world download speed. Two consequences:

- **Segment count looks flat** (1→16 all within ~20%). That is expected: the point of segmentation is
  to hide *latency* and work around per-connection server throttling, neither of which exists on
  loopback. This run does not — and cannot — measure the real benefit of parallel segments; it only
  confirms that adding segments does not *regress* on a fast path (it doesn't) and that allocations
  scale modestly with concurrency.
- **Copy-buffer differences (64/128/1024 KB) are within run-to-run noise** on loopback; the 1 MB case
  is mildly slower and allocates more (larger `ArrayPool` rentals). No real-network signal here.

The one lever with a **clear, real signal** is checkpoint cadence.

## The real lever: fsync / checkpoint cadence

A 1 MB checkpoint interval cut throughput to ~35% of the 8 MB interval (1540 vs 4322 MB/s) — the cost
of fsync-per-data + log append + log fsync (§6c durability ordering) happening 8× more often.
Loosening from 8 MB → 64 MB gained essentially nothing (4322 → 4402, within noise) while increasing
the bytes re-downloaded after a crash (work since the last durable checkpoint is replayed).

This is a real durability/throughput trade-off and the 8 MB default sits at the knee of the curve.

## Decision: keep the defaults

| Lever | Default | Verdict |
|---|---|---|
| `CheckpointIntervalBytes` | 8 MB | **Keep** — knee of the curve; 1 MB is ~3× slower, 64 MB adds crash-redownload risk for no gain. |
| `CopyBufferSize` | 128 KB | **Keep** — within noise vs 64 KB on loopback; matches spec §3 (64–128 KB); 1 MB regresses + allocates more. |
| `MaxConnectionsPerServer` / segments | 16 / requested | **Keep** — no regression; real benefit is latency-hiding, not measurable on loopback. |

No default changes are justified by measurement. Per the spec, allocations on the network/disk-bound
path were **not** micro-optimized — the run allocates single-digit MB end to end, dominated by
`ArrayPool` rentals that are already pooled.

## Memory ceiling

8 concurrent × 64 MB (512 MB transferred): **peak working set 79.6 MB**, managed heap 16.3 MB —
comfortably under the 200 MB ceiling. This is the expected result of bounded `ArrayPool` copy buffers
(one rental per active segment) + positioned `RandomAccess` writes (no full-file buffering) + lock-free
counters. Reported actual, not asserted.