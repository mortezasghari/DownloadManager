using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Engine;

/// <summary>
/// Aggregates per-segment completion into a whole-download snapshot. Tracks each segment's completed
/// byte count independently so a segment restart (e.g. after a resource-changed fallback) correctly
/// lowers the total rather than double-counting. Thread-safe for the parallel segments of Phase 2.
/// </summary>
internal sealed class ProgressAggregator(long totalBytes)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, long> _completedPerSegment = new();
    private readonly long _totalBytes = totalBytes;

    public void Set(int segmentId, long completedBytes)
    {
        lock (_gate)
        {
            _completedPerSegment[segmentId] = completedBytes;
        }
    }

    public DownloadProgress Snapshot()
    {
        lock (_gate)
        {
            long sum = 0;
            foreach (var value in _completedPerSegment.Values)
            {
                sum += value;
            }

            return new DownloadProgress(sum, _totalBytes);
        }
    }
}