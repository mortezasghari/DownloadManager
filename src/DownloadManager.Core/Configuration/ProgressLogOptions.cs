namespace DownloadManager.Core.Configuration;

/// <summary>Tunables for the append-only progress log (spec §6b).</summary>
public sealed record ProgressLogOptions
{
    public const string SectionName = "ProgressLog";

    /// <summary>
    /// When the log file grows past this size it is compacted: rewritten from the current
    /// per-segment maxima (one record per segment) via an atomic replace. Keeps recovery scans fast.
    /// </summary>
    public long CompactionThresholdBytes { get; init; } = 1L * 1024 * 1024;
}