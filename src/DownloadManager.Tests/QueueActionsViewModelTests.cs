using DownloadManager.Core.Domain;
using DownloadManager.Core.Lifecycle;
using DownloadManager.Persistence.Lifecycle;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 2 (ADR-0022) — the four-action queue model at the orchestration (view-model) layer: global
/// Pause/Play, Postpone, Stop, Re-add, all as append-event-first lifecycle operations. The scheduler-level
/// behavior of Postpone/Pause is covered separately by <see cref="SchedulerQueueActionsTests"/>. Headless.
/// </summary>
public sealed class QueueActionsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dlm-qactions", Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, "queue.log");

    public QueueActionsViewModelTests() => Directory.CreateDirectory(_dir);

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

    private JsonLifecycleLog NewLog() => new(LogPath, NullLogger<JsonLifecycleLog>.Instance);

    private IReadOnlyList<LifecycleEvent> ReadAll()
    {
        using var log = NewLog();
        return log.ReadAll();
    }

    private MainWindowViewModel NewVm(FakeUiScheduler scheduler, JsonLifecycleLog log, RecordingHistoryStore? history = null) =>
        new(scheduler,
            new FakeTimeProvider(),
            new FakeFilePicker(null),
            new FakeCredentialPrompt(null),
            new FakeImportDialog(),
            NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: _dir,
            historyStore: history,
            lifecycleLog: log);

    private static async Task<DownloadItemViewModel> EnqueueAsync(MainWindowViewModel vm, string url)
    {
        vm.NewUrl = url;
        await vm.AddCurrentUrlAsync();
        return vm.Downloads[^1];
    }

    private static void SetStatus(FakeUiScheduler scheduler, DownloadItemViewModel item, DownloadStatus status) =>
        ((FakeDownloadHandle)scheduler.Find(item.Id)!).Status = status;

    // ----------------------------- Global Pause / Play -----------------------------

    [Fact]
    public async Task Global_pause_blocks_promotion_and_pauses_active_downloads_play_resumes_them()
    {
        var scheduler = new FakeUiScheduler();
        var vmLog = NewLog();
        var vm = NewVm(scheduler, vmLog);

        var running = await EnqueueAsync(vm, "https://example.test/run.bin");
        await EnqueueAsync(vm, "https://example.test/wait.bin"); // stays queued
        SetStatus(scheduler, running, DownloadStatus.Running);
        vm.Tick();

        await vm.ToggleQueuePauseAsync(); // PAUSE

        Assert.True(vm.IsQueuePaused);
        Assert.True(scheduler.IsQueuePaused);              // promotion blocked
        Assert.Equal(1, scheduler.QueuePauseCount);
        Assert.Contains(running.Id, scheduler.Paused);     // the active download was paused

        await vm.ToggleQueuePauseAsync(); // PLAY

        Assert.False(vm.IsQueuePaused);
        Assert.False(scheduler.IsQueuePaused);
        Assert.Equal(1, scheduler.QueueResumeCount);
        Assert.Contains(running.Id, scheduler.Resumed);    // the paused download was resumed
        vmLog.Dispose();
    }

    [Fact]
    public async Task Pause_play_does_not_stop_anything_terminally_items_survive_the_cycle()
    {
        var scheduler = new FakeUiScheduler();
        var vmLog = NewLog();
        var vm = NewVm(scheduler, vmLog);

        var a = await EnqueueAsync(vm, "https://example.test/a.bin");
        var b = await EnqueueAsync(vm, "https://example.test/b.bin");
        SetStatus(scheduler, a, DownloadStatus.Running);
        vm.Tick();

        await vm.ToggleQueuePauseAsync();
        await vm.ToggleQueuePauseAsync();

        Assert.Equal(2, vm.Downloads.Count);                 // nothing dropped
        Assert.DoesNotContain(a.Id, scheduler.Canceled);     // nothing stopped terminally
        Assert.DoesNotContain(b.Id, scheduler.Canceled);
        vmLog.Dispose();
    }

    // ----------------------------- Stop (terminal) -----------------------------

    [Fact]
    public async Task Stop_appends_the_event_first_then_the_download_leaves_the_queue_and_is_in_history()
    {
        var scheduler = new FakeUiScheduler();
        var history = new RecordingHistoryStore();
        var vmLog = NewLog();
        var vm = NewVm(scheduler, vmLog, history);
        var item = await EnqueueAsync(vm, "https://example.test/a.bin");
        SetStatus(scheduler, item, DownloadStatus.Running);
        vm.Tick();

        await vm.StopAsync(item);

        Assert.Contains(item.Id, scheduler.Canceled);        // dispatched via cancel
        // Terminal → leaves the queue and lands in history on the tick.
        SetStatus(scheduler, item, DownloadStatus.Canceled);
        vm.Tick();
        Assert.Empty(vm.Downloads);
        Assert.Single(history.Records);
        Assert.Single(vm.History);
        vmLog.Dispose();
    }

    [Fact]
    public async Task Stop_is_in_model_a_log_replay_places_the_stopped_download_in_history_only()
    {
        var scheduler = new FakeUiScheduler();
        var vmLog = NewLog();
        var vm = NewVm(scheduler, vmLog, new RecordingHistoryStore());
        var item = await EnqueueAsync(vm, "https://example.test/a.bin");

        await vm.StopAsync(item);
        vmLog.Dispose();

        var projection = QueueProjection.Reduce(ReadAll());
        Assert.Empty(projection.Active);                            // not active anymore
        Assert.Equal(item.Id.ToString(), Assert.Single(projection.History).Id); // history only
    }

    // ----------------------------- Postpone -----------------------------

    [Fact]
    public async Task Postpone_appends_a_requeue_event_first_then_reflects_via_the_scheduler()
    {
        var scheduler = new FakeUiScheduler();
        var vmLog = NewLog();
        var vm = NewVm(scheduler, vmLog);
        var item = await EnqueueAsync(vm, "https://example.test/a.bin");

        await vm.PostponeAsync(item);
        vmLog.Dispose();

        Assert.Contains(item.Id, scheduler.Postponed);        // reflected via the scheduler reposition
        // Append-event-first: the log has the initial Queued plus the postpone re-queue Queued event.
        var queuedEvents = ReadAll().Count(e => e.Id == item.Id.ToString() && e.Type == LifecycleEventType.Queued);
        Assert.Equal(2, queuedEvents);
        // Still active (non-terminal — no Postponed state).
        var projection = QueueProjection.Reduce(ReadAll());
        Assert.Equal(item.Id.ToString(), Assert.Single(projection.Active).Id.ToString());
        Assert.Empty(projection.History);
    }

    // ----------------------------- Interaction: actions while globally paused -----------------------------

    [Fact]
    public async Task Stop_postpone_and_re_add_work_while_globally_paused()
    {
        var scheduler = new FakeUiScheduler();
        var history = new RecordingHistoryStore();
        var vmLog = NewLog();
        var vm = NewVm(scheduler, vmLog, history);
        var toStop = await EnqueueAsync(vm, "https://example.test/stop.bin");
        var toPostpone = await EnqueueAsync(vm, "https://example.test/postpone.bin");

        await vm.ToggleQueuePauseAsync(); // PAUSE the whole queue
        Assert.True(vm.IsQueuePaused);

        // Stop while paused → terminal.
        await vm.StopAsync(toStop);
        Assert.Contains(toStop.Id, scheduler.Canceled);

        // Postpone while paused → reposition reflected; nothing promotes (the scheduler gate stays closed).
        await vm.PostponeAsync(toPostpone);
        Assert.Contains(toPostpone.Id, scheduler.Postponed);
        Assert.True(scheduler.IsQueuePaused); // still paused

        // Re-add from history while paused (re-add the stopped one).
        SetStatus(scheduler, toStop, DownloadStatus.Canceled);
        vm.Tick(); // toStop lands in history
        var enqueuedBefore = scheduler.Enqueued.Count;
        await vm.ReAddFromHistoryAsync(toStop.Id.ToString());
        Assert.Equal(enqueuedBefore + 1, scheduler.Enqueued.Count); // re-queued (new id), still paused
        Assert.True(vm.IsQueuePaused);
        vmLog.Dispose();
    }
}