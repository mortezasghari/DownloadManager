using System.Net;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;
using DownloadManager.Tests.Fakes;
using Xunit;

namespace DownloadManager.Tests;

public class SchedulerTests
{
    // Deadlock guard: tests are deterministic (gated handler / FakeTimeProvider); a hang fails here.
    private static CancellationToken Guard => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static Task Running(IDownloadHandle h) => h.WaitForStatusAsync(s => s == DownloadStatus.Running, Guard);

    private static Task Reaches(IDownloadHandle h, DownloadStatus status) =>
        h.WaitForStatusAsync(s => s == status, Guard);

    [Fact]
    public async Task Concurrency_gate_runs_at_most_max_and_a_completion_admits_exactly_one()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 2, content: EngineHarness.Pattern(2048));

        var handles = new List<IDownloadHandle>();
        for (var i = 0; i < 5; i++)
        {
            handles.Add(await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard));
        }

        await h.Gate.WaitStartedAsync(2, Guard);

        Assert.Equal(2, h.Gate.Started);   // never more than max-concurrent
        Assert.Equal(2, h.Gate.InFlight);
        Assert.Equal(2, handles.Count(x => x.Status == DownloadStatus.Running));
        Assert.Equal(3, handles.Count(x => x.Status == DownloadStatus.Queued));

        h.Gate.ReleaseOne(); // one completes -> frees a slot
        await h.Gate.WaitStartedAsync(3, Guard);

        Assert.Equal(3, h.Gate.Started);   // exactly one more admitted
        Assert.Equal(2, h.Gate.InFlight);

        h.Gate.OpenGate();
        foreach (var handle in handles)
        {
            await Reaches(handle, DownloadStatus.Completed);
        }
    }

    [Fact]
    public async Task Pause_then_resume_round_trips_through_persistence()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(4096));
        var req = h.NewRequest();
        var handle = await h.Scheduler.EnqueueAsync(req, Guard);

        await h.Gate.WaitStartedAsync(1, Guard); // running, blocked in the work request
        await h.Scheduler.PauseAsync(req.Id, Guard);
        await Reaches(handle, DownloadStatus.Paused);

        await h.Scheduler.ResumeAsync(req.Id, Guard);
        h.Gate.OpenGate();
        await Reaches(handle, DownloadStatus.Completed);

        Assert.Equal(h.Server.Content, h.ReadTarget(req));
        // Resume rehydrated from disk and revalidated with If-Range — not an in-memory hand-off.
        Assert.Contains(h.Server.Requests, r => r is { RangeFrom: 0, RangeTo: 0, IfRange: "\"v1\"" });
    }

    [Fact]
    public async Task Scheduled_run_resumes_only_incomplete_segments()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(40_000), smallFileThreshold: 1);
        var req = h.NewRequest(segmentCount: 4);

        // Segments 0 and 1 already durable on disk; only 2 and 3 should be fetched.
        await h.SeedCompletedSegmentsAsync(req, segmentCount: 4, 0, 1);

        var handle = await h.Scheduler.EnqueueAsync(req, Guard);
        h.Gate.OpenGate();
        await Reaches(handle, DownloadStatus.Completed);

        Assert.Equal(h.Server.Content, h.ReadTarget(req));

        var workStarts = h.Server.Requests.Where(r => r.RangeTo is > 0).Select(r => r.RangeFrom!.Value).ToList();
        Assert.Equal([20_000L, 30_000L], workStarts.OrderBy(x => x)); // only the incomplete segments
        Assert.Contains(h.Server.Requests, r => r is { RangeFrom: 0, RangeTo: 0, IfRange: "\"v1\"" });
    }

    [Fact]
    public async Task Cancel_during_multisegment_stops_siblings_discards_state_and_frees_the_slot()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(40_000), smallFileThreshold: 1);

        var reqA = h.NewRequest(segmentCount: 4);
        var handleA = await h.Scheduler.EnqueueAsync(reqA, Guard);
        await h.Gate.WaitStartedAsync(4, Guard); // all 4 segments in flight

        var reqB = h.NewRequest(segmentCount: 1);
        var handleB = await h.Scheduler.EnqueueAsync(reqB, Guard); // queued behind A

        await h.Scheduler.CancelAsync(reqA.Id, Guard);
        await Reaches(handleA, DownloadStatus.Canceled);

        // Siblings stopped and partial state discarded (cancel policy).
        Assert.False(h.SidecarsOrTargetExist(reqA));

        // Slot released -> B starts and completes.
        await Running(handleB);
        h.Gate.OpenGate();
        await Reaches(handleB, DownloadStatus.Completed);
        Assert.Equal(h.Server.Content, h.ReadTarget(reqB));
    }

    [Fact]
    public async Task Illegal_lifecycle_operations_are_rejected_loudly()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(2048));
        var req = h.NewRequest();
        var handle = await h.Scheduler.EnqueueAsync(req, Guard);
        h.Gate.OpenGate();
        await Reaches(handle, DownloadStatus.Completed);

        await Assert.ThrowsAsync<InvalidDownloadTransitionException>(() => h.Scheduler.ResumeAsync(req.Id));
        await Assert.ThrowsAsync<InvalidDownloadTransitionException>(() => h.Scheduler.PauseAsync(req.Id));
    }

    [Fact]
    public async Task Cancel_concurrent_with_resume_does_not_deadlock_or_corrupt_state()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(2048));
        var req = h.NewRequest();
        var handle = await h.Scheduler.EnqueueAsync(req, Guard);
        await h.Gate.WaitStartedAsync(1, Guard);
        await h.Scheduler.PauseAsync(req.Id, Guard);
        await Reaches(handle, DownloadStatus.Paused);

        var resume = Capture(() => h.Scheduler.ResumeAsync(req.Id));
        var cancel = Capture(() => h.Scheduler.CancelAsync(req.Id));
        var errors = await Task.WhenAll(resume, cancel); // no deadlock

        // At most one op is rejected as illegal; the rest succeed.
        Assert.True(errors.Count(e => e is InvalidDownloadTransitionException) <= 1);
        Assert.Contains(handle.Status, new[]
        {
            DownloadStatus.Queued, DownloadStatus.Running, DownloadStatus.Paused,
            DownloadStatus.Canceled, DownloadStatus.Completed,
        });

        h.Gate.OpenGate();
    }

    [Fact]
    public async Task Pause_concurrent_with_cancel_on_a_running_download_resolves_consistently()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(2048));
        var req = h.NewRequest();
        var handle = await h.Scheduler.EnqueueAsync(req, Guard);
        await h.Gate.WaitStartedAsync(1, Guard);

        var pause = Capture(() => h.Scheduler.PauseAsync(req.Id));
        var cancel = Capture(() => h.Scheduler.CancelAsync(req.Id));
        await Task.WhenAll(pause, cancel); // no deadlock

        h.Gate.OpenGate();
        await handle.WaitForStatusAsync(
            s => s is DownloadStatus.Canceled or DownloadStatus.Paused or DownloadStatus.Completed, Guard);
        Assert.Contains(handle.Status, new[] { DownloadStatus.Canceled, DownloadStatus.Paused, DownloadStatus.Completed });
    }

    [Fact]
    public async Task Cancelling_mid_backoff_returns_promptly_without_waiting_the_delay()
    {
        var retry = new RetryOptions { BaseDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromMinutes(5) };
        await using var h = SchedulerHarness.Plain(maxConcurrent: 1, content: EngineHarness.Pattern(2048), retry: retry);
        h.Server.WorkStatusOverride = HttpStatusCode.ServiceUnavailable; // transient

        var req = h.NewRequest();
        var handle = await h.Scheduler.EnqueueAsync(req, Guard);
        await Reaches(handle, DownloadStatus.Retrying); // entered backoff

        // Cancel WITHOUT advancing the FakeTimeProvider: if the backoff Task.Delay were awaited, this hangs.
        await h.Scheduler.CancelAsync(req.Id, Guard);
        await Reaches(handle, DownloadStatus.Canceled);
    }

    [Fact]
    public async Task A_download_in_backoff_still_counts_against_the_concurrency_gate()
    {
        var retry = new RetryOptions { BaseDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromMinutes(5) };
        await using var h = SchedulerHarness.Plain(maxConcurrent: 1, content: EngineHarness.Pattern(2048), retry: retry);
        h.Server.WorkStatusOverride = HttpStatusCode.ServiceUnavailable;

        var reqA = h.NewRequest();
        var handleA = await h.Scheduler.EnqueueAsync(reqA, Guard);
        await Reaches(handleA, DownloadStatus.Retrying); // A holds the only slot while backing off

        var reqB = h.NewRequest();
        var handleB = await h.Scheduler.EnqueueAsync(reqB, Guard);

        Assert.Equal(DownloadStatus.Queued, handleB.Status); // B cannot start: retry counts against the gate
        Assert.Equal(DownloadStatus.Retrying, handleA.Status);

        await h.Scheduler.CancelAsync(reqA.Id, Guard); // free the slot
        h.Server.WorkStatusOverride = null;            // let B succeed
        await Reaches(handleB, DownloadStatus.Completed);
    }

    private static Task<Exception?> Capture(Func<Task> action) => Task.Run(async () =>
    {
        try
        {
            await action();
            return (Exception?)null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    });
}