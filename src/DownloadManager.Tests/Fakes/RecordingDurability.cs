using System.Collections.Concurrent;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;

namespace DownloadManager.Tests.Fakes;

/// <summary>
/// Instrumented target file that tracks how many bytes are <i>durable</i> (i.e. have been flushed).
/// Bytes written but not yet flushed do not count as durable — exactly the distinction the §6c
/// invariant turns on.
/// </summary>
internal sealed class RecordingTargetFile : ITargetFile
{
    private long _writtenHighWater;

    public long DurableBytes { get; private set; }

    public List<string> Operations { get; } = [];

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        _writtenHighWater = Math.Max(_writtenHighWater, offset + buffer.Length);
        return ValueTask.CompletedTask;
    }

    public void FlushToDisk()
    {
        DurableBytes = _writtenHighWater;
        Operations.Add($"data-flush@{DurableBytes}");
    }

    public long Length => _writtenHighWater;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Instrumented progress log that asserts the durability invariant on every append: a recorded
/// durable offset must never exceed the target's actually-durable byte count (spec §6c).
/// </summary>
internal sealed class RecordingProgressLog(RecordingTargetFile target) : IProgressLog
{
    private readonly RecordingTargetFile _target = target;

    public long MaxRecordedOffset { get; private set; }

    public bool InvariantViolated { get; private set; }

    public void Append(SegmentCheckpoint checkpoint)
    {
        if (checkpoint.DurableOffset > _target.DurableBytes)
        {
            InvariantViolated = true;
        }

        MaxRecordedOffset = Math.Max(MaxRecordedOffset, checkpoint.DurableOffset);
        _target.Operations.Add($"log-append@{checkpoint.DurableOffset}");
    }

    public void FlushToDisk() => _target.Operations.Add("log-flush");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingTargetFileFactory(RecordingTargetFile file) : ITargetFileFactory
{
    public ITargetFile Open(
        string path, long expectedSize, PreallocationMode mode, long maxFullPreallocationBytes = long.MaxValue) => file;
}

internal sealed class RecordingProgressLogStore(ProgressLogSession session) : IProgressLogStore
{
    public ProgressLogSession Open(string targetPath) => session;

    public void Delete(string targetPath)
    {
    }
}

/// <summary>In-memory <see cref="IMetadataStore"/> so durability tests need no disk.</summary>
internal sealed class InMemoryMetadataStore : IMetadataStore
{
    private readonly ConcurrentDictionary<string, DownloadMetadata> _store = new();

    public Task SaveAsync(string targetPath, DownloadMetadata metadata, CancellationToken cancellationToken)
    {
        _store[targetPath] = metadata;
        return Task.CompletedTask;
    }

    public Task<DownloadMetadata?> TryLoadAsync(string targetPath, CancellationToken cancellationToken) =>
        Task.FromResult(_store.GetValueOrDefault(targetPath));

    public void Delete(string targetPath) => _store.TryRemove(targetPath, out _);

    public IEnumerable<string> EnumerateTargets(string directory) => _store.Keys;
}