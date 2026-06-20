using DownloadManager.UI.Services;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 9 / ADR-0019: the per-platform open / reveal command construction. The one genuinely per-OS
/// code in this phase — verified for every RID here (pure command building, no GUI launch needed).
/// </summary>
public class FileLauncherTests
{
    private const string Path = "/home/u/Downloads/video.mp4";

    [Fact]
    public void Linux_open_uses_xdg_open_on_the_file()
    {
        var cmd = LaunchCommands.OpenFile(LaunchOs.Linux, Path);

        Assert.Equal("xdg-open", cmd.FileName);
        Assert.Equal("\"/home/u/Downloads/video.mp4\"", cmd.Arguments);
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void Linux_reveal_opens_the_containing_directory_xdg_open_cannot_select()
    {
        var cmd = LaunchCommands.RevealInFolder(LaunchOs.Linux, Path);

        Assert.Equal("xdg-open", cmd.FileName);
        // The directory, not the file. Computed host-aware (GetDirectoryName uses the host separator) so
        // this asserts identically on a Windows or Linux test runner; at runtime Linux reveal only ever
        // runs on Linux, where the separators are '/'.
        var expectedDir = System.IO.Path.GetDirectoryName(Path)!;
        Assert.Equal($"\"{expectedDir}\"", cmd.Arguments);
        Assert.DoesNotContain("video.mp4", cmd.Arguments);
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void Windows_open_shell_executes_the_path_itself()
    {
        var cmd = LaunchCommands.OpenFile(LaunchOs.Windows, @"C:\Users\u\Downloads\video.mp4");

        Assert.Equal(@"C:\Users\u\Downloads\video.mp4", cmd.FileName);
        Assert.True(cmd.UseShellExecute);
        Assert.Equal(string.Empty, cmd.Arguments);
    }

    [Fact]
    public void Windows_reveal_uses_explorer_select_with_the_quoted_path()
    {
        var cmd = LaunchCommands.RevealInFolder(LaunchOs.Windows, @"C:\Users\u\Downloads\video.mp4");

        Assert.Equal("explorer.exe", cmd.FileName);
        Assert.Equal("/select,\"C:\\Users\\u\\Downloads\\video.mp4\"", cmd.Arguments);
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void MacOS_open_uses_open_on_the_file()
    {
        var cmd = LaunchCommands.OpenFile(LaunchOs.MacOS, Path);

        Assert.Equal("open", cmd.FileName);
        Assert.Equal("\"/home/u/Downloads/video.mp4\"", cmd.Arguments);
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void MacOS_reveal_uses_open_dash_R_to_select_the_file()
    {
        var cmd = LaunchCommands.RevealInFolder(LaunchOs.MacOS, Path);

        Assert.Equal("open", cmd.FileName);
        Assert.Equal("-R \"/home/u/Downloads/video.mp4\"", cmd.Arguments);
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void Current_resolves_to_the_host_os()
    {
        var expected = OperatingSystem.IsWindows() ? LaunchOs.Windows
            : OperatingSystem.IsMacOS() ? LaunchOs.MacOS
            : LaunchOs.Linux;

        Assert.Equal(expected, LaunchCommands.Current());
    }

    [Fact]
    public void Real_launcher_reports_failure_for_a_missing_file_without_throwing()
    {
        var launcher = new ProcessFileLauncher();

        var result = launcher.OpenFile(Path.Replace("video.mp4", $"missing-{Guid.NewGuid():N}.mp4"));

        Assert.False(result.Ok);
        Assert.Contains("no longer exists", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}