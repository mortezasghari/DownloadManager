using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Recovery;

/// <summary>
/// A download discovered by the startup recovery scan (spec §6d) that has persisted metadata and can
/// be handed back to <see cref="Abstractions.IDownloadEngine"/> to resume. The engine performs the
/// actual <c>If-Range</c> revalidation and offset reconciliation when re-run.
/// </summary>
public sealed record ResumeCandidate(string TargetPath, Uri OriginalUrl, DownloadMetadata Metadata)
{
    /// <summary>Builds the request that resumes this download (the engine re-reads the persisted layout).</summary>
    public DownloadRequest ToRequest() => new()
    {
        Id = DownloadId.New(),
        Url = OriginalUrl,
        TargetPath = TargetPath,
        SegmentCount = Metadata.Segments.Length > 0 ? Metadata.Segments.Length : 1,
        ExpectedSha256 = Metadata.ExpectedSha256,
    };
}