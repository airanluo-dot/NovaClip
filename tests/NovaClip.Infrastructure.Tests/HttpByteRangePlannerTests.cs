using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class HttpByteRangePlannerTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(17, 4)]
    [InlineData(10000001, 4)]
    [InlineData(long.MaxValue, 256)]
    [InlineData(40000000, 999)]
    [InlineData(40000000, 0)]
    public void CoversExactlyOnceWithoutGapsOrOverflow(long size, int requested)
    {
        var ranges = HttpByteRangePlanner.Create(size, requested);
        Assert.InRange(ranges.Count, 1, 256);
        Assert.Equal(0, ranges[0].From);
        Assert.Equal(size - 1, ranges[^1].To);
        long total = 0;
        for (var index = 0; index < ranges.Count; index++)
        {
            Assert.True(ranges[index].Length > 0);
            if (index > 0) Assert.Equal(ranges[index - 1].To + 1, ranges[index].From);
            total = checked(total + ranges[index].Length);
        }
        Assert.Equal(size, total);
    }

    [Fact]
    public void SmallFilesRemainSingleConnection() =>
        Assert.Single(HttpByteRangePlanner.Create(1024, 16));

    [Fact]
    public void RejectsUnknownLength() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpByteRangePlanner.Create(0, 4));
}
