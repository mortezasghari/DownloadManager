using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Abstractions;

/// <summary>
/// The hot, high-frequency append-only progress log (<c>*.dllog</c>, spec §6b). The engine appends a
/// checkpoint per segment roughly every <c>CheckpointIntervalBytes</c> and flushes it — always
/// <b>after</b> the target file's data has been flushed (§6c), so recorded progress can never exceed
/// durable file bytes.
/// </summary>
public interface IProgressLog : IAsyncDisposable
{
    /// <summary>Append one fixed-size, CRC-protected checkpoint record. Does not flush.</summary>
    void Append(SegmentCheckpoint checkpoint);

    /// <summary>fsync the log so the just-appended checkpoint is durable.</summary>
    void FlushToDisk();
}

/// <summary>
/// Opens a progress log for a download, performing crash recovery as it does so: scanning records,
/// taking the highest valid (CRC-passing) offset per segment, and truncating any torn tail (§6b/§6d).
/// Keyed by the download's target path; the sidecar path convention lives in the implementation.
/// </summary>
public interface IProgressLogStore
{
    ProgressLogSession Open(string targetPath);

    void Delete(string targetPath);
}

/// <summary>An opened log plus the per-segment durable offsets recovered from it.</summary>
public sealed class ProgressLogSession(IProgressLog log, IReadOnlyDictionary<int, long> recoveredOffsets)
    : IAsyncDisposable
{
    public IProgressLog Log { get; } = log;

    /// <summary>Highest durable offset per segment id recovered from the log (empty for a fresh download).</summary>
    public IReadOnlyDictionary<int, long> RecoveredOffsets { get; } = recoveredOffsets;

    public ValueTask DisposeAsync() => Log.DisposeAsync();
}