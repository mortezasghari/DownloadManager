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

    /// <summary>Open an http/https URL in the default browser (the notify-only "view release" link, ADR-0025).</summary>
    LaunchResult OpenUrl(string url);
}

/// <summary>The three desktop platforms the launcher branches on. Explicit so command construction is
/// unit-testable per RID without depending on the host OS.</summary>
public enum LaunchOs
{
    Linux,
    Windows,
    MacOS,
}

/// <summary>A fully-formed command: program + an <b>argument list</b> (each element a separate argv token,
/// so .NET — not hand-rolled quoting — handles escaping) + the shell-execute flag for the Windows
/// "open the path itself" case. Pure data, so tests can assert the exact per-OS argv (ADR-0020 / F2).</summary>
public readonly record struct LaunchCommand(string FileName, IReadOnlyList<string> Arguments, bool UseShellExecute);

/// <summary>
/// Pure, side-effect-free construction of the per-OS open / reveal commands (ADR-0019/0020). Separated
/// from execution so the platform branching is verifiable on every RID even where no GUI app can launch.
/// Arguments are an explicit argv list (never a hand-quoted string), so a filename containing a quote
/// cannot break out and inject extra flags (audit F2). The path is always absolute, which also keeps a
/// leading-dash filename from being parsed as an option.
/// <list type="bullet">
/// <item>Linux: open → <c>xdg-open &lt;path&gt;</c>; reveal → <c>xdg-open &lt;dir&gt;</c> (xdg-open has no
/// select-file, so opening the containing directory is the portable behavior).</item>
/// <item>Windows: open → shell-execute the path; reveal → <c>explorer.exe</c> with a single
/// <c>/select,&lt;path&gt;</c> argv token.</item>
/// <item>macOS: open → <c>open &lt;path&gt;</c>; reveal → <c>open -R &lt;path&gt;</c>.</item>
/// </list>
/// </summary>
public static class LaunchCommands
{
    public static LaunchCommand OpenFile(LaunchOs os, string path) => os switch
    {
        LaunchOs.Windows => new LaunchCommand(path, [], UseShellExecute: true),
        LaunchOs.MacOS => new LaunchCommand("open", [path], UseShellExecute: false),
        _ => new LaunchCommand("xdg-open", [path], UseShellExecute: false),
    };

    /// <summary>Open a URL: Windows shell-executes it; macOS <c>open</c>; Linux <c>xdg-open</c>.</summary>
    public static LaunchCommand OpenUrl(LaunchOs os, string url) => os switch
    {
        LaunchOs.Windows => new LaunchCommand(url, [], UseShellExecute: true),
        LaunchOs.MacOS => new LaunchCommand("open", [url], UseShellExecute: false),
        _ => new LaunchCommand("xdg-open", [url], UseShellExecute: false),
    };

    public static LaunchCommand RevealInFolder(LaunchOs os, string path) => os switch
    {
        // explorer wants "/select,<path>" as a SINGLE token; .NET quotes it as needed when it reaches argv.
        LaunchOs.Windows => new LaunchCommand("explorer.exe", [$"/select,{path}"], UseShellExecute: false),
        LaunchOs.MacOS => new LaunchCommand("open", ["-R", path], UseShellExecute: false),
        // xdg-open cannot select a file; open the containing directory instead.
        _ => new LaunchCommand("xdg-open", [ContainingDirectory(path)], UseShellExecute: false),
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
}