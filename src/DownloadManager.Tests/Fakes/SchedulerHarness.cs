using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Engine;
using DownloadManager.Core.Http;
using DownloadManager.Core.Scheduler;
using DownloadManager.Persistence;
using DownloadManager.Persistence.Io;
using DownloadManager.Persistence.Metadata;
using DownloadManager.Persistence.Progress;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DownloadManager.Tests.Fakes;

/// <summary>
/// Builds a real <see cref="DownloadScheduler"/> over the real engine/persistence stack, a
/// <see cref="FakeTimeProvider"/>, and either a gated or plain fake HTTP origin. One target file per
/// download under a temp directory.
/// </summary>
internal sealed class SchedulerHarness : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dlm-sched-tests", Guid.NewGuid().ToString("N"));

    private readonly HttpClient _httpClient;
    private readonly GatedHttpServer? _gate;

    private SchedulerHarness(
        FakeContentServer server, HttpMessageHandler handler, int maxConcurrent, RetryOptions retry,
        long smallFileThreshold, GatedHttpServer? gate)
    {
        Directory.CreateDirectory(_directory);
        Server = server;
        _gate = gate;
        _httpClient = new HttpClient(handler);

        var engineOptions = new EngineOptions
        {
            CopyBufferSize = 8 * 1024,
            CheckpointIntervalBytes = 16 * 1024,
            SmallFileThresholdBytes = smallFileThreshold,
        };

        var prober = new RangeProber(
            _httpClient, new HttpOptions(), engineOptions, Time, NullLogger<RangeProber>.Instance);
        var engine = new DownloadEngine(
            prober,
            _httpClient,
            new TargetFileFactory(NullLogger<TargetFileFactory>.Instance),
            new BinaryProgressLogStore(new ProgressLogOptions(), NullLogger<BinaryProgressLogStore>.Instance),
            new JsonMetadataStore(NullLogger<JsonMetadataStore>.Instance),
            new ChecksumVerifier(Time, NullLogger<ChecksumVerifier>.Instance),
            engineOptions,
            Time,
            NullLogger<DownloadEngine>.Instance);

        Scheduler = new DownloadScheduler(
            engine,
            new RetryPolicy(retry),
            new SchedulerOptions { MaxConcurrentDownloads = maxConcurrent, QueueCapacity = 1024 },
            Time,
            NullLogger<DownloadScheduler>.Instance);
    }

    public FakeTimeProvider Time { get; } = new();

    public FakeContentServer Server { get; }

    public GatedHttpServer Gate => _gate ?? throw new InvalidOperationException("This harness is not gated.");

    public DownloadScheduler Scheduler { get; }

    /// <summary>Gated origin: work requests block until released; single-segment unless thresholds set.</summary>
    public static SchedulerHarness Gated(
        int maxConcurrent, byte[] content, long smallFileThreshold = long.MaxValue, RetryOptions? retry = null)
    {
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };
        var gate = new GatedHttpServer(server);
        return new SchedulerHarness(
            server, new AsyncFakeHttpMessageHandler(gate.HandleAsync), maxConcurrent,
            retry ?? new RetryOptions(), smallFileThreshold, gate);
    }

    /// <summary>Plain origin: responses are immediate (used for transient-failure/backoff tests).</summary>
    public static SchedulerHarness Plain(int maxConcurrent, byte[] content, RetryOptions? retry = null)
    {
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };
        return new SchedulerHarness(
            server, new FakeHttpMessageHandler(server.Handle), maxConcurrent,
            retry ?? new RetryOptions(), long.MaxValue, gate: null);
    }

    public DownloadRequest NewRequest(int segmentCount = 1) => new()
    {
        Id = DownloadId.New(),
        Url = new Uri("http://origin.test/file"),
        TargetPath = Path.Combine(_directory, $"{Guid.NewGuid():N}.bin"),
        SegmentCount = segmentCount,
    };

    /// <summary>Pre-seeds a target as if some segments already downloaded durably (metadata + bytes + log),
    /// so a scheduled run resumes only the incomplete segments.</summary>
    public async Task SeedCompletedSegmentsAsync(DownloadRequest request, int segmentCount, params int[] completedSegmentIds)
    {
        var content = Server.Content;
        var layout = SegmentLayout.Split(content.Length, segmentCount);
        var url = request.Url.ToString();

        await new JsonMetadataStore(NullLogger<JsonMetadataStore>.Instance).SaveAsync(request.TargetPath, new DownloadMetadata
        {
            OriginalUrl = url,
            FinalUrl = url,
            ETag = "\"v1\"",
            TotalSize = content.Length,
            AcceptsRanges = true,
            Segments = layout.Segments.ToArray(),
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);

        var logStore = new BinaryProgressLogStore(new ProgressLogOptions(), NullLogger<BinaryProgressLogStore>.Instance);
        var session = logStore.Open(request.TargetPath);
        using (var handle = File.OpenHandle(request.TargetPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            foreach (var segmentId in completedSegmentIds)
            {
                var segment = layout[segmentId];
                RandomAccess.Write(handle, content.AsSpan((int)segment.Start, (int)segment.Length), segment.Start);
                session.Log.Append(new SegmentCheckpoint(segmentId, segment.EndInclusive + 1));
            }
        }

        session.Log.FlushToDisk();
        await session.DisposeAsync();
    }

    public byte[] ReadTarget(DownloadRequest request) => File.ReadAllBytes(request.TargetPath);

    public bool SidecarsOrTargetExist(DownloadRequest request) =>
        File.Exists(request.TargetPath)
        || File.Exists(PersistencePaths.MetadataPath(request.TargetPath))
        || File.Exists(PersistencePaths.ProgressLogPath(request.TargetPath));

    public async ValueTask DisposeAsync()
    {
        await Scheduler.DisposeAsync();
        _httpClient.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}