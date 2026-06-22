using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Scheduler;
using DownloadManager.UI.Services;

namespace DownloadManager.Tests.Fakes;

/// <summary>In-memory <see cref="IHistoryStore"/> for headless view-model tests; can be pre-seeded.</summary>
internal sealed class RecordingHistoryStore : IHistoryStore
{
    public RecordingHistoryStore(params HistoryRecord[] seed) => Records.AddRange(seed);

    public List<HistoryRecord> Records { get; } = [];

    public IReadOnlyList<HistoryRecord> Load() => Records.ToArray();

    public void Append(HistoryRecord record) => Records.Add(record);

    public void Rebuild(IReadOnlyList<HistoryRecord> records)
    {
        Records.Clear();
        Records.AddRange(records);
    }
}

/// <summary>A settable <see cref="IDownloadHandle"/> for headless view-model tests.</summary>
internal sealed class FakeDownloadHandle : IDownloadHandle
{
    public DownloadId Id { get; init; } = DownloadId.New();

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    public bool NeedsCredentials { get; set; }

    public DownloadProgress Progress { get; set; }

    public Task WaitForStatusAsync(Func<DownloadStatus, bool> predicate, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Records control calls; hands back a controllable <see cref="FakeDownloadHandle"/> on enqueue.</summary>
internal sealed class FakeUiScheduler : IDownloadScheduler
{
    private readonly Dictionary<DownloadId, IDownloadHandle> _handles = new();

    public List<DownloadRequest> Enqueued { get; } = [];

    public List<DownloadId> Paused { get; } = [];

    public List<DownloadId> Resumed { get; } = [];

    public List<DownloadId> Canceled { get; } = [];

    public List<DownloadId> Retried { get; } = [];

    /// <summary>Optional hook to customise the handle returned for a request (e.g. preset status).</summary>
    public Func<DownloadRequest, FakeDownloadHandle>? HandleFactory { get; set; }

    /// <summary>When set, the next <see cref="EnqueueAsync"/> throws — simulates a crash during the
    /// in-memory reflect step, after the append-first lifecycle event was already written (ADR-0021).</summary>
    public bool FailNextEnqueue { get; set; }

    public Task<IDownloadHandle> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        if (FailNextEnqueue)
        {
            FailNextEnqueue = false;
            throw new InvalidOperationException("Simulated crash during in-memory enqueue reflect.");
        }

        Enqueued.Add(request);
        var handle = HandleFactory?.Invoke(request) ?? new FakeDownloadHandle { Id = request.Id };
        _handles[request.Id] = handle;
        return Task.FromResult<IDownloadHandle>(handle);
    }

    public Task PauseAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        Paused.Add(id);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        Resumed.Add(id);
        return Task.CompletedTask;
    }

    public Task CancelAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        Canceled.Add(id);
        return Task.CompletedTask;
    }

    public Task RetryAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        Retried.Add(id);
        return Task.CompletedTask;
    }

    public IDownloadHandle? Find(DownloadId id) => _handles.GetValueOrDefault(id);

    public List<DownloadId> Postponed { get; } = [];

    public Task PostponeAsync(DownloadId id, CancellationToken cancellationToken = default)
    {
        Postponed.Add(id);
        return Task.CompletedTask;
    }

    public bool IsQueuePaused { get; private set; }

    public int QueuePauseCount { get; private set; }

    public int QueueResumeCount { get; private set; }

    public void PauseQueue()
    {
        IsQueuePaused = true;
        QueuePauseCount++;
    }

    public void ResumeQueue()
    {
        IsQueuePaused = false;
        QueueResumeCount++;
    }

    /// <summary>Records the concurrency the panel applied; starts at 3 (the default).</summary>
    public int MaxConcurrency { get; private set; } = 3;

    public List<int> ConcurrencyChanges { get; } = [];

    public void SetMaxConcurrency(int maxConcurrentDownloads)
    {
        MaxConcurrency = maxConcurrentDownloads;
        ConcurrencyChanges.Add(maxConcurrentDownloads);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeFilePicker(string? path) : IFilePicker
{
    public Task<string?> PickListFileAsync() => Task.FromResult(path);
}

internal sealed class FakeCredentialPrompt(DownloadCredentials? credentials) : ICredentialPrompt
{
    public string? LastPromptedName { get; private set; }

    public Task<DownloadCredentials?> PromptAsync(string downloadName)
    {
        LastPromptedName = downloadName;
        return Task.FromResult(credentials);
    }
}

internal sealed class FakeClipboardTextSource(string? text) : IClipboardTextSource
{
    public Task<string?> GetTextAsync() => Task.FromResult(text);
}

/// <summary>Records the path each open / reveal call targeted, and returns a configurable result so the
/// missing-file → error path is testable without touching the filesystem.</summary>
internal sealed class FakeFileLauncher : IFileLauncher
{
    public FakeFileLauncher(LaunchResult? result = null) => Result = result ?? LaunchResult.Success;

    public LaunchResult Result { get; set; }

    public List<string> OpenedFiles { get; } = [];

    public List<string> RevealedPaths { get; } = [];

    public List<string> OpenedUrls { get; } = [];

    public LaunchResult OpenFile(string savedPath)
    {
        OpenedFiles.Add(savedPath);
        return Result;
    }

    public LaunchResult RevealInFolder(string savedPath)
    {
        RevealedPaths.Add(savedPath);
        return Result;
    }

    public LaunchResult OpenUrl(string url)
    {
        OpenedUrls.Add(url);
        return Result;
    }
}

/// <summary>Captures the add-path the import dialog would be shown with (and can auto-invoke it).</summary>
internal sealed class FakeImportDialog : IImportDialog
{
    public Func<IReadOnlyList<Uri>, Task>? CapturedAddToQueue { get; private set; }

    public Task ShowAsync(Func<IReadOnlyList<Uri>, Task> addToQueue)
    {
        CapturedAddToQueue = addToQueue;
        return Task.CompletedTask;
    }
}