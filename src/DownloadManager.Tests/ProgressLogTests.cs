using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Persistence;
using DownloadManager.Persistence.Progress;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DownloadManager.Tests;

public sealed class ProgressLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dlm-log-tests", Guid.NewGuid().ToString("N"));

    private string TargetPath => Path.Combine(_directory, "file.bin");

    private string LogPath => PersistencePaths.ProgressLogPath(TargetPath);

    public ProgressLogTests() => Directory.CreateDirectory(_directory);

    private BinaryProgressLogStore NewStore(long compactionThreshold = 1L * 1024 * 1024) =>
        new(new ProgressLogOptions { CompactionThresholdBytes = compactionThreshold },
            NullLogger<BinaryProgressLogStore>.Instance);

    [Fact]
    public async Task Recovers_the_highest_offset_per_segment()
    {
        var store = NewStore();
        var session = store.Open(TargetPath);
        session.Log.Append(new SegmentCheckpoint(0, 1_000));
        session.Log.Append(new SegmentCheckpoint(1, 2_000));
        session.Log.Append(new SegmentCheckpoint(0, 5_000));
        session.Log.FlushToDisk();
        await session.DisposeAsync();

        var reopened = store.Open(TargetPath);
        try
        {
            Assert.Equal(5_000, reopened.RecoveredOffsets[0]);
            Assert.Equal(2_000, reopened.RecoveredOffsets[1]);
        }
        finally
        {
            await reopened.DisposeAsync();
        }
    }

    [Fact]
    public async Task Empty_log_recovers_nothing()
    {
        var store = NewStore();
        var session = store.Open(TargetPath);
        try
        {
            Assert.Empty(session.RecoveredOffsets);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Truncates_a_torn_tail_of_partial_bytes()
    {
        var store = NewStore();
        var session = store.Open(TargetPath);
        session.Log.Append(new SegmentCheckpoint(0, 4_096));
        session.Log.FlushToDisk();
        await session.DisposeAsync();

        // Append a partial (torn) record: fewer bytes than a full record.
        await using (var stream = new FileStream(LogPath, FileMode.Append))
        {
            await stream.WriteAsync(new byte[10]);
        }

        var reopened = store.Open(TargetPath);
        try
        {
            Assert.Equal(4_096, reopened.RecoveredOffsets[0]);
        }
        finally
        {
            await reopened.DisposeAsync();
        }

        // The torn tail was normalized away: a clean reopen sees a single, aligned record.
        var expectedLength = 16 /* header */ + 24 /* one record */;
        Assert.Equal(expectedLength, new FileInfo(LogPath).Length);
    }

    [Fact]
    public async Task Rejects_a_record_with_a_bad_crc_and_keeps_the_prior_valid_offset()
    {
        var store = NewStore();
        var session = store.Open(TargetPath);
        session.Log.Append(new SegmentCheckpoint(0, 100));
        session.Log.Append(new SegmentCheckpoint(0, 200));
        session.Log.FlushToDisk();
        await session.DisposeAsync();

        // Corrupt a payload byte of the SECOND record (header 16 + record 24 + offset field at +4).
        var bytes = await File.ReadAllBytesAsync(LogPath);
        bytes[16 + 24 + 4] ^= 0xFF;
        await File.WriteAllBytesAsync(LogPath, bytes);

        var reopened = store.Open(TargetPath);
        try
        {
            // The corrupt record is rejected; recovery falls back to the last good offset.
            Assert.Equal(100, reopened.RecoveredOffsets[0]);
        }
        finally
        {
            await reopened.DisposeAsync();
        }
    }

    [Fact]
    public async Task Compacts_when_it_grows_past_the_threshold()
    {
        // Threshold small enough that a handful of appends triggers compaction.
        var store = NewStore(compactionThreshold: 16 + (5 * 24));
        var session = store.Open(TargetPath);
        try
        {
            for (var i = 1; i <= 50; i++)
            {
                session.Log.Append(new SegmentCheckpoint(0, i * 1_000L));
                session.Log.FlushToDisk();
            }
        }
        finally
        {
            await session.DisposeAsync();
        }

        // A single segment compacts to header + one record, far below 50 raw records.
        Assert.True(new FileInfo(LogPath).Length <= 16 + (10 * 24));

        var reopened = store.Open(TargetPath);
        try
        {
            Assert.Equal(50_000, reopened.RecoveredOffsets[0]);
        }
        finally
        {
            await reopened.DisposeAsync();
        }
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