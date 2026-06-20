using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Lifecycle;
using DownloadManager.Persistence.Lifecycle;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Bug fix: a terminal download (Completed / Stopped / Failed) must LEAVE the active queue and appear only
/// in history — because it's terminal, mirroring the projection (ADR-0021). The live view drops it; a log
/// replay places it in history only. Red against the pre-fix code (terminal stayed in the queue). Headless.
/// </summary>
public sealed class TerminalLeavesQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dlm-terminal", Guid.NewGuid().ToString("N"));

    public TerminalLeavesQueueTests() => Directory.CreateDirectory(_dir);

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

    private MainWindowViewModel NewVm(FakeUiScheduler scheduler, RecordingHistoryStore history) =>
        new(scheduler,
            new FakeTimeProvider(),
            new FakeFilePicker(null),
            new FakeCredentialPrompt(null),
            new FakeImportDialog(),
            NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: _dir,
            historyStore: history);

    private static async Task<DownloadItemViewModel> EnqueueAsync(MainWindowViewModel vm, string url)
    {
        vm.NewUrl = url;
        await vm.AddCurrentUrlAsync();
        return vm.Downloads[^1];
    }

    private static void SetStatus(FakeUiScheduler scheduler, DownloadItemViewModel item, DownloadStatus status) =>
        ((FakeDownloadHandle)scheduler.Find(item.Id)!).Status = status;

    private static bool InQueue(MainWindowViewModel vm, DownloadItemViewModel item) =>
        vm.Downloads.Contains(item) || vm.Running.Contains(item) || vm.Waiting.Contains(item) || vm.Paused.Contains(item);

    [Theory]
    [InlineData(DownloadStatus.Completed, HistoryState.Completed)]
    [InlineData(DownloadStatus.Failed, HistoryState.Failed)]
    [InlineData(DownloadStatus.Canceled, HistoryState.Cancelled)] // user Stop → terminal "Stopped" history
    public async Task A_terminal_download_leaves_the_queue_and_appears_only_in_history(
        DownloadStatus terminal, HistoryState expected)
    {
        var scheduler = new FakeUiScheduler();
        var history = new RecordingHistoryStore();
        var vm = NewVm(scheduler, history);
        var item = await EnqueueAsync(vm, "https://example.test/a.bin");

        SetStatus(scheduler, item, DownloadStatus.Running);
        vm.Tick();
        Assert.True(InQueue(vm, item)); // running → in the queue

        SetStatus(scheduler, item, terminal);
        vm.Tick();

        // Absent from the active queue (every section + the master list)…
        Assert.False(InQueue(vm, item));
        Assert.Empty(vm.Running);
        Assert.Empty(vm.Waiting);
        Assert.Empty(vm.Paused);
        Assert.False(vm.HasRunning);
        // …and present in history with the right state.
        var record = Assert.Single(history.Records);
        Assert.Equal(expected, record.State);
        Assert.Equal(item.Id.ToString(), record.Id);
        Assert.Single(vm.History);
    }

    [Fact]
    public async Task A_running_or_queued_download_remains_in_the_active_queue()
    {
        var scheduler = new FakeUiScheduler();
        var history = new RecordingHistoryStore();
        var vm = NewVm(scheduler, history);

        var queued = await EnqueueAsync(vm, "https://example.test/queued.bin");
        var running = await EnqueueAsync(vm, "https://example.test/running.bin");
        SetStatus(scheduler, running, DownloadStatus.Running);

        vm.Tick();

        Assert.True(InQueue(vm, queued));  // queued stays
        Assert.True(InQueue(vm, running)); // running stays
        Assert.Equal(2, vm.Downloads.Count);
        Assert.Single(vm.Running);
        Assert.Single(vm.Waiting);
        Assert.Empty(history.Records); // nothing terminal → nothing in history
    }

    [Fact]
    public void After_completion_a_log_replay_places_the_download_in_history_only_not_the_queue()
    {
        // Proves the fix is in the projection model, not a transient UI patch: a Queued then Completed log
        // reduces to history-only on restart.
        var id = DownloadId.New().ToString();
        var logPath = Path.Combine(_dir, "queue.log");
        using (var log = new JsonLifecycleLog(logPath, NullLogger<JsonLifecycleLog>.Instance))
        {
            log.Append(new LifecycleEvent
            {
                Id = id, Type = LifecycleEventType.Queued,
                Url = "https://example.test/a.bin", TargetPath = Path.Combine(_dir, "a.bin"), SegmentCount = 4,
            });
            log.Append(new LifecycleEvent
            {
                Id = id, Type = LifecycleEventType.Completed,
                Url = "https://example.test/a.bin", TargetPath = Path.Combine(_dir, "a.bin"),
                Name = "a.bin", Size = 123,
            });
        }

        QueueProjection.Result projection;
        using (var log = new JsonLifecycleLog(logPath, NullLogger<JsonLifecycleLog>.Instance))
        {
            projection = QueueProjection.Reduce(log.ReadAll());
        }

        Assert.Empty(projection.Active);                  // not re-enqueued on restart
        Assert.Equal(id, Assert.Single(projection.History).Id); // history only
    }
}