using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Regression coverage for the control-plane CTS hazards (ADR-0010): no <c>CancellationTokenSource</c>
/// operation runs while <c>_gate</c> is held, so neither a blocking token callback nor a concurrent
/// dispose/recreate can stall or fault the control plane.
/// </summary>
public class DeadlockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static DownloadHandle RunningHandle(out CancellationToken token, CancellationToken shutdown = default)
    {
        var handle = new DownloadHandle(
            new DownloadRequest
            {
                Id = DownloadId.New(),
                Url = new Uri("http://example/"),
                TargetPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            },
            shutdown);
        Assert.True(handle.TryBeginRun(out token));
        return handle;
    }

    [Fact]
    public async Task Cancel_does_not_stall_when_a_token_callback_blocks_on_an_external_lock()
    {
        var handle = RunningHandle(out var token);

        // A registered callback that blocks until released. Pre-fix, Cancel() ran this synchronously
        // under _gate and stalled; post-fix the cancellation is armed on a pool thread, so Cancel()
        // returns immediately and the callback blocks harmlessly off the control-plane lock.
        using var callbackGate = new SemaphoreSlim(0, 1);
        using var registration = token.Register(() => callbackGate.Wait());

        var cancelTask = Task.Run(() => handle.Cancel());
        try
        {
            // The assertion: Cancel() completes promptly while the callback is still blocked.
            // WaitAsync throws TimeoutException (failing the test) if Cancel() stalled under the lock.
            await cancelTask.WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Cancel() stalled while a token callback held an external lock — CTS op under _gate.");
        }
        finally
        {
            callbackGate.Release(); // let the pool-thread callback finish
        }
    }

    [Fact]
    public async Task Cancel_completes_quickly_when_callbacks_are_fast()
    {
        var handle = RunningHandle(out var token);
        using var registration = token.Register(() => { /* fast, non-blocking */ });

        var cancelTask = Task.Run(() => handle.Cancel());
        await cancelTask.WaitAsync(Timeout); // throws (fails) if it stalls
    }

    [Fact]
    public async Task Dispose_concurrent_with_an_in_flight_cancel_does_not_stall_or_fault()
    {
        // Hammer the widened window: Cancel() arms cancellation on a pool thread while another thread
        // disposes the same run CTS. No stall, no ObjectDisposedException must escape either call.
        for (var i = 0; i < 200; i++)
        {
            var handle = RunningHandle(out var token);
            using var registration = token.Register(() => Thread.SpinWait(50)); // keep a callback briefly in flight

            var cancel = Task.Run(() => handle.Cancel());
            var dispose = Task.Run(handle.DisposeRunCts);

            await Task.WhenAll(cancel, dispose).WaitAsync(Timeout); // surfaces both stalls and exceptions
            handle.Dispose();
        }
    }

    [Fact]
    public async Task Full_dispose_concurrent_with_an_in_flight_cancel_does_not_stall_or_fault()
    {
        for (var i = 0; i < 200; i++)
        {
            var handle = RunningHandle(out var token);
            using var registration = token.Register(() => Thread.SpinWait(50));

            var cancel = Task.Run(() => handle.Cancel());
            var dispose = Task.Run(handle.Dispose);

            await Task.WhenAll(cancel, dispose).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Recreate_run_token_concurrent_with_an_in_flight_cancel_does_not_stall_or_fault()
    {
        // BeginBackoff() drives RecreateRunToken(), which disposes the superseded CTS — concurrently
        // with a Cancel() that captured that same CTS. Exercises single-owner disposal + guarded signal.
        for (var i = 0; i < 200; i++)
        {
            var handle = RunningHandle(out var token);
            using var registration = token.Register(() => Thread.SpinWait(50));

            var cancel = Task.Run(() => handle.Cancel());
            var recreate = Task.Run(() => handle.BeginBackoff()); // Running -> Retrying, recreates the token

            await Task.WhenAll(cancel, recreate).WaitAsync(Timeout);
            handle.Dispose();
        }
    }
}