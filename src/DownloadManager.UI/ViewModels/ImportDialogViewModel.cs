using System.Collections.ObjectModel;
using DownloadManager.Core.Import;
using DownloadManager.UI.Services;

namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Import-review dialog (Phase 6). Raw text — pasted, or auto-pasted from the clipboard via the injected
/// <see cref="IClipboardTextSource"/> seam — is fed to the unchanged <see cref="UrlListImporter"/>; the
/// parsed http/https URLs become a ticked checklist and the importer's skip-with-reason summary is
/// surfaced. "Add to Queue" enqueues the <b>ticked</b> URLs via the normal add-path — they start like any
/// other download (no Held state, no manual start). Avalonia-free, so it is headless-testable.
/// </summary>
public sealed class ImportDialogViewModel : ObservableObject
{
    private readonly IClipboardTextSource _clipboard;
    private readonly Func<IReadOnlyList<Uri>, Task> _addToQueue;

    private string _rawText = string.Empty;
    private string _summary = "No URLs found.";

    public ImportDialogViewModel(IClipboardTextSource clipboard, Func<IReadOnlyList<Uri>, Task> addToQueue)
    {
        _clipboard = clipboard;
        _addToQueue = addToQueue;
        PasteFromClipboardCommand = new AsyncRelayCommand(LoadFromClipboardAsync);
        AddToQueueCommand = new AsyncRelayCommand(AddSelectedToQueueAsync, () => Candidates.Count > 0);
        CancelCommand = new AsyncRelayCommand(() => { CloseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; });
    }

    /// <summary>Raised when the dialog should close (after a successful add, or on cancel).</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<ImportCandidate> Candidates { get; } = [];

    /// <summary>The editable source text. Editing it re-parses the checklist live.</summary>
    public string RawText
    {
        get => _rawText;
        set
        {
            if (SetProperty(ref _rawText, value))
            {
                Reparse();
            }
        }
    }

    /// <summary>Importer summary: count imported + per-line skip reasons, or "No URLs found.".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool HasCandidates => Candidates.Count > 0;

    public AsyncRelayCommand PasteFromClipboardCommand { get; }

    public AsyncRelayCommand AddToQueueCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    /// <summary>Reads the clipboard (auto-paste on open / explicit paste) and re-parses. Null/empty/
    /// whitespace/non-text clipboards yield "No URLs found." with no candidates — never throws.</summary>
    public async Task LoadFromClipboardAsync()
    {
        var text = await _clipboard.GetTextAsync().ConfigureAwait(true);
        _rawText = text ?? string.Empty;
        OnPropertyChanged(nameof(RawText));
        Reparse();
    }

    /// <summary>Enqueues the ticked candidates via the add-path, then requests close. No-op if none ticked.</summary>
    public async Task AddSelectedToQueueAsync()
    {
        var selected = Candidates.Where(c => c.IsSelected).Select(c => c.Url).ToList();
        if (selected.Count == 0)
        {
            return; // nothing ticked: no-op
        }

        await _addToQueue(selected).ConfigureAwait(true);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Reparse()
    {
        Candidates.Clear();
        var result = UrlListImporter.ParseText(_rawText);
        foreach (var url in result.Urls)
        {
            Candidates.Add(new ImportCandidate(url));
        }

        Summary = result.ImportedCount == 0 && result.SkippedCount == 0
            ? "No URLs found."
            : FormatSummary(result);

        OnPropertyChanged(nameof(HasCandidates));
        AddToQueueCommand.RaiseCanExecuteChanged();
    }

    private static string FormatSummary(UrlImportResult result)
    {
        if (result.SkippedCount == 0)
        {
            return $"Found {result.ImportedCount} URL(s).";
        }

        var reasons = result.Skipped.Select(s => $"  line {s.LineNumber}: {s.Reason} — {s.RawLine}");
        return $"Found {result.ImportedCount}, skipped {result.SkippedCount}:{Environment.NewLine}"
            + string.Join(Environment.NewLine, reasons);
    }
}