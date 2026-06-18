namespace DownloadManager.Core.Domain;

/// <summary>Which phase of a run a <see cref="DownloadProgress"/> snapshot describes (spec Phase 4).</summary>
public enum DownloadPhase
{
    /// <summary>Streaming bytes network→disk.</summary>
    Downloading,

    /// <summary>Hashing the completed file for SHA-256 verification (a long op on large files).</summary>
    Verifying,
}

/// <summary>
/// A point-in-time progress snapshot pushed from the engine. Byte counts only — speed and ETA are
/// derived in the UI/view-model layer from successive snapshots and an injected <see cref="TimeProvider"/>
/// (spec §11/§12), so the engine stays free of clock reads. <see cref="Phase"/> distinguishes the
/// download stream from the post-completion checksum pass.
/// </summary>
public readonly record struct DownloadProgress(
    long CompletedBytes, long TotalBytes, DownloadPhase Phase = DownloadPhase.Downloading)
{
    /// <summary>Fraction in [0,1], or null when the total size is unknown.</summary>
    public double? Fraction =>
        TotalBytes > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes, 0d, 1d) : null;
}