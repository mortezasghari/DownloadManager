using System.Collections.ObjectModel;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Http;
using DownloadManager.Core.Import;
using DownloadManager.Core.Lifecycle;
using DownloadManager.Core.Recovery;
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
    private readonly ILifecycleLog? _lifecycleLog;

    // Tracks each row's last-bucketed section so Tick only moves rows whose section actually changed.
    private readonly Dictionary<DownloadItemViewModel, QueueSection> _section = [];

    // Ids already written to history, so each terminal download is recorded exactly once (Phase 9).
    private readonly HashSet<DownloadId> _historyWritten = [];

    // Last lifecycle-event type appended per id, so Tick logs a transition only when it actually changes
    // (the lifecycle log is the source of truth; ADR-0021).
    private readonly Dictionary<DownloadId, LifecycleEventType> _lastLifecycle = [];

    // Downloads this VM paused as part of a GLOBAL queue pause (ADR-0022), to resume on Play.
    private readonly HashSet<DownloadId> _globallyPaused = [];

    private string _newUrl = string.Empty;
    private string _importSummary = string.Empty;
    private string? _historyError;
    private bool _isQueuePaused;

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
        IFileLauncher? fileLauncher = null,
        ILifecycleLog? lifecycleLog = null,
        QueueRecoveryService? recovery = null)
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
        // Lifecycle-event log = source of truth (ADR-0021); the queue + history are projections of it.
        _lifecycleLog = lifecycleLog;
        // Recover FIRST: replay the log, rebuild history.json from it, and capture the active downloads to
        // re-enqueue — so the history seeding below reflects the log-reconciled projection.
        RecoveredActive = recovery?.Recover();
        if (_historyStore is not null)
        {
            // Seed the view newest-first from the (now log-reconciled) store, and pre-mark seeded ids so
            // a reload does not re-append (the store appends; the view reverses for display).
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
        ToggleQueuePauseCommand = new AsyncRelayCommand(ToggleQueuePauseAsync);
    }

    /// <summary>Active downloads recovered from the lifecycle log, to be re-enqueued on startup (ADR-0021).</summary>
    public IReadOnlyList<DownloadRequest>? RecoveredActive { get; }

    /// <summary>Re-enqueue the recovered active downloads (no new lifecycle event — already in the log).</summary>
    public async Task RestoreRecoveredAsync()
    {
        if (RecoveredActive is null)
        {
            return;
        }

        foreach (var request in RecoveredActive)
        {
            // Already logged as active (id preserved); don't re-emit Queued on the first tick.
            _lastLifecycle[request.Id] = LifecycleEventType.Queued;
            await EnqueueRequestAsync(request, appendQueued: false).ConfigureAwait(true);
        }
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

    /// <summary>Rows that are parked (paused). Terminal downloads are not in the queue — they're in history.</summary>
    public ObservableCollection<DownloadItemViewModel> Paused { get; } = [];

    public bool HasRunning => Running.Count > 0;

    public bool HasWaiting => Waiting.Count > 0;

    public bool HasPaused => Paused.Count > 0;

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

    /// <summary>Global queue Pause/Play toggle (ADR-0022) — one action for the whole queue.</summary>
    public AsyncRelayCommand ToggleQueuePauseCommand { get; }

    /// <summary>Whether the whole queue is globally paused (ADR-0022). Orthogonal to per-item Postpone.</summary>
    public bool IsQueuePaused
    {
        get => _isQueuePaused;
        private set
        {
            if (SetProperty(ref _isQueuePaused, value))
            {
                OnPropertyChanged(nameof(QueuePauseLabel));
            }
        }
    }

    /// <summary>Label for the global Pause/Play button.</summary>
    public string QueuePauseLabel => _isQueuePaused ? "▶  Play queue" : "⏸  Pause queue";

    /// <summary>
    /// Global Pause/Play (ADR-0022). Pause halts the whole queue: the scheduler blocks promotion and every
    /// active download is paused via the existing per-download pause (bytes retained). Play un-blocks
    /// promotion and resumes the downloads we paused. One action, the whole queue — there is no per-item pause.
    /// </summary>
    public async Task ToggleQueuePauseAsync()
    {
        if (!_isQueuePaused)
        {
            _scheduler.PauseQueue(); // block promotion first, then stop the active transfers
            foreach (var item in Downloads)
            {
                if (item.Status is DownloadStatus.Running or DownloadStatus.Retrying && _globallyPaused.Add(item.Id))
                {
                    try
                    {
                        await _scheduler.PauseAsync(item.Id).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is InvalidDownloadTransitionException or KeyNotFoundException)
                    {
                        _globallyPaused.Remove(item.Id); // raced to terminal — nothing to pause
                    }
                }
            }

            IsQueuePaused = true;
            LogQueue("paused");
        }
        else
        {
            foreach (var id in _globallyPaused)
            {
                try
                {
                    await _scheduler.ResumeAsync(id).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is InvalidDownloadTransitionException or KeyNotFoundException)
                {
                    // Raced to terminal between pause and play — fine.
                }
            }

            _globallyPaused.Clear();
            _scheduler.ResumeQueue(); // un-block promotion last
            IsQueuePaused = false;
            LogQueue("resumed");
        }
    }

    /// <summary>UI-timer tick: refresh every row's derived state (lock-free reads) and re-bucket any whose
    /// section changed. Runs on the UI thread.</summary>
    public void Tick()
    {
        List<DownloadItemViewModel>? terminal = null;
        foreach (var item in Downloads)
        {
            item.Refresh();
            TryLogTransition(item);

            // A terminal download leaves the active queue BECAUSE it's terminal — the same classification
            // the projection uses (terminal → history, not active; ADR-0021). It is recorded to history and
            // then removed from the live queue, so it appears only in history, mirroring a log replay.
            if (IsTerminal(item.Status))
            {
                TryRecordHistory(item);
                (terminal ??= []).Add(item);
            }
            else
            {
                Regroup(item);
            }
        }

        // Deferred removal (can't mutate Downloads while enumerating it).
        if (terminal is not null)
        {
            foreach (var item in terminal)
            {
                RemoveRow(item);
            }
        }
    }

    /// <summary>
    /// Append the observed lifecycle transition to the log when an item's mapped event type changes
    /// (ADR-0021) — best-effort, source-of-truth for the queue/history projections. The authoritative
    /// append-first <c>Queued</c> is emitted at enqueue; this captures Started/Paused/terminal afterwards.
    /// </summary>
    private void TryLogTransition(DownloadItemViewModel item)
    {
        if (_lifecycleLog is null)
        {
            return;
        }

        var type = MapToLifecycle(item.Status);
        if (_lastLifecycle.TryGetValue(item.Id, out var last) && last == type)
        {
            return;
        }

        _lastLifecycle[item.Id] = type;
        AppendLifecycle(item.Id, type, item.Request, item.Name, item.SizeBytes);
    }

    private static LifecycleEventType MapToLifecycle(DownloadStatus status) => status switch
    {
        DownloadStatus.Queued => LifecycleEventType.Queued,
        DownloadStatus.Running or DownloadStatus.Retrying => LifecycleEventType.Started,
        DownloadStatus.Paused => LifecycleEventType.Paused,
        DownloadStatus.Completed => LifecycleEventType.Completed,
        DownloadStatus.Failed => LifecycleEventType.Failed,
        _ => LifecycleEventType.Stopped, // Canceled
    };

    /// <summary>Append one lifecycle event (URL userinfo redacted, SH-1 F4). No-op without a log.</summary>
    private void AppendLifecycle(DownloadId id, LifecycleEventType type, DownloadRequest request, string name, long size) =>
        _lifecycleLog?.Append(new LifecycleEvent
        {
            Id = id.ToString(),
            Type = type,
            Url = UrlRedaction.Redact(request.Url),
            TargetPath = request.TargetPath,
            SegmentCount = request.SegmentCount,
            ExpectedSha256 = request.ExpectedSha256,
            Name = name,
            Size = size,
        });

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
        new(record, _fileLauncher, error => HistoryError = error, ReAddFromHistoryAsync);

    /// <summary>
    /// Re-add a finished download to the queue (ADR-0021): append a fresh <c>Queued</c> event (new id,
    /// reconstructed from the original's logged URL/target). The original terminal record stays in history;
    /// the new id appears in the active queue — both projections derive from the same log.
    /// </summary>
    public async Task ReAddFromHistoryAsync(string originalId)
    {
        if (_lifecycleLog is null)
        {
            return;
        }

        // The history record holds no URL; recover it from the original download's logged events.
        LifecycleEvent? source = null;
        foreach (var e in _lifecycleLog.ReadAll())
        {
            if (e.Id == originalId && e.Url is not null && e.TargetPath is not null)
            {
                source = e;
            }
        }

        if (source is null)
        {
            return;
        }

        var request = new DownloadRequest
        {
            Id = DownloadId.New(),
            Url = new Uri(source.Url!),
            TargetPath = source.TargetPath!,
            SegmentCount = source.SegmentCount > 0 ? source.SegmentCount : 1,
            ExpectedSha256 = source.ExpectedSha256,
        };

        await EnqueueRequestAsync(request, appendQueued: true).ConfigureAwait(true);
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

    /// <summary>
    /// Stop a download (ADR-0022): terminal. Append the <c>Stopped</c> lifecycle event <b>first</b> (durable
    /// truth), then stop it via the existing cancel path. It leaves the active queue and appears in history
    /// through the same terminal projection as a completed download — not removed here directly.
    /// </summary>
    public async Task StopAsync(DownloadItemViewModel item)
    {
        if (IsTerminal(item.Status))
        {
            return;
        }

        // Append-event-first: the terminal Stopped event is the durable truth; pre-mark so the tick that
        // observes the resulting Canceled state does not log a duplicate transition.
        AppendLifecycle(item.Id, LifecycleEventType.Stopped, item.Request, item.Name, item.SizeBytes);
        _lastLifecycle[item.Id] = LifecycleEventType.Stopped;

        try
        {
            await _scheduler.CancelAsync(item.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidDownloadTransitionException or KeyNotFoundException)
        {
            // Raced to terminal already — fine; the tick still records history and drops it from the queue.
        }

        LogStopped(item.Id);
    }

    /// <summary>
    /// Postpone a download (ADR-0022): send it to the tail of the queue and stop its active transfer if
    /// running (bytes retained). Append-event-first (a fresh <c>Queued</c> re-queue marker), then reflect
    /// via the scheduler's reposition. Not terminal, no benched state, no un-postpone — postpone again to
    /// go further back. Resumes naturally as the queue drains.
    /// </summary>
    public async Task PostponeAsync(DownloadItemViewModel item)
    {
        if (IsTerminal(item.Status))
        {
            return;
        }

        AppendLifecycle(item.Id, LifecycleEventType.Queued, item.Request, item.Name, item.SizeBytes);
        _lastLifecycle[item.Id] = LifecycleEventType.Queued;

        try
        {
            await _scheduler.PostponeAsync(item.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidDownloadTransitionException or KeyNotFoundException)
        {
            // Raced to terminal — nothing to postpone.
        }

        LogPostponed(item.Id);
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

    private Task EnqueueAsync(Uri url, DownloadCredentials credentials)
    {
        var request = new DownloadRequest
        {
            Id = DownloadId.New(),
            Url = url,
            TargetPath = ResolveTargetPath(url),
            SegmentCount = _defaults.SegmentCount, // live: a settings Save changes this for new downloads
            Credentials = credentials,
        };

        return EnqueueRequestAsync(request, appendQueued: true);
    }

    /// <summary>
    /// Enqueue a fully-built request. When <paramref name="appendQueued"/>, the <c>Queued</c> lifecycle
    /// event is appended to the log <b>first</b> (the durable, non-destructive step) and only then is the
    /// download reflected in the scheduler + the in-memory rows (the disposable projection) — so a crash
    /// in the window recovers from the log rather than losing the download (ADR-0021). Recovery passes
    /// <c>false</c> because the event is already in the log.
    /// </summary>
    private async Task EnqueueRequestAsync(DownloadRequest request, bool appendQueued)
    {
        if (appendQueued)
        {
            // Append-event-first: durable truth before any in-memory reflect.
            AppendLifecycle(request.Id, LifecycleEventType.Queued, request, Path.GetFileName(request.TargetPath), 0);
            _lastLifecycle[request.Id] = LifecycleEventType.Queued;
        }

        var handle = await _scheduler.EnqueueAsync(request).ConfigureAwait(true);
        AddRow(NewItem(request, handle), Downloads.Count);
        LogEnqueued(request.Id, UrlRedaction.Redact(request.Url));
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
        _ => Paused,
    };

    private void RaiseBucketCounts()
    {
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(HasPaused));
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
        new(request, handle, _scheduler, _timeProvider, StopAsync, ReauthorizeAndResumeAsync, PostponeAsync, _speedWindow);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "UI stopped download {Id}.")]
    private partial void LogStopped(DownloadId id);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI postponed download {Id} to the queue tail.")]
    private partial void LogPostponed(DownloadId id);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI {Action} the queue.")]
    private partial void LogQueue(string action);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI re-authorized download {OldId}; resuming as {NewId}.")]
    private partial void LogReauthorized(DownloadId oldId, DownloadId newId);
}