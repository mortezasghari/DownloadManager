using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;
using Xunit;

namespace DownloadManager.Tests;

public class StateMachineTests
{
    private static DownloadHandle NewHandle() => new(
        new DownloadRequest { Id = DownloadId.New(), Url = new Uri("http://x/y"), TargetPath = "/tmp/x.bin" },
        CancellationToken.None);

    [Fact]
    public void Pause_from_queued_goes_to_paused()
    {
        var h = NewHandle();
        h.Pause();
        Assert.Equal(DownloadStatus.Paused, h.Status);
    }

    [Fact]
    public void Resume_from_paused_goes_to_queued()
    {
        var h = NewHandle();
        h.Pause();
        h.Resume();
        Assert.Equal(DownloadStatus.Queued, h.Status);
    }

    [Fact]
    public void Cancel_from_queued_is_direct_and_returns_discard()
    {
        var h = NewHandle();
        Assert.True(h.Cancel());
        Assert.Equal(DownloadStatus.Canceled, h.Status);
    }

    [Fact]
    public void Cancel_while_running_signals_and_worker_finishes_the_transition()
    {
        var h = NewHandle();
        Assert.True(h.TryBeginRun(out _));
        Assert.False(h.Cancel()); // signalled, not direct
        Assert.True(h.CancelRequested);
        Assert.Equal(DownloadStatus.Running, h.Status); // worker still owns the transition
    }

    [Fact]
    public void TryBeginRun_skips_a_download_paused_while_queued()
    {
        var h = NewHandle();
        h.Pause(); // Queued -> Paused
        Assert.False(h.TryBeginRun(out _));
        Assert.Equal(DownloadStatus.Paused, h.Status);
    }

    [Fact]
    public void Resume_of_a_completed_download_is_rejected()
    {
        var h = NewHandle();
        h.TryBeginRun(out _);
        h.CompleteRun();
        Assert.Throws<InvalidDownloadTransitionException>(h.Resume);
        Assert.Throws<InvalidDownloadTransitionException>(h.Pause);
        Assert.Throws<InvalidDownloadTransitionException>(() => h.Cancel());
    }

    [Fact]
    public void Pause_of_a_canceled_download_is_rejected()
    {
        var h = NewHandle();
        h.Cancel();
        Assert.Throws<InvalidDownloadTransitionException>(h.Pause);
        Assert.Throws<InvalidDownloadTransitionException>(h.Resume);
    }

    [Fact]
    public void Retry_is_only_legal_from_failed()
    {
        var h = NewHandle();
        Assert.Throws<InvalidDownloadTransitionException>(h.Retry); // from Queued

        h.TryBeginRun(out _);
        h.FailRun();
        h.Retry(); // Failed -> Queued
        Assert.Equal(DownloadStatus.Queued, h.Status);
    }

    [Fact]
    public async Task WaitForStatusAsync_completes_on_transition()
    {
        var h = NewHandle();
        var wait = h.WaitForStatusAsync(s => s == DownloadStatus.Paused);
        Assert.False(wait.IsCompleted);
        h.Pause();
        await wait; // completes
    }
}