using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Tests;

public class TimeseriesJsonBuilderTests
{
    [Fact]
    public void Build_NoSeries_ReturnsEmptyResult()
    {
        var result = TimeseriesJsonBuilder.Build(new());

        Assert.Empty(result.Series);
        Assert.Empty(result.Points);
    }

    [Fact]
    public void Build_SingleSeries_WritesOnePointPerTimestamp()
    {
        var series = new Dictionary<string, List<(long UnixTimestamp, double Value)>>
        {
            ["speed"] = [(100, 10), (101, 20)]
        };

        var result = TimeseriesJsonBuilder.Build(series);

        Assert.Equal(["speed"], result.Series);
        Assert.Equal(2, result.Points.Count);
        Assert.Equal(new DateTime(1970, 1, 1, 0, 1, 40, DateTimeKind.Utc), result.Points[0].Timestamp);
        Assert.Equal([10.0], result.Points[0].Values);
        Assert.Equal([20.0], result.Points[1].Values);
    }

    [Fact]
    public void Build_SeriesWithDifferentExtents_OuterJoinsAndLeavesGapsNull()
    {
        // "speed" only has a point at 101, but "battery_power" spans 100-102 - the missing speed
        // values at 100 and 102 must render as null, not be dropped from the result or default to 0.
        var series = new Dictionary<string, List<(long UnixTimestamp, double Value)>>
        {
            ["speed"] = [(101, 50)],
            ["battery_power"] = [(100, 500), (101, 600), (102, 700)]
        };

        var result = TimeseriesJsonBuilder.Build(series);

        Assert.Equal(["speed", "battery_power"], result.Series);
        Assert.Equal(3, result.Points.Count);
        Assert.Equal([null, 500.0], result.Points[0].Values);
        Assert.Equal([50.0, 600.0], result.Points[1].Values);
        Assert.Equal([null, 700.0], result.Points[2].Values);
    }

    [Fact]
    public void Build_PointsAreOrderedAscendingByTimestamp()
    {
        var series = new Dictionary<string, List<(long UnixTimestamp, double Value)>>
        {
            ["speed"] = [(102, 1), (100, 2), (101, 3)]
        };

        var result = TimeseriesJsonBuilder.Build(series);

        Assert.Equal([100, 101, 102], result.Points.Select(p => new DateTimeOffset(p.Timestamp).ToUnixTimeSeconds()));
    }

    [Fact]
    public void Build_TimestampsAreUtcKind()
    {
        var series = new Dictionary<string, List<(long UnixTimestamp, double Value)>>
        {
            ["speed"] = [(100, 1)]
        };

        var result = TimeseriesJsonBuilder.Build(series);

        Assert.Equal(DateTimeKind.Utc, result.Points[0].Timestamp.Kind);
    }
}
