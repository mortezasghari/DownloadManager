using DownloadManager.Core.Configuration;

namespace DownloadManager.Core.Abstractions;

/// <summary>
/// A handle to the final target file that supports concurrent positioned writes from multiple
/// segment writers (spec §5). Backed by a single shared file handle; positioned writes do not share
/// a cursor and are safe for distinct, non-overlapping offset ranges. <see cref="FlushToDisk"/> is
/// the durability fence that must precede a progress checkpoint (spec §6c).
/// </summary>
public interface ITargetFile : IAsyncDisposable
{
    /// <summary>Positioned write at an absolute file offset. Never advances a shared cursor.</summary>
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Flush previously written bytes to durable storage (fsync / F_FULLFSYNC / FlushFileBuffers).
    /// Synchronous on purpose: it is a durability barrier, and the caller's ordering depends on it
    /// having completed before the progress log is touched.
    /// </summary>
    void FlushToDisk();

    /// <summary>Current file length in bytes.</summary>
    long Length { get; }
}

/// <summary>Opens/creates a target file and applies the requested preallocation strategy.</summary>
public interface ITargetFileFactory
{
    /// <param name="maxFullPreallocationBytes">Upper bound on bytes reserved by <b>Full</b> preallocation
    /// (ADR-0020, audit F6); a larger <paramref name="expectedSize"/> (or one above most of the free disk)
    /// degrades to sparse. Defaults to no cap for callers that don't supply one.</param>
    ITargetFile Open(
        string path, long expectedSize, PreallocationMode mode, long maxFullPreallocationBytes = long.MaxValue);
}