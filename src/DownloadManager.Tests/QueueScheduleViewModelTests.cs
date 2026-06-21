using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Time-based scheduling at the view-model layer (ADR-0023): effective pause = manual OR (enabled AND
/// outside window), evaluated each tick against the injected <c>TimeProvider</c>. The two gates are
/// independent; the boundary pauses via the EXISTING global pause path. Headless, FakeTimeProvider.
/// </summary>
public sealed class QueueScheduleViewModelTests
{
    private static FakeTimeProvider ClockAt(string hhmm)
    {
        var t = TimeOnly.ParseExact(hhmm, "HH:mm");
        // FakeTimeProvider's local zone is UTC by default, so GetLocalNow().TimeOfDay == this time.
        return new FakeTimeProvider(new DateTimeOffset(2026, 6, 21, t.Hour, t.Minute, 0, TimeSpan.Zero));
    }

    private static (MainWindowViewModel Vm, FakeUiScheduler Scheduler) NewVm(
        ScheduleOptions schedule, FakeTimeProvider clock)
    {
        var scheduler = new FakeUiScheduler();
        var vm = new MainWindowViewModel(
            scheduler, clock,
            new FakeFilePicker(null), new FakeCredentialPrompt(null), new FakeImportDialog(),
            NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: Path.Combine(Path.GetTempPath(), "dlm-sched", Guid.NewGuid().ToString("N")),
            schedule: schedule);
        return (vm, scheduler);
    }

    private static ScheduleOptions Window(bool enabled, string start, string stop) => new()
    {
        Enabled = enabled,
        Start = TimeOnly.ParseExact(start, "HH:mm"),
        Stop = TimeOnly.ParseExact(stop, "HH:mm"),
    };

    // ----------------------------- truth table -----------------------------

    [Fact]
    public void Schedule_disabled_never_pauses_regardless_of_time()
    {
        var (vm, _) = NewVm(Window(enabled: false, "09:00", "17:00"), ClockAt("03:00")); // outside, but off
        vm.Tick();
        Assert.False(vm.IsQueuePaused);
        Assert.Equal(QueuePauseReason.None, vm.PauseReason);
    }

    [Fact]
    public void Enabled_inside_window_no_manual_runs()
    {
        var (vm, _) = NewVm(Window(true, "09:00", "17:00"), ClockAt("12:00"));
        vm.Tick();
        Assert.False(vm.IsQueuePaused);
    }

    [Fact]
    public void Enabled_outside_window_no_manual_is_paused_by_schedule()
    {
        var (vm, scheduler) = NewVm(Window(true, "09:00", "17:00"), ClockAt("20:00"));
        vm.Tick();
        Assert.True(vm.IsQueuePaused);
        Assert.Equal(QueuePauseReason.Schedule, vm.PauseReason);
        Assert.Equal("Outside scheduled hours", vm.PauseReasonText);
        Assert.Equal(1, scheduler.QueuePauseCount); // via the existing global pause path
    }

    [Fact]
    public async Task Manual_pause_inside_window_is_paused_by_manual()
    {
        var (vm, _) = NewVm(Window(true, "09:00", "17:00"), ClockAt("12:00"));
        vm.Tick();

        await vm.ToggleQueuePauseAsync(); // manual pause

        Assert.True(vm.IsQueuePaused);
        Assert.Equal(QueuePauseReason.Manual, vm.PauseReason);
        Assert.Equal("Paused by you", vm.PauseReasonText);
    }

    [Fact]
    public async Task Both_gates_assert_reports_both()
    {
        var (vm, _) = NewVm(Window(true, "09:00", "17:00"), ClockAt("20:00")); // outside
        vm.Tick();                       // schedule asserts
        await vm.ToggleQueuePauseAsync(); // + manual

        Assert.Equal(QueuePauseReason.Manual | QueuePauseReason.Schedule, vm.PauseReason);
        Assert.Contains("by you", vm.PauseReasonText);
        Assert.Contains("scheduled hours", vm.PauseReasonText);
    }

    [Fact]
    public async Task Clearing_manual_while_still_outside_the_window_stays_paused_by_schedule()
    {
        // The subtle case: gates are independent, so clearing one doesn't clear the other.
        var (vm, _) = NewVm(Window(true, "09:00", "17:00"), ClockAt("20:00")); // outside
        vm.Tick();
        await vm.ToggleQueuePauseAsync(); // manual ON  → both assert
        Assert.Equal(QueuePauseReason.Manual | QueuePauseReason.Schedule, vm.PauseReason);

        await vm.ToggleQueuePauseAsync(); // manual OFF → schedule still asserts

        Assert.True(vm.IsQueuePaused);
        Assert.Equal(QueuePauseReason.Schedule, vm.PauseReason);
    }

    // ----------------------------- boundary transitions -----------------------------

    [Fact]
    public void Window_open_transition_with_no_manual_resumes_via_the_existing_play_path()
    {
        var schedule = Window(true, "09:00", "17:00");
        var clock = ClockAt("08:00"); // start outside (before the window opens)
        var (vm, scheduler) = NewVm(schedule, clock);

        vm.Tick();
        Assert.True(vm.IsQueuePaused);
        Assert.Equal(1, scheduler.QueuePauseCount);

        // Time advances into the window → the gate clears on the next tick → queue resumes.
        clock.SetUtcNow(new DateTimeOffset(2026, 6, 21, 10, 0, 0, TimeSpan.Zero)); // 10:00 inside
        vm.Tick();

        Assert.False(vm.IsQueuePaused);
        Assert.Equal(1, scheduler.QueueResumeCount); // existing global play, exactly once
    }

    [Fact]
    public async Task Window_open_transition_with_manual_still_asserting_stays_paused()
    {
        var schedule = Window(true, "09:00", "17:00");
        var clock = ClockAt("08:00");
        var (vm, scheduler) = NewVm(schedule, clock);

        vm.Tick();                        // schedule pauses
        await vm.ToggleQueuePauseAsync(); // + manual

        clock.SetUtcNow(new DateTimeOffset(2026, 6, 21, 10, 0, 0, TimeSpan.Zero)); // into the window
        vm.Tick();                        // schedule clears, but manual still asserts

        Assert.True(vm.IsQueuePaused);
        Assert.Equal(QueuePauseReason.Manual, vm.PauseReason);
        Assert.Equal(0, scheduler.QueueResumeCount); // did NOT resume — manual holds it
    }

    [Fact]
    public async Task Boundary_pauses_active_downloads_via_the_existing_per_download_pause()
    {
        var schedule = Window(true, "09:00", "17:00");
        var clock = ClockAt("12:00"); // start inside
        var (vm, scheduler) = NewVm(schedule, clock);

        vm.NewUrl = "https://example.test/a.bin";
        await vm.AddCurrentUrlAsync();
        var item = vm.Downloads[^1];
        ((FakeDownloadHandle)scheduler.Find(item.Id)!).Status = DownloadStatus.Running;
        vm.Tick();
        Assert.False(vm.IsQueuePaused);

        // Cross the boundary out of the window → schedule pauses, and the running download is paused.
        clock.SetUtcNow(new DateTimeOffset(2026, 6, 21, 18, 0, 0, TimeSpan.Zero)); // 18:00 outside
        vm.Tick();

        Assert.True(vm.IsQueuePaused);
        Assert.Contains(item.Id, scheduler.Paused);  // existing per-download pause, no new mechanism
        Assert.Equal(1, scheduler.QueuePauseCount);
    }

    [Fact]
    public void Overnight_window_pauses_at_midday_and_runs_overnight()
    {
        var schedule = Window(true, "23:00", "06:00"); // run overnight, pause during the day

        var (paused, _) = NewVm(schedule, ClockAt("12:00"));
        paused.Tick();
        Assert.True(paused.IsQueuePaused); // 12:00 is outside [23:00,06:00)

        var (running, _) = NewVm(schedule, ClockAt("00:30"));
        running.Tick();
        Assert.False(running.IsQueuePaused); // 00:30 is inside the overnight window
    }
}