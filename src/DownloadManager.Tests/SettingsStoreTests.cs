using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;
using DownloadManager.Tests.Fakes;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Externalized-config tests (ADR-0016): values parse and reach the engine/scheduler, missing/malformed
/// files are handled without crashing, and out-of-range edits clamp to legal bounds. All headless.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private static CancellationToken Guard => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dlm-cfg-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Valid_file_values_parse_into_the_resolved_option_records()
    {
        File.WriteAllText(SettingsPath, """
            {
              "scheduler": { "maxConcurrentDownloads": 2, "queueCapacity": 64 },
              "engine": {
                "segmentsPerDownload": 4, "maxSegmentsPerDownload": 12,
                "copyBufferBytes": 65536, "checkpointIntervalBytes": 1048576,
                "perAttemptTimeoutSeconds": 50, "smallFileThresholdBytes": 1024
              },
              "retry": { "maxAttempts": 7, "baseDelaySeconds": 2, "maxDelaySeconds": 40, "jitterFactor": 0.1 }
            }
            """);
        var logger = new CollectingLogger<SettingsStoreTests>();

        var resolved = SettingsStore.LoadOrCreate(SettingsPath, logger, userProfile: _dir);

        Assert.Equal(2, resolved.Scheduler.MaxConcurrentDownloads);
        Assert.Equal(64, resolved.Scheduler.QueueCapacity);
        Assert.Equal(65536, resolved.Engine.CopyBufferSize);
        Assert.Equal(1048576, resolved.Engine.CheckpointIntervalBytes);
        Assert.Equal(TimeSpan.FromSeconds(50), resolved.Engine.PerAttemptTimeout);
        Assert.Equal(12, resolved.Engine.MaxSegmentsPerDownload);
        Assert.Equal(1024, resolved.Engine.SmallFileThresholdBytes);
        Assert.Equal(4, resolved.Defaults.SegmentCount);
        Assert.Equal(7, resolved.Retry.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), resolved.Retry.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(40), resolved.Retry.MaxDelay);
        Assert.Equal(0.1, resolved.Retry.JitterFactor);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("clamped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Loaded_max_concurrent_reaches_the_scheduler_and_gates_concurrency()
    {
        File.WriteAllText(SettingsPath, """{ "scheduler": { "maxConcurrentDownloads": 2 } }""");
        var resolved = SettingsStore.LoadOrCreate(SettingsPath, new CollectingLogger<SettingsStoreTests>(), userProfile: _dir);

        // Build the real scheduler stack with the loaded value and prove only 2 run at once.
        await using var harness = SchedulerHarness.Gated(
            maxConcurrent: resolved.Scheduler.MaxConcurrentDownloads, content: EngineHarness.Pattern(2048));

        var handles = new List<IDownloadHandle>();
        for (var i = 0; i < 4; i++)
        {
            handles.Add(await harness.Scheduler.EnqueueAsync(harness.NewRequest(), Guard));
        }

        await harness.Gate.WaitStartedAsync(2, Guard);

        Assert.Equal(2, harness.Gate.InFlight);
        Assert.Equal(2, handles.Count(h => h.Status == DownloadStatus.Running));
        Assert.Equal(2, handles.Count(h => h.Status == DownloadStatus.Queued));
    }

    [Fact]
    public void Loaded_retry_values_reach_the_retry_policy()
    {
        File.WriteAllText(SettingsPath, """
            { "retry": { "maxAttempts": 3, "baseDelaySeconds": 5, "maxDelaySeconds": 60, "jitterFactor": 0 } }
            """);
        var resolved = SettingsStore.LoadOrCreate(SettingsPath, new CollectingLogger<SettingsStoreTests>(), userProfile: _dir);

        var policy = new RetryPolicy(resolved.Retry);

        Assert.True(policy.ShouldRetry(2));
        Assert.False(policy.ShouldRetry(3)); // MaxAttempts = 3
        Assert.Equal(TimeSpan.FromSeconds(5), policy.NextDelay(1, retryAfter: null)); // base, jitter 0
    }

    [Fact]
    public void Missing_file_applies_defaults_and_writes_a_default_file_with_readme()
    {
        Assert.False(File.Exists(SettingsPath));
        var logger = new CollectingLogger<SettingsStoreTests>();

        var resolved = SettingsStore.LoadOrCreate(SettingsPath, logger, userProfile: _dir);

        Assert.True(File.Exists(SettingsPath));
        Assert.True(File.Exists(Path.Combine(_dir, "settings.README.md")));
        Assert.Equal(3, resolved.Scheduler.MaxConcurrentDownloads);   // defaults
        Assert.Equal(8, resolved.Defaults.SegmentCount);
        // The written file is itself valid and round-trips.
        var reloaded = SettingsStore.LoadOrCreate(SettingsPath, new CollectingLogger<SettingsStoreTests>(), userProfile: _dir);
        Assert.Equal(resolved.Scheduler.MaxConcurrentDownloads, reloaded.Scheduler.MaxConcurrentDownloads);
    }

    [Fact]
    public void Malformed_json_uses_defaults_warns_and_does_not_overwrite_the_users_file()
    {
        const string broken = "{ this is not valid json ";
        File.WriteAllText(SettingsPath, broken);
        var logger = new CollectingLogger<SettingsStoreTests>();

        var resolved = SettingsStore.LoadOrCreate(SettingsPath, logger, userProfile: _dir);

        Assert.Equal(3, resolved.Scheduler.MaxConcurrentDownloads); // defaults applied
        Assert.Equal(broken, File.ReadAllText(SettingsPath));       // user's broken edit untouched
        Assert.Contains(logger.Messages, m => m.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Out_of_range_values_are_clamped_to_legal_bounds_with_warnings()
    {
        File.WriteAllText(SettingsPath, """
            {
              "scheduler": { "maxConcurrentDownloads": 999, "queueCapacity": 0 },
              "engine": { "segmentsPerDownload": -5, "maxSegmentsPerDownload": 999, "copyBufferBytes": 1, "perAttemptTimeoutSeconds": 0 },
              "retry": { "maxAttempts": 0, "jitterFactor": 5, "baseDelaySeconds": 100, "maxDelaySeconds": 1 }
            }
            """);
        var logger = new CollectingLogger<SettingsStoreTests>();

        var resolved = SettingsStore.LoadOrCreate(SettingsPath, logger, userProfile: _dir);

        Assert.Equal(64, resolved.Scheduler.MaxConcurrentDownloads); // clamped to max
        Assert.Equal(1, resolved.Scheduler.QueueCapacity);           // clamped to min
        Assert.Equal(16, resolved.Engine.MaxSegmentsPerDownload);    // clamped to hard cap
        Assert.Equal(1, resolved.Defaults.SegmentCount);             // negative -> min
        Assert.Equal(4 * 1024, resolved.Engine.CopyBufferSize);      // below floor -> min
        Assert.Equal(TimeSpan.FromSeconds(1), resolved.Engine.PerAttemptTimeout);
        Assert.Equal(1, resolved.Retry.MaxAttempts);                 // 0 -> min
        Assert.Equal(1.0, resolved.Retry.JitterFactor);              // 5 -> max
        // maxDelay (1s) raised to at least baseDelay (100s) — the coherence clamp.
        Assert.True(resolved.Retry.MaxDelay >= resolved.Retry.BaseDelay);
        Assert.Contains(logger.Messages, m => m.Contains("clamped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Default_config_directory_resolves_under_the_platform_application_data_folder()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DownloadManager");

        Assert.Equal(expected, SettingsStore.DefaultDirectory());
        Assert.Equal(Path.Combine(expected, "settings.json"), SettingsStore.DefaultPath());
    }
}