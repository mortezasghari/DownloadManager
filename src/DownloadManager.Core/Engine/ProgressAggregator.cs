using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Engine;

/// <summary>
/// Aggregates per-segment completion into a whole-download snapshot. Lock-free: each segment owns one
/// slot of a fixed array (sized to the segment count at layout time — no dict, no resize), updated
/// with <see cref="Volatile"/> writes and summed with <see cref="Volatile"/> reads. Segments write
/// disjoint slots, so there is no contention and no torn/lost update (64-bit long access is atomic).
/// Phase 3 Stage 1: this replaced a dict + lock; the control plane and durability are untouched.
/// </summary>
internal sealed class ProgressAggregator
{
    private readonly long[] _completedPerSegment;
    private readonly long _totalBytes;

    public ProgressAggregator(long totalBytes, int segmentCount)
    {
        _totalBytes = totalBytes;
        _completedPerSegment = new long[Math.Max(1, segmentCount)];
    }

    /// <summary>Set the completed-byte count for one segment. Called only by that segment's writer.</summary>
    public void Set(int segmentId, long completedBytes) =>
        Volatile.Write(ref _completedPerSegment[segmentId], completedBytes);

    public DownloadProgress Snapshot()
    {
        long sum = 0;
        for (var i = 0; i < _completedPerSegment.Length; i++)
        {
            sum += Volatile.Read(ref _completedPerSegment[i]);
        }

        return new DownloadProgress(sum, _totalBytes);
    }
}