namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Which section of the queue a download row belongs to (Phase 8). The home surface is the queue, split
/// so a running download is visually separable from one that is merely waiting to start.
/// </summary>
public enum QueueSection
{
    /// <summary>Actively running or in retry backoff.</summary>
    Running,

    /// <summary>Queued but not yet started by a worker.</summary>
    Waiting,

    /// <summary>Paused or terminal (completed / failed / canceled).</summary>
    Finished,
}