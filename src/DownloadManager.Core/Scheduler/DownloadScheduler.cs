using System.Collections.Concurrent;
using System.Threading.Channels;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Core.Scheduler;

/// <summary>
/// Owns the lifecycle of every download (spec §8). A bounded <see cref="Channel{T}"/> holds the
/// queue; a fixed pool of <c>MaxConcurrentDownloads</c> workers drains it, which IS the global
/// concurrency gate — a worker stays occupied through a download's retry backoff, so retries count
/// against the gate. Control operations mutate the per-download <see cref="DownloadHandle"/> under its
/// lock; workers observe the result. Nothing runs except through here.
/// </summary>
public sealed partial class DownloadScheduler : IDownloadScheduler
{
    private readonly IDownloadEngine _engine;
    private readonly RetryPolicy _retryPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DownloadScheduler> _logger;

    private readonly ConcurrentDictionary<DownloadId, DownloadHandle> _handles = new();
    private readonly Channel<DownloadId> _queue;
    private readonly CancellationTokenSource _shutdown = new();

    // The concurrency gate IS the live worker count. Resizing (Phase 8) spawns or retires workers under
    // this lock; a retired worker leaves only between downloads, never mid-run.
    private readonly object _concurrencyLock = new();
    private readonly List<Task> _workers = [];
    private int _targetConcurrency;
    private int _aliveWorkers;

    public DownloadScheduler(
        IDownloadEngine engine,
        RetryPolicy retryPolicy,
        SchedulerOptions options,
        TimeProvider timeProvider,
        ILogger<DownloadScheduler> logger)
    {
        _engine = engine;
        _retryPolicy = retryPolicy;
        _timeProvider = timeProvider;
        _logger = logger;

        _queue = Channel.CreateBounded<DownloadId>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        SetMaxConcurrency(options.MaxConcurrentDownloads);
    }

    public int MaxConcurrency
    {
        get
        {
            lock (_concurrencyLock)
            {
                return _targetConcurrency;
            }
        }
    }

    public void SetMaxConcurrency(int maxConcurrentDownloads)
    {
        var target = Math.Max(1, maxConcurrentDownloads);
        lock (_concurrencyLock)
        {
            _targetConcurrency = target;

            // Raise immediately by spawning workers (a waiting download starts at once); lowering is lazy —
            // workers retire between downloads via TryRetire, so a running download is never killed.
            while (_aliveWorkers < _targetConcurrency)
            {
                _aliveWorkers++;
                _workers.Add(Task.Run(WorkerLoopAsync));
            }
        }

        LogConcurrency(target);
    }

    /// <summary>A worker leaves the pool iff the live count exceeds the target (a lowering took effect).</summary>
    private bool TryRetire()
    {
        lock (_concurrencyLock)
        {
            if (_aliveWorkers > _targetConcurrency)
            {
                _aliveWorkers--;
                return true;
            }

            return false;
        }
    }

    public async Task<IDownloadHandle> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var handle = new DownloadHandle(request, _shutdown.Token);
        if (!_handles.TryAdd(request.Id, handle))
        {
            throw new InvalidOperationException($"Download {request.Id} is already scheduled.");
        }

        await _queue.Writer.WriteAsync(request.Id, cancellationToken).ConfigureAwait(false);
        LogEnqueued(request.Id);
        return handle;
    }

    public Task PauseAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        Get(id).Pause();
        LogControl(id, "pause");
        return Task.CompletedTask;
    }

    public async Task ResumeAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        var handle = Get(id);
        handle.Resume();
        await _queue.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
        LogControl(id, "resume");
    }

    public Task CancelAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        var handle = Get(id);
        if (handle.Cancel())
        {
            // Transitioned straight to Canceled (was Queued/Paused/Failed); discard now. A running
            // download is discarded by its worker once the run unwinds.
            _engine.Discard(handle.Request.TargetPath);
        }

        LogControl(id, "cancel");
        return Task.CompletedTask;
    }

    public async Task RetryAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        var handle = Get(id);
        handle.Retry();
        await _queue.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
        LogControl(id, "retry");
    }

    public IDownloadHandle? Find(DownloadId id) => _handles.GetValueOrDefault(id);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();

        Task[] workers;
        lock (_concurrencyLock)
        {
            workers = [.. _workers];
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: workers unwind on shutdown.
        }

        foreach (var handle in _handles.Values)
        {
            handle.Dispose();
        }

        _shutdown.Dispose();
    }

    private DownloadHandle Get(DownloadId id) =>
        _handles.TryGetValue(id, out var handle)
            ? handle
            : throw new KeyNotFoundException($"Unknown download {id}.");

    private async Task WorkerLoopAsync()
    {
        try
        {
            var reader = _queue.Reader;
            while (await reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                // A lowering takes effect here, before taking new work — never mid-run. The item we did
                // not take stays in the channel for a surviving worker.
                if (TryRetire())
                {
                    return;
                }

                if (!reader.TryRead(out var id) || !_handles.TryGetValue(id, out var handle))
                {
                    continue;
                }

                // Only one worker can win Queued -> Running; stale/duplicate queue entries are skipped.
                if (!handle.TryBeginRun(out var token))
                {
                    continue;
                }

                await ProcessAsync(handle, token).ConfigureAwait(false);

                // Finished a download: retire now if a lowering left us over target.
                if (TryRetire())
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task ProcessAsync(DownloadHandle handle, CancellationToken token)
    {
        try
        {
            while (true)
            {
                // The handle is the run's progress sink (lock-free counters surfaced to the UI, ADR-0010/0013).
                var outcome = await _engine.RunAsync(handle.Request, handle, token).ConfigureAwait(false);

                // 1. A real completion wins over any racing control op (the file is done, sidecars gone).
                if (outcome.Kind == DownloadResultKind.Completed)
                {
                    handle.CompleteRun();
                    LogTerminal(handle.Id, DownloadStatus.Completed);
                    return;
                }

                // 2. Cancel intent beats everything else. Discard *before* publishing Canceled, so an
                // observer that sees Canceled also sees the state already discarded.
                if (handle.CancelRequested)
                {
                    _engine.Discard(handle.Request.TargetPath);
                    handle.MarkCanceled();
                    LogTerminal(handle.Id, DownloadStatus.Canceled);
                    return;
                }

                // 3. Pause (or shutdown) leaves a consistent, resumable checkpoint on disk.
                if (handle.PauseRequested || _shutdown.IsCancellationRequested)
                {
                    handle.MarkPaused();
                    LogTerminal(handle.Id, DownloadStatus.Paused);
                    return;
                }

                // 4. Defensive: token cancelled without our intent -> treat as pause (resumable).
                if (outcome.Kind == DownloadResultKind.Canceled)
                {
                    handle.MarkPaused();
                    LogTerminal(handle.Id, DownloadStatus.Paused);
                    return;
                }

                // 5. Failure: retry with backoff, or give up. A "needs credentials" (401/403) outcome is
                // non-transient, so it lands here and terminates as Failed-with-reason — no retry-loop,
                // and the partial download is retained (not discarded) so a re-auth can resume (ADR-0011).
                var attempts = handle.IncrementAttempts();
                if (!outcome.IsTransient || !_retryPolicy.ShouldRetry(attempts))
                {
                    handle.FailRun(outcome.NeedsCredentials);
                    LogFailed(handle.Id, attempts, outcome.Error ?? "(none)");
                    return;
                }

                var backoffToken = handle.BeginBackoff();
                var delay = _retryPolicy.NextDelay(attempts, outcome.RetryAfter);
                LogBackoff(handle.Id, attempts, delay);
                try
                {
                    await Task.Delay(delay, _timeProvider, backoffToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancelling mid-backoff returns promptly — it does not wait out the delay.
                    if (handle.CancelRequested)
                    {
                        _engine.Discard(handle.Request.TargetPath);
                        handle.MarkCanceled();
                        LogTerminal(handle.Id, DownloadStatus.Canceled);
                        return;
                    }

                    handle.MarkPaused();
                    LogTerminal(handle.Id, DownloadStatus.Paused);
                    return;
                }

                token = handle.BeginRetryRun();
            }
        }
        finally
        {
            handle.DisposeRunCts();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Concurrency gate set to {Target}.")]
    private partial void LogConcurrency(int target);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Download {Id} enqueued.")]
    private partial void LogEnqueued(DownloadId id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Download {Id}: {Operation} requested.")]
    private partial void LogControl(DownloadId id, string operation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} -> {Status}.")]
    private partial void LogTerminal(DownloadId id, DownloadStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id} failed after {Attempts} attempt(s): {Reason}")]
    private partial void LogFailed(DownloadId id, int attempts, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} backing off after attempt {Attempts} for {Delay}.")]
    private partial void LogBackoff(DownloadId id, int attempts, TimeSpan delay);
}