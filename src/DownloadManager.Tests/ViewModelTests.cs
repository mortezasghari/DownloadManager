using DownloadManager.Core.Domain;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 5: headless view-model logic — no Avalonia render. Speed/ETA smoothing, state-driven command
/// enablement, import-summary surfacing, and the NeedsCredentials resume flow.
/// </summary>
public class ViewModelTests
{
    private static DownloadRequest Request(string target = "/tmp/dlm-vm/file.bin") => new()
    {
        Id = DownloadId.New(),
        Url = new Uri("http://origin.test/file.bin"),
        TargetPath = target,
    };

    private static DownloadItemViewModel Item(
        FakeDownloadHandle handle, TimeProvider time, TimeSpan? window = null) =>
        new(Request(), handle, new FakeUiScheduler(), time,
            _ => Task.CompletedTask, _ => Task.CompletedTask, window ?? TimeSpan.FromSeconds(5));

    // ---- Speed / ETA smoothing ----

    [Fact]
    public void Smoother_returns_null_until_two_samples_then_averages_over_the_window()
    {
        var smoother = new SpeedSmoother(TimeSpan.FromSeconds(5));
        var t = DateTimeOffset.UnixEpoch;

        smoother.Add(t, 0);
        Assert.Null(smoother.BytesPerSecond()); // one sample: unknown

        smoother.Add(t + TimeSpan.FromSeconds(2), 400);
        Assert.Equal(200d, smoother.BytesPerSecond()); // 400 bytes / 2 s
    }

    [Fact]
    public void Smoother_reports_zero_when_stalled_not_negative()
    {
        var smoother = new SpeedSmoother(TimeSpan.FromSeconds(5));
        var t = DateTimeOffset.UnixEpoch;
        smoother.Add(t, 1000);
        smoother.Add(t + TimeSpan.FromSeconds(1), 1000); // no progress
        Assert.Equal(0d, smoother.BytesPerSecond());
    }

    [Fact]
    public void Item_computes_smoothed_speed_and_eta_from_progress_counters()
    {
        var time = new FakeTimeProvider();
        var handle = new FakeDownloadHandle
        {
            Status = DownloadStatus.Running,
            Progress = new DownloadProgress(0, 1_000_000),
        };
        var item = Item(handle, time);

        item.Refresh(); // sample 1 @ 0 bytes
        time.Advance(TimeSpan.FromSeconds(1));
        handle.Progress = new DownloadProgress(200_000, 1_000_000);
        item.Refresh(); // sample 2 @ 200_000 bytes, +1s

        Assert.Equal("195.3 KB/s", item.SpeedText); // 200_000 B/s
        Assert.Equal("00:04", item.EtaText);        // 800_000 / 200_000 = 4s
        Assert.Equal(20d, item.ProgressPercent);
    }

    [Fact]
    public void Item_shows_dashes_for_speed_and_eta_when_not_downloading()
    {
        var time = new FakeTimeProvider();
        var handle = new FakeDownloadHandle { Status = DownloadStatus.Paused, Progress = new DownloadProgress(50, 100) };
        var item = Item(handle, time);

        item.Refresh();

        Assert.Equal("—", item.SpeedText);
        Assert.Equal("—", item.EtaText);
    }

    [Fact]
    public void Item_is_indeterminate_for_unknown_size_and_eta_is_dash()
    {
        var time = new FakeTimeProvider();
        var handle = new FakeDownloadHandle { Status = DownloadStatus.Running, Progress = new DownloadProgress(1234, -1) };
        var item = Item(handle, time);

        item.Refresh();

        Assert.True(item.IsIndeterminate);
        Assert.Equal("—", item.EtaText);
    }

    // ---- Command enablement reflects the state machine ----

    [Theory]
    [InlineData(DownloadStatus.Queued, true, false, false, false)]
    [InlineData(DownloadStatus.Running, true, false, false, false)]
    [InlineData(DownloadStatus.Retrying, true, false, false, false)]
    [InlineData(DownloadStatus.Paused, false, true, false, false)]
    [InlineData(DownloadStatus.Completed, false, false, false, false)]
    public void Command_enablement_tracks_status(
        DownloadStatus status, bool canPause, bool canResume, bool canRetry, bool canReauth)
    {
        var time = new FakeTimeProvider();
        var handle = new FakeDownloadHandle { Status = status };
        var item = Item(handle, time);

        item.Refresh();

        Assert.Equal(canPause, item.CanPause);
        Assert.Equal(canResume, item.CanResume);
        Assert.Equal(canRetry, item.CanRetry);
        Assert.Equal(canReauth, item.CanReauthorize);
        Assert.Equal(canPause, item.PauseCommand.CanExecute(null));
        Assert.Equal(canResume, item.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public void Failed_without_credentials_offers_retry_not_reauthorize()
    {
        var time = new FakeTimeProvider();
        var handle = new FakeDownloadHandle { Status = DownloadStatus.Failed, NeedsCredentials = false };
        var item = Item(handle, time);

        item.Refresh();

        Assert.True(item.CanRetry);
        Assert.False(item.CanReauthorize);
        Assert.Equal("Failed", item.StatusText);
    }

    [Fact]
    public void Failed_needing_credentials_offers_reauthorize_not_retry()
    {
        var time = new FakeTimeProvider();
        var handle = new FakeDownloadHandle { Status = DownloadStatus.Failed, NeedsCredentials = true };
        var item = Item(handle, time);

        item.Refresh();

        Assert.True(item.CanReauthorize);
        Assert.False(item.CanRetry);
        Assert.Equal("Needs credentials", item.StatusText);
    }

    [Fact]
    public void Verifying_phase_is_surfaced_in_status_text()
    {
        var time = new FakeTimeProvider();
        var handle = new FakeDownloadHandle
        {
            Status = DownloadStatus.Running,
            Progress = new DownloadProgress(100, 100, DownloadPhase.Verifying),
        };
        var item = Item(handle, time);

        item.Refresh();

        Assert.Equal("Verifying…", item.StatusText);
    }

    // ---- Import summary surfacing ----

    [Fact]
    public async Task Import_enqueues_valid_urls_and_surfaces_skip_reasons()
    {
        var listPath = Path.Combine(Path.GetTempPath(), $"dlm-import-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(listPath,
            """
            https://example.test/a.bin
            ftp://nope.test/x
            https://example.test/a.bin
            not a url
            https://example.test/b.bin
            """);
        try
        {
            var scheduler = new FakeUiScheduler();
            var vm = NewMainVm(scheduler, filePath: listPath);

            await vm.ImportListAsync();

            Assert.Equal(2, vm.Downloads.Count);            // a, b
            Assert.Equal(2, scheduler.Enqueued.Count);
            Assert.Contains("Imported 2, skipped 3", vm.ImportSummary);
            Assert.Contains("duplicate URL", vm.ImportSummary);
            Assert.Contains("unsupported scheme", vm.ImportSummary);
            Assert.Contains("malformed URL", vm.ImportSummary);
        }
        finally
        {
            File.Delete(listPath);
        }
    }

    // ---- NeedsCredentials resume flow ----

    [Fact]
    public async Task Reauthorize_resumes_same_target_with_new_credentials_and_replaces_row()
    {
        var scheduler = new FakeUiScheduler();
        var creds = new DownloadCredentials { AuthorizationHeaders = ["Bearer FRESH"] };
        var vm = NewMainVm(scheduler, credentials: creds);

        // Enqueue one download, then drive it to Failed+NeedsCredentials.
        vm.NewUrl = "http://origin.test/file.bin";
        await vm.AddCurrentUrlAsync();
        var item = Assert.Single(vm.Downloads);
        var originalTarget = item.Request.TargetPath;
        ((FakeDownloadHandle)scheduler.Find(item.Id)!).Status = DownloadStatus.Failed;
        ((FakeDownloadHandle)scheduler.Find(item.Id)!).NeedsCredentials = true;
        item.Refresh();
        Assert.True(item.CanReauthorize);

        await vm.ReauthorizeAndResumeAsync(item);

        // A second enqueue happened, same target, carrying the fresh credentials; the row was replaced.
        Assert.Equal(2, scheduler.Enqueued.Count);
        var resumed = scheduler.Enqueued[^1];
        Assert.Equal(originalTarget, resumed.TargetPath);
        Assert.Equal(["Bearer FRESH"], resumed.Credentials.AuthorizationHeaders);
        var replacement = Assert.Single(vm.Downloads);
        Assert.NotEqual(item.Id, replacement.Id);
    }

    [Fact]
    public async Task Reauthorize_cancelled_prompt_does_not_enqueue()
    {
        var scheduler = new FakeUiScheduler();
        var vm = NewMainVm(scheduler, credentials: null); // prompt returns null = cancel

        vm.NewUrl = "http://origin.test/file.bin";
        await vm.AddCurrentUrlAsync();
        var item = Assert.Single(vm.Downloads);

        await vm.ReauthorizeAndResumeAsync(item);

        Assert.Single(scheduler.Enqueued);      // only the original
        Assert.Same(item, Assert.Single(vm.Downloads));
    }

    [Fact]
    public async Task Delete_routes_a_live_download_through_the_scheduler_cancel_path_and_drops_the_row()
    {
        var scheduler = new FakeUiScheduler();
        var vm = NewMainVm(scheduler);
        vm.NewUrl = "http://origin.test/file.bin";
        await vm.AddCurrentUrlAsync();
        var item = Assert.Single(vm.Downloads);
        ((FakeDownloadHandle)scheduler.Find(item.Id)!).Status = DownloadStatus.Running;
        item.Refresh();

        await vm.RemoveAsync(item);

        Assert.Contains(item.Id, scheduler.Canceled); // dispatched via the existing Cancel path
        Assert.Empty(vm.Downloads);                    // row dropped
    }

    [Fact]
    public async Task Import_dialog_add_path_enqueues_through_the_normal_queue()
    {
        var scheduler = new FakeUiScheduler();
        var dialog = new FakeImportDialog();
        var vm = new MainWindowViewModel(
            scheduler, new FakeTimeProvider(), new FakeFilePicker(null), new FakeCredentialPrompt(null),
            dialog, NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: Path.Combine(Path.GetTempPath(), "dlm-vm-tests", Guid.NewGuid().ToString("N")));

        vm.ImportDialogCommand.Execute(null); // FakeImportDialog.ShowAsync completes synchronously
        Assert.NotNull(dialog.CapturedAddToQueue);

        // Drive the captured add-path as the dialog would on "Add to Queue".
        await dialog.CapturedAddToQueue!([new Uri("https://example.test/x"), new Uri("https://example.test/y")]);

        Assert.Equal(2, vm.Downloads.Count);
        Assert.Equal(2, scheduler.Enqueued.Count);
    }

    [Fact]
    public void Add_command_disabled_for_non_http_input()
    {
        var vm = NewMainVm(new FakeUiScheduler());

        vm.NewUrl = "ftp://nope/x";
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.NewUrl = "https://ok.test/a";
        Assert.True(vm.AddCommand.CanExecute(null));
    }

    private static MainWindowViewModel NewMainVm(
        FakeUiScheduler scheduler, string? filePath = null, DownloadCredentials? credentials = null) =>
        new(scheduler,
            new FakeTimeProvider(),
            new FakeFilePicker(filePath),
            new FakeCredentialPrompt(credentials),
            new FakeImportDialog(),
            NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: Path.Combine(Path.GetTempPath(), "dlm-vm-tests", Guid.NewGuid().ToString("N")));
}