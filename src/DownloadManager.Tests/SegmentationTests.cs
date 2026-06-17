using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Persistence.Metadata;
using DownloadManager.Persistence.Progress;
using DownloadManager.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DownloadManager.Tests;

public class SegmentationTests
{
    // Count distinct segment-GET start offsets, excluding the bytes=0-0 probe/revalidation (To == 0).
    private static int DistinctSegmentStarts(FakeContentServer server) =>
        server.Requests
            .Where(r => r is { RangeFrom: not null, RangeTo: > 0 })
            .Select(r => r.RangeFrom!.Value)
            .Distinct()
            .Count();

    [Fact]
    public async Task Parallel_multisegment_download_completes_with_exact_bytes()
    {
        using var harness = new EngineHarness { EngineOptions = SegmentingOptions() };
        var content = EngineHarness.Pattern(256 * 1024);
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(segmentCount: 4), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());
        Assert.Equal(4, DistinctSegmentStarts(server));
    }

    [Fact]
    public async Task Last_segment_remainder_tiles_exactly_end_to_end()
    {
        using var harness = new EngineHarness { EngineOptions = SegmentingOptions() };
        // 70000 / 3 is not even: segments are 23333, 23333, 23334 — exercises the off-by-one boundary.
        var content = EngineHarness.Pattern(70_000);
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(segmentCount: 3), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());
        Assert.Equal(3, DistinctSegmentStarts(server));
    }

    [Fact]
    public async Task Segment_count_is_clamped_to_sixteen()
    {
        using var harness = new EngineHarness { EngineOptions = SegmentingOptions() };
        var content = EngineHarness.Pattern(64 * 1024); // 16 segments of 4096 when clamped
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(segmentCount: 100), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());
        Assert.Equal(16, DistinctSegmentStarts(server)); // clamped to 16, not 100
    }

    [Fact]
    public async Task Files_below_the_small_file_threshold_use_a_single_stream()
    {
        using var harness = new EngineHarness
        {
            EngineOptions = SegmentingOptions() with { SmallFileThresholdBytes = 8L * 1024 * 1024 },
        };
        var content = EngineHarness.Pattern(1 * 1024 * 1024); // 1 MB < 8 MB threshold
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(segmentCount: 8), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());
        Assert.Equal(1, DistinctSegmentStarts(server)); // not segmented despite SegmentCount=8
    }

    [Fact]
    public async Task Mixed_state_recovery_resumes_only_incomplete_segments()
    {
        using var harness = new EngineHarness { EngineOptions = SegmentingOptions() };
        var content = EngineHarness.Pattern(40_000);
        var url = harness.Url.ToString();

        // Layout: [0,9999] [10000,19999] [20000,29999] [30000,39999]
        var segments = SegmentLayout.Split(40_000, 4).Segments.ToArray();

        // Persist the intended layout/size first (recovery's source of truth).
        var metadataStore = new JsonMetadataStore(NullLogger<JsonMetadataStore>.Instance);
        await metadataStore.SaveAsync(harness.TargetPath, new DownloadMetadata
        {
            OriginalUrl = url,
            FinalUrl = url,
            ETag = "\"v1\"",
            TotalSize = 40_000,
            AcceptsRanges = true,
            Segments = segments,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);

        // Pre-seed durable bytes: seg0 complete, seg1 partial (4000), seg2 untouched, seg3 partial (5000).
        WriteTargetBytes(harness.TargetPath, content, 0, 10_000);
        WriteTargetBytes(harness.TargetPath, content, 10_000, 14_000);
        WriteTargetBytes(harness.TargetPath, content, 30_000, 35_000);

        // Pre-seed the progress log to match (seg2 has no record => not started).
        var logStore = new BinaryProgressLogStore(new ProgressLogOptions(), NullLogger<BinaryProgressLogStore>.Instance);
        var session = logStore.Open(harness.TargetPath);
        session.Log.Append(new SegmentCheckpoint(0, 10_000));
        session.Log.Append(new SegmentCheckpoint(1, 14_000));
        session.Log.Append(new SegmentCheckpoint(3, 35_000));
        session.Log.FlushToDisk();
        await session.DisposeAsync();

        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };
        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(segmentCount: 4), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());

        // seg0 (complete) was not re-downloaded; the others resumed from their own offsets.
        Assert.DoesNotContain(server.Requests, r => r is { RangeFrom: 0, RangeTo: 9_999 });
        Assert.Contains(server.Requests, r => r.RangeFrom == 14_000); // seg1 resumed mid-way
        Assert.Contains(server.Requests, r => r.RangeFrom == 20_000); // seg2 started from scratch
        Assert.Contains(server.Requests, r => r.RangeFrom == 35_000); // seg3 resumed mid-way
    }

    [Fact]
    public async Task Interrupted_multisegment_run_leaves_consistent_state_and_resumes_correctly()
    {
        using var harness = new EngineHarness { EngineOptions = SegmentingOptions() };
        var content = EngineHarness.Pattern(256 * 1024);
        var server = new FakeContentServer
        {
            Content = content,
            ETag = "\"v1\"",
            SupportsRanges = true,
            MaxBytesPerResponse = 20 * 1024, // every segment is cut off mid-stream
        };

        // First pass: one segment fails, siblings are cancelled mid-segment. Whatever each flushed is
        // recorded only after its data was fsynced (§6c), so the partial state is consistent.
        var first = await harness.BuildEngine(server)
            .RunAsync(harness.Request(segmentCount: 4), null, CancellationToken.None);
        Assert.Equal(DownloadResultKind.Failed, first.Kind);
        Assert.True(harness.SidecarsExist);

        // Resume: the recorded per-segment offsets are all <= durable bytes, so resuming each from its
        // own offset reconstructs the exact file with no corruption.
        server.MaxBytesPerResponse = null;
        var second = await harness.BuildEngine(server)
            .RunAsync(harness.Request(segmentCount: 4), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, second.Kind);
        Assert.Equal(content, harness.ReadTarget());
    }

    private static EngineOptions SegmentingOptions() => new()
    {
        CopyBufferSize = 8 * 1024,
        CheckpointIntervalBytes = 16 * 1024,
        SmallFileThresholdBytes = 1, // segment even modestly-sized test content
    };

    private static void WriteTargetBytes(string path, byte[] content, int start, int endExclusive)
    {
        using var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        RandomAccess.Write(handle, content.AsSpan(start, endExclusive - start), start);
    }
}