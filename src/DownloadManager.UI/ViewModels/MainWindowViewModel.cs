using System.Collections.ObjectModel;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Http;
using DownloadManager.Core.Import;
using DownloadManager.Core.Routing;
using DownloadManager.Core.Scheduler;
using DownloadManager.UI.Services;
using Microsoft.Extensions.Logging;

namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Root view-model. Owns the download rows and the top-level commands (Add URL, Import list). Holds no
/// engine logic — it talks to <see cref="IDownloadScheduler"/> only and reflects state read from each
/// <see cref="IDownloadHandle"/>. Pure BCL + Core; Avalonia-free so it is headless-testable.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDownloadScheduler _scheduler;
    private readonly TimeProvider _timeProvider;
    private readonly IFilePicker _filePicker;
    private readonly ICredentialPrompt _credentialPrompt;
    private readonly IImportDialog _importDialog;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly TimeSpan? _speedWindow;
    private readonly IFileRouter? _router;
    private readonly DownloadDefaults _defaults;
    private readonly IHistoryStore? _historyStore;
    private readonly IFileLauncher? _fileLauncher;

    // Tracks each row's last-bucketed section so Tick only moves rows whose section actually changed.
    private readonly Dictionary<DownloadItemViewModel, QueueSection> _section = [];

    // Ids already written to history, so each terminal download is recorded exactly once (Phase 9).
    private readonly HashSet<DownloadId> _historyWritten = [];

    private string _newUrl = string.Empty;
    private string _importSummary = string.Empty;
    private string? _historyError;

    public MainWindowViewModel(
        IDownloadScheduler scheduler,
        TimeProvider timeProvider,
        IFilePicker filePicker,
        ICredentialPrompt credentialPrompt,
        IImportDialog importDialog,
        ILogger<MainWindowViewModel> logger,
        string? downloadsDirectory = null,
        TimeSpan? speedWindow = null,
        IFileRouter? router = null,
        DownloadDefaults? defaults = null,
        QueueSettingsViewModel? queueSettings = null,
        IHistoryStore? historyStore = null,
        IFileLauncher? fileLauncher = null)
    {
        _scheduler = scheduler;
        _timeProvider = timeProvider;
        _filePicker = filePicker;
        _credentialPrompt = credentialPrompt;
        _importDialog = importDialog;
        _logger = logger;
        _speedWindow = speedWindow;
        // Routing (ADR-0017) chooses the per-download destination by extension. When no router is
        // injected (headless tests), fall back to the flat Downloads directory.
        _router = router;
        // Read live (Phase 8): a settings-panel Save updates this shared instance, so new downloads pick
        // up the new segment count while in-flight ones keep theirs.
        _defaults = defaults ?? new DownloadDefaults();
        QueueSettings = queueSettings;
        // Read-only history (Phase 9). Optional in headless tests that don't exercise it.
        _historyStore = historyStore;
        _fileLauncher = fileLauncher;
        if (_historyStore is not null)
        {
            // Seed the view newest-first from the chronological store, and pre-mark seeded ids so a
            // reload does not re-append (the store appends; the view reverses for display).
            foreach (var record in _historyStore.Load())
            {
                History.Insert(0, NewHistoryItem(record));
            }
        }
        DownloadsDirectory = downloadsDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        AddCommand = new AsyncRelayCommand(AddCurrentUrlAsync, () => IsValidHttpUrl(_newUrl));
        ImportCommand = new AsyncRelayCommand(ImportListAsync);
        ImportDialogCommand = new AsyncRelayCommand(() => _importDialog.ShowAsync(EnqueueManyAsync));
    }

    public string Title => "Download Manager";

    public string DownloadsDirectory { get; }

    /// <summary>The inline queue-settings panel (Phase 8); null in headless tests that don't exercise it.</summary>
    public QueueSettingsViewModel? QueueSettings { get; }

    /// <summary>Every row, in arrival order — the authoritative list the buckets are partitioned from.</summary>
    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

    /// <summary>Rows currently running or retrying (Phase 8 queue split).</summary>
    public ObservableCollection<DownloadItemViewModel> Running { get; } = [];

    /// <summary>Rows queued but not yet started.</summary>
    public ObservableCollection<DownloadItemViewModel> Waiting { get; } = [];

    /// <summary>Rows that are paused or terminal.</summary>
    public ObservableCollection<DownloadItemViewModel> Finished { get; } = [];

    public bool HasRunning => Running.Count > 0;

    public bool HasWaiting => Waiting.Count > 0;

    public bool HasFinished => Finished.Count > 0;

    /// <summary>Read-only download history (Phase 9), newest-first. Persists across sessions.</summary>
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    /// <summary>Last open / reveal failure (e.g. the file was moved or deleted), or null. Surfaced in the UI.</summary>
    public string? HistoryError
    {
        get => _historyError;
        private set => SetProperty(ref _historyError, value);
    }

    public string NewUrl
    {
        get => _newUrl;
        set
        {
            if (SetProperty(ref _newUrl, value))
            {
                AddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ImportSummary
    {
        get => _importSummary;
        private set => SetProperty(ref _importSummary, value);
    }

    public AsyncRelayCommand AddCommand { get; }

    public AsyncRelayCommand ImportCommand { get; }

    /// <summary>Opens the import-review dialog (paste / clipboard auto-paste); ticked URLs enqueue normally.</summary>
    public AsyncRelayCommand ImportDialogCommand { get; }

    /// <summary>UI-timer tick: refresh every row's derived state (lock-free reads) and re-bucket any whose
    /// section changed. Runs on the UI thread.</summary>
    public void Tick()
    {
        foreach (var item in Downloads)
        {
            item.Refresh();
            Regroup(item);
            TryRecordHistory(item);
        }
    }

    /// <summary>Append a history record the first time a row is observed in a terminal state (Phase 9).</summary>
    private void TryRecordHistory(DownloadItemViewModel item)
    {
        if (_historyStore is null || !IsTerminal(item.Status) || !_historyWritten.Add(item.Id))
        {
            return;
        }

        var state = item.Status switch
        {
            DownloadStatus.Completed => HistoryState.Completed,
            DownloadStatus.Canceled => HistoryState.Cancelled,
            _ => HistoryState.Failed,
        };

        var record = HistoryRecord.From(item.Id, item.Name, item.SizeBytes, state, item.TargetPath);
        _historyStore.Append(record);
        History.Insert(0, NewHistoryItem(record)); // newest-first
        OnPropertyChanged(nameof(HasHistory));
    }

    private HistoryItemViewModel NewHistoryItem(HistoryRecord record) =>
        new(record, _fileLauncher, error => HistoryError = error);

    public async Task AddCurrentUrlAsync()
    {
        if (!Uri.TryCreate(_newUrl, UriKind.Absolute, out var url) || !IsHttp(url))
        {
            return;
        }

        await EnqueueAsync(url, DownloadCredentials.None).ConfigureAwait(true);
        NewUrl = string.Empty;
    }

    public async Task ImportListAsync()
    {
        var path = await _filePicker.PickListFileAsync().ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var result = await UrlListImporter.ImportFileAsync(path).ConfigureAwait(true);
        foreach (var url in result.Urls)
        {
            await EnqueueAsync(url, DownloadCredentials.None).ConfigureAwait(true);
        }

        ImportSummary = FormatImportSummary(result);
        LogImported(result.ImportedCount, result.SkippedCount, path);
    }

    /// <summary>Remove a row; cancels (and discards) first if the download is still live.</summary>
    public async Task RemoveAsync(DownloadItemViewModel item)
    {
        if (!IsTerminal(item.Status))
        {
            try
            {
                await _scheduler.CancelAsync(item.Id).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is InvalidDownloadTransitionException or KeyNotFoundException)
            {
                // Raced to terminal/removed already — fine, we're removing it anyway.
            }
        }

        RemoveRow(item);
        LogRemoved(item.Id);
    }

    /// <summary>
    /// NeedsCredentials flow (ADR-0011): prompt for fresh credentials, then re-enqueue the same target so
    /// the engine resumes from the retained on-disk progress. Credentials stay session-memory only. The
    /// row is replaced in place; the old (Failed) handle is left inert.
    /// </summary>
    public async Task ReauthorizeAndResumeAsync(DownloadItemViewModel item)
    {
        var credentials = await _credentialPrompt.PromptAsync(item.Name).ConfigureAwait(true);
        if (credentials is null)
        {
            return; // user cancelled
        }

        var resumed = new DownloadRequest
        {
            Id = DownloadId.New(),
            Url = item.Request.Url,
            TargetPath = item.Request.TargetPath,
            SegmentCount = item.Request.SegmentCount,
            Preallocation = item.Request.Preallocation,
            ExpectedSha256 = item.Request.ExpectedSha256,
            Credentials = credentials,
        };

        var handle = await _scheduler.EnqueueAsync(resumed).ConfigureAwait(true);
        var replacement = NewItem(resumed, handle);

        var index = Downloads.IndexOf(item);
        RemoveRow(item);
        AddRow(replacement, index < 0 ? Downloads.Count : index);

        LogReauthorized(item.Id, resumed.Id);
    }

    /// <summary>Enqueue several URLs via the normal add-path (used by the import dialog). They start normally.</summary>
    private async Task EnqueueManyAsync(IReadOnlyList<Uri> urls)
    {
        foreach (var url in urls)
        {
            await EnqueueAsync(url, DownloadCredentials.None).ConfigureAwait(true);
        }
    }

    private async Task EnqueueAsync(Uri url, DownloadCredentials credentials)
    {
        var request = new DownloadRequest
        {
            Id = DownloadId.New(),
            Url = url,
            TargetPath = ResolveTargetPath(url),
            SegmentCount = _defaults.SegmentCount, // live: a settings Save changes this for new downloads
            Credentials = credentials,
        };

        var handle = await _scheduler.EnqueueAsync(request).ConfigureAwait(true);
        AddRow(NewItem(request, handle), Downloads.Count);
        LogEnqueued(request.Id, UrlRedaction.Redact(url));
    }

    /// <summary>Add a row to the master list and its current section bucket (Phase 8).</summary>
    private void AddRow(DownloadItemViewModel item, int index)
    {
        Downloads.Insert(Math.Clamp(index, 0, Downloads.Count), item);
        var section = item.Section;
        _section[item] = section;
        BucketFor(section).Add(item);
        RaiseBucketCounts();
    }

    /// <summary>Remove a row from the master list and whichever bucket holds it.</summary>
    private void RemoveRow(DownloadItemViewModel item)
    {
        Downloads.Remove(item);
        if (_section.Remove(item, out var section))
        {
            BucketFor(section).Remove(item);
            RaiseBucketCounts();
        }
    }

    /// <summary>Move a row to a new bucket if (and only if) its section changed since last bucketing.</summary>
    private void Regroup(DownloadItemViewModel item)
    {
        var section = item.Section;
        if (_section.TryGetValue(item, out var previous) && previous == section)
        {
            return;
        }

        if (_section.TryGetValue(item, out previous))
        {
            BucketFor(previous).Remove(item);
        }

        _section[item] = section;
        BucketFor(section).Add(item);
        RaiseBucketCounts();
    }

    private ObservableCollection<DownloadItemViewModel> BucketFor(QueueSection section) => section switch
    {
        QueueSection.Running => Running,
        QueueSection.Waiting => Waiting,
        _ => Finished,
    };

    private void RaiseBucketCounts()
    {
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(HasFinished));
    }

    /// <summary>Extension routing (ADR-0017) when a router is present; otherwise the flat Downloads folder.</summary>
    private string ResolveTargetPath(Uri url)
    {
        var fileName = FileNameFor(url);
        if (_router is not null)
        {
            return _router.ResolveDestination(fileName, explicitPath: null);
        }

        // No router (headless): still sanitize the untrusted name to a safe leaf before it becomes a path.
        Directory.CreateDirectory(DownloadsDirectory);
        return Path.Combine(DownloadsDirectory, SafeFileName.Sanitize(fileName));
    }

    private DownloadItemViewModel NewItem(DownloadRequest request, IDownloadHandle handle) =>
        new(request, handle, _scheduler, _timeProvider, RemoveAsync, ReauthorizeAndResumeAsync, _speedWindow);

    private static string FileNameFor(Uri url)
    {
        var name = Path.GetFileName(url.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? $"download-{DateTime.UtcNow:yyyyMMddHHmmss}" : name;
    }

    private static string FormatImportSummary(UrlImportResult result)
    {
        if (result.SkippedCount == 0)
        {
            return $"Imported {result.ImportedCount} URL(s).";
        }

        var reasons = result.Skipped.Select(s => $"  line {s.LineNumber}: {s.Reason} — {s.RawLine}");
        return $"Imported {result.ImportedCount}, skipped {result.SkippedCount}:{Environment.NewLine}"
            + string.Join(Environment.NewLine, reasons);
    }

    private bool IsValidHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && IsHttp(url);

    private static bool IsHttp(Uri url) =>
        url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps;

    private static bool IsTerminal(DownloadStatus status) =>
        status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

    [LoggerMessage(Level = LogLevel.Information, Message = "UI enqueued download {Id} for {Url}.")]
    private partial void LogEnqueued(DownloadId id, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI imported {Imported} URL(s), skipped {Skipped} from {Path}.")]
    private partial void LogImported(int imported, int skipped, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI removed download {Id}.")]
    private partial void LogRemoved(DownloadId id);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI re-authorized download {OldId}; resuming as {NewId}.")]
    private partial void LogReauthorized(DownloadId oldId, DownloadId newId);
}