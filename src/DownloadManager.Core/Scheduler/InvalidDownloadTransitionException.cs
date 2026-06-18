using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Scheduler;

/// <summary>
/// Thrown when a lifecycle operation is requested from a state that does not allow it (e.g. resume a
/// Completed download, pause a Canceled one). Illegal transitions are rejected loudly (spec §8).
/// </summary>
public sealed class InvalidDownloadTransitionException(DownloadStatus from, string operation)
    : InvalidOperationException($"Cannot {operation} a download in state {from}.")
{
    public DownloadStatus From { get; } = from;

    public string Operation { get; } = operation;
}