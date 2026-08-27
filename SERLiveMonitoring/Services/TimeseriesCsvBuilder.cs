using System.Globalization;
using System.Text;

namespace SERLiveMonitoring.Services;

// Turns DataManager.GetAllSeriesRange's per-series result into one CSV table, for the bulk export
// endpoint used by external analysis tooling (see TimeseriesEndpoints).
public static class TimeseriesCsvBuilder
{
    public static string Build(Dictionary<string, List<(long UnixTimestamp, double Value)>> series)
    {
        var columns = series.Keys.ToList();

        // Outer join onto the union of timestamps actually present across all series, rather than
        // assuming every series covers the same span - they can start/stop recording at slightly
        // different times (e.g. a device connecting late).
        var timestamps = series.Values
            .SelectMany(points => points.Select(p => p.UnixTimestamp))
            .Distinct()
            .Order()
            .ToList();

        var lookups = columns.ToDictionary(
            name => name,
            name => series[name].ToDictionary(p => p.UnixTimestamp, p => p.Value));

        var csv = new StringBuilder();
        csv.Append("timestamp,").AppendJoin(',', columns).Append('\n');

        foreach (var timestamp in timestamps)
        {
            csv.Append(timestamp.ToString(CultureInfo.InvariantCulture));

            foreach (var column in columns)
            {
                csv.Append(',');
                // A blank cell (rather than 0) for a series with no value at this timestamp - read
                // as NaN by pandas.read_csv, rather than silently implying "reported zero".
                if (lookups[column].TryGetValue(timestamp, out var value))
                    csv.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            csv.Append('\n');
        }

        return csv.ToString();
    }
}
