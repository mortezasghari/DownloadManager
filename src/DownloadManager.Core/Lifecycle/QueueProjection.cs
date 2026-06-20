using DownloadManager.Core.Domain;
using DownloadManager.Core.History;

namespace DownloadManager.Core.Lifecycle;

/// <summary>
/// Reduces the lifecycle-event log to its two read models (ADR-0021): the <b>active</b> queue (downloads
/// whose latest event is non-terminal and runnable) and the <b>history</b> (terminal downloads), honoring
/// tombstones. Pure and deterministic — the channel and <c>history.json</c> are both rebuilt from here, so
/// they can never disagree with the log. "Latest per id" is by <see cref="LifecycleEvent.Sequence"/>;
/// first-seen order is preserved for stable output.
/// </summary>
public static class QueueProjection
{
    public sealed record Result(IReadOnlyList<DownloadRequest> Active, IReadOnlyList<HistoryRecord> History);

    public static Result Reduce(IReadOnlyList<LifecycleEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Latest event per id (by sequence), preserving first-seen order for deterministic output.
        var latest = new Dictionary<string, LifecycleEvent>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var e in events)
        {
            if (!latest.TryGetValue(e.Id, out var existing))
            {
                order.Add(e.Id);
                latest[e.Id] = e;
            }
            else if (e.Sequence >= existing.Sequence)
            {
                latest[e.Id] = e;
            }
        }

        var active = new List<DownloadRequest>();
        var history = new List<HistoryRecord>();
        foreach (var id in order)
        {
            var e = latest[id];
            if (e.Type == LifecycleEventType.Deleted)
            {
                continue; // tombstone: hidden from every projection
            }

            if (e.IsActive)
            {
                active.Add(ToRequest(e));
            }
            else if (e.IsTerminal)
            {
                history.Add(ToHistory(e));
            }

            // Paused (parked) is neither active nor terminal — recoverable via re-add (accepted trade).
        }

        return new Result(active, history);
    }

    private static DownloadRequest ToRequest(LifecycleEvent e) => new()
    {
        // Preserve the original id so re-enqueue stays the same logical download across restarts (no
        // cross-restart duplication); the persisted target path makes the engine resume the same on-disk
        // file via its .dlmeta/.dllog (durability path untouched).
        Id = DownloadId.Parse(e.Id),
        Url = new Uri(e.Url ?? throw new InvalidOperationException($"Queued event {e.Id} has no URL.")),
        TargetPath = e.TargetPath ?? throw new InvalidOperationException($"Queued event {e.Id} has no target path."),
        SegmentCount = e.SegmentCount > 0 ? e.SegmentCount : 1,
        ExpectedSha256 = e.ExpectedSha256,
    };

    private static HistoryRecord ToHistory(LifecycleEvent e) => new()
    {
        Id = e.Id,
        Name = e.Name ?? Path.GetFileName(e.TargetPath ?? string.Empty),
        Size = e.Size,
        State = e.Type switch
        {
            LifecycleEventType.Completed => HistoryState.Completed,
            LifecycleEventType.Failed => HistoryState.Failed,
            _ => HistoryState.Cancelled, // Stopped
        },
        SavedPath = e.TargetPath ?? string.Empty,
    };
}