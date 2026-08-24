using SERLiveMonitoring.Models;
using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Tests;

public class GoogleMapsTrackBuilderTests
{
    [Fact]
    public void Build_NoPoints_ReturnsBaseUrlAndZeroCounts()
    {
        var export = GoogleMapsTrackBuilder.Build([]);

        Assert.Equal("https://www.google.com/maps/dir/", export.Url);
        Assert.Equal(0, export.SourceCount);
        Assert.Equal(0, export.ExportedCount);
        Assert.False(export.ReducedByUrlLimit);
    }

    [Fact]
    public void Build_WithinLimit_DoesNotReduce()
    {
        var points = Enumerable.Range(0, 20)
            .Select(i => NewPoint(i, i))
            .ToList();

        var export = GoogleMapsTrackBuilder.Build(points, maxUrlLength: 5000);

        Assert.Equal(points.Count, export.SourceCount);
        Assert.Equal(points.Count, export.ExportedCount);
        Assert.Equal(1, export.GapPoints);
        Assert.False(export.ReducedByUrlLimit);
    }

    [Fact]
    public void Build_OverLimit_ReducesAndPreservesEndpoints()
    {
        var points = Enumerable.Range(0, 400)
            .Select(i => NewPoint(i * 0.0002, i * 0.0003))
            .ToList();

        var export = GoogleMapsTrackBuilder.Build(points, maxUrlLength: 400);

        Assert.True(export.ReducedByUrlLimit);
        Assert.True(export.ExportedCount < points.Count);
        Assert.True(export.Url.Length <= 400);
        Assert.Contains(Waypoint(points[0]), export.Url);
        Assert.EndsWith(Waypoint(points[^1]), export.Url, StringComparison.Ordinal);
        Assert.True(export.GapPoints > 1);
    }

    private static GpsPoint NewPoint(double latOffset, double lonOffset)
    {
        return new GpsPoint
        {
            Timestamp = DateTime.Now,
            Latitude = 52.000000 + latOffset,
            Longitude = 13.000000 + lonOffset
        };
    }

    private static string Waypoint(GpsPoint point)
    {
        return $"{point.Latitude:0.000000},{point.Longitude:0.000000}";
    }
}
