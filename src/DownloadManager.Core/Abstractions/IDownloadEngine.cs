using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Abstractions;

/// <summary>
/// The single download code path (spec Non-Negotiable #5). Probes the resource, lays out segments,
/// resumes from durable state, streams network→disk with correct durability ordering, and reports
/// progress. A non-segmented download is just the 1-segment case.
/// </summary>
public interface IDownloadEngine
{
    Task<DownloadOutcome> RunAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}