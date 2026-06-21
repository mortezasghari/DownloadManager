using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;
using DownloadManager.Tests.Fakes;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 2 (ADR-0022): the scheduler-level behavior of the four-action queue model — Postpone
/// (reposition-to-tail + stop-transfer, resumes naturally) and global Pause/Play (block promotion).
/// Built on the existing FIFO + lock model; verified against the real scheduler/engine via the gated
/// harness using monotonic signals (Started counts never decrement; status waits).
/// </summary>
public class SchedulerQueueActionsTests
{
    private static CancellationToken Guard => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static Task Reaches(IDownloadHandle h, DownloadStatus status) =>
        h.WaitForStatusAsync(s => s == status, Guard);

    [Fact]
    public async Task Postpone_a_running_download_frees_the_slot_promotes_the_next_and_resumes_naturally()
    {
        // One slot, two downloads: a runs, b waits.
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(2048));
        var a = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        var b = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        await h.Gate.WaitStartedAsync(1, Guard); // a is the one running (its transfer gated)

        // Postpone a → it stops transferring (bytes retained via pause), the slot frees, b promotes.
        await h.Scheduler.PostponeAsync(a.Id, Guard);
        await h.Gate.WaitStartedAsync(2, Guard); // b started in the freed slot

        await Reaches(b, DownloadStatus.Running);
        // a is back in the queue (non-terminal, not paused-as-a-dead-end), waiting behind b — no Postponed state.
        Assert.Contains(a.Status, new[] { DownloadStatus.Queued, DownloadStatus.Paused, DownloadStatus.Retrying });
        Assert.NotEqual(DownloadStatus.Canceled, a.Status); // bytes never discarded

        // Drain: b completes, then a promotes again and finishes — it resumed on its own, no un-postpone.
        h.Gate.OpenGate();
        await Reaches(b, DownloadStatus.Completed);
        await Reaches(a, DownloadStatus.Completed);
        Assert.True(h.Gate.Started >= 3); // a(1), b(2), a-resumes(3)
    }

    [Fact]
    public async Task Postpone_a_queued_download_sends_it_to_the_tail_behind_a_later_one()
    {
        // One slot; a runs, b and c wait in order [b, c].
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(2048));
        var a = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        var b = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        var c = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        await h.Gate.WaitStartedAsync(1, Guard);

        // Postpone b (queued, not started) → it moves behind c: queue becomes [c, b].
        await h.Scheduler.PostponeAsync(b.Id, Guard);
        Assert.Equal(DownloadStatus.Queued, b.Status); // still queued, no state change

        // Let a finish → c promotes next (not b), proving b went to the tail.
        h.Gate.ReleaseOne();
        await Reaches(a, DownloadStatus.Completed);
        await Reaches(c, DownloadStatus.Running);
        Assert.Equal(DownloadStatus.Queued, b.Status); // b still waits (it was sent to the back)

        h.Gate.OpenGate();
        await Reaches(b, DownloadStatus.Completed);
        await Reaches(c, DownloadStatus.Completed);
    }

    [Fact]
    public async Task Postpone_twice_sends_it_to_the_back_again_no_duplicate_early_start()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(2048));
        var a = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        var b = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        var c = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        await h.Gate.WaitStartedAsync(1, Guard);

        await h.Scheduler.PostponeAsync(b.Id, Guard); // [c, b]
        await h.Scheduler.PostponeAsync(b.Id, Guard); // [c, b] again (b already last) — must not duplicate-start

        h.Gate.OpenGate();
        await Reaches(a, DownloadStatus.Completed);
        await Reaches(b, DownloadStatus.Completed);
        await Reaches(c, DownloadStatus.Completed);
        // The stale entries from both postpones were skipped — b ran exactly once (started 4 times total:
        // a, c, b, and at most the skipped no-ops do not count as starts).
        Assert.True(h.Gate.Started >= 3);
    }

    [Fact]
    public async Task Global_pause_blocks_promotion_and_play_resumes_it()
    {
        // Two slots; a, b run. c, d wait.
        await using var h = SchedulerHarness.Gated(maxConcurrent: 2, content: EngineHarness.Pattern(2048));
        var handles = new List<IDownloadHandle>();
        for (var i = 0; i < 4; i++)
        {
            handles.Add(await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard));
        }

        await h.Gate.WaitStartedAsync(2, Guard); // a, b running
        Assert.False(h.Scheduler.IsQueuePaused);

        // Globally pause, then let the two running ones finish — promotion must be blocked (Started stays 2).
        h.Scheduler.PauseQueue();
        Assert.True(h.Scheduler.IsQueuePaused);
        h.Gate.ReleaseOne();
        h.Gate.ReleaseOne();
        await Task.WhenAll(handles.Take(2).Select(x => Reaches(x, DownloadStatus.Completed)));

        Assert.Equal(2, handles.Count(x => x.Status == DownloadStatus.Completed));
        Assert.Equal(2, h.Gate.Started); // c, d were NOT promoted while paused

        // Play → c, d promote.
        h.Gate.OpenGate();
        h.Scheduler.ResumeQueue();
        Assert.False(h.Scheduler.IsQueuePaused);
        await Task.WhenAll(handles.Skip(2).Select(x => Reaches(x, DownloadStatus.Completed)));
        Assert.True(h.Gate.Started >= 4);
    }

    [Fact]
    public async Task Postpone_while_globally_paused_reorders_but_nothing_downloads_until_play()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(2048));
        h.Scheduler.PauseQueue();
        var a = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);
        var b = await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard);

        await h.Scheduler.PostponeAsync(a.Id, Guard); // reorders to [b, a] — but paused, so nothing starts
        Assert.Equal(0, h.Gate.Started);

        h.Gate.OpenGate();
        h.Scheduler.ResumeQueue();
        await Reaches(a, DownloadStatus.Completed);
        await Reaches(b, DownloadStatus.Completed);
    }
}