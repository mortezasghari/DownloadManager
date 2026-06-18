namespace DownloadManager.Core.Configuration;

/// <summary>Scheduler tunables (spec §8).</summary>
public sealed record SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>Global cap on downloads running (or in backoff) at once. The hard concurrency gate.</summary>
    public int MaxConcurrentDownloads { get; init; } = 3;

    /// <summary>Bounded queue capacity; enqueue applies backpressure when full.</summary>
    public int QueueCapacity { get; init; } = 1024;
}