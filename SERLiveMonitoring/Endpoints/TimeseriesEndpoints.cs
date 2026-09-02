using Microsoft.AspNetCore.Mvc;
using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Endpoints;

// Bulk CSV export of every stored timeseries channel, for external analysis tooling (e.g. a Python
// script pulling a race's data into pandas) - not for the Blazor UI, which reads DataManager
// in-process instead. Kestrel already binds to 0.0.0.0 (see appsettings.json), so this is reachable
// from anywhere on the same network, same trusted-network/no-auth assumption as the GPS API.
public static class TimeseriesEndpoints
{
    public static void MapTimeseriesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/timeseries");

        // GET /api/timeseries?start={unixSeconds}&end={unixSeconds}
        //
        // CSV, one row per second in [start, end], one column per series DataManager tracks (see
        // DataManager.SeriesNames - speed, mppt1-4_power, motor_current/voltage/power,
        // battery_voltage/current/power). See TimeseriesCsvBuilder for the exact layout.
        group.MapGet("/", (long? start, long? end, DataManager dataManager) =>
        {
            if (start is null || end is null)
                return Results.BadRequest("start and end query parameters (unix seconds) are required.");
            if (end <= start)
                return Results.BadRequest("end must be after start.");

            var startTime = DateTimeOffset.FromUnixTimeSeconds(start.Value).LocalDateTime;
            var endTime = DateTimeOffset.FromUnixTimeSeconds(end.Value).LocalDateTime;

            var csv = TimeseriesCsvBuilder.Build(dataManager.GetAllSeriesRange(startTime, endTime));
            return Results.Text(csv, "text/csv");
        });

        // GET /api/timeseries/range?from={0}&to={1}&series={2}
        //
        //   from   - required. UTC timestamp marking the lower bound of the range (inclusive).
        //   to     - optional. UTC timestamp marking the upper bound (inclusive). Open-ended (up
        //            to the newest record) if omitted.
        //   series - optional, comma-separated series names (see DataManager.SeriesNames) to
        //            include, e.g. "speed,battery_voltage". Defaults to every series if omitted.
        //
        // Both from/to accept a full ISO 8601 timestamp (with offset or "Z") or a shortened prefix
        // of one, e.g. "2026-09-02T19" or "2026-09-02" - floored to the start of that unit, since
        // from/to mark the outer limits of the range rather than an exact instant. JSON
        // alternative to GET "/" above, which stays CSV/unix-seconds for existing consumers.
        group.MapGet("/range", (
            [FromQuery(Name = "from")] string? from,
            [FromQuery(Name = "to")] string? to,
            [FromQuery(Name = "series")] string? series,
            DataManager dataManager) =>
        {
            if (string.IsNullOrWhiteSpace(from))
                return Results.BadRequest("from is required.");

            var fromUtc = UtcRangeTimestampParser.Parse(from);
            if (fromUtc is null)
                return Results.BadRequest("from is not a valid UTC timestamp.");

            DateTime? toUtc = null;
            if (!string.IsNullOrWhiteSpace(to))
            {
                toUtc = UtcRangeTimestampParser.Parse(to);
                if (toUtc is null)
                    return Results.BadRequest("to is not a valid UTC timestamp.");
            }

            if (toUtc is not null && toUtc < fromUtc)
                return Results.BadRequest("to must not be earlier than from.");

            List<string>? requestedSeries = null;
            if (!string.IsNullOrWhiteSpace(series))
            {
                requestedSeries = series.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
                var unknown = requestedSeries.Where(name => !dataManager.SeriesNames.Contains(name)).ToList();
                if (unknown.Count > 0)
                    return Results.BadRequest(
                        $"Unknown series: {string.Join(", ", unknown)}. Available series: {string.Join(", ", dataManager.SeriesNames)}.");
            }

            // GetAllSeriesRange (like TimeSeries.AddAndInterpolate) compares against local-kind
            // DateTime, matching the existing CSV endpoint's unix-seconds-to-LocalDateTime conversion.
            var fromLocal = fromUtc.Value.ToLocalTime();
            var toLocal = toUtc?.ToLocalTime() ?? DateTime.Now;

            var allSeries = dataManager.GetAllSeriesRange(fromLocal, toLocal);
            var filtered = requestedSeries is null
                ? allSeries
                : allSeries.Where(kv => requestedSeries.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

            return Results.Ok(TimeseriesJsonBuilder.Build(filtered));
        });
    }
}

