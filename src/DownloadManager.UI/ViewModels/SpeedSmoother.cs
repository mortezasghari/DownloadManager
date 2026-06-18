namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Smooths download speed over a moving time window (ADR-0013). Instantaneous bytes/tick jitters
/// uselessly on a bursty network; instead we keep recent (timestamp, total-bytes) samples and report the
/// average rate across the window. Speed is <c>null</c> until there is enough history; a stalled transfer
/// reports <c>0</c> (so ETA shows "—" rather than Infinity). Pure BCL; the clock is injected, so this is
/// fully deterministic under a <c>FakeTimeProvider</c>.
/// </summary>
public sealed class SpeedSmoother(TimeSpan window)
{
    private readonly TimeSpan _window = window;
    private readonly Queue<Sample> _samples = new();

    public void Reset() => _samples.Clear();

    /// <summary>Record the cumulative completed-byte count observed at <paramref name="at"/>.</summary>
    public void Add(DateTimeOffset at, long completedBytes)
    {
        _samples.Enqueue(new Sample(at, completedBytes));

        // Drop samples older than the window, but always keep at least two so a rate stays computable.
        var cutoff = at - _window;
        while (_samples.Count > 2 && _samples.Peek().At < cutoff)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Smoothed bytes/second across the window; <c>null</c> when not yet computable.</summary>
    public double? BytesPerSecond()
    {
        if (_samples.Count < 2)
        {
            return null;
        }

        Sample oldest = default;
        Sample newest = default;
        var first = true;
        foreach (var sample in _samples)
        {
            if (first)
            {
                oldest = sample;
                first = false;
            }

            newest = sample;
        }

        var seconds = (newest.At - oldest.At).TotalSeconds;
        if (seconds <= 0)
        {
            return null;
        }

        var delta = newest.Bytes - oldest.Bytes;
        return delta <= 0 ? 0d : delta / seconds; // stalled -> 0, never negative
    }

    private readonly record struct Sample(DateTimeOffset At, long Bytes);
}