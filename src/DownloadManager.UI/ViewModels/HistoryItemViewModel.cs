using DownloadManager.Core.History;
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

    public HistoryItemViewModel(HistoryRecord record, IFileLauncher? launcher, Action<string?> reportError)
    {
        _record = record;
        _launcher = launcher;
        _reportError = reportError;

        OpenFileCommand = new AsyncRelayCommand(() => { OpenFile(); return Task.CompletedTask; });
        OpenFolderCommand = new AsyncRelayCommand(() => { OpenFolder(); return Task.CompletedTask; });
    }

    public string Id => _record.Id;

    public string Name => _record.Name;

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