using DownloadManager.Core.History;

namespace DownloadManager.Core.Abstractions;

/// <summary>
/// Read-only download history (ADR-0019): a record is appended when a download reaches a terminal state,
/// and the finished view reads the accumulated records. Persistence is source-gen JSON, atomically
/// written, at <c>{ApplicationData}/DownloadManager/history.json</c> — mirroring the Phase-7 settings
/// store. This phase exposes load + append only; per-entry delete and clear-all are intentionally not
/// here yet (the flat, id-keyed shape leaves them trivial to add later).
/// </summary>
public interface IHistoryStore
{
    /// <summary>All recorded entries in chronological (append) order. The view sorts newest-first for display.</summary>
    IReadOnlyList<HistoryRecord> Load();

    /// <summary>Append one terminal record and persist. Called once per terminal download.</summary>
    void Append(HistoryRecord record);

    /// <summary>
    /// Replace the entire cache with <paramref name="records"/> (chronological order) and persist it
    /// (ADR-0021). Used to reconcile this read model to the lifecycle log on startup — history is a
    /// projection, so a lost/corrupt file is rebuilt from the log rather than trusted.
    /// </summary>
    void Rebuild(IReadOnlyList<HistoryRecord> records);
}