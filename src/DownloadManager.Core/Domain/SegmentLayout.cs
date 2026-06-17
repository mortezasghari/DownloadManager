namespace DownloadManager.Core.Domain;

/// <summary>
/// The non-overlapping partition of a known-size resource into segments. A single-stream download
/// is simply the 1-segment case (spec Non-Negotiable #5: one code path, not two engines). The
/// scheduler computes ranges from total size and segment count; segments never overlap.
/// </summary>
public sealed class SegmentLayout
{
    private readonly SegmentRange[] _segments;

    private SegmentLayout(SegmentRange[] segments, long totalSize)
    {
        _segments = segments;
        TotalSize = totalSize;
    }

    public IReadOnlyList<SegmentRange> Segments => _segments;

    public int Count => _segments.Length;

    public long TotalSize { get; }

    public SegmentRange this[int segmentId] => _segments[segmentId];

    /// <summary>The whole resource as one segment — the single-stream / non-resumable shape.</summary>
    public static SegmentLayout Single(long totalSize) => Split(totalSize, 1);

    /// <summary>
    /// Split <paramref name="totalSize"/> bytes into up to <paramref name="requestedSegments"/>
    /// contiguous, non-overlapping segments. Each segment gets <c>totalSize / count</c> bytes and
    /// the <b>last segment absorbs the remainder</b>, so the union is exactly <c>[0, totalSize-1]</c>.
    /// The count is capped at <paramref name="totalSize"/> so no zero-length segment is ever produced.
    /// </summary>
    public static SegmentLayout Split(long totalSize, int requestedSegments)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedSegments);

        var count = (int)Math.Min(requestedSegments, totalSize);
        var baseSize = totalSize / count;

        var segments = new SegmentRange[count];
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            // Last segment runs to the final byte, soaking up the division remainder.
            var endInclusive = i == count - 1 ? totalSize - 1 : start + baseSize - 1;
            segments[i] = SegmentRange.Create(start, endInclusive);
            start = endInclusive + 1;
        }

        return new SegmentLayout(segments, totalSize);
    }

    /// <summary>Rehydrate a persisted layout, validating that it tiles <paramref name="totalSize"/> exactly.</summary>
    public static SegmentLayout FromPersisted(IReadOnlyList<SegmentRange> segments, long totalSize)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("Layout must have at least one segment.", nameof(segments));
        }

        var ordered = segments.ToArray();
        long expectedStart = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Start != expectedStart)
            {
                throw new ArgumentException(
                    $"Segment {i} starts at {ordered[i].Start}, expected {expectedStart} (segments must be contiguous and non-overlapping).",
                    nameof(segments));
            }

            expectedStart = ordered[i].EndInclusive + 1;
        }

        if (expectedStart != totalSize)
        {
            throw new ArgumentException(
                $"Segments cover {expectedStart} bytes but total size is {totalSize}.", nameof(segments));
        }

        return new SegmentLayout(ordered, totalSize);
    }
}