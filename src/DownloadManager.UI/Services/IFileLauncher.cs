namespace DownloadManager.UI.Services;

/// <summary>Outcome of an open / reveal attempt. Failures carry a user-facing message; nothing throws.</summary>
public readonly record struct LaunchResult(bool Ok, string? Error)
{
    public static LaunchResult Success { get; } = new(true, null);

    public static LaunchResult Failure(string error) => new(false, error);
}

/// <summary>
/// Opens a finished download's file with the OS default app, or reveals it in the system file manager
/// (ADR-0019). Behind a seam so the view-model stays Avalonia-free and headless-testable; the real
/// implementation shells out per platform. A missing file is reported as a failure, never a crash.
/// </summary>
public interface IFileLauncher
{
    LaunchResult OpenFile(string savedPath);

    LaunchResult RevealInFolder(string savedPath);
}

/// <summary>The three desktop platforms the launcher branches on. Explicit so command construction is
/// unit-testable per RID without depending on the host OS.</summary>
public enum LaunchOs
{
    Linux,
    Windows,
    MacOS,
}

/// <summary>A fully-formed shell command: program + raw argument string (+ shell-execute flag for the
/// Windows "open the path itself" case). Pure data, so tests can assert the exact per-OS invocation.</summary>
public readonly record struct LaunchCommand(string FileName, string Arguments, bool UseShellExecute);

/// <summary>
/// Pure, side-effect-free construction of the per-OS open / reveal commands (ADR-0019). Separated from
/// execution so the platform branching is verifiable on every RID even where no GUI app can launch.
/// <list type="bullet">
/// <item>Linux: open → <c>xdg-open &lt;path&gt;</c>; reveal → <c>xdg-open &lt;dir&gt;</c> (xdg-open has no
/// select-file, so opening the containing directory is the portable behavior).</item>
/// <item>Windows: open → shell-execute the path; reveal → <c>explorer.exe /select,"&lt;path&gt;"</c>.</item>
/// <item>macOS: open → <c>open "&lt;path&gt;"</c>; reveal → <c>open -R "&lt;path&gt;"</c>.</item>
/// </list>
/// </summary>
public static class LaunchCommands
{
    public static LaunchCommand OpenFile(LaunchOs os, string path) => os switch
    {
        LaunchOs.Windows => new LaunchCommand(path, string.Empty, UseShellExecute: true),
        LaunchOs.MacOS => new LaunchCommand("open", Quote(path), UseShellExecute: false),
        _ => new LaunchCommand("xdg-open", Quote(path), UseShellExecute: false),
    };

    public static LaunchCommand RevealInFolder(LaunchOs os, string path) => os switch
    {
        LaunchOs.Windows => new LaunchCommand("explorer.exe", $"/select,{Quote(path)}", UseShellExecute: false),
        LaunchOs.MacOS => new LaunchCommand("open", $"-R {Quote(path)}", UseShellExecute: false),
        // xdg-open cannot select a file; open the containing directory instead.
        _ => new LaunchCommand("xdg-open", Quote(ContainingDirectory(path)), UseShellExecute: false),
    };

    /// <summary>The current host OS as a <see cref="LaunchOs"/> (AOT-safe — no reflection).</summary>
    public static LaunchOs Current()
    {
        if (OperatingSystem.IsWindows())
        {
            return LaunchOs.Windows;
        }

        return OperatingSystem.IsMacOS() ? LaunchOs.MacOS : LaunchOs.Linux;
    }

    internal static string ContainingDirectory(string path) =>
        Path.GetDirectoryName(path) is { Length: > 0 } dir ? dir : ".";

    private static string Quote(string value) => $"\"{value}\"";
}