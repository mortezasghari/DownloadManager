using DownloadManager.Core.Domain;
using DownloadManager.Core.Recovery;
using DownloadManager.Persistence;
using DownloadManager.Persistence.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DownloadManager.Tests;

public sealed class RecoveryServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dlm-recovery-tests", Guid.NewGuid().ToString("N"));

    private readonly JsonMetadataStore _store = new(NullLogger<JsonMetadataStore>.Instance);

    public RecoveryServiceTests() => Directory.CreateDirectory(_directory);

    private RecoveryService NewService() => new(_store, NullLogger<RecoveryService>.Instance);

    private static DownloadMetadata Metadata(string url) => new()
    {
        OriginalUrl = url,
        FinalUrl = url,
        TotalSize = 1024,
        AcceptsRanges = true,
        Segments = SegmentLayout.Single(1024).Segments.ToArray(),
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Scan_finds_every_download_with_a_metadata_sidecar()
    {
        var a = Path.Combine(_directory, "a.bin");
        var b = Path.Combine(_directory, "b.iso");
        await _store.SaveAsync(a, Metadata("http://origin/a"), CancellationToken.None);
        await _store.SaveAsync(b, Metadata("http://origin/b"), CancellationToken.None);

        // A bare target file with no sidecar must not be reported.
        await File.WriteAllTextAsync(Path.Combine(_directory, "c.txt"), "no sidecar");

        var candidates = await NewService().ScanAsync(_directory, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.TargetPath == a && c.OriginalUrl == new Uri("http://origin/a"));
        Assert.Contains(candidates, c => c.TargetPath == b && c.OriginalUrl == new Uri("http://origin/b"));
    }

    [Fact]
    public async Task Scan_skips_unreadable_metadata()
    {
        var good = Path.Combine(_directory, "good.bin");
        await _store.SaveAsync(good, Metadata("http://origin/good"), CancellationToken.None);

        var bad = Path.Combine(_directory, "bad.bin");
        await File.WriteAllTextAsync(PersistencePaths.MetadataPath(bad), "not json");

        var candidates = await NewService().ScanAsync(_directory, CancellationToken.None);

        Assert.Single(candidates);
        Assert.Equal(good, candidates[0].TargetPath);
    }

    [Fact]
    public async Task Scan_of_empty_or_missing_directory_returns_nothing()
    {
        var candidates = await NewService().ScanAsync(_directory, CancellationToken.None);
        Assert.Empty(candidates);

        var missing = await NewService()
            .ScanAsync(Path.Combine(_directory, "does-not-exist"), CancellationToken.None);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task Resume_candidate_builds_a_request_for_the_same_target()
    {
        var target = Path.Combine(_directory, "movie.mkv");
        await _store.SaveAsync(target, Metadata("http://origin/movie"), CancellationToken.None);

        var candidate = Assert.Single(await NewService().ScanAsync(_directory, CancellationToken.None));
        var request = candidate.ToRequest();

        Assert.Equal(target, request.TargetPath);
        Assert.Equal(new Uri("http://origin/movie"), request.Url);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}