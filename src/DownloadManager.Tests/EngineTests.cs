using DownloadManager.Core.Domain;
using DownloadManager.Tests.Fakes;
using Xunit;

namespace DownloadManager.Tests;

public class EngineTests
{
    [Fact]
    public async Task Fresh_ranged_download_writes_exact_bytes_and_cleans_sidecars()
    {
        using var harness = new EngineHarness();
        var content = EngineHarness.Pattern(100 * 1024);
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"", SupportsRanges = true };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());
        Assert.False(harness.SidecarsExist);
    }

    [Fact]
    public async Task Server_that_ignores_ranges_still_downloads_via_single_stream()
    {
        using var harness = new EngineHarness();
        var content = EngineHarness.Pattern(40 * 1024);
        var server = new FakeContentServer { Content = content, SupportsRanges = false };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());
    }

    [Fact]
    public async Task Unknown_size_resource_downloads_as_non_resumable_stream()
    {
        using var harness = new EngineHarness();
        var content = EngineHarness.Pattern(30 * 1024);
        var server = new FakeContentServer
        {
            Content = content,
            SupportsRanges = false,
            AdvertiseLength = false,
        };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());
    }

    [Fact]
    public async Task Resume_after_dropped_connection_completes_with_if_range_and_offset()
    {
        using var harness = new EngineHarness();
        var content = EngineHarness.Pattern(100 * 1024);
        var server = new FakeContentServer
        {
            Content = content,
            ETag = "\"v1\"",
            SupportsRanges = true,
            MaxBytesPerResponse = 30 * 1024, // simulate an early close
        };

        var first = await harness.BuildEngine(server).RunAsync(harness.Request(), null, CancellationToken.None);
        Assert.Equal(DownloadResultKind.Failed, first.Kind);
        Assert.True(harness.SidecarsExist); // partial state retained for resume

        server.MaxBytesPerResponse = null; // network recovers
        var second = await harness.BuildEngine(server).RunAsync(harness.Request(), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, second.Kind);
        Assert.Equal(content, harness.ReadTarget());
        Assert.False(harness.SidecarsExist);

        // A resume request must continue from a non-zero offset and carry the If-Range precondition.
        Assert.Contains(server.Requests, r => r.RangeFrom is > 0 && r.IfRange == "\"v1\"");
    }

    [Fact]
    public async Task If_range_200_means_changed_resource_so_state_is_discarded_and_restarted()
    {
        using var harness = new EngineHarness();
        var v1 = EngineHarness.Pattern(80 * 1024);
        var server = new FakeContentServer
        {
            Content = v1,
            ETag = "\"v1\"",
            SupportsRanges = true,
            MaxBytesPerResponse = 20 * 1024,
        };

        var first = await harness.BuildEngine(server).RunAsync(harness.Request(), null, CancellationToken.None);
        Assert.Equal(DownloadResultKind.Failed, first.Kind);

        // Resource changes: new content + new validator. The server now fails the If-Range precondition.
        var v2 = (byte[])v1.Clone();
        Array.Reverse(v2);
        server.Content = v2;
        server.ETag = "\"v2\"";
        server.MaxBytesPerResponse = null;

        var second = await harness.BuildEngine(server).RunAsync(harness.Request(), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, second.Kind);
        Assert.Equal(v2, harness.ReadTarget()); // no stitching of old+new bytes
    }

    [Fact]
    public async Task Missing_validator_forces_restart_rather_than_unsafe_resume()
    {
        using var harness = new EngineHarness();
        var content = EngineHarness.Pattern(60 * 1024);
        var server = new FakeContentServer
        {
            Content = content,
            ETag = null,
            LastModified = null,
            SupportsRanges = true,
            MaxBytesPerResponse = 20 * 1024,
        };

        var first = await harness.BuildEngine(server).RunAsync(harness.Request(), null, CancellationToken.None);
        Assert.Equal(DownloadResultKind.Failed, first.Kind);

        server.MaxBytesPerResponse = null;
        var second = await harness.BuildEngine(server).RunAsync(harness.Request(), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, second.Kind);
        Assert.Equal(content, harness.ReadTarget());
    }

    [Fact]
    public async Task Content_range_offset_mismatch_fails_loudly()
    {
        using var harness = new EngineHarness();
        var server = new FakeContentServer
        {
            Content = EngineHarness.Pattern(40 * 1024),
            ETag = "\"v1\"",
            SupportsRanges = true,
            ReportedFromDelta = 5, // server reports a Content-Range start that doesn't match the request
        };

        var outcome = await harness.BuildEngine(server).RunAsync(harness.Request(), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Failed, outcome.Kind);
    }

    [Fact]
    public async Task Cancellation_yields_a_canceled_outcome()
    {
        using var harness = new EngineHarness();
        var server = new FakeContentServer { Content = EngineHarness.Pattern(1024), SupportsRanges = true };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(), null, new CancellationToken(canceled: true));

        Assert.Equal(DownloadResultKind.Canceled, outcome.Kind);
    }
}