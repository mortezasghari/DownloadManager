using System.Collections.ObjectModel;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
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
    private readonly int _defaultSegmentCount;

    private string _newUrl = string.Empty;
    private string _importSummary = string.Empty;

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
        DownloadDefaults? defaults = null)
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
        _defaultSegmentCount = defaults?.SegmentCount ?? 8;
        DownloadsDirectory = downloadsDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        AddCommand = new AsyncRelayCommand(AddCurrentUrlAsync, () => IsValidHttpUrl(_newUrl));
        ImportCommand = new AsyncRelayCommand(ImportListAsync);
        ImportDialogCommand = new AsyncRelayCommand(() => _importDialog.ShowAsync(EnqueueManyAsync));
    }

    public string Title => "Download Manager";

    public string DownloadsDirectory { get; }

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

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

    /// <summary>UI-timer tick: refresh every row's derived state (lock-free reads). Runs on the UI thread.</summary>
    public void Tick()
    {
        foreach (var item in Downloads)
        {
            item.Refresh();
        }
    }

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

        Downloads.Remove(item);
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

        var index = Downloads.IndexOf(item);
        var handle = await _scheduler.EnqueueAsync(resumed).ConfigureAwait(true);
        var replacement = NewItem(resumed, handle);

        if (index >= 0)
        {
            Downloads[index] = replacement;
        }
        else
        {
            Downloads.Add(replacement);
        }

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
            SegmentCount = _defaultSegmentCount,
            Credentials = credentials,
        };

        var handle = await _scheduler.EnqueueAsync(request).ConfigureAwait(true);
        Downloads.Add(NewItem(request, handle));
        LogEnqueued(request.Id, url);
    }

    /// <summary>Extension routing (ADR-0017) when a router is present; otherwise the flat Downloads folder.</summary>
    private string ResolveTargetPath(Uri url)
    {
        var fileName = FileNameFor(url);
        if (_router is not null)
        {
            return _router.ResolveDestination(fileName, explicitPath: null);
        }

        Directory.CreateDirectory(DownloadsDirectory);
        return Path.Combine(DownloadsDirectory, fileName);
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
    private partial void LogEnqueued(DownloadId id, Uri url);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI imported {Imported} URL(s), skipped {Skipped} from {Path}.")]
    private partial void LogImported(int imported, int skipped, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI removed download {Id}.")]
    private partial void LogRemoved(DownloadId id);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI re-authorized download {OldId}; resuming as {NewId}.")]
    private partial void LogReauthorized(DownloadId oldId, DownloadId newId);
}