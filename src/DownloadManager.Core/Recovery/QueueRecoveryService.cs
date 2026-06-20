using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Lifecycle;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Core.Recovery;

/// <summary>
/// Rebuilds the volatile projections from the lifecycle log on startup (ADR-0021): replay the log, reduce
/// to active + terminal, reconcile <c>history.json</c> to the terminal projection, and hand back the
/// active downloads to re-enqueue. The log is the anchor; the channel and history are derived. Pure
/// orchestration — no engine, durability, or scheduler-internal change.
/// </summary>
public sealed partial class QueueRecoveryService(
    ILifecycleLog lifecycleLog, IHistoryStore historyStore, ILogger<QueueRecoveryService> logger)
{
    private readonly ILifecycleLog _lifecycleLog = lifecycleLog;
    private readonly IHistoryStore _historyStore = historyStore;
    private readonly ILogger<QueueRecoveryService> _logger = logger;

    /// <summary>
    /// Replay the log, rebuild the history read model from it (so a lost/corrupt <c>history.json</c> is
    /// restored), and return the active downloads to re-enqueue into the channel projection.
    /// </summary>
    public IReadOnlyList<DownloadRequest> Recover()
    {
        var events = _lifecycleLog.ReadAll();
        var projection = QueueProjection.Reduce(events);

        // history.json is a cache of the terminal projection — reconcile it to the log every startup.
        _historyStore.Rebuild(projection.History);

        LogRecovered(events.Count, projection.Active.Count, projection.History.Count);
        return projection.Active;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Queue recovery replayed {Events} event(s): {Active} active, {History} history record(s).")]
    private partial void LogRecovered(int events, int active, int history);
}