namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Which section of the queue a download row belongs to. The queue holds only <b>non-terminal</b>
/// downloads (terminal ones leave the queue and live in history — ADR-0021), so the sections are exactly
/// the active states: running, waiting, and parked (paused).
/// </summary>
public enum QueueSection
{
    /// <summary>Actively running or in retry backoff.</summary>
    Running,

    /// <summary>Queued but not yet started by a worker.</summary>
    Waiting,

    /// <summary>Parked: paused, resumable. (Terminal downloads are not in the queue at all.)</summary>
    Paused,
}