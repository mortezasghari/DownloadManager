using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Scheduler;

/// <summary>Read-only view of a scheduled download, for observers/tests.</summary>
public interface IDownloadHandle
{
    DownloadId Id { get; }

    DownloadStatus Status { get; }

    /// <summary>When <see cref="Status"/> is <see cref="DownloadStatus.Failed"/>, true if the reason is a
    /// 401/403 "needs credentials" (ADR-0011) rather than a generic failure. The partial download is
    /// retained; supplying credentials and retrying can resume it.</summary>
    bool NeedsCredentials { get; }

    /// <summary>Latest progress snapshot for the active/most-recent run — a lock-free
    /// <see cref="Volatile"/> read of the engine's progress counters, safe to poll from the UI thread.
    /// Bytes only; speed/ETA are derived by the consumer from successive snapshots + a clock.</summary>
    DownloadProgress Progress { get; }

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

    /// <summary>
    /// Postpone a download (ADR-0022): send it to the <b>end</b> of the queue and stop its active transfer
    /// if running (bytes retained). The freed slot promotes the next download; the postponed one resumes
    /// naturally when the queue works back to it. Not a parked/terminal state and there is no un-postpone —
    /// to send it further back, postpone again. Built from the existing FIFO (tail-append + skip the stale
    /// entry) and the per-download pause; no ordered/priority queue.
    /// </summary>
    Task PostponeAsync(DownloadId id, CancellationToken cancellationToken = default);

    /// <summary>Whether the whole queue is globally paused (ADR-0022).</summary>
    bool IsQueuePaused { get; }

    /// <summary>
    /// Global pause (ADR-0022): halt the running queue — block promotion of any further download. Active
    /// downloads are paused by the orchestrator via the existing per-download pause. Orthogonal to
    /// Postpone; while paused nothing downloads regardless of per-item state.
    /// </summary>
    void PauseQueue();

    /// <summary>Global play (ADR-0022): un-block promotion so the queue resumes.</summary>
    void ResumeQueue();

    /// <summary>The current size of the live concurrency gate (number of slots).</summary>
    int MaxConcurrency { get; }

    /// <summary>
    /// Resize the live concurrency gate (Phase 8). Raising it spawns workers, admitting a waiting download
    /// immediately; lowering it retires workers only after they finish their current download — a running
    /// download is never killed. This is the gate's supported control surface: the run model, state
    /// machine, durability ordering, channel, and retry path are unchanged.
    /// </summary>
    void SetMaxConcurrency(int maxConcurrentDownloads);
}