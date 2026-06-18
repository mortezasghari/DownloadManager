using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;

namespace DownloadManager.UI.ViewModels;

/// <summary>
/// One download row. Holds no engine logic — it polls the handle's lock-free <see cref="IDownloadHandle.Status"/>
/// and <see cref="IDownloadHandle.Progress"/> on <see cref="Refresh"/> (driven by the UI timer), derives
/// smoothed speed/ETA, and reflects the state machine in command enablement. Control commands call the
/// scheduler asynchronously; never blocks the UI thread.
/// </summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private readonly IDownloadHandle _handle;
    private readonly IDownloadScheduler _scheduler;
    private readonly TimeProvider _timeProvider;
    private readonly SpeedSmoother _smoother;

    private DownloadStatus _status;
    private bool _needsCredentials;
    private string _statusText = "Queued";
    private double _progressPercent;
    private bool _isIndeterminate;
    private string _speedText = "—";
    private string _etaText = "—";
    private long _completedBytes;
    private long _totalBytes;

    public DownloadItemViewModel(
        DownloadRequest request,
        IDownloadHandle handle,
        IDownloadScheduler scheduler,
        TimeProvider timeProvider,
        Func<DownloadItemViewModel, Task> onRemove,
        Func<DownloadItemViewModel, Task> onReauthorize,
        TimeSpan? speedWindow = null)
    {
        Request = request;
        _handle = handle;
        _scheduler = scheduler;
        _timeProvider = timeProvider;
        _smoother = new SpeedSmoother(speedWindow ?? TimeSpan.FromSeconds(5));

        var fileName = Path.GetFileName(request.TargetPath);
        Name = string.IsNullOrEmpty(fileName) ? request.Url.Host : fileName;

        PauseCommand = new AsyncRelayCommand(() => _scheduler.PauseAsync(Id), () => CanPause);
        ResumeCommand = new AsyncRelayCommand(() => _scheduler.ResumeAsync(Id), () => CanResume);
        RetryCommand = new AsyncRelayCommand(() => _scheduler.RetryAsync(Id), () => CanRetry);
        ReauthorizeCommand = new AsyncRelayCommand(() => onReauthorize(this), () => CanReauthorize);
        RemoveCommand = new AsyncRelayCommand(() => onRemove(this));

        _status = handle.Status;
        UpdateStatusText();
        UpdateCommandStates();
    }

    public DownloadRequest Request { get; }

    public DownloadId Id => Request.Id;

    public string Name { get; }

    public string Url => Request.Url.ToString();

    public string TargetPath => Request.TargetPath;

    public DownloadStatus Status => _status;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    public bool NeedsCredentials
    {
        get => _needsCredentials;
        private set => SetProperty(ref _needsCredentials, value);
    }

    public AsyncRelayCommand PauseCommand { get; }

    public AsyncRelayCommand ResumeCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public AsyncRelayCommand ReauthorizeCommand { get; }

    public AsyncRelayCommand RemoveCommand { get; }

    // State-machine-driven command enablement (spec Phase 5): the UI reflects legality, not just rejects it.
    public bool CanPause => _status is DownloadStatus.Queued or DownloadStatus.Running or DownloadStatus.Retrying;

    public bool CanResume => _status is DownloadStatus.Paused;

    public bool CanRetry => _status is DownloadStatus.Failed && !_needsCredentials;

    public bool CanReauthorize => _status is DownloadStatus.Failed && _needsCredentials;

    public bool IsActive =>
        _status is DownloadStatus.Queued or DownloadStatus.Running or DownloadStatus.Retrying;

    /// <summary>
    /// Polls the handle and recomputes derived display state. Called on the UI thread from the refresh
    /// timer (and directly from tests with a <c>FakeTimeProvider</c>). Lock-free; no engine work.
    /// </summary>
    public void Refresh()
    {
        var now = _timeProvider.GetUtcNow();
        var progress = _handle.Progress;
        var status = _handle.Status;
        var needsCreds = _handle.NeedsCredentials;

        _completedBytes = progress.CompletedBytes;
        _totalBytes = progress.TotalBytes;

        // Progress bar
        if (progress.TotalBytes > 0)
        {
            IsIndeterminate = false;
            ProgressPercent = Math.Clamp(progress.CompletedBytes * 100d / progress.TotalBytes, 0, 100);
        }
        else
        {
            IsIndeterminate = status is DownloadStatus.Running;
            ProgressPercent = 0;
        }

        // Speed/ETA only meaningful while actively downloading bytes.
        if (status is DownloadStatus.Running && progress.Phase is DownloadPhase.Downloading)
        {
            _smoother.Add(now, progress.CompletedBytes);
            var bps = _smoother.BytesPerSecond();
            SpeedText = DisplayFormat.Rate(bps);
            EtaText = DisplayFormat.Eta(progress.CompletedBytes, progress.TotalBytes, bps);
        }
        else
        {
            _smoother.Reset();
            SpeedText = "—";
            EtaText = "—";
        }

        var stateChanged = _status != status || _needsCredentials != needsCreds;
        _status = status;
        NeedsCredentials = needsCreds;
        UpdateStatusText(progress.Phase);

        if (stateChanged)
        {
            UpdateCommandStates();
        }
    }

    private void UpdateStatusText(DownloadPhase phase = DownloadPhase.Downloading) =>
        StatusText = _status switch
        {
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Running => phase is DownloadPhase.Verifying ? "Verifying…" : "Downloading",
            DownloadStatus.Retrying => "Retrying…",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Failed => _needsCredentials ? "Needs credentials" : "Failed",
            DownloadStatus.Canceled => "Canceled",
            _ => _status.ToString(),
        };

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanReauthorize));
        OnPropertyChanged(nameof(IsActive));
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        ReauthorizeCommand.RaiseCanExecuteChanged();
    }
}