namespace DownloadManager.Core.Domain;

/// <summary>
/// A single durable-progress checkpoint for one segment: <paramref name="DurableOffset"/> is the
/// absolute file offset up to which bytes are guaranteed flushed to disk (exclusive — it is the
/// offset of the next byte to write). By the durability ordering in §6c, the file always contains
/// at least this many durable bytes, so resuming from here can never corrupt the target.
/// </summary>
public readonly record struct SegmentCheckpoint(int SegmentId, long DurableOffset);