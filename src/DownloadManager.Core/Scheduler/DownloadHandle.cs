using System.Runtime.CompilerServices;
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
    private bool _needsCredentials;
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

    // UI-facing read: lock-free Volatile read of the int-backed status (Stage 1). All transitions
    // still happen under _gate; this only removes the lock from the observational read path.
    public DownloadStatus Status =>
        (DownloadStatus)Volatile.Read(ref Unsafe.As<DownloadStatus, int>(ref _status));

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

    /// <summary>True when the download is Failed because the server demanded credentials (401/403) and the
    /// supplied ones were missing/stale. A <i>reason</i> on the Failed state (ADR-0011), not a new state:
    /// the partial download is retained and the user can re-supply credentials and retry.</summary>
    public bool NeedsCredentials
    {
        get { lock (_gate) { return _needsCredentials; } }
    }

    // ---- Control operations (external) ----

    /// <summary>Pause: legal from Queued/Running/Retrying. Queued pauses directly; an active run is
    /// signalled and the worker completes the transition.</summary>
    public void Pause()
    {
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            switch (_status)
            {
                case DownloadStatus.Queued:
                    TransitionTo(DownloadStatus.Paused);
                    break;
                case DownloadStatus.Running or DownloadStatus.Retrying:
                    _pauseRequested = true;
                    toCancel = _runCts; // cancel outside the lock (below)
                    break;
                default:
                    throw new InvalidDownloadTransitionException(_status, "pause");
            }
        }

        SignalRunCancellation(toCancel);
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
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            switch (_status)
            {
                case DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Failed:
                    TransitionTo(DownloadStatus.Canceled);
                    return true;
                case DownloadStatus.Running or DownloadStatus.Retrying:
                    _cancelRequested = true;
                    toCancel = _runCts; // cancel outside the lock (below)
                    break;
                default:
                    throw new InvalidDownloadTransitionException(_status, "cancel");
            }
        }

        SignalRunCancellation(toCancel);
        return false;
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
        CancellationTokenSource? toDispose = null;
        bool began;
        lock (_gate)
        {
            if (_status != DownloadStatus.Queued)
            {
                token = default;
                began = false;
            }
            else
            {
                _pauseRequested = false;
                _cancelRequested = false;
                _needsCredentials = false;
                TransitionTo(DownloadStatus.Running);
                token = RecreateRunToken(out toDispose);
                began = true;
            }
        }

        toDispose?.Dispose(); // dispose the superseded CTS outside _gate
        return began;
    }

    public CancellationToken BeginBackoff()
    {
        CancellationTokenSource? toDispose;
        CancellationToken token;
        lock (_gate)
        {
            TransitionTo(DownloadStatus.Retrying);
            token = RecreateRunToken(out toDispose);
        }

        toDispose?.Dispose();
        return token;
    }

    public CancellationToken BeginRetryRun()
    {
        CancellationTokenSource? toDispose;
        CancellationToken token;
        lock (_gate)
        {
            TransitionTo(DownloadStatus.Running);
            token = RecreateRunToken(out toDispose);
        }

        toDispose?.Dispose();
        return token;
    }

    public void CompleteRun() => SetTerminal(DownloadStatus.Completed);

    /// <summary>Terminal Failed. <paramref name="needsCredentials"/> records the 401/403 "needs credentials"
    /// reason (ADR-0011) as an attribute of the Failed state, not a separate state.</summary>
    public void FailRun(bool needsCredentials = false)
    {
        lock (_gate)
        {
            _needsCredentials = needsCredentials;
            TransitionTo(DownloadStatus.Failed);
        }
    }

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
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            toDispose = _runCts;
            _runCts = null;
        }

        toDispose?.Dispose(); // dispose outside _gate (see SignalRunCancellation / ADR-0010)
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
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            _disposed = true;
            toDispose = _runCts;
            _runCts = null;
        }

        toDispose?.Dispose(); // dispose outside _gate
    }

    // ---- internals (all callers hold _gate) ----

    private void SetTerminal(DownloadStatus to)
    {
        lock (_gate)
        {
            TransitionTo(to);
        }
    }

    /// <summary>
    /// Triggers cancellation of the captured run CTS <b>outside</b> the <c>_gate</c> lock and without
    /// running its registered callbacks on the caller's thread (ADR-0010). <c>CancellationTokenSource.Cancel()</c>
    /// invokes callbacks synchronously, so calling it under <c>_gate</c> let a callback that re-enters the
    /// handle — or blocks on any other lock — stall the whole control plane. Using <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// with <see cref="TimeSpan.Zero"/> arms the cancellation on a pool thread and returns immediately, so a
    /// pathological callback can never block a pause/cancel. A disposed CTS (the run already ended) is benign.
    /// </summary>
    private static void SignalRunCancellation(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.CancelAfter(TimeSpan.Zero);
        }
        catch (ObjectDisposedException)
        {
            // The run completed/was replaced between capture and signal; nothing left to cancel.
        }
    }

    /// <summary>
    /// Swaps in a fresh run CTS and hands back the <paramref name="previous"/> one (without disposing it)
    /// so the caller can dispose it <b>outside</b> <c>_gate</c> (ADR-0010). The superseded CTS is removed
    /// from the field atomically under the lock, so no two callers ever capture the same reference — the
    /// disposal is single-owner and cannot double-dispose or race a concurrent recreate.
    /// </summary>
    private CancellationToken RecreateRunToken(out CancellationTokenSource? previous)
    {
        previous = _runCts;
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

        // Publish via Volatile so the lock-free Status reader observes the new value promptly.
        Volatile.Write(ref Unsafe.As<DownloadStatus, int>(ref _status), (int)to);
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