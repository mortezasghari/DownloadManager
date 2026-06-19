namespace DownloadManager.Core.Configuration;

/// <summary>Retry/backoff tunables (spec §8). Backoff is exponential with jitter, bounded by attempts.</summary>
/// <remarks>
/// Attempts and backoff bounds are <c>set</c> rather than <c>init</c> so the queue-settings panel
/// (Phase 8/ADR-0018) can update this shared singleton live. <see cref="RetryPolicy"/> reads them per
/// call, so a change applies to the next attempt — including for an in-flight download — with no
/// scheduler/policy code change.
/// </remarks>
public sealed record RetryOptions
{
    public const string SectionName = "Retry";

    /// <summary>Total run attempts including the first (so 5 = first try + 4 retries).</summary>
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fraction of the computed delay added as random jitter, in [0, JitterFactor).</summary>
    public double JitterFactor { get; init; } = 0.2;
}