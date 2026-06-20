using System.Text.Json.Serialization;

namespace DownloadManager.Core.Lifecycle;

/// <summary>
/// A download lifecycle transition appended to the durable event log (ADR-0021). The log is the single
/// source of truth; the in-memory queue (channel) and <c>history.json</c> are projections of it.
/// </summary>
public enum LifecycleEventType
{
    /// <summary>Added to (or returned to) the active queue. Carries the reconstruction payload.</summary>
    Queued,

    /// <summary>Began running.</summary>
    Started,

    /// <summary>Parked (paused) — not in the active work list, resumable.</summary>
    Paused,

    /// <summary>Stopped/cancelled by the user — terminal, surfaced in history.</summary>
    Stopped,

    /// <summary>Finished successfully — terminal.</summary>
    Completed,

    /// <summary>Gave up after retries (or a non-transient error) — terminal.</summary>
    Failed,

    /// <summary>Tombstone: the id is removed from every projection (hidden). Deletes never mutate in place.</summary>
    Deleted,
}

/// <summary>
/// One append-only lifecycle-event record (ADR-0021). Replay keeps the latest event per id (by
/// <see cref="Sequence"/>); a tombstone (<see cref="LifecycleEventType.Deleted"/>) hides the id. The
/// reconstruction fields (<see cref="Url"/>, <see cref="TargetPath"/>, …) let the channel projection
/// rebuild a runnable request without consulting any other store; the history fields
/// (<see cref="Name"/>, <see cref="Size"/>) let the history projection rebuild a row. Serialized one
/// compact JSON object per line via the source-gen context — AOT-safe, no reflection.
/// </summary>
public sealed record LifecycleEvent
{
    public required string Id { get; init; }

    public required LifecycleEventType Type { get; init; }

    /// <summary>Monotonic append order, assigned by the log on write. Higher = later.</summary>
    public long Sequence { get; init; }

    // ---- reconstruction payload (carried on Queued so the active projection is self-contained) ----

    public string? Url { get; init; }

    public string? TargetPath { get; init; }

    public int SegmentCount { get; init; }

    public string? ExpectedSha256 { get; init; }

    // ---- history payload (carried on terminal events) ----

    /// <summary>Display filename for the history projection.</summary>
    public string? Name { get; init; }

    /// <summary>Size in bytes for the history projection.</summary>
    public long Size { get; init; }

    [JsonIgnore]
    public bool IsTerminal =>
        Type is LifecycleEventType.Completed or LifecycleEventType.Failed or LifecycleEventType.Stopped;

    [JsonIgnore]
    public bool IsActive => Type is LifecycleEventType.Queued or LifecycleEventType.Started;
}