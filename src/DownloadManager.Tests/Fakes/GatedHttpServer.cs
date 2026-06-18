namespace DownloadManager.Tests.Fakes;

/// <summary>
/// Wraps a <see cref="FakeContentServer"/> and gates <b>work</b> requests (probes pass through
/// immediately), so scheduler tests can deterministically hold downloads "running", observe the
/// concurrency gate, and release work one at a time or by segment offset. No real sleeps.
/// </summary>
internal sealed class GatedHttpServer(FakeContentServer inner)
{
    private readonly FakeContentServer _inner = inner;
    private readonly Lock _gate = new();
    private readonly List<Blocked> _blocked = [];
    private readonly HashSet<long> _completed = [];
    private readonly List<(Func<bool> Condition, TaskCompletionSource Tcs)> _waiters = [];

    private long _seq;
    private int _started;
    private int _inFlight;
    private bool _open;

    public int Started { get { lock (_gate) { return _started; } } }

    public int InFlight { get { lock (_gate) { return _inFlight; } } }

    public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range is { From: 0, To: 0 })
        {
            return _inner.Handle(request); // probe: never gated
        }

        var from = range?.From ?? 0;
        TaskCompletionSource? tcs = null;
        lock (_gate)
        {
            _started++;
            _inFlight++;
            if (!_open)
            {
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _blocked.Add(new Blocked(_seq++, from, tcs));
            }

            Signal();
        }

        if (tcs is not null)
        {
            await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            await tcs.Task.ConfigureAwait(false); // blocks until released or cancelled
        }

        var response = _inner.Handle(request);
        lock (_gate)
        {
            _inFlight--;
            _completed.Add(from);
            Signal();
        }

        return response;
    }

    /// <summary>Release the oldest blocked work request (used when offsets are not distinct).</summary>
    public void ReleaseOne()
    {
        lock (_gate)
        {
            if (_blocked.Count > 0)
            {
                var b = _blocked[0];
                _blocked.RemoveAt(0);
                b.Tcs.TrySetResult();
            }
        }
    }

    /// <summary>Release the blocked work request for a specific segment start offset.</summary>
    public void Release(long from)
    {
        lock (_gate)
        {
            var i = _blocked.FindIndex(b => b.From == from);
            if (i >= 0)
            {
                var b = _blocked[i];
                _blocked.RemoveAt(i);
                b.Tcs.TrySetResult();
            }
        }
    }

    /// <summary>Open the gate: release everything blocked and let future work requests pass freely.</summary>
    public void OpenGate()
    {
        lock (_gate)
        {
            _open = true;
            foreach (var b in _blocked)
            {
                b.Tcs.TrySetResult();
            }

            _blocked.Clear();
        }
    }

    public Task WaitStartedAsync(int count, CancellationToken ct) => WaitAsync(() => _started >= count, ct);

    public Task WaitInFlightAsync(int count, CancellationToken ct) => WaitAsync(() => _inFlight == count, ct);

    public Task WaitCompletedAsync(long from, CancellationToken ct) => WaitAsync(() => _completed.Contains(from), ct);

    private Task WaitAsync(Func<bool> condition, CancellationToken ct)
    {
        lock (_gate)
        {
            if (condition())
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
            _waiters.Add((condition, tcs));
            return tcs.Task;
        }
    }

    private void Signal()
    {
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].Condition())
            {
                _waiters[i].Tcs.TrySetResult();
                _waiters.RemoveAt(i);
            }
        }
    }

    private readonly record struct Blocked(long Seq, long From, TaskCompletionSource Tcs);
}