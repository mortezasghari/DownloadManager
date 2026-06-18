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
    private readonly Task[] _workers;

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

        var concurrency = Math.Max(1, options.MaxConcurrentDownloads);
        _workers = new Task[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            _workers[i] = Task.Run(WorkerLoopAsync);
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

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
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
            await foreach (var id in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (!_handles.TryGetValue(id, out var handle))
                {
                    continue;
                }

                // Only one worker can win Queued -> Running; stale/duplicate queue entries are skipped.
                if (!handle.TryBeginRun(out var token))
                {
                    continue;
                }

                await ProcessAsync(handle, token).ConfigureAwait(false);
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
                var outcome = await _engine.RunAsync(handle.Request, progress: null, token).ConfigureAwait(false);

                // 1. A real completion wins over any racing control op (the file is done, sidecars gone).
                if (outcome.Kind == DownloadResultKind.Completed)
                {
                    handle.CompleteRun();
                    LogTerminal(handle.Id, DownloadStatus.Completed);
                    return;
                }

                // 2. Cancel intent beats everything else.
                if (handle.CancelRequested)
                {
                    handle.MarkCanceled();
                    _engine.Discard(handle.Request.TargetPath);
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

                // 5. Failure: retry with backoff, or give up.
                var attempts = handle.IncrementAttempts();
                if (!outcome.IsTransient || !_retryPolicy.ShouldRetry(attempts))
                {
                    handle.FailRun();
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
                        handle.MarkCanceled();
                        _engine.Discard(handle.Request.TargetPath);
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