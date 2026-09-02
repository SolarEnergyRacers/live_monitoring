namespace SERLiveMonitoring.Services;

// JSON counterpart to TimeseriesCsvBuilder, for GET /api/timeseries/range - same outer-join-by-
// timestamp approach, but shaped as { series, points } instead of a CSV table.
public static class TimeseriesJsonBuilder
{
    public static TimeseriesRangeResult Build(Dictionary<string, List<(long UnixTimestamp, double Value)>> series)
    {
        var columns = series.Keys.ToList();

        // Outer join onto the union of timestamps actually present across all series, rather than
        // assuming every series covers the same span - matches TimeseriesCsvBuilder.Build.
        var timestamps = series.Values
            .SelectMany(points => points.Select(p => p.UnixTimestamp))
            .Distinct()
            .Order()
            .ToList();

        var lookups = columns.ToDictionary(
            name => name,
            name => series[name].ToDictionary(p => p.UnixTimestamp, p => p.Value));

        var points = timestamps.Select(timestamp => new TimeseriesRangePoint(
            DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime,
            columns.Select(column => lookups[column].TryGetValue(timestamp, out var value) ? (double?)value : null).ToArray()
        )).ToList();

        return new TimeseriesRangeResult(columns, points);
    }
}

public record TimeseriesRangeResult(List<string> Series, List<TimeseriesRangePoint> Points);

public record TimeseriesRangePoint(DateTime Timestamp, double?[] Values);
