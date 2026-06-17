using DownloadManager.Core.Domain;
using Xunit;

namespace DownloadManager.Tests;

public class SegmentLayoutTests
{
    [Fact]
    public void Single_segment_covers_the_whole_resource()
    {
        var layout = SegmentLayout.Single(100);
        Assert.Equal(1, layout.Count);
        Assert.Equal(new SegmentRange(0, 99), layout[0]);
        Assert.Equal(100, layout[0].Length);
    }

    [Fact]
    public void Split_gives_the_remainder_to_the_last_segment()
    {
        var layout = SegmentLayout.Split(100, 8);

        Assert.Equal(8, layout.Count);
        // 100 / 8 = 12 each; the last segment absorbs the remaining 16.
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(12, layout[i].Length);
        }

        Assert.Equal(new SegmentRange(84, 99), layout[7]);
        Assert.Equal(16, layout[7].Length);
    }

    [Fact]
    public void Split_segments_are_contiguous_and_cover_exactly_the_total()
    {
        var layout = SegmentLayout.Split(1000, 7);

        long expectedStart = 0;
        foreach (var segment in layout.Segments)
        {
            Assert.Equal(expectedStart, segment.Start);
            expectedStart = segment.EndInclusive + 1;
        }

        Assert.Equal(1000, expectedStart);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 8)]   // more segments requested than bytes -> capped to 1
    [InlineData(5, 8)]
    public void Split_never_produces_a_zero_length_segment(long total, int requested)
    {
        var layout = SegmentLayout.Split(total, requested);
        Assert.All(layout.Segments, s => Assert.True(s.Length >= 1));
        Assert.Equal(total, SumLengths(layout));
    }

    [Fact]
    public void Split_off_by_one_even_division()
    {
        var layout = SegmentLayout.Split(10, 2);
        Assert.Equal(new SegmentRange(0, 4), layout[0]);
        Assert.Equal(new SegmentRange(5, 9), layout[1]);
    }

    [Fact]
    public void FromPersisted_accepts_a_contiguous_tiling()
    {
        var segments = new[] { new SegmentRange(0, 4), new SegmentRange(5, 9) };
        var layout = SegmentLayout.FromPersisted(segments, 10);
        Assert.Equal(2, layout.Count);
    }

    [Fact]
    public void FromPersisted_rejects_a_gap()
    {
        var segments = new[] { new SegmentRange(0, 4), new SegmentRange(6, 9) };
        Assert.Throws<ArgumentException>(() => SegmentLayout.FromPersisted(segments, 10));
    }

    [Fact]
    public void FromPersisted_rejects_wrong_total()
    {
        var segments = new[] { new SegmentRange(0, 9) };
        Assert.Throws<ArgumentException>(() => SegmentLayout.FromPersisted(segments, 100));
    }

    private static long SumLengths(SegmentLayout layout)
    {
        long sum = 0;
        foreach (var s in layout.Segments)
        {
            sum += s.Length;
        }

        return sum;
    }
}

public class SegmentRangeTests
{
    [Fact]
    public void Create_rejects_negative_start() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentRange.Create(-1, 10));

    [Fact]
    public void Create_rejects_end_before_start() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentRange.Create(10, 9));

    [Fact]
    public void Single_byte_range_has_length_one()
    {
        var range = SegmentRange.Create(0, 0);
        Assert.Equal(1, range.Length);
        Assert.True(range.Contains(0));
        Assert.False(range.Contains(1));
    }
}

public class ResourceValidatorsTests
{
    [Fact]
    public void Strong_etag_is_preferred_for_if_range()
    {
        var validators = new ResourceValidators("\"abc\"", DateTimeOffset.UtcNow);
        Assert.True(validators.HasStrongETag);
        Assert.Equal("\"abc\"", validators.ToIfRangeHeaderValue());
    }

    [Fact]
    public void Weak_etag_falls_back_to_last_modified()
    {
        var lastModified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var validators = new ResourceValidators("W/\"abc\"", lastModified);

        Assert.False(validators.HasStrongETag);
        Assert.Equal(lastModified.ToString("R"), validators.ToIfRangeHeaderValue());
    }

    [Fact]
    public void No_validators_means_no_if_range_value()
    {
        Assert.False(ResourceValidators.None.HasUsableValidator);
        Assert.Null(ResourceValidators.None.ToIfRangeHeaderValue());
    }
}