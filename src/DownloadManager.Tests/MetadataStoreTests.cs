using DownloadManager.Core.Domain;
using DownloadManager.Persistence;
using DownloadManager.Persistence.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DownloadManager.Tests;

public sealed class MetadataStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dlm-meta-tests", Guid.NewGuid().ToString("N"));

    private readonly JsonMetadataStore _store = new(NullLogger<JsonMetadataStore>.Instance);

    private string TargetPath => Path.Combine(_directory, "file.bin");

    public MetadataStoreTests() => Directory.CreateDirectory(_directory);

    private static DownloadMetadata Sample() => new()
    {
        OriginalUrl = "http://origin/file",
        FinalUrl = "http://cdn/file",
        ETag = "\"v1\"",
        LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        TotalSize = 123_456,
        AcceptsRanges = true,
        Segments = SegmentLayout.Split(123_456, 4).Segments.ToArray(),
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Round_trips_through_source_generated_json()
    {
        var metadata = Sample();
        await _store.SaveAsync(TargetPath, metadata, CancellationToken.None);

        var loaded = await _store.TryLoadAsync(TargetPath, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(metadata.FinalUrl, loaded!.FinalUrl);
        Assert.Equal(metadata.TotalSize, loaded.TotalSize);
        Assert.Equal(metadata.ETag, loaded.ETag);
        Assert.Equal(metadata.Segments.Length, loaded.Segments.Length);
        Assert.Equal(metadata.Segments[^1], loaded.Segments[^1]);
    }

    [Fact]
    public async Task Missing_metadata_loads_as_null()
    {
        var loaded = await _store.TryLoadAsync(TargetPath, CancellationToken.None);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Unreadable_metadata_loads_as_null_instead_of_throwing()
    {
        await File.WriteAllTextAsync(PersistencePaths.MetadataPath(TargetPath), "{ this is not valid json");
        var loaded = await _store.TryLoadAsync(TargetPath, CancellationToken.None);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Interrupted_rename_leaves_the_committed_metadata_intact()
    {
        // Commit a good version.
        await _store.SaveAsync(TargetPath, Sample() with { TotalSize = 1 }, CancellationToken.None);

        // Simulate a crash during a *second* save after the temp file was written but before the
        // atomic rename happened: a stray temp sits in the directory, the target is untouched.
        var metaPath = PersistencePaths.MetadataPath(TargetPath);
        var strayTemp = Path.Combine(_directory, $".{Path.GetFileName(metaPath)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(strayTemp, "half-written, never renamed");

        // The committed metadata must still load cleanly — the interrupted write could not corrupt it.
        var loaded = await _store.TryLoadAsync(TargetPath, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.TotalSize);

        // And the stray temp must not be mistaken for a real sidecar by the recovery scan.
        Assert.DoesNotContain(strayTemp, _store.EnumerateTargets(_directory));
    }

    [Fact]
    public async Task Atomic_overwrite_leaves_no_temp_files_and_the_latest_value_wins()
    {
        await _store.SaveAsync(TargetPath, Sample() with { TotalSize = 1 }, CancellationToken.None);
        await _store.SaveAsync(TargetPath, Sample() with { TotalSize = 2 }, CancellationToken.None);

        var loaded = await _store.TryLoadAsync(TargetPath, CancellationToken.None);
        Assert.Equal(2, loaded!.TotalSize);

        // The temp-then-rename strategy must not leave partial *.tmp files behind.
        var stray = Directory.GetFiles(_directory, "*.tmp");
        Assert.Empty(stray);
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