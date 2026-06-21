namespace DownloadManager.Core.Configuration;

/// <summary>
/// Resolved, engine-ready time-based schedule (ADR-0023): an opt-in single daily window that gates the
/// global queue pause. Mutable (<c>set</c>) so the queue-settings panel can update this shared singleton
/// live, like the other panel knobs. When <see cref="Enabled"/> is false the schedule gate never asserts.
/// </summary>
public sealed record ScheduleOptions
{
    /// <summary>Opt-in. Default off — a user who never enables it never experiences a time-based pause.</summary>
    public bool Enabled { get; set; }

    /// <summary>Window start (time-of-day). The queue runs (re: the schedule gate) inside <c>[Start, Stop)</c>.</summary>
    public TimeOnly Start { get; set; }

    public TimeOnly Stop { get; set; }
}

/// <summary>
/// Pure predicate for the schedule gate (ADR-0023). Evaluated periodically against the injected
/// <c>TimeProvider</c>'s "now" — a predicate, not exact-time events, so a skipped/doubled DST clock time
/// is simply re-read on the next tick (nothing to miss). Supports an overnight-wrapping window.
/// </summary>
public static class ScheduleGate
{
    /// <summary>True when scheduling is enabled and <paramref name="now"/> is OUTSIDE the active window.</summary>
    public static bool Asserts(ScheduleOptions schedule, TimeOnly now)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return schedule.Enabled && !IsInsideWindow(schedule.Start, schedule.Stop, now);
    }

    /// <summary>
    /// Whether <paramref name="now"/> is inside <c>[start, stop)</c>. A same-day window uses
    /// <c>start &lt;= now &lt; stop</c>; an overnight window (<c>start &gt; stop</c>, e.g. 23:00–06:00)
    /// uses <c>now &gt;= start OR now &lt; stop</c>. <c>start == stop</c> is treated as an all-day window
    /// (always inside) so an accidental equal-time setting never pauses the queue forever.
    /// </summary>
    public static bool IsInsideWindow(TimeOnly start, TimeOnly stop, TimeOnly now)
    {
        if (start == stop)
        {
            return true;
        }

        return start < stop
            ? now >= start && now < stop
            : now >= start || now < stop;
    }
}