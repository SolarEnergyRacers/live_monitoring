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
    }
}
