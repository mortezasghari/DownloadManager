namespace DownloadManager.Core.Domain;

public enum DownloadStatus
{
    Queued,
    Probing,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled,
}