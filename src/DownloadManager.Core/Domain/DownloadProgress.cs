namespace DownloadManager.Core.Domain;

/// <summary>
/// A point-in-time progress snapshot pushed from the engine. Byte counts only — speed and ETA are
/// derived in the UI/view-model layer from successive snapshots and an injected <see cref="TimeProvider"/>
/// (spec §11/§12), so the engine stays free of clock reads.
/// </summary>
public readonly record struct DownloadProgress(long CompletedBytes, long TotalBytes)
{
    /// <summary>Fraction in [0,1], or null when the total size is unknown.</summary>
    public double? Fraction =>
        TotalBytes > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes, 0d, 1d) : null;
}