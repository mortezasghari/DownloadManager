using DownloadManager.Core.Routing;

namespace DownloadManager.Core.Configuration;

/// <summary>
/// The validated, engine-ready result of loading <c>settings.json</c> (ADR-0016): the strongly-typed
/// option records the scheduler/engine actually consume, plus the resolved routing map and the UI's
/// default segment count. Every value here has already been clamped to a legal range, so nothing
/// downstream can receive an invariant-breaking input.
/// </summary>
public sealed record ResolvedSettings
{
    public required EngineOptions Engine { get; init; }

    public required SchedulerOptions Scheduler { get; init; }

    public required RetryOptions Retry { get; init; }

    public required RoutingOptions Routing { get; init; }

    public required DownloadDefaults Defaults { get; init; }
}

/// <summary>Per-download defaults chosen by the UI when building a <c>DownloadRequest</c>.</summary>
public sealed record DownloadDefaults
{
    /// <summary>Desired segment count for a new download. The engine caps this to its legal maximum.</summary>
    public int SegmentCount { get; init; } = 8;
}