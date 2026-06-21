using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Scheduler;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.Services;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// UI cleanup (ADR-0024): queue/history rows surface the SH-1 sanitized filename (never the raw URL), and
/// the adaptive settings layout's column breakpoint is correct. Headless — VM/layout logic only.
/// </summary>
public sealed class UiCleanupTests
{
    private static DownloadRequest Request(Uri url, string targetPath) => new()
    {
        Id = DownloadId.New(),
        Url = url,
        TargetPath = targetPath,
    };

    private static DownloadItemViewModel Item(DownloadRequest request) =>
        new(request, new FakeDownloadHandle { Id = request.Id }, new FakeUiScheduler(), new FakeTimeProvider(),
            onStop: _ => Task.CompletedTask, onReauthorize: _ => Task.CompletedTask, onPostpone: _ => Task.CompletedTask);

    // ---------------- Sanitized filename display (not the raw URL) ----------------

    [Fact]
    public void Queue_row_display_name_is_the_filename_not_the_url()
    {
        var item = Item(Request(new Uri("https://host.example/path/movie.mp4"), "/downloads/movie.mp4"));

        Assert.Equal("movie.mp4", item.Name);
        Assert.DoesNotContain("host.example", item.Name); // not the URL/host
        Assert.DoesNotContain("https", item.Name);
    }

    [Fact]
    public void Queue_row_display_name_strips_bidi_controls_no_extension_spoofing()
    {
        // A right-to-left override in the name would render "exe.jpg" while the bytes are "gpj.exe" (F3/F5).
        // The router-sanitized TargetPath leaf is bidi-stripped; the row must surface that, not a raw name.
        var item = Item(Request(new Uri("https://host.example/x"), "/downloads/photo\u202Egpj.exe"));

        Assert.Equal("photogpj.exe", item.Name);          // bidi override removed
        Assert.DoesNotContain('\u202E', item.Name);
    }

    [Fact]
    public void History_row_display_name_is_the_sanitized_filename()
    {
        var record = HistoryRecord.From(
            DownloadId.New(), "clip\u202Egpj.exe", 10, HistoryState.Completed, "/downloads/clip.exe");
        var historyItem = new HistoryItemViewModel(record, new FakeFileLauncher(), _ => { });

        Assert.Equal("clipgpj.exe", historyItem.Name);    // sanitized, non-spoofing
        Assert.DoesNotContain('\u202E', historyItem.Name);
    }

    // ---------------- Adaptive settings layout breakpoint ----------------

    [Theory]
    [InlineData(1200, 2)] // wide → two columns
    [InlineData(820, 2)]  // two 380 blocks + 16 gap = 776 → fits two
    [InlineData(776, 2)]  // exactly at the breakpoint
    [InlineData(775, 1)]  // just below → one column
    [InlineData(500, 1)]  // narrow → one column
    [InlineData(380, 1)]
    public void Settings_layout_column_count_tracks_the_width_breakpoint(double width, int expectedColumns)
    {
        Assert.Equal(expectedColumns, SettingsLayout.ColumnsForWidth(width));
    }

    [Fact]
    public void Settings_layout_breakpoint_is_two_blocks_plus_one_gap()
    {
        Assert.Equal((SettingsLayout.GroupWidth * 2) + SettingsLayout.Gap, SettingsLayout.TwoColumnBreakpoint);
    }

    // ---------------- Icon buttons keep the state-machine enable/disable ----------------

    [Theory]
    [InlineData(DownloadStatus.Queued, true, true, false)]
    [InlineData(DownloadStatus.Running, true, true, false)]
    [InlineData(DownloadStatus.Failed, false, false, true)] // Failed: only Retry (postpone/stop are for live states)
    [InlineData(DownloadStatus.Completed, false, false, false)]
    public void Icon_buttons_retain_command_enablement(
        DownloadStatus status, bool canPostpone, bool canStop, bool canRetry)
    {
        var handle = new FakeDownloadHandle { Status = status };
        var item = new DownloadItemViewModel(
            Request(new Uri("https://h/f.bin"), "/d/f.bin"), handle, new FakeUiScheduler(), new FakeTimeProvider(),
            onStop: _ => Task.CompletedTask, onReauthorize: _ => Task.CompletedTask, onPostpone: _ => Task.CompletedTask);
        item.Refresh();

        Assert.Equal(canPostpone, item.PostponeCommand.CanExecute(null));
        Assert.Equal(canStop, item.StopCommand.CanExecute(null));
        Assert.Equal(canRetry, item.RetryCommand.CanExecute(null));
    }
}