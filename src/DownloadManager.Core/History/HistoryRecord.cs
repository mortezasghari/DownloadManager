using System.Text.Json.Serialization;
using DownloadManager.Core.Domain;

namespace DownloadManager.Core.History;

/// <summary>
/// Terminal outcome recorded in the read-only download history (ADR-0019). Mirrors the spec's terminal
/// set exactly; the engine's <see cref="DownloadStatus.Canceled"/> maps to <see cref="Cancelled"/>.
/// </summary>
public enum HistoryState
{
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One read-only history entry, written once when a download reaches a terminal state (ADR-0019). Fields
/// are deliberately minimal. <see cref="SavedPath"/> is required because the open / reveal actions depend
/// on it. Records carry their own <see cref="Id"/> so future per-entry delete / clear-all is trivial to
/// add (not implemented this phase).
/// </summary>
public sealed record HistoryRecord
{
    /// <summary>Stable identity (the download's id), serialized as the canonical 32-char hex form.</summary>
    public required string Id { get; init; }

    /// <summary>Display name (the final filename).</summary>
    public required string Name { get; init; }

    /// <summary>Size in bytes (the known total, or the bytes written for a partial terminal state).</summary>
    public long Size { get; init; }

    public required HistoryState State { get; init; }

    /// <summary>The final on-disk path from the Phase-7 router. Required: open / reveal act on it.</summary>
    public required string SavedPath { get; init; }

    public static HistoryRecord From(DownloadId id, string name, long size, HistoryState state, string savedPath) => new()
    {
        Id = id.ToString(),
        Name = name,
        Size = size,
        State = state,
        SavedPath = savedPath,
    };
}

/// <summary>
/// On-disk shape of <c>history.json</c>: a flat, chronologically-appended list of <see cref="HistoryRecord"/>
/// plus a schema <see cref="Version"/>. A list (not a dictionary) preserves append order for the
/// newest-first display sort; each record's <see cref="HistoryRecord.Id"/> still keys it for future
/// delete-one / clear-all (ADR-0019).
/// </summary>
public sealed class HistoryFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("records")]
    public List<HistoryRecord> Records { get; set; } = [];
}