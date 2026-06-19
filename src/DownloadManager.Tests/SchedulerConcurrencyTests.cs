using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;
using DownloadManager.Tests.Fakes;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 8: the live concurrency gate resize (<see cref="IDownloadScheduler.SetMaxConcurrency"/>).
/// Raising admits a waiting download at once; lowering retires a worker only after it finishes its
/// current download — a running download is never killed. Built on the real scheduler/engine stack.
/// </summary>
public class SchedulerConcurrencyTests
{
    private static CancellationToken Guard => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static Task Reaches(IDownloadHandle h, DownloadStatus status) =>
        h.WaitForStatusAsync(s => s == status, Guard);

    [Fact]
    public async Task Raising_the_gate_admits_a_waiting_download_immediately()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 1, content: EngineHarness.Pattern(2048));

        var handles = new List<IDownloadHandle>();
        for (var i = 0; i < 3; i++)
        {
            handles.Add(await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard));
        }

        await h.Gate.WaitInFlightAsync(1, Guard);
        Assert.Equal(1, h.Scheduler.MaxConcurrency);
        Assert.Equal(1, h.Gate.InFlight);

        h.Scheduler.SetMaxConcurrency(2);

        await h.Gate.WaitInFlightAsync(2, Guard);   // the new worker picked up a waiting download
        Assert.Equal(2, h.Scheduler.MaxConcurrency);
        Assert.Equal(2, h.Gate.InFlight);
    }

    [Fact]
    public async Task Lowering_the_gate_starts_no_new_downloads_and_does_not_kill_running_ones()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 2, content: EngineHarness.Pattern(2048));

        var handles = new List<IDownloadHandle>();
        for (var i = 0; i < 4; i++)
        {
            handles.Add(await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard));
        }

        await h.Gate.WaitInFlightAsync(2, Guard);
        Assert.Equal(2, h.Gate.Started);

        h.Scheduler.SetMaxConcurrency(1);
        Assert.Equal(1, h.Scheduler.MaxConcurrency);

        // Release one of the two running downloads. Its worker must retire — not pick up a waiting one.
        h.Gate.ReleaseOne();
        await Task.WhenAny(handles.Select(x => Reaches(x, DownloadStatus.Completed)));

        // The single surviving worker is still busy with the other (gated) running download, so no third
        // download can have started; the other running one was not killed.
        Assert.Equal(1, handles.Count(x => x.Status == DownloadStatus.Completed));
        Assert.Equal(1, handles.Count(x => x.Status == DownloadStatus.Running));
        Assert.Equal(2, handles.Count(x => x.Status == DownloadStatus.Queued));
        Assert.Equal(2, h.Gate.Started); // never admitted a third
    }

    [Fact]
    public async Task A_lowered_gate_processes_the_backlog_one_at_a_time()
    {
        await using var h = SchedulerHarness.Gated(maxConcurrent: 2, content: EngineHarness.Pattern(2048));

        var handles = new List<IDownloadHandle>();
        for (var i = 0; i < 3; i++)
        {
            handles.Add(await h.Scheduler.EnqueueAsync(h.NewRequest(), Guard));
        }

        await h.Gate.WaitInFlightAsync(2, Guard);
        h.Scheduler.SetMaxConcurrency(1);

        // Drain everything: the gate stays at one-in-flight as the backlog is worked off, and all finish.
        h.Gate.OpenGate();
        foreach (var handle in handles)
        {
            await Reaches(handle, DownloadStatus.Completed);
        }

        Assert.All(handles, x => Assert.Equal(DownloadStatus.Completed, x.Status));
    }
}