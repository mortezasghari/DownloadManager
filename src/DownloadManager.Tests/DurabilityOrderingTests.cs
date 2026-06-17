using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Engine;
using DownloadManager.Core.Http;
using DownloadManager.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Proves the §6c ordering and invariant directly: data is flushed before a progress record is
/// appended, and a recorded durable offset never exceeds the bytes actually flushed to the target.
/// </summary>
public class DurabilityOrderingTests
{
    [Fact]
    public async Task Progress_is_recorded_only_after_data_is_durable_and_never_exceeds_it()
    {
        var content = EngineHarness.Pattern(100 * 1024);
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };

        var target = new RecordingTargetFile();
        var log = new RecordingProgressLog(target);
        var session = new ProgressLogSession(log, new Dictionary<int, long>());

        var httpClient = new HttpClient(new FakeHttpMessageHandler(server.Handle));
        var options = new EngineOptions { CopyBufferSize = 8 * 1024, CheckpointIntervalBytes = 16 * 1024 };
        var time = new FakeTimeProvider();

        var engine = new DownloadEngine(
            new RangeProber(httpClient, new HttpOptions(), options, time, NullLogger<RangeProber>.Instance),
            httpClient,
            new RecordingTargetFileFactory(target),
            new RecordingProgressLogStore(session),
            new InMemoryMetadataStore(),
            options,
            time,
            NullLogger<DownloadEngine>.Instance);

        var request = new DownloadRequest
        {
            Id = DownloadId.New(),
            Url = new Uri("http://origin.test/file"),
            TargetPath = "/virtual/file.bin",
            SegmentCount = 1,
        };

        var outcome = await engine.RunAsync(request, null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.False(log.InvariantViolated);
        Assert.Equal(content.Length, log.MaxRecordedOffset);

        // Every checkpoint is the exact triplet: data-flush -> log-append -> log-flush.
        var ops = target.Operations;
        var appendIndices = ops.Select((op, i) => (op, i)).Where(x => x.op.StartsWith("log-append")).Select(x => x.i);
        Assert.NotEmpty(appendIndices);
        foreach (var i in appendIndices)
        {
            Assert.StartsWith("data-flush@", ops[i - 1]);
            Assert.Equal("log-flush", ops[i + 1]);
        }
    }
}