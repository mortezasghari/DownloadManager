using System.Diagnostics;

namespace DownloadManager.UI.Services;

/// <summary>
/// Real <see cref="IFileLauncher"/>: a per-platform <see cref="Process"/> shell-out (ADR-0019), AOT-safe
/// (no reflection). The exact command is built by <see cref="LaunchCommands"/>; this only checks the file
/// still exists and runs it, turning any failure into a <see cref="LaunchResult"/> rather than throwing.
/// </summary>
public sealed class ProcessFileLauncher : IFileLauncher
{
    public LaunchResult OpenFile(string savedPath) =>
        Run(savedPath, LaunchCommands.OpenFile(LaunchCommands.Current(), savedPath));

    public LaunchResult RevealInFolder(string savedPath) =>
        Run(savedPath, LaunchCommands.RevealInFolder(LaunchCommands.Current(), savedPath));

    private static LaunchResult Run(string savedPath, LaunchCommand command)
    {
        if (string.IsNullOrWhiteSpace(savedPath))
        {
            return LaunchResult.Failure("No file path recorded for this download.");
        }

        // The file may have been moved or deleted since the download finished. Per ADR-0019 we attempt and
        // report an error on failure — no file-existence tracking or greying-out — and an up-front check
        // gives a clear message instead of a silent xdg-open/open no-op.
        if (!File.Exists(savedPath))
        {
            return LaunchResult.Failure($"File no longer exists: {savedPath}");
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                UseShellExecute = command.UseShellExecute,
            };

            using var process = Process.Start(info);
            return process is null
                ? LaunchResult.Failure($"Could not start '{command.FileName}'.")
                : LaunchResult.Success;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return LaunchResult.Failure($"Could not open '{savedPath}': {ex.Message}");
        }
    }
}