using DownloadManager.Core.Configuration;

namespace DownloadManager.Core.Domain;

/// <summary>What the caller asks the engine to do. Auth/cookies/proxy breadth arrives in Phase 4.</summary>
public sealed record DownloadRequest
{
    public required DownloadId Id { get; init; }

    public required Uri Url { get; init; }

    /// <summary>Absolute path of the final target file. Sidecars live beside it (see PersistencePaths).</summary>
    public required string TargetPath { get; init; }

    /// <summary>Desired segment count. 1 = single-stream. Effective count is capped by size/range support.</summary>
    public int SegmentCount { get; init; } = 1;

    public PreallocationMode Preallocation { get; init; } = PreallocationMode.Full;

    /// <summary>Optional expected SHA-256 (hex) for post-completion verification.</summary>
    public string? ExpectedSha256 { get; init; }
}