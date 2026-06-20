using DownloadManager.Core.History;
using DownloadManager.Core.Routing;
using DownloadManager.UI.Services;

namespace DownloadManager.UI.ViewModels;

/// <summary>
/// One read-only history row (ADR-0019): name, size, terminal state, plus Open-file and Open-folder
/// actions that shell out through the injected <see cref="IFileLauncher"/>. A failure (e.g. the file was
/// moved or deleted) is surfaced via <paramref name="reportError"/> rather than thrown. Pure BCL +
/// Core + the launcher seam — no Avalonia types, so it is headless-testable.
/// </summary>
public sealed class HistoryItemViewModel : ObservableObject
{
    private readonly HistoryRecord _record;
    private readonly IFileLauncher? _launcher;
    private readonly Action<string?> _reportError;
    private readonly Func<string, Task>? _onReAdd;

    public HistoryItemViewModel(
        HistoryRecord record, IFileLauncher? launcher, Action<string?> reportError, Func<string, Task>? onReAdd = null)
    {
        _record = record;
        _launcher = launcher;
        _reportError = reportError;
        _onReAdd = onReAdd;

        OpenFileCommand = new AsyncRelayCommand(() => { OpenFile(); return Task.CompletedTask; });
        OpenFolderCommand = new AsyncRelayCommand(() => { OpenFolder(); return Task.CompletedTask; });
        ReAddCommand = new AsyncRelayCommand(() => _onReAdd?.Invoke(_record.Id) ?? Task.CompletedTask, () => _onReAdd is not null);
    }

    public string Id => _record.Id;

    // Strip bidi/control chars from the displayed name so a right-to-left override can't disguise the
    // extension in the history list (audit F3).
    public string Name => SafeFileName.StripBidiControls(_record.Name);

    public string SavedPath => _record.SavedPath;

    public string SizeText => DisplayFormat.Bytes(_record.Size);

    public string StateText => _record.State switch
    {
        HistoryState.Completed => "Completed",
        HistoryState.Failed => "Failed",
        HistoryState.Cancelled => "Cancelled",
        _ => _record.State.ToString(),
    };

    public AsyncRelayCommand OpenFileCommand { get; }

    public AsyncRelayCommand OpenFolderCommand { get; }

    /// <summary>Re-add this finished download to the queue (ADR-0021): appends a fresh Queued event.</summary>
    public AsyncRelayCommand ReAddCommand { get; }

    public void OpenFile() => Apply(_launcher?.OpenFile(_record.SavedPath));

    public void OpenFolder() => Apply(_launcher?.RevealInFolder(_record.SavedPath));

    private void Apply(LaunchResult? result)
    {
        if (result is { } r)
        {
            _reportError(r.Ok ? null : r.Error);
        }
    }
}