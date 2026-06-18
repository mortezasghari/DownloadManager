namespace DownloadManager.Core.Configuration;

/// <summary>Retry/backoff tunables (spec §8). Backoff is exponential with jitter, bounded by attempts.</summary>
public sealed record RetryOptions
{
    public const string SectionName = "Retry";

    /// <summary>Total run attempts including the first (so 5 = first try + 4 retries).</summary>
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Fraction of the computed delay added as random jitter, in [0, JitterFactor).</summary>
    public double JitterFactor { get; init; } = 0.2;
}