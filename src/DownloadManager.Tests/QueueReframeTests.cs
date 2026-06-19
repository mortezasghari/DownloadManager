using DownloadManager.Core.Domain;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 8 Part A: the queue-first reframe. The home list is the queue, partitioned so a running download
/// is visually separable from one merely waiting. The buckets are built from the per-handle rows (the
/// handle dictionary), never by draining the channel. Headless.
/// </summary>
public sealed class QueueReframeTests
{
    private static MainWindowViewModel NewVm(FakeUiScheduler scheduler) =>
        new(scheduler,
            new FakeTimeProvider(),
            new FakeFilePicker(null),
            new FakeCredentialPrompt(null),
            new FakeImportDialog(),
            NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: Path.Combine(Path.GetTempPath(), "dlm-reframe", Guid.NewGuid().ToString("N")));

    private static async Task<DownloadItemViewModel> EnqueueAsync(MainWindowViewModel vm, string url)
    {
        vm.NewUrl = url;
        await vm.AddCurrentUrlAsync();
        return vm.Downloads[^1];
    }

    [Fact]
    public async Task A_newly_added_download_lands_in_the_waiting_section()
    {
        var scheduler = new FakeUiScheduler();
        var vm = NewVm(scheduler);

        await EnqueueAsync(vm, "https://example.test/a.bin");

        Assert.Single(vm.Waiting);
        Assert.Empty(vm.Running);
        Assert.True(vm.HasWaiting);
        Assert.False(vm.HasRunning);
    }

    [Fact]
    public async Task Running_and_waiting_downloads_are_in_separate_sections_after_a_tick()
    {
        var scheduler = new FakeUiScheduler();
        var vm = NewVm(scheduler);

        var a = await EnqueueAsync(vm, "https://example.test/a.bin");
        await EnqueueAsync(vm, "https://example.test/b.bin");

        // The list reflects handle state read from the scheduler's handle dictionary, not the channel.
        ((FakeDownloadHandle)scheduler.Find(a.Id)!).Status = DownloadStatus.Running;
        vm.Tick();

        Assert.Equal(a.Id, Assert.Single(vm.Running).Id);
        Assert.Single(vm.Waiting);
        Assert.Equal(2, vm.Downloads.Count); // master list keeps everything
        Assert.Equal(DownloadStatus.Running, a.Status);
    }

    [Fact]
    public async Task A_completed_download_moves_to_the_finished_section()
    {
        var scheduler = new FakeUiScheduler();
        var vm = NewVm(scheduler);

        var a = await EnqueueAsync(vm, "https://example.test/a.bin");
        ((FakeDownloadHandle)scheduler.Find(a.Id)!).Status = DownloadStatus.Running;
        vm.Tick();
        Assert.Single(vm.Running);

        ((FakeDownloadHandle)scheduler.Find(a.Id)!).Status = DownloadStatus.Completed;
        vm.Tick();

        Assert.Empty(vm.Running);
        Assert.Empty(vm.Waiting);
        Assert.Single(vm.Finished);
        Assert.True(vm.HasFinished);
    }

    [Fact]
    public async Task Deleting_a_row_removes_it_from_its_section_and_the_master_list()
    {
        var scheduler = new FakeUiScheduler();
        var vm = NewVm(scheduler);

        var a = await EnqueueAsync(vm, "https://example.test/a.bin");
        Assert.Single(vm.Waiting);

        await vm.RemoveAsync(a);

        Assert.Empty(vm.Waiting);
        Assert.Empty(vm.Downloads);
        Assert.False(vm.HasWaiting);
    }
}