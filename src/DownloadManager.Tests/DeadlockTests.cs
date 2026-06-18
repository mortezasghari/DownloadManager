using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;
using Xunit;

namespace DownloadManager.Tests
{
    public class DeadlockTests
    {
        [Fact]
        public async Task CancelBlocksWhenCallbackHoldsExternalLock()
        {
            var request = new DownloadRequest
            {
                Id = DownloadId.New(),
                Url = new Uri("http://example/"),
                TargetPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            };

            using var shutdown = new CancellationTokenSource();
            var handle = new DownloadHandle(request, shutdown.Token);

            Assert.True(handle.TryBeginRun(out var token));

            var externalLock = new object();

            // Callback attempts to acquire an external lock. If Cancel() invokes callbacks
            // synchronously while the caller holds that lock, Cancel will block until the
            // lock is released — reproducing the problematic blocking behaviour.
            token.Register(() => { lock (externalLock) { /* no-op */ } });

            Task cancelTask;
            lock (externalLock)
            {
                // Start the cancel on a thread-pool thread while we hold the external lock.
                cancelTask = Task.Run(() => handle.Cancel());

                // Sleep while holding the external lock so the cancellation callback (which
                // attempts to take the external lock) will block if Cancel invokes callbacks
                // synchronously on the cancelling thread.
                Thread.Sleep(200);

                // The correct behavior is for Cancel() to NOT be blocked by callbacks that
                // attempt to acquire unrelated external locks. Assert that Cancel completed
                // promptly even while we hold the external lock. This assertion will FAIL
                // with the current implementation and should pass once the bug is fixed.
                Assert.True(cancelTask.IsCompleted, "Cancel should complete promptly even if callbacks block on external locks.");
            }

            // After releasing the external lock the cancellation should complete promptly.
            var winner2 = await Task.WhenAny(cancelTask, Task.Delay(1000));
            Assert.Equal(cancelTask, winner2);
            Assert.True(cancelTask.IsCompleted, "Cancel did not complete after external lock was released.");
        }

        [Fact]
        public async Task CancelCompletesQuicklyWhenCallbacksAreFast()
        {
            var request = new DownloadRequest
            {
                Id = DownloadId.New(),
                Url = new Uri("http://example/"),
                TargetPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            };

            using var shutdown = new CancellationTokenSource();
            var handle = new DownloadHandle(request, shutdown.Token);

            Assert.True(handle.TryBeginRun(out var token));

            // Fast callback: should not block Cancel.
            token.Register(() => { /* quick, non-blocking callback */ });

            var cancelTask = Task.Run(() => handle.Cancel());
            var winner = await Task.WhenAny(cancelTask, Task.Delay(500));
            Assert.Equal(cancelTask, winner);
        }
    }
}


