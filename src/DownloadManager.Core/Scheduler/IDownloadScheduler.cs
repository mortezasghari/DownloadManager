using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Scheduler;

/// <summary>Read-only view of a scheduled download, for observers/tests.</summary>
public interface IDownloadHandle
{
    DownloadId Id { get; }

    DownloadStatus Status { get; }

    /// <summary>Completes when the status first satisfies <paramref name="predicate"/>.</summary>
    Task WaitForStatusAsync(Func<DownloadStatus, bool> predicate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the lifecycle of every download (spec §8). No download does work except through the
/// scheduler. Control operations reject illegal transitions loudly and are safe under concurrency.
/// </summary>
public interface IDownloadScheduler : IAsyncDisposable
{
    /// <summary>Enqueue a new download. Applies backpressure if the bounded queue is full.</summary>
    Task<IDownloadHandle> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default);

    Task PauseAsync(DownloadId id, CancellationToken cancellationToken = default);

    Task ResumeAsync(DownloadId id, CancellationToken cancellationToken = default);

    Task CancelAsync(DownloadId id, CancellationToken cancellationToken = default);

    Task RetryAsync(DownloadId id, CancellationToken cancellationToken = default);

    IDownloadHandle? Find(DownloadId id);
}