using System.Text.Json.Serialization;

namespace DownloadManager.Core.Domain;

/// <summary>
/// A half-open-free, fully inclusive byte range <c>[Start, EndInclusive]</c> assigned to one
/// segment. Inclusive end matches HTTP <c>Range: bytes=start-end</c> semantics exactly, which
/// keeps the offset math honest (no +/-1 translation between the wire and the file). All values
/// are <see cref="long"/> (spec §3: <c>int</c> for any size/offset is a defect).
/// </summary>
public readonly record struct SegmentRange(long Start, long EndInclusive)
{
    /// <summary>Number of bytes in the range. <c>[0,0]</c> is one byte.</summary>
    [JsonIgnore]
    public long Length => EndInclusive - Start + 1;

    /// <summary>Validated factory. Use this over the positional constructor outside of deserialization.</summary>
    public static SegmentRange Create(long start, long endInclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(endInclusive, start);
        return new SegmentRange(start, endInclusive);
    }

    public bool Contains(long offset) => offset >= Start && offset <= EndInclusive;

    public override string ToString() => $"[{Start}, {EndInclusive}] ({Length} bytes)";
}