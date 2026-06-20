using System.Text.Json.Serialization;

namespace DownloadManager.Core.Configuration;

/// <summary>
/// The user-editable <c>settings.json</c> shape (ADR-0016). This is the <b>raw</b> deserialized form —
/// plain JSON-friendly primitives (ints, seconds, byte counts) so the file is hand-editable. It is then
/// validated and clamped into the strongly-typed engine option records by <see cref="SettingsStore"/>;
/// nothing here is consumed by the engine directly.
/// </summary>
/// <remarks>
/// Bound exclusively via the <see cref="AppSettingsJsonContext"/> source generator — no reflection
/// binder (e.g. <c>Microsoft.Extensions.Configuration</c>), which throws under Native AOT (spec §1).
/// Unknown JSON members are ignored, so users may freely add comment keys.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>Human-readable schema note written into the default file; ignored on read.</summary>
    [JsonPropertyName("_comment")]
    public string? Comment { get; set; }

    public SchedulerSettings Scheduler { get; set; } = new();

    public EngineSettings Engine { get; set; } = new();

    public RetrySettings Retry { get; set; } = new();

    public RoutingSettings Routing { get; set; } = new();

    /// <summary>Defaults whose routing folders are platform-correct for the current OS (macOS → Movies).</summary>
    public static AppSettings CreateDefault() => new()
    {
        Comment =
            "DownloadManager settings. Out-of-range values are clamped to legal bounds at load (a warning "
            + "is logged); a malformed file falls back to defaults and is left untouched. See settings.README.md.",
        Scheduler = new SchedulerSettings(),
        Engine = new EngineSettings(),
        Retry = new RetrySettings(),
        Routing = RoutingSettings.CreateDefault(),
    };
}

/// <summary>Scheduler knobs. See <see cref="SchedulerOptions"/>.</summary>
public sealed class SchedulerSettings
{
    public int MaxConcurrentDownloads { get; set; } = 3;

    public int QueueCapacity { get; set; } = 1024;
}

/// <summary>Engine + copy-loop knobs. See <see cref="EngineOptions"/>.</summary>
public sealed class EngineSettings
{
    /// <summary>Default desired segments for a new download (the requested count; engine caps it).</summary>
    public int SegmentsPerDownload { get; set; } = 8;

    /// <summary>Hard upper bound on segments per download.</summary>
    public int MaxSegmentsPerDownload { get; set; } = 16;

    public long SmallFileThresholdBytes { get; set; } = 8L * 1024 * 1024;

    public int CopyBufferBytes { get; set; } = 128 * 1024;

    public long CheckpointIntervalBytes { get; set; } = 8L * 1024 * 1024;

    public int PerAttemptTimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// Upper bound on bytes reserved by <b>Full</b> preallocation (ADR-0020, audit F6). A server-advertised
    /// size above this (or above most of the free disk) falls back to sparse instead of reserving up front.
    /// </summary>
    public long MaxFullPreallocationBytes { get; set; } = 16L * 1024 * 1024 * 1024;
}

/// <summary>Retry/backoff knobs. See <see cref="RetryOptions"/>.</summary>
public sealed class RetrySettings
{
    public int MaxAttempts { get; set; } = 5;

    public double BaseDelaySeconds { get; set; } = 1;

    public double MaxDelaySeconds { get; set; } = 30;

    public double JitterFactor { get; set; } = 0.2;
}