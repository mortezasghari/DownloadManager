using DownloadManager.Core.Configuration;
using DownloadManager.Core.Scheduler;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.ViewModels;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 8 / ADR-0018: the inline queue-settings panel. Edits are local until Save; Save validates the
/// whole set through the existing store (clamp + warn, can't persist an illegal config), writes
/// settings.json, and applies per knob with honest timing. Headless — no Avalonia.
/// </summary>
public sealed class QueueSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dlm-qsettings", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public QueueSettingsTests() => Directory.CreateDirectory(_dir);

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

    private sealed class Fixture
    {
        public FakeUiScheduler Scheduler { get; } = new();

        public EngineOptions Engine { get; } = new();

        public RetryOptions Retry { get; } = new();

        public DownloadDefaults Defaults { get; } = new();

        public ScheduleOptions Schedule { get; } = new();

        public CollectingLogger<QueueSettingsTests> Logger { get; } = new();

        public QueueSettingsViewModel Panel { get; }

        public Fixture(string settingsPath, string userProfile)
        {
            Panel = new QueueSettingsViewModel(
                Scheduler, Engine, Retry, Defaults, Schedule, settingsPath, Logger, userProfile);
        }
    }

    private Fixture NewFixture() => new(SettingsPath, _dir);

    [Fact]
    public void Panel_opens_with_the_current_live_values()
    {
        var f = NewFixture();

        Assert.Equal(3, f.Panel.MaxConcurrentDownloads);      // FakeUiScheduler default
        Assert.Equal(8, f.Panel.SegmentsPerDownload);          // DownloadDefaults default
        Assert.Equal(8d, f.Panel.SmallFileThresholdMiB);       // 8 MiB default
        Assert.Equal(5, f.Panel.MaxAttempts);
        Assert.Equal(100, f.Panel.PerAttemptTimeoutSeconds);
    }

    [Fact]
    public void Edits_stay_local_until_save_and_cancel_discards_without_writing()
    {
        var f = NewFixture();

        f.Panel.MaxConcurrentDownloads = 7;
        f.Panel.SegmentsPerDownload = 2;

        Assert.False(File.Exists(SettingsPath));        // nothing persisted yet
        Assert.Empty(f.Scheduler.ConcurrencyChanges);   // nothing applied yet

        f.Panel.CancelCommand.Execute(null);

        Assert.False(File.Exists(SettingsPath));         // Cancel does not write
        Assert.Equal(3, f.Panel.MaxConcurrentDownloads); // reverted to live value
        Assert.Equal(8, f.Panel.SegmentsPerDownload);
    }

    [Fact]
    public void Save_writes_settings_json_and_round_trips_through_the_store()
    {
        var f = NewFixture();

        f.Panel.MaxConcurrentDownloads = 4;
        f.Panel.SegmentsPerDownload = 6;
        f.Panel.SmallFileThresholdMiB = 16;
        f.Panel.MaxAttempts = 7;
        f.Panel.BaseDelaySeconds = 2;
        f.Panel.MaxDelaySeconds = 45;
        f.Panel.PerAttemptTimeoutSeconds = 75;

        f.Panel.SaveCommand.Execute(null);

        Assert.True(File.Exists(SettingsPath));
        var raw = SettingsStore.ReadRaw(SettingsPath);
        Assert.Equal(4, raw.Scheduler.MaxConcurrentDownloads);
        Assert.Equal(6, raw.Engine.SegmentsPerDownload);
        Assert.Equal(16L * 1024 * 1024, raw.Engine.SmallFileThresholdBytes);
        Assert.Equal(7, raw.Retry.MaxAttempts);
        Assert.Equal(75, raw.Engine.PerAttemptTimeoutSeconds);

        // Re-resolving the saved file yields the same values (loader-legal).
        var resolved = SettingsStore.LoadOrCreate(SettingsPath, f.Logger, _dir);
        Assert.Equal(4, resolved.Scheduler.MaxConcurrentDownloads);
        Assert.Equal(6, resolved.Defaults.SegmentCount);
    }

    [Fact]
    public void Save_clamps_out_of_range_edits_warns_and_cannot_persist_an_illegal_config()
    {
        var f = NewFixture();

        f.Panel.MaxConcurrentDownloads = 999;   // > max (64)
        f.Panel.MaxAttempts = 0;                 // < min (1)
        f.Panel.SegmentsPerDownload = -4;        // < min (1)

        f.Panel.SaveCommand.Execute(null);

        var raw = SettingsStore.ReadRaw(SettingsPath);
        Assert.Equal(64, raw.Scheduler.MaxConcurrentDownloads); // persisted value is clamped
        Assert.Equal(1, raw.Retry.MaxAttempts);
        Assert.Equal(1, raw.Engine.SegmentsPerDownload);
        Assert.Equal(64, f.Panel.MaxConcurrentDownloads);       // fields show the clamped values
        Assert.Contains(f.Logger.Messages, m => m.Contains("clamped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_applies_max_concurrent_live_to_the_scheduler_gate()
    {
        var f = NewFixture();

        f.Panel.MaxConcurrentDownloads = 5;
        f.Panel.SaveCommand.Execute(null);

        Assert.Equal(5, Assert.Single(f.Scheduler.ConcurrencyChanges));
        Assert.Equal(5, f.Scheduler.MaxConcurrency);
    }

    [Fact]
    public void Save_applies_segments_to_new_downloads_only_not_in_flight_ones()
    {
        var f = NewFixture();
        // The view-model shares the same DownloadDefaults instance the panel mutates.
        var vm = NewMainVm(f.Scheduler, f.Defaults);

        vm.NewUrl = "https://example.test/a.bin";
        vm.AddCommand.Execute(null);
        var firstSegments = f.Scheduler.Enqueued[^1].SegmentCount; // captured at enqueue (the "in-flight" one)

        f.Panel.SegmentsPerDownload = 3;
        f.Panel.SaveCommand.Execute(null);

        vm.NewUrl = "https://example.test/b.bin";
        vm.AddCommand.Execute(null);
        var secondSegments = f.Scheduler.Enqueued[^1].SegmentCount;

        Assert.Equal(8, firstSegments);  // the already-enqueued download keeps its parameters
        Assert.Equal(3, secondSegments); // the newly started one uses the new value
        Assert.Equal(3, f.Defaults.SegmentCount);
    }

    [Fact]
    public void Save_applies_threshold_to_the_shared_engine_options_for_new_downloads()
    {
        var f = NewFixture();

        f.Panel.SmallFileThresholdMiB = 32;
        f.Panel.SaveCommand.Execute(null);

        // The engine reads SmallFileThresholdBytes at download start, so mutating the shared instance
        // changes what the next-started download sees, without touching an in-flight one.
        Assert.Equal(32L * 1024 * 1024, f.Engine.SmallFileThresholdBytes);
    }

    [Fact]
    public void Save_applies_retry_and_timeout_to_the_next_attempt()
    {
        var f = NewFixture();

        f.Panel.MaxAttempts = 9;
        f.Panel.BaseDelaySeconds = 3;
        f.Panel.MaxDelaySeconds = 50;
        f.Panel.PerAttemptTimeoutSeconds = 42;
        f.Panel.SaveCommand.Execute(null);

        // RetryPolicy and the engine read these shared instances per attempt, so the change applies to the
        // next attempt — including for an in-flight download.
        Assert.Equal(9, f.Retry.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(3), f.Retry.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(50), f.Retry.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(42), f.Engine.PerAttemptTimeout);

        var policy = new RetryPolicy(f.Retry);
        Assert.True(policy.ShouldRetry(8));   // attempts raised to 9
        Assert.False(policy.ShouldRetry(9));
    }

    // ---- Schedule via the TimePicker (ADR-0023): persisted shape unchanged by the control swap ----

    [Fact]
    public void Schedule_picker_values_round_trip_through_settings_json_as_unchanged_hh_mm_strings()
    {
        var f = NewFixture();

        f.Panel.ScheduleEnabled = true;
        f.Panel.ScheduleStart = new TimeSpan(23, 0, 0); // picker produces a TimeSpan
        f.Panel.ScheduleStop = new TimeSpan(6, 30, 0);  // overnight window
        f.Panel.SaveCommand.Execute(null);

        // settings.json shape is unchanged: still HH:mm strings.
        var raw = SettingsStore.ReadRaw(SettingsPath);
        Assert.True(raw.Schedule.Enabled);
        Assert.Equal("23:00", raw.Schedule.Start);
        Assert.Equal("06:30", raw.Schedule.Stop);

        // Applied live to the shared ScheduleOptions, and re-resolvable to the same times.
        Assert.True(f.Schedule.Enabled);
        Assert.Equal(new TimeOnly(23, 0), f.Schedule.Start);
        var resolved = SettingsStore.LoadOrCreate(SettingsPath, f.Logger, _dir);
        Assert.Equal(new TimeOnly(6, 30), resolved.Schedule.Stop);
    }

    [Fact]
    public void Picker_time_is_normalized_to_minute_precision_and_null_is_midnight()
    {
        var f = NewFixture();

        f.Panel.ScheduleEnabled = true;
        f.Panel.ScheduleStart = new TimeSpan(0, 9, 0, 45); // 09:00:45 — only valid times producible
        f.Panel.ScheduleStop = null;                        // → midnight
        f.Panel.SaveCommand.Execute(null);

        var raw = SettingsStore.ReadRaw(SettingsPath);
        Assert.Equal("09:00", raw.Schedule.Start); // seconds dropped — always a valid HH:mm
        Assert.Equal("00:00", raw.Schedule.Stop);
    }

    private static MainWindowViewModel NewMainVm(FakeUiScheduler scheduler, DownloadDefaults defaults) =>
        new(scheduler,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(),
            new FakeFilePicker(null),
            new FakeCredentialPrompt(null),
            new FakeImportDialog(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: Path.Combine(Path.GetTempPath(), "dlm-qsettings-vm", Guid.NewGuid().ToString("N")),
            defaults: defaults);
}