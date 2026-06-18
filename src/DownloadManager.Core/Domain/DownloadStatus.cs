namespace DownloadManager.Core.Domain;

/// <summary>
/// Lifecycle state of a scheduled download (spec §8, ADR-0008). Legal transitions only:
/// <code>
/// Queued    -> Running | Paused | Canceled
/// Running   -> Completed | Failed | Retrying | Paused | Canceled
/// Retrying  -> Running | Paused | Canceled      (a distinct, cancellable backoff state)
/// Paused    -> Queued | Canceled
/// Failed    -> Queued | Canceled                (Queued = manual retry)
/// Completed -> (terminal)
/// Canceled  -> (terminal)
/// </code>
/// </summary>
public enum DownloadStatus
{
    Queued,
    Running,

    /// <summary>In a cancellable backoff delay between retry attempts.</summary>
    Retrying,
    Paused,
    Completed,
    Failed,
    Canceled,
}