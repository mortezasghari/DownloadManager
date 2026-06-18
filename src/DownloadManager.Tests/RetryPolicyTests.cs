using DownloadManager.Core.Configuration;
using DownloadManager.Core.Scheduler;
using Xunit;

namespace DownloadManager.Tests;

public class RetryPolicyTests
{
    private static RetryPolicy Policy(double jitter = 0.0) => new(new RetryOptions
    {
        MaxAttempts = 5,
        BaseDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(30),
        JitterFactor = jitter,
    });

    [Fact]
    public void Should_retry_until_max_attempts()
    {
        var policy = Policy();
        Assert.True(policy.ShouldRetry(1));
        Assert.True(policy.ShouldRetry(4));
        Assert.False(policy.ShouldRetry(5));
        Assert.False(policy.ShouldRetry(6));
    }

    [Fact]
    public void Backoff_grows_exponentially_and_caps_at_max_delay()
    {
        var policy = Policy(); // no jitter
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(1, null));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay(2, null));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.NextDelay(3, null));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.NextDelay(4, null));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.NextDelay(10, null)); // capped
    }

    [Fact]
    public void Retry_after_is_honored_when_longer_than_the_computed_backoff()
    {
        var policy = Policy();
        var retryAfter = TimeSpan.FromSeconds(15);
        // attempt 1 backoff would be 1s; Retry-After of 15s wins.
        Assert.Equal(retryAfter, policy.NextDelay(1, retryAfter));
    }

    [Fact]
    public void Computed_backoff_wins_when_longer_than_retry_after()
    {
        var policy = Policy();
        // attempt 10 caps at 30s; a 5s Retry-After does not shorten it.
        Assert.Equal(TimeSpan.FromSeconds(30), policy.NextDelay(10, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Jitter_stays_within_bounds()
    {
        var policy = Policy(jitter: 0.5);
        for (var i = 0; i < 100; i++)
        {
            var delay = policy.NextDelay(1, null); // base 1s + up to 50% jitter
            Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5));
        }
    }
}