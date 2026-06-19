using DownloadManager.Core.Domain;
using DownloadManager.Core.Scheduler;
using DownloadManager.UI.Services;

namespace DownloadManager.Tests.Fakes;

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

    public Task<IDownloadHandle> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
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