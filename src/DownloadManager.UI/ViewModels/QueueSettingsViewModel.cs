using DownloadManager.Core.Configuration;
using DownloadManager.Core.Scheduler;
using DownloadManager.UI.Services;
using DownloadManager.UI.Versioning;
using Microsoft.Extensions.Logging;

namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Inline queue-settings panel (Phase 8 / ADR-0018): an expand-to-edit "ribbon", not a separate window.
/// Exposes exactly the five queue knobs. Edits are local until <see cref="SaveCommand"/> — there is no
/// instant-apply and no debounce, because these knobs are a coupled set and applying mid-edit would push
/// the engine through transient bad states and clamp half-typed values. On Save the whole set is
/// validated/persisted through the existing Phase-7 store (so the UI can never write a config the loader
/// would reject) and then applied <b>per knob, honestly</b>:
/// <list type="bullet">
/// <item>Max concurrent → live (resizes the gate now).</item>
/// <item>Segments / small-file threshold → newly started downloads only.</item>
/// <item>Retry attempts / backoff / timeout → the next attempt, including for in-flight downloads.</item>
/// </list>
/// Pure BCL + Core; references no Avalonia types, so it is headless-testable. Routing and the file-only
/// advanced knobs (copy buffer, checkpoint cadence) are deliberately not here (ADR-0018).
/// </summary>
public sealed class QueueSettingsViewModel : ObservableObject
{
    private const double BytesPerMiB = 1024d * 1024d;

    private readonly IDownloadScheduler _scheduler;
    private readonly EngineOptions _engineOptions;
    private readonly RetryOptions _retryOptions;
    private readonly DownloadDefaults _downloadDefaults;
    private readonly ScheduleOptions _schedule;
    private readonly string _settingsPath;
    private readonly ILogger _logger;
    private readonly string? _userProfile;
    private readonly IUpdateChecker? _updateChecker;
    private readonly IFileLauncher? _launcher;

    private string _updateStatus = string.Empty;
    private string? _releaseUrl;
    private bool _isExpanded;
    private int _maxConcurrentDownloads;
    private int _segmentsPerDownload;
    private double _smallFileThresholdMiB;
    private int _maxAttempts;
    private double _baseDelaySeconds;
    private double _maxDelaySeconds;
    private int _perAttemptTimeoutSeconds;
    private bool _scheduleEnabled;
    private TimeSpan? _scheduleStart = TimeSpan.Zero;
    private TimeSpan? _scheduleStop = TimeSpan.Zero;

    public QueueSettingsViewModel(
        IDownloadScheduler scheduler,
        EngineOptions engineOptions,
        RetryOptions retryOptions,
        DownloadDefaults downloadDefaults,
        ScheduleOptions schedule,
        string settingsPath,
        ILogger logger,
        string? userProfile = null,
        IUpdateChecker? updateChecker = null,
        IFileLauncher? launcher = null)
    {
        _scheduler = scheduler;
        _engineOptions = engineOptions;
        _retryOptions = retryOptions;
        _downloadDefaults = downloadDefaults;
        _schedule = schedule;
        _settingsPath = settingsPath;
        _logger = logger;
        _userProfile = userProfile;
        _updateChecker = updateChecker;
        _launcher = launcher;

        SaveCommand = new AsyncRelayCommand(() => { Save(); return Task.CompletedTask; });
        CancelCommand = new AsyncRelayCommand(() => { Cancel(); return Task.CompletedTask; });
        ToggleCommand = new AsyncRelayCommand(() => { Toggle(); return Task.CompletedTask; });
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
        ViewReleaseCommand = new AsyncRelayCommand(() => { ViewRelease(); return Task.CompletedTask; });

        LoadFromLive();
    }

    /// <summary>The app's own version, read from the assembly (ADR-0025) — never a hardcoded string.</summary>
    public string AppVersionText => $"Version {AppVersion.CurrentString()}";

    /// <summary>Result of the last update check: "Up to date", "Update available …", or empty.</summary>
    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    /// <summary>Whether an update was found and a release page can be opened.</summary>
    public bool CanViewRelease => !string.IsNullOrEmpty(_releaseUrl);

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public AsyncRelayCommand ViewReleaseCommand { get; }

    /// <summary>
    /// Manual, notify-only update check (ADR-0025): compares the running version to the latest GitHub
    /// release and reports the result. Never downloads/installs; any failure is a silent no-op surfaced as
    /// "couldn't check" — not a crash. A button (not a startup network call) keeps it unobtrusive.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        _releaseUrl = null;
        OnPropertyChanged(nameof(CanViewRelease));

        if (_updateChecker is null)
        {
            return;
        }

        UpdateStatus = "Checking…";
        var info = await _updateChecker.CheckAsync().ConfigureAwait(true);
        if (info is { } update)
        {
            _releaseUrl = update.ReleaseUrl;
            UpdateStatus = $"Update available: you're on {update.Current}, latest is {update.Latest}.";
        }
        else
        {
            UpdateStatus = $"You're on the latest version ({AppVersion.CurrentString()}).";
        }

        OnPropertyChanged(nameof(CanViewRelease));
    }

    private void ViewRelease()
    {
        if (!string.IsNullOrEmpty(_releaseUrl))
        {
            _launcher?.OpenUrl(_releaseUrl);
        }
    }

    /// <summary>Whether the ribbon is expanded for editing.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetProperty(ref _isExpanded, value);
    }

    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set => SetProperty(ref _maxConcurrentDownloads, value);
    }

    public int SegmentsPerDownload
    {
        get => _segmentsPerDownload;
        set => SetProperty(ref _segmentsPerDownload, value);
    }

    /// <summary>Small-file threshold in MiB (size under which a download is not segmented).</summary>
    public double SmallFileThresholdMiB
    {
        get => _smallFileThresholdMiB;
        set => SetProperty(ref _smallFileThresholdMiB, value);
    }

    public int MaxAttempts
    {
        get => _maxAttempts;
        set => SetProperty(ref _maxAttempts, value);
    }

    public double BaseDelaySeconds
    {
        get => _baseDelaySeconds;
        set => SetProperty(ref _baseDelaySeconds, value);
    }

    public double MaxDelaySeconds
    {
        get => _maxDelaySeconds;
        set => SetProperty(ref _maxDelaySeconds, value);
    }

    public int PerAttemptTimeoutSeconds
    {
        get => _perAttemptTimeoutSeconds;
        set => SetProperty(ref _perAttemptTimeoutSeconds, value);
    }

    /// <summary>Opt-in time-based schedule (ADR-0023). When off, the schedule gate never pauses the queue.</summary>
    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set => SetProperty(ref _scheduleEnabled, value);
    }

    /// <summary>Window start, bound to a <c>TimePicker</c> (time-of-day; only valid times are producible).</summary>
    public TimeSpan? ScheduleStart
    {
        get => _scheduleStart;
        set => SetProperty(ref _scheduleStart, value);
    }

    /// <summary>Window stop, bound to a <c>TimePicker</c>.</summary>
    public TimeSpan? ScheduleStop
    {
        get => _scheduleStop;
        set => SetProperty(ref _scheduleStop, value);
    }

    // Honest, per-knob apply-timing notes surfaced beside the relevant fields.
    public string ConcurrencyNote => "Applies immediately.";

    public string ScheduleNote => "Pauses the queue outside these hours. Off by default.";

    public string NewDownloadsNote => "Applies to new downloads.";

    public string NextAttemptNote => "Applies to the next attempt (including in-flight).";

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand ToggleCommand { get; }

    /// <summary>
    /// Persist the edited set and apply it. The whole set goes through the store's clamp/validate path,
    /// so only legal values are written; the clamped result is then applied per knob and reflected back
    /// into the fields.
    /// </summary>
    private void Save()
    {
        // Start from the persisted file so routing + file-only advanced knobs are preserved, then overlay
        // exactly the five queue knobs this panel owns.
        var raw = SettingsStore.ReadRaw(_settingsPath);
        raw.Scheduler ??= new SchedulerSettings();
        raw.Engine ??= new EngineSettings();
        raw.Retry ??= new RetrySettings();
        raw.Schedule ??= new ScheduleSettings();

        raw.Scheduler.MaxConcurrentDownloads = MaxConcurrentDownloads;
        raw.Engine.SegmentsPerDownload = SegmentsPerDownload;
        raw.Engine.SmallFileThresholdBytes = (long)Math.Round(SmallFileThresholdMiB * BytesPerMiB);
        raw.Engine.PerAttemptTimeoutSeconds = PerAttemptTimeoutSeconds;
        raw.Retry.MaxAttempts = MaxAttempts;
        raw.Retry.BaseDelaySeconds = BaseDelaySeconds;
        raw.Retry.MaxDelaySeconds = MaxDelaySeconds;
        raw.Schedule.Enabled = ScheduleEnabled;
        // Persisted shape is unchanged: still HH:mm strings in settings.json (the picker just produces the time).
        raw.Schedule.Start = FormatTime(ScheduleStart);
        raw.Schedule.Stop = FormatTime(ScheduleStop);

        var resolved = SettingsStore.Save(_settingsPath, raw, _logger, _userProfile);

        // Apply per knob to the shared singletons the engine/scheduler already read at their own cadence.
        _scheduler.SetMaxConcurrency(resolved.Scheduler.MaxConcurrentDownloads);   // live gate resize
        _downloadDefaults.SegmentCount = resolved.Defaults.SegmentCount;            // newly started downloads
        _engineOptions.SmallFileThresholdBytes = resolved.Engine.SmallFileThresholdBytes; // newly started downloads
        _engineOptions.PerAttemptTimeout = resolved.Engine.PerAttemptTimeout;       // next attempt
        _retryOptions.MaxAttempts = resolved.Retry.MaxAttempts;                     // next attempt
        _retryOptions.BaseDelay = resolved.Retry.BaseDelay;                         // next attempt
        _retryOptions.MaxDelay = resolved.Retry.MaxDelay;                           // next attempt
        // Schedule applies live: the view-model re-evaluates the gate each tick from this shared instance.
        _schedule.Enabled = resolved.Schedule.Enabled;
        _schedule.Start = resolved.Schedule.Start;
        _schedule.Stop = resolved.Schedule.Stop;

        LoadFromLive();   // show the clamped values that were actually applied
        IsExpanded = false;
    }

    /// <summary>Expand the panel programmatically (e.g. the <c>--open-settings</c> launch flag, so the AOT
    /// GUI smoke-launch renders the schedule TimePicker). Loads current values first, like Toggle.</summary>
    public void Open()
    {
        LoadFromLive();
        IsExpanded = true;
    }

    /// <summary>Discard unsaved edits and collapse — no write.</summary>
    private void Cancel()
    {
        LoadFromLive();
        IsExpanded = false;
    }

    private void Toggle()
    {
        if (!IsExpanded)
        {
            LoadFromLive(); // open with the current applied values, discarding any stale local edits
        }

        IsExpanded = !IsExpanded;
    }

    /// <summary>Populate the editable fields from the live/applied values (the shared singletons).</summary>
    private void LoadFromLive()
    {
        MaxConcurrentDownloads = _scheduler.MaxConcurrency;
        SegmentsPerDownload = _downloadDefaults.SegmentCount;
        SmallFileThresholdMiB = _engineOptions.SmallFileThresholdBytes / BytesPerMiB;
        PerAttemptTimeoutSeconds = (int)Math.Round(_engineOptions.PerAttemptTimeout.TotalSeconds);
        MaxAttempts = _retryOptions.MaxAttempts;
        BaseDelaySeconds = _retryOptions.BaseDelay.TotalSeconds;
        MaxDelaySeconds = _retryOptions.MaxDelay.TotalSeconds;
        ScheduleEnabled = _schedule.Enabled;
        ScheduleStart = _schedule.Start.ToTimeSpan();
        ScheduleStop = _schedule.Stop.ToTimeSpan();
    }

    /// <summary>A picker time → the persisted HH:mm string (minute precision). Null → midnight.</summary>
    private static string FormatTime(TimeSpan? time) =>
        time is { } t ? TimeOnly.FromTimeSpan(t).ToString("HH:mm") : "00:00";
}