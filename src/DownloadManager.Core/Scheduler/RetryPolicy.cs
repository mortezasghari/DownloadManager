using DownloadManager.Core.Configuration;

namespace DownloadManager.Core.Scheduler;

/// <summary>
/// Exponential backoff with jitter, honoring a server <c>Retry-After</c> (spec §8). Pure computation —
/// the actual wait is a <c>Task.Delay</c> driven by the injected <see cref="TimeProvider"/> in the
/// scheduler, so it is deterministic under <c>FakeTimeProvider</c> and cancellable.
/// </summary>
public sealed class RetryPolicy(RetryOptions options)
{
    private readonly RetryOptions _options = options;

    /// <summary>Whether another attempt is allowed after <paramref name="attemptsMade"/> have run.</summary>
    public bool ShouldRetry(int attemptsMade) => attemptsMade < _options.MaxAttempts;

    /// <summary>
    /// Delay before the next attempt. Exponential in the attempt number, capped at <c>MaxDelay</c>,
    /// plus jitter; never less than a server-supplied <paramref name="retryAfter"/>.
    /// </summary>
    public TimeSpan NextDelay(int attemptsMade, TimeSpan? retryAfter)
    {
        // attemptsMade >= 1 here (we only back off after a failure).
        var exponent = Math.Max(0, attemptsMade - 1);
        var scaled = _options.BaseDelay.Ticks * Math.Pow(2, exponent);
        var cappedTicks = (long)Math.Min(scaled, _options.MaxDelay.Ticks);

        var jitterTicks = (long)(cappedTicks * _options.JitterFactor * Random.Shared.NextDouble());
        var delay = TimeSpan.FromTicks(cappedTicks + jitterTicks);

        if (retryAfter is { } hint && hint > delay)
        {
            delay = hint;
        }

        return delay;
    }
}