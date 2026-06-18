using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Http;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Core.Engine;

/// <summary>
/// The single download code path (spec Non-Negotiable #5). Resolves/revalidates the resource, lays
/// out segments, resumes from durable state, streams network→disk with the §6c durability ordering,
/// and reports progress. A non-segmented download is just the 1-segment case; unknown-size resources
/// degrade to a non-resumable single stream (spec §3).
/// </summary>
public sealed partial class DownloadEngine(
    RangeProber prober,
    HttpClient httpClient,
    ITargetFileFactory targetFileFactory,
    IProgressLogStore progressLogStore,
    IMetadataStore metadataStore,
    EngineOptions engineOptions,
    TimeProvider timeProvider,
    ILogger<DownloadEngine> logger) : IDownloadEngine
{
    private readonly RangeProber _prober = prober;
    private readonly HttpClient _httpClient = httpClient;
    private readonly ITargetFileFactory _targetFileFactory = targetFileFactory;
    private readonly IProgressLogStore _progressLogStore = progressLogStore;
    private readonly IMetadataStore _metadataStore = metadataStore;
    private readonly EngineOptions _engineOptions = engineOptions;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<DownloadEngine> _logger = logger;

    public async Task<DownloadOutcome> RunAsync(
        DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var metadata = await ResolveMetadataAsync(request, cancellationToken).ConfigureAwait(false);
            return metadata.TotalSize > 0
                ? await RunSegmentedAsync(request, metadata, progress, cancellationToken).ConfigureAwait(false)
                : await RunUnknownSizeAsync(request, metadata, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogCanceled(request.Id);
            return DownloadOutcome.Canceled(0);
        }
        catch (DownloadException ex)
        {
            LogFailed(request.Id, ex.IsTransient, ex.Message);
            return DownloadOutcome.Failed(0, ex.Message, ex.IsTransient, ex.RetryAfter);
        }
    }

    /// <summary>Discards a download's partial state (sidecars + target). Used by the scheduler on cancel.</summary>
    public void Discard(string targetPath) => DiscardState(targetPath);

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var delay = date - _timeProvider.GetUtcNow();
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Decides whether to resume from persisted metadata (after revalidating the resource is
    /// unchanged) or start fresh (probing and writing new metadata). Resource-changed / unusable-
    /// validator cases discard prior state and restart, so the download paths below always operate on
    /// a validated resource (spec §6d/§7).
    /// </summary>
    private async Task<DownloadMetadata> ResolveMetadataAsync(DownloadRequest request, CancellationToken ct)
    {
        var existing = await _metadataStore.TryLoadAsync(request.TargetPath, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.TotalSize > 0)
            {
                var revalidation = await _prober
                    .RevalidateAsync(new Uri(existing.FinalUrl), existing.Validators, existing.TotalSize, ct)
                    .ConfigureAwait(false);

                if (revalidation.Unchanged)
                {
                    LogResuming(request.Id, request.TargetPath, existing.TotalSize, existing.ETag);
                    return existing;
                }

                LogResourceChanged(request.Id, request.TargetPath, existing.ETag, existing.LastModified);
            }

            DiscardState(request.TargetPath);
        }

        var probe = await _prober.ProbeAsync(request.Url, ct).ConfigureAwait(false);

        SegmentRange[] segments;
        if (probe.TotalSize > 0)
        {
            var segmentCount = ChooseSegmentCount(probe.TotalSize, probe.AcceptsRanges, request.SegmentCount);
            segments = SegmentLayout.Split(probe.TotalSize, segmentCount).Segments.ToArray();
        }
        else
        {
            segments = [];
        }

        var now = _timeProvider.GetUtcNow();
        var metadata = new DownloadMetadata
        {
            OriginalUrl = request.Url.ToString(),
            FinalUrl = probe.FinalUrl.ToString(),
            ETag = probe.Validators.ETag,
            LastModified = probe.Validators.LastModified,
            TotalSize = probe.TotalSize,
            AcceptsRanges = probe.AcceptsRanges,
            Segments = segments,
            ExpectedSha256 = request.ExpectedSha256,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _metadataStore.SaveAsync(request.TargetPath, metadata, ct).ConfigureAwait(false);
        LogStarting(request.Id, probe.FinalUrl, probe.TotalSize, probe.AcceptsRanges, segments.Length);
        return metadata;
    }

    private async Task<DownloadOutcome> RunSegmentedAsync(
        DownloadRequest request, DownloadMetadata metadata, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var layout = SegmentLayout.FromPersisted(metadata.Segments, metadata.TotalSize);
        var aggregator = new ProgressAggregator(metadata.TotalSize, layout.Count);

        var target = _targetFileFactory.Open(request.TargetPath, metadata.TotalSize, request.Preallocation);
        var session = _progressLogStore.Open(request.TargetPath);
        long completed;
        try
        {
            var recovered = session.RecoveredOffsets;

            // Seed the aggregator from recovered offsets so progress starts where the last run left off.
            for (var segmentId = 0; segmentId < layout.Count; segmentId++)
            {
                aggregator.Set(segmentId, ClampStart(recovered, segmentId, layout[segmentId]) - layout[segmentId].Start);
            }

            progress?.Report(aggregator.Snapshot());

            // Only segments that aren't already complete need work (mixed-state recovery: some done,
            // some partial, some not started). Each resumes from its own offset with If-Range.
            var pending = new List<(int Id, SegmentRange Segment, long From)>();
            for (var segmentId = 0; segmentId < layout.Count; segmentId++)
            {
                var segment = layout[segmentId];
                var from = ClampStart(recovered, segmentId, segment);
                if (from <= segment.EndInclusive)
                {
                    pending.Add((segmentId, segment, from));
                }
            }

            await RunSegmentsAsync(request.Id, pending, metadata, target, session.Log, aggregator, progress, ct)
                .ConfigureAwait(false);

            completed = aggregator.Snapshot().CompletedBytes;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);
        }

        // Success: the target is complete and durable, so the sidecars are no longer needed.
        _metadataStore.Delete(request.TargetPath);
        _progressLogStore.Delete(request.TargetPath);
        LogCompleted(request.Id, completed);
        return DownloadOutcome.Completed(completed);
    }

    /// <summary>
    /// Runs the pending segments in parallel, bounded by <c>MaxSegmentConcurrency</c>. On the first
    /// real failure all siblings are cancelled (no point burning bandwidth) and the originating
    /// <see cref="DownloadException"/> is surfaced; on user cancellation the cancellation propagates.
    /// Durable state stays consistent regardless because each segment obeys the §6c ordering.
    /// </summary>
    private async Task RunSegmentsAsync(
        DownloadId id, IReadOnlyList<(int Id, SegmentRange Segment, long From)> pending, DownloadMetadata metadata,
        ITargetFile target, IProgressLog log, ProgressAggregator aggregator,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var concurrency = Math.Clamp(_engineOptions.MaxSegmentConcurrency, 1, pending.Count);
        using var gate = new SemaphoreSlim(concurrency);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task RunOneAsync((int Id, SegmentRange Segment, long From) item)
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await DownloadSegmentAsync(
                    id, item.Id, item.Segment, item.From, metadata, target, log, aggregator, progress, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                linked.Cancel(); // stop the siblings on a genuine failure
                throw;
            }
            finally
            {
                gate.Release();
            }
        }

        var tasks = new Task[pending.Count];
        for (var i = 0; i < pending.Count; i++)
        {
            tasks[i] = RunOneAsync(pending[i]);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // A user cancellation wins; otherwise surface the originating download failure rather than
            // a sibling's induced cancellation.
            ct.ThrowIfCancellationRequested();
            var failure = tasks
                .Where(t => t.IsFaulted)
                .Select(t => t.Exception!.GetBaseException())
                .FirstOrDefault(e => e is DownloadException);
            if (failure is not null)
            {
                throw failure;
            }

            throw;
        }
    }

    private async Task DownloadSegmentAsync(
        DownloadId id, int segmentId, SegmentRange segment, long from, DownloadMetadata metadata,
        ITargetFile target, IProgressLog log, ProgressAggregator aggregator,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var writeOffset = from;
        var isResume = writeOffset > segment.Start;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(metadata.FinalUrl));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.Range = new RangeHeaderValue(writeOffset, segment.EndInclusive);
        if (isResume && metadata.Validators.ToIfRangeHeaderValue() is { } ifRange)
        {
            request.Headers.TryAddWithoutValidation("If-Range", ifRange);
        }

        using var timeoutCts = new CancellationTokenSource(_engineOptions.PerAttemptTimeout, _timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var response = await SendWithClassificationAsync(request, attempt.Token, ct).ConfigureAwait(false);
        writeOffset = ValidateSegmentResponse(response, segment, writeOffset, metadata.TotalSize, isResume);

        var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(_engineOptions.CopyBufferSize);
        try
        {
            long sinceCheckpoint = 0;
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, _engineOptions.CopyBufferSize), attempt.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TransientDownloadException($"Segment {segmentId} stream timed out.");
                }
                catch (IOException ex)
                {
                    throw new TransientDownloadException($"Segment {segmentId} stream error.", ex);
                }

                if (read == 0)
                {
                    break;
                }

                // Defend against a server that streams past the requested range.
                var writable = segment.EndInclusive - writeOffset + 1;
                if (read > writable)
                {
                    read = (int)writable;
                }

                if (read <= 0)
                {
                    break;
                }

                await target.WriteAsync(writeOffset, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                writeOffset += read;
                sinceCheckpoint += read;
                aggregator.Set(segmentId, writeOffset - segment.Start);

                if (sinceCheckpoint >= _engineOptions.CheckpointIntervalBytes)
                {
                    Checkpoint(target, log, segmentId, writeOffset);
                    sinceCheckpoint = 0;
                    progress?.Report(aggregator.Snapshot());
                }

                if (writeOffset > segment.EndInclusive)
                {
                    break;
                }
            }

            // Final durable checkpoint for this segment.
            Checkpoint(target, log, segmentId, writeOffset);
            aggregator.Set(segmentId, writeOffset - segment.Start);
            progress?.Report(aggregator.Snapshot());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (writeOffset != segment.EndInclusive + 1)
        {
            // The connection closed before the segment finished — transient, safe to resume later.
            throw new TransientDownloadException(
                $"Segment {segmentId} ended at {writeOffset}, expected {segment.EndInclusive + 1}.");
        }

        LogSegmentComplete(id, segmentId, segment.Length);
    }

    private async Task<DownloadOutcome> RunUnknownSizeAsync(
        DownloadRequest request, DownloadMetadata metadata, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // No size and/or no range support => non-resumable single stream (spec §3). No progress log:
        // there is nothing safe to resume from, so each run starts at offset 0.
        var target = _targetFileFactory.Open(request.TargetPath, expectedSize: -1, request.Preallocation);
        long written = 0;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(metadata.FinalUrl));
            httpRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

            using var timeoutCts = new CancellationTokenSource(_engineOptions.PerAttemptTimeout, _timeProvider);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var response = await SendWithClassificationAsync(httpRequest, attempt.Token, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw HttpErrorClassifier.ForStatus(response.StatusCode, ReadRetryAfter(response));
            }

            var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(_engineOptions.CopyBufferSize);
            try
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(0, _engineOptions.CopyBufferSize), attempt.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TransientDownloadException("Stream timed out.");
                    }
                    catch (IOException ex)
                    {
                        throw new TransientDownloadException("Stream error.", ex);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(written, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    progress?.Report(new DownloadProgress(written, -1));
                }

                target.FlushToDisk();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            await target.DisposeAsync().ConfigureAwait(false);
        }

        _metadataStore.Delete(request.TargetPath);
        _progressLogStore.Delete(request.TargetPath);
        LogCompleted(request.Id, written);
        return DownloadOutcome.Completed(written);
    }

    private async Task<HttpResponseMessage> SendWithClassificationAsync(
        HttpRequestMessage request, CancellationToken attemptToken, CancellationToken userToken)
    {
        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            throw new TransientDownloadException("Request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new TransientDownloadException("Transport error.", ex);
        }
    }

    /// <summary>Validates a segment response and returns the offset writing should actually start at.</summary>
    private long ValidateSegmentResponse(
        HttpResponseMessage response, SegmentRange segment, long expectedFrom, long totalSize, bool isResume)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.PartialContent: // 206 — the expected resume/segment response
                var contentRange = response.Content.Headers.ContentRange
                    ?? throw new ResourceChangedException("206 response without a Content-Range header.");
                if (contentRange.Length is { } total && total != totalSize)
                {
                    throw new ResourceChangedException($"Size changed: server reports {total}, expected {totalSize}.");
                }

                if (contentRange.From is { } from && from != expectedFrom)
                {
                    throw new ResourceChangedException($"Range start mismatch: server {from}, expected {expectedFrom}.");
                }

                return expectedFrom;

            case HttpStatusCode.OK: // 200 — only valid as a full stream of a single segment from offset 0
                if (segment.Start != 0 || expectedFrom != 0 || isResume)
                {
                    throw new ResourceChangedException(
                        "Server returned 200 to a ranged request; cannot resume the resource as laid out.");
                }

                if (response.Content.Headers.ContentLength is { } length && length != totalSize)
                {
                    throw new ResourceChangedException($"Size changed: server reports {length}, expected {totalSize}.");
                }

                return 0;

            case HttpStatusCode.RequestedRangeNotSatisfiable: // 416
                throw new ResourceChangedException("416 Range Not Satisfiable: the resource shrank or changed.");

            default:
                throw HttpErrorClassifier.ForStatus(response.StatusCode, ReadRetryAfter(response));
        }
    }

    /// <summary>The §6c durability ordering, in one place: data fsync → progress append → progress fsync.</summary>
    private static void Checkpoint(ITargetFile target, IProgressLog log, int segmentId, long offset)
    {
        target.FlushToDisk();
        log.Append(new SegmentCheckpoint(segmentId, offset));
        log.FlushToDisk();
    }

    /// <summary>
    /// Picks the effective segment count: 1 unless the probe confirmed real <c>206</c> range support
    /// and the file is at least the small-file threshold, then the requested count clamped to
    /// <c>[1, MaxSegmentsPerDownload]</c> (ADR-0007). <see cref="SegmentLayout.Split"/> further caps it
    /// so no zero-length segment is produced.
    /// </summary>
    private int ChooseSegmentCount(long totalSize, bool acceptsRanges, int requested)
    {
        if (!acceptsRanges || totalSize < _engineOptions.SmallFileThresholdBytes)
        {
            return 1;
        }

        return Math.Clamp(requested, 1, _engineOptions.MaxSegmentsPerDownload);
    }

    private static long ClampStart(IReadOnlyDictionary<int, long> recovered, int segmentId, SegmentRange segment)
    {
        var from = recovered.TryGetValue(segmentId, out var offset) ? offset : segment.Start;
        if (from < segment.Start || from > segment.EndInclusive + 1)
        {
            from = segment.Start; // ignore an out-of-range recovered offset
        }

        return from;
    }

    private void DiscardState(string targetPath)
    {
        _metadataStore.Delete(targetPath);
        _progressLogStore.Delete(targetPath);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Download {Id} starting: {Url}, total {TotalSize}, acceptsRanges {AcceptsRanges}, {SegmentCount} segment(s).")]
    private partial void LogStarting(DownloadId id, Uri url, long totalSize, bool acceptsRanges, int segmentCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} resuming {TargetPath} ({TotalSize} bytes, etag {ETag}).")]
    private partial void LogResuming(DownloadId id, string targetPath, long totalSize, string? eTag);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id} resource changed for {TargetPath} (stored etag {ETag}, last-modified {LastModified}); discarding partial state and restarting.")]
    private partial void LogResourceChanged(DownloadId id, string targetPath, string? eTag, DateTimeOffset? lastModified);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Download {Id} segment {SegmentId} complete ({Length} bytes).")]
    private partial void LogSegmentComplete(DownloadId id, int segmentId, long length);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} completed ({CompletedBytes} bytes).")]
    private partial void LogCompleted(DownloadId id, long completedBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} canceled.")]
    private partial void LogCanceled(DownloadId id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Download {Id} failed (transient={IsTransient}): {Reason}")]
    private partial void LogFailed(DownloadId id, bool isTransient, string reason);
}