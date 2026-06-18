using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Scheduler;

/// <summary>
/// The per-download state machine (spec §8, ADR-0008). All state, transitions, the pause/cancel
/// intents, and the run cancellation source live behind one lock, so control operations and the
/// worker never race into an illegal state, a double-start, or a leaked token. Pause and cancel are
/// <b>distinct intents</b> (separate flags), not the same signal.
/// </summary>
public sealed class DownloadHandle : IDownloadHandle, IDisposable
{
    private static readonly (DownloadStatus From, DownloadStatus To)[] LegalTransitions =
    [
        (DownloadStatus.Queued, DownloadStatus.Running),
        (DownloadStatus.Queued, DownloadStatus.Paused),
        (DownloadStatus.Queued, DownloadStatus.Canceled),
        (DownloadStatus.Running, DownloadStatus.Completed),
        (DownloadStatus.Running, DownloadStatus.Failed),
        (DownloadStatus.Running, DownloadStatus.Retrying),
        (DownloadStatus.Running, DownloadStatus.Paused),
        (DownloadStatus.Running, DownloadStatus.Canceled),
        (DownloadStatus.Retrying, DownloadStatus.Running),
        (DownloadStatus.Retrying, DownloadStatus.Paused),
        (DownloadStatus.Retrying, DownloadStatus.Canceled),
        (DownloadStatus.Paused, DownloadStatus.Queued),
        (DownloadStatus.Paused, DownloadStatus.Canceled),
        (DownloadStatus.Failed, DownloadStatus.Queued),
        (DownloadStatus.Failed, DownloadStatus.Canceled),
    ];

    private readonly Lock _gate = new();
    private readonly CancellationToken _shutdownToken;
    private readonly List<Waiter> _waiters = [];

    private DownloadStatus _status = DownloadStatus.Queued;
    private bool _pauseRequested;
    private bool _cancelRequested;
    private int _attemptsMade;
    private CancellationTokenSource? _runCts;
    private bool _disposed;

    public DownloadHandle(DownloadRequest request, CancellationToken shutdownToken)
    {
        Request = request;
        _shutdownToken = shutdownToken;
    }

    public DownloadId Id => Request.Id;

    public DownloadRequest Request { get; }

    public DownloadStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public bool PauseRequested
    {
        get { lock (_gate) { return _pauseRequested; } }
    }

    public bool CancelRequested
    {
        get { lock (_gate) { return _cancelRequested; } }
    }

    public int AttemptsMade
    {
        get { lock (_gate) { return _attemptsMade; } }
    }

    // ---- Control operations (external) ----

    /// <summary>Pause: legal from Queued/Running/Retrying. Queued pauses directly; an active run is
    /// signalled and the worker completes the transition.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            switch (_status)
            {
                case DownloadStatus.Queued:
                    TransitionTo(DownloadStatus.Paused);
                    break;
                case DownloadStatus.Running or DownloadStatus.Retrying:
                    _pauseRequested = true;
                    _runCts?.Cancel();
                    break;
                default:
                    throw new InvalidDownloadTransitionException(_status, "pause");
            }
        }
    }

    /// <summary>Resume: legal only from Paused. Moves to Queued; the scheduler re-enqueues.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_status != DownloadStatus.Paused)
            {
                throw new InvalidDownloadTransitionException(_status, "resume");
            }

            TransitionTo(DownloadStatus.Queued);
        }
    }

    /// <summary>Cancel: legal from any non-terminal state. Returns true if it transitioned straight to
    /// Canceled (so the caller should discard state now); false if an active run was signalled to stop.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            switch (_status)
            {
                case DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Failed:
                    TransitionTo(DownloadStatus.Canceled);
                    return true;
                case DownloadStatus.Running or DownloadStatus.Retrying:
                    _cancelRequested = true;
                    _runCts?.Cancel();
                    return false;
                default:
                    throw new InvalidDownloadTransitionException(_status, "cancel");
            }
        }
    }

    /// <summary>Manual retry: legal only from Failed. Resets the attempt budget; the scheduler re-enqueues.</summary>
    public void Retry()
    {
        lock (_gate)
        {
            if (_status != DownloadStatus.Failed)
            {
                throw new InvalidDownloadTransitionException(_status, "retry");
            }

            _attemptsMade = 0;
            TransitionTo(DownloadStatus.Queued);
        }
    }

    // ---- Worker-driven transitions ----

    /// <summary>Try to begin a run (Queued -> Running). Returns false if the download was paused/canceled
    /// while queued (the worker then skips it). Resets intents and creates a fresh run token.</summary>
    public bool TryBeginRun(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_status != DownloadStatus.Queued)
            {
                token = default;
                return false;
            }

            _pauseRequested = false;
            _cancelRequested = false;
            TransitionTo(DownloadStatus.Running);
            token = RecreateRunToken();
            return true;
        }
    }

    public CancellationToken BeginBackoff()
    {
        lock (_gate)
        {
            TransitionTo(DownloadStatus.Retrying);
            return RecreateRunToken();
        }
    }

    public CancellationToken BeginRetryRun()
    {
        lock (_gate)
        {
            TransitionTo(DownloadStatus.Running);
            return RecreateRunToken();
        }
    }

    public void CompleteRun() => SetTerminal(DownloadStatus.Completed);

    public void FailRun() => SetTerminal(DownloadStatus.Failed);

    public void MarkPaused() => SetTerminal(DownloadStatus.Paused);

    public void MarkCanceled() => SetTerminal(DownloadStatus.Canceled);

    public int IncrementAttempts()
    {
        lock (_gate)
        {
            return ++_attemptsMade;
        }
    }

    public void DisposeRunCts()
    {
        lock (_gate)
        {
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    public Task WaitForStatusAsync(Func<DownloadStatus, bool> predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            if (predicate(_status))
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs)
                : default;
            _waiters.Add(new Waiter(predicate, tcs, registration));
            return tcs.Task;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    // ---- internals (all callers hold _gate) ----

    private void SetTerminal(DownloadStatus to)
    {
        lock (_gate)
        {
            TransitionTo(to);
        }
    }

    private CancellationToken RecreateRunToken()
    {
        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        return _runCts.Token;
    }

    private void TransitionTo(DownloadStatus to)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var legal = false;
        foreach (var (from, allowed) in LegalTransitions)
        {
            if (from == _status && allowed == to)
            {
                legal = true;
                break;
            }
        }

        if (!legal)
        {
            throw new InvalidDownloadTransitionException(_status, $"transition to {to}");
        }

        _status = to;
        SignalWaiters();
    }

    private void SignalWaiters()
    {
        // tcs uses RunContinuationsAsynchronously, so completing under the lock won't run continuations inline.
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].Predicate(_status))
            {
                _waiters[i].Completion.TrySetResult();
                _waiters[i].Registration.Dispose();
                _waiters.RemoveAt(i);
            }
        }
    }

    private readonly record struct Waiter(
        Func<DownloadStatus, bool> Predicate, TaskCompletionSource Completion, CancellationTokenRegistration Registration);
}