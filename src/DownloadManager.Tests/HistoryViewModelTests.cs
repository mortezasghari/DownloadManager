using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.Services;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 9 / ADR-0019: the history view-model behavior — terminal-only writes, newest-first display,
/// open/reveal through the launcher seam, and error-on-missing-file. Headless; the VM uses no Avalonia.
/// </summary>
public sealed class HistoryViewModelTests
{
    private static MainWindowViewModel NewVm(
        FakeUiScheduler scheduler, RecordingHistoryStore history, IFileLauncher? launcher = null) =>
        new(scheduler,
            new FakeTimeProvider(),
            new FakeFilePicker(null),
            new FakeCredentialPrompt(null),
            new FakeImportDialog(),
            NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: Path.Combine(Path.GetTempPath(), "dlm-hist-vm", Guid.NewGuid().ToString("N")),
            historyStore: history,
            fileLauncher: launcher ?? new FakeFileLauncher());

    private static async Task<DownloadItemViewModel> EnqueueAsync(MainWindowViewModel vm, string url)
    {
        vm.NewUrl = url;
        await vm.AddCurrentUrlAsync();
        return vm.Downloads[^1];
    }

    private static void SetStatus(FakeUiScheduler scheduler, DownloadItemViewModel item, DownloadStatus status) =>
        ((FakeDownloadHandle)scheduler.Find(item.Id)!).Status = status;

    [Theory]
    [InlineData(DownloadStatus.Completed, HistoryState.Completed)]
    [InlineData(DownloadStatus.Failed, HistoryState.Failed)]
    [InlineData(DownloadStatus.Canceled, HistoryState.Cancelled)]
    public async Task A_terminal_download_writes_one_history_record(DownloadStatus terminal, HistoryState expected)
    {
        var scheduler = new FakeUiScheduler();
        var history = new RecordingHistoryStore();
        var vm = NewVm(scheduler, history);
        var item = await EnqueueAsync(vm, "https://example.test/a.bin");

        SetStatus(scheduler, item, terminal);
        vm.Tick();

        var record = Assert.Single(history.Records);
        Assert.Equal(expected, record.State);
        Assert.Equal(item.Id.ToString(), record.Id);
        Assert.Equal(item.TargetPath, record.SavedPath); // the router-resolved final path
        Assert.Single(vm.History);
    }

    [Fact]
    public async Task A_terminal_download_is_recorded_exactly_once_across_ticks()
    {
        var scheduler = new FakeUiScheduler();
        var history = new RecordingHistoryStore();
        var vm = NewVm(scheduler, history);
        var item = await EnqueueAsync(vm, "https://example.test/a.bin");

        SetStatus(scheduler, item, DownloadStatus.Completed);
        vm.Tick();
        vm.Tick();
        vm.Tick();

        Assert.Single(history.Records);
        Assert.Single(vm.History);
    }

    [Fact]
    public async Task Progress_without_reaching_a_terminal_state_writes_no_history()
    {
        var scheduler = new FakeUiScheduler();
        var history = new RecordingHistoryStore();
        var vm = NewVm(scheduler, history);
        var item = await EnqueueAsync(vm, "https://example.test/a.bin");

        SetStatus(scheduler, item, DownloadStatus.Running);
        var handle = (FakeDownloadHandle)scheduler.Find(item.Id)!;
        handle.Progress = new DownloadProgress(500, 1000);
        vm.Tick();
        handle.Progress = new DownloadProgress(900, 1000);
        vm.Tick();

        Assert.Empty(history.Records);
        Assert.Empty(vm.History);
    }

    [Fact]
    public void Finished_view_orders_newest_first_from_the_chronological_store()
    {
        // Store holds records in append (chronological) order: oldest → newest.
        var history = new RecordingHistoryStore(
            HistoryRecord.From(DownloadId.New(), "old.bin", 1, HistoryState.Completed, "/d/old.bin"),
            HistoryRecord.From(DownloadId.New(), "mid.bin", 1, HistoryState.Completed, "/d/mid.bin"),
            HistoryRecord.From(DownloadId.New(), "new.bin", 1, HistoryState.Completed, "/d/new.bin"));

        var vm = NewVm(new FakeUiScheduler(), history);

        Assert.Equal(["new.bin", "mid.bin", "old.bin"], vm.History.Select(h => h.Name)); // newest-first
    }

    [Fact]
    public async Task A_new_terminal_record_appears_at_the_front_newest_first()
    {
        var history = new RecordingHistoryStore(
            HistoryRecord.From(DownloadId.New(), "old.bin", 1, HistoryState.Completed, "/d/old.bin"));
        var scheduler = new FakeUiScheduler();
        var vm = NewVm(scheduler, history);
        var item = await EnqueueAsync(vm, "https://example.test/fresh.bin");

        SetStatus(scheduler, item, DownloadStatus.Completed);
        vm.Tick();

        Assert.Equal("fresh.bin", vm.History[0].Name); // newest at the front
        Assert.Equal("old.bin", vm.History[1].Name);
    }

    [Fact]
    public void Open_file_invokes_the_launcher_with_the_saved_path()
    {
        var launcher = new FakeFileLauncher();
        var history = new RecordingHistoryStore(
            HistoryRecord.From(DownloadId.New(), "a.bin", 1, HistoryState.Completed, "/downloads/a.bin"));
        var vm = NewVm(new FakeUiScheduler(), history, launcher);

        vm.History[0].OpenFile();

        Assert.Equal("/downloads/a.bin", Assert.Single(launcher.OpenedFiles));
        Assert.Empty(launcher.RevealedPaths);
        Assert.Null(vm.HistoryError);
    }

    [Fact]
    public void Open_folder_invokes_the_launcher_reveal_with_the_saved_path()
    {
        var launcher = new FakeFileLauncher();
        var history = new RecordingHistoryStore(
            HistoryRecord.From(DownloadId.New(), "a.bin", 1, HistoryState.Completed, "/downloads/a.bin"));
        var vm = NewVm(new FakeUiScheduler(), history, launcher);

        vm.History[0].OpenFolder();

        Assert.Equal("/downloads/a.bin", Assert.Single(launcher.RevealedPaths)); // launcher derives the dir
        Assert.Empty(launcher.OpenedFiles);
    }

    [Fact]
    public void A_launcher_failure_surfaces_an_error_on_the_view_model_without_crashing()
    {
        var launcher = new FakeFileLauncher(LaunchResult.Failure("File no longer exists: /downloads/a.bin"));
        var history = new RecordingHistoryStore(
            HistoryRecord.From(DownloadId.New(), "a.bin", 1, HistoryState.Completed, "/downloads/a.bin"));
        var vm = NewVm(new FakeUiScheduler(), history, launcher);

        vm.History[0].OpenFile();

        Assert.NotNull(vm.HistoryError);
        Assert.Contains("no longer exists", vm.HistoryError, StringComparison.OrdinalIgnoreCase);
    }
}