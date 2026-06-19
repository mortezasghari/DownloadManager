using DownloadManager.Core.Domain;
using DownloadManager.Tests.Fakes;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 6: delete-from-queue dispatches on state via the EXISTING Cancel path — Cancel for a running
/// download (state-machine teardown, durable-consistent), tombstone for a queued one (flip to terminal,
/// leave its id in the channel; the worker skips it on dequeue). No channel surgery, no new state.
/// </summary>
public class QueueDeleteTests
{
    // 30s guard matches the existing scheduler tests — generous headroom under heavy parallel CI load
    // on slower runners (this is a liveness deadline, not an expected duration).
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Fact]
    public async Task Delete_running_routes_to_cancel_and_tears_down_durably()
    {
        await using var harness = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(64 * 1024));
        var request = harness.NewRequest();
        var handle = await harness.Scheduler.EnqueueAsync(request);
        await harness.Gate.WaitStartedAsync(1, Timeout()); // running: work request is blocked at the gate

        await harness.Scheduler.CancelAsync(request.Id);   // delete a RUNNING download
        // Do NOT open the gate: the run-token cancellation unblocks the gated request *as canceled*, so the
        // download tears down through the state machine. Opening the gate would race the (async) cancel
        // against completion of this tiny download — the bug a prior revision hit.

        await handle.WaitForStatusAsync(s => s == DownloadStatus.Canceled, Timeout());
        Assert.Equal(DownloadStatus.Canceled, handle.Status);
        // The existing Cancel path discarded partial state durably — no target, no sidecars.
        Assert.False(harness.SidecarsOrTargetExist(request));
    }

    [Fact]
    public async Task Delete_queued_tombstones_and_the_worker_skips_it_on_dequeue()
    {
        await using var harness = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(64 * 1024));

        // One worker: r1 occupies it (blocked at the gate); r2 and r3 wait in the channel.
        var r1 = harness.NewRequest();
        var h1 = await harness.Scheduler.EnqueueAsync(r1);
        await harness.Gate.WaitStartedAsync(1, Timeout());

        var r2 = harness.NewRequest();
        var h2 = await harness.Scheduler.EnqueueAsync(r2); // queued, not started
        var r3 = harness.NewRequest();
        var h3 = await harness.Scheduler.EnqueueAsync(r3); // sentinel queued behind r2

        await harness.Scheduler.CancelAsync(r2.Id);        // delete the QUEUED one -> tombstone (Canceled)
        await h2.WaitForStatusAsync(s => s == DownloadStatus.Canceled, Timeout());

        harness.Gate.OpenGate();                           // r1 finishes; worker advances past r2 to r3
        await h1.WaitForStatusAsync(s => s == DownloadStatus.Completed, Timeout());
        await h3.WaitForStatusAsync(s => s == DownloadStatus.Completed, Timeout());

        // r2 was tombstoned and skipped on dequeue: it never started, produced no file...
        Assert.Equal(DownloadStatus.Canceled, h2.Status);
        Assert.False(File.Exists(r2.TargetPath));
        // ...and deleting it did not affect the others.
        Assert.Equal(DownloadStatus.Completed, h1.Status);
        Assert.Equal(DownloadStatus.Completed, h3.Status);
    }
}