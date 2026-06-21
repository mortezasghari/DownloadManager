using DownloadManager.Core.Configuration;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Pure schedule-predicate tests (ADR-0023): the opt-in gate asserts only when enabled AND the time is
/// outside the daily window, with overnight-wrap support. No clock — pure time-of-day inputs.
/// </summary>
public class ScheduleGateTests
{
    private static TimeOnly T(string hhmm) => TimeOnly.ParseExact(hhmm, "HH:mm");

    [Fact]
    public void Disabled_schedule_never_asserts_regardless_of_time()
    {
        var off = new ScheduleOptions { Enabled = false, Start = T("09:00"), Stop = T("17:00") };

        Assert.False(ScheduleGate.Asserts(off, T("03:00"))); // outside the window, but disabled
        Assert.False(ScheduleGate.Asserts(off, T("12:00")));
    }

    // ---- same-day window 02:00–06:00 ----

    [Theory]
    [InlineData("02:00", true)]  // inclusive start → inside
    [InlineData("04:00", true)]
    [InlineData("05:59", true)]
    [InlineData("06:00", false)] // exclusive stop → outside
    [InlineData("01:59", false)]
    [InlineData("23:00", false)]
    public void Same_day_window_boundaries(string now, bool inside)
    {
        var s = new ScheduleOptions { Enabled = true, Start = T("02:00"), Stop = T("06:00") };
        Assert.Equal(inside, ScheduleGate.IsInsideWindow(s.Start, s.Stop, T(now)));
        Assert.Equal(!inside, ScheduleGate.Asserts(s, T(now))); // asserts == outside
    }

    // ---- overnight window 23:00–06:00 (crosses midnight) ----

    [Theory]
    [InlineData("00:30", true)]
    [InlineData("23:30", true)]
    [InlineData("23:00", true)]  // inclusive start
    [InlineData("05:59", true)]
    [InlineData("06:00", false)] // exclusive stop
    [InlineData("06:30", false)]
    [InlineData("12:00", false)]
    public void Overnight_wrapping_window_boundaries(string now, bool inside)
    {
        var s = new ScheduleOptions { Enabled = true, Start = T("23:00"), Stop = T("06:00") };
        Assert.Equal(inside, ScheduleGate.IsInsideWindow(s.Start, s.Stop, T(now)));
        Assert.Equal(!inside, ScheduleGate.Asserts(s, T(now)));
    }

    [Fact]
    public void Equal_start_and_stop_is_an_all_day_window_never_asserts()
    {
        var s = new ScheduleOptions { Enabled = true, Start = T("00:00"), Stop = T("00:00") };
        Assert.True(ScheduleGate.IsInsideWindow(s.Start, s.Stop, T("13:37")));
        Assert.False(ScheduleGate.Asserts(s, T("13:37")));
    }
}