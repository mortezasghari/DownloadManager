using DownloadManager.Core.Lifecycle;

namespace DownloadManager.Core.Abstractions;

/// <summary>
/// The durable, append-only lifecycle-event log — the single source of truth for queue membership and
/// terminal state (ADR-0021). Mirrors the <c>.dllog</c> discipline: atomic append + fsync, replay on
/// recovery, torn final record discarded, delete = tombstone append, never an in-place mutation.
/// </summary>
public interface ILifecycleLog
{
    /// <summary>Append one event durably (assigns its sequence and fsyncs). Append-event-first is the
    /// non-destructive step that must precede reflecting the change in any in-memory projection.</summary>
    void Append(LifecycleEvent lifecycleEvent);

    /// <summary>All valid events in append order; a torn/partial final record is discarded.</summary>
    IReadOnlyList<LifecycleEvent> ReadAll();
}