using DownloadManager.UI.Services;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Per-platform open / reveal command construction (ADR-0019), now asserting the argv <b>list</b> after
/// the F2 hardening (ADR-0020): arguments are discrete tokens, never a hand-quoted string, so a filename
/// containing a quote cannot inject flags. Verified for every RID here (pure command building).
/// </summary>
public class FileLauncherTests
{
    private const string Path = "/home/u/Downloads/video.mp4";

    [Fact]
    public void Linux_open_uses_xdg_open_on_the_file()
    {
        var cmd = LaunchCommands.OpenFile(LaunchOs.Linux, Path);

        Assert.Equal("xdg-open", cmd.FileName);
        Assert.Equal([Path], cmd.Arguments);
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void Linux_reveal_opens_the_containing_directory_xdg_open_cannot_select()
    {
        var cmd = LaunchCommands.RevealInFolder(LaunchOs.Linux, Path);

        Assert.Equal("xdg-open", cmd.FileName);
        var expectedDir = System.IO.Path.GetDirectoryName(Path)!; // host-aware (separators)
        Assert.Equal([expectedDir], cmd.Arguments); // the directory, not the file
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void Windows_open_shell_executes_the_path_itself()
    {
        var cmd = LaunchCommands.OpenFile(LaunchOs.Windows, @"C:\Users\u\Downloads\video.mp4");

        Assert.Equal(@"C:\Users\u\Downloads\video.mp4", cmd.FileName);
        Assert.True(cmd.UseShellExecute);
        Assert.Empty(cmd.Arguments);
    }

    [Fact]
    public void Windows_reveal_uses_explorer_select_as_a_single_argv_token()
    {
        var cmd = LaunchCommands.RevealInFolder(LaunchOs.Windows, @"C:\Users\u\Downloads\video.mp4");

        Assert.Equal("explorer.exe", cmd.FileName);
        // One token: "/select,<path>". .NET escapes it for argv; no hand-rolled quoting.
        Assert.Equal([@"/select,C:\Users\u\Downloads\video.mp4"], cmd.Arguments);
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void MacOS_open_uses_open_on_the_file()
    {
        var cmd = LaunchCommands.OpenFile(LaunchOs.MacOS, Path);

        Assert.Equal("open", cmd.FileName);
        Assert.Equal([Path], cmd.Arguments);
        Assert.False(cmd.UseShellExecute);
    }

    [Fact]
    public void MacOS_reveal_uses_open_dash_R_to_select_the_file()
    {
        var cmd = LaunchCommands.RevealInFolder(LaunchOs.MacOS, Path);

        Assert.Equal("open", cmd.FileName);
        Assert.Equal(["-R", Path], cmd.Arguments); // two discrete tokens
        Assert.False(cmd.UseShellExecute);
    }

    // ---- F2 regression: a filename with an embedded quote cannot inject extra args/flags ----

    [Theory]
    [InlineData(LaunchOs.Linux)]
    [InlineData(LaunchOs.MacOS)]
    public void Embedded_quote_in_filename_stays_a_single_argv_token_open(LaunchOs os)
    {
        // The exact audit exploit: a path containing a quote + " -a App" that previously split into argv.
        var evilPath = "/home/u/Downloads/a\" -a Calculator \"b.mp4";

        var cmd = LaunchCommands.OpenFile(os, evilPath);

        // The whole malicious path is ONE token — no -a / Calculator injected.
        Assert.Equal([evilPath], cmd.Arguments);
        Assert.DoesNotContain("-a", cmd.Arguments);
        Assert.DoesNotContain("Calculator", cmd.Arguments);
    }

    [Fact]
    public void Embedded_quote_in_filename_stays_a_single_argv_token_macos_reveal()
    {
        var evilPath = "/home/u/Downloads/a\" -a Calculator \"b.mp4";

        var cmd = LaunchCommands.RevealInFolder(LaunchOs.MacOS, evilPath);

        Assert.Equal(["-R", evilPath], cmd.Arguments); // -R then the whole path as ONE token
        Assert.DoesNotContain("Calculator", cmd.Arguments);
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