using DownloadManager.Core.Engine;
using Xunit;

namespace DownloadManager.Tests;

public class ProgressAggregatorTests
{
    [Fact]
    public void Snapshot_sums_per_segment_completion_and_reports_total()
    {
        var aggregator = new ProgressAggregator(totalBytes: 1000, segmentCount: 3);
        aggregator.Set(0, 100);
        aggregator.Set(1, 250);
        aggregator.Set(2, 50);

        var snapshot = aggregator.Snapshot();
        Assert.Equal(400, snapshot.CompletedBytes);
        Assert.Equal(1000, snapshot.TotalBytes);
    }

    [Fact]
    public void Latest_set_per_segment_wins_and_a_segment_can_drop()
    {
        var aggregator = new ProgressAggregator(totalBytes: 1000, segmentCount: 2);
        aggregator.Set(0, 400);
        aggregator.Set(0, 900); // climbs
        aggregator.Set(0, 200); // resource-changed fallback lowers it
        aggregator.Set(1, 100);

        Assert.Equal(300, aggregator.Snapshot().CompletedBytes);
    }

    [Fact]
    public async Task Concurrent_writers_on_disjoint_slots_sum_exactly_with_no_torn_or_lost_updates()
    {
        const int segments = 16;
        const int steps = 50_000;
        var aggregator = new ProgressAggregator(totalBytes: segments * (long)steps, segmentCount: segments);

        // Each writer exclusively owns one slot and climbs it to a known final value, while a reader
        // concurrently snapshots. Disjoint slots + Volatile => no torn long, no lost cross-slot update.
        using var done = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!done.Token.IsCancellationRequested)
            {
                var seen = aggregator.Snapshot().CompletedBytes;
                Assert.InRange(seen, 0, segments * (long)steps); // never torn/negative/over-total
            }
        });

        var writers = Enumerable.Range(0, segments).Select(segmentId => Task.Run(() =>
        {
            for (var step = 1; step <= steps; step++)
            {
                aggregator.Set(segmentId, step); // climbs from 1..steps
            }
        })).ToArray();

        await Task.WhenAll(writers);
        await done.CancelAsync();
        await reader;

        // Every slot finished at `steps`; the total is exact (nothing lost).
        Assert.Equal(segments * (long)steps, aggregator.Snapshot().CompletedBytes);
    }
}