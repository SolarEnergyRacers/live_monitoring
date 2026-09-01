using Microsoft.AspNetCore.Mvc;
using SERLiveMonitoring.Models;
using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Endpoints;

// A GPS-reporting device (phone, tracker, etc.) POSTs its current fix here. Kestrel already binds
// to 0.0.0.0 (see appsettings.json), so a phone on the same network can reach this without any
// extra setup - just http://<this machine's LAN IP>:5240/api/gps.
public static class GpsEndpoints
{
    public static void MapGpsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/gps").RequireCors(GpsCorsPolicy);

        // GET /api/gps has no handler - only POST "/", GET "/latest" and GET "/report" are mapped
        // below. Browsing to the bare group URL otherwise 404s with no explanation, so this
        // lists what's actually available.
        group.MapGet("/", () => Results.Ok(new
        {
            endpoints = new[]
            {
                "POST /api/gps - body: { deviceName, latitude, longitude, timestamp?, speedKmh?, accuracyMeters? }",
                "GET /api/gps/latest - latest recorded GPS point",
                "GET /api/gps/report?device={0}&lat={1}&lon={2}&timestamp={3}&hdop={4}&altitude={5}&speed={6}&bearing={7}&eta={8}&etfa={9}&eda={10}&edfa={11}&batproc={12}"
            }
        }));

        group.MapPost("/", (GpsLocationRequest request, GpsTrackService gpsTrackService) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceName))
                return Results.BadRequest("deviceName is required.");
            if (request.Latitude is < -90 or > 90)
                return Results.BadRequest("Latitude must be between -90 and 90.");
            if (request.Longitude is < -180 or > 180)
                return Results.BadRequest("Longitude must be between -180 and 180.");

            var point = gpsTrackService.Add(new GpsPoint
            {
                DeviceName = request.DeviceName,
                // Prefer the device's own fix time when it sends one - it knows when the fix was
                // actually taken, which can lag slightly behind when the upload arrives. Normalized
                // to local so it stays comparable to DateTime.Now (e.g. the GPS LED's age check).
                Timestamp = NormalizeToLocal(request.Timestamp) ?? DateTime.Now,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                SpeedKmh = request.SpeedKmh,
                AccuracyMeters = request.AccuracyMeters
            });

            return Results.Created($"/api/gps/{point.Id}", point);
        });

        group.MapGet("/latest", (DataManager dataManager) =>
        {
            var latest = dataManager.GetLatestGpsPoint();
            return latest is null ? Results.NotFound() : Results.Ok(latest);
        });

        // GET /api/gps/report?lat={0}&lon={1}&timestamp={2}&hdop={3}&altitude={4}&speed={5}&bearing={6}&eta={7}&etfa={8}&eda={9}&edfa={10}&batproc={11}
        //
        // Query-string variant of POST "/", for GPS-reporting apps that can only fire plain GET
        // requests (e.g. a "share location via URL" feature) rather than send a JSON body.
        //
        //   device   - required. Name identifying the reporting device.
        //   lat      - required. Latitude in degrees, -90..90.
        //   lon      - required. Longitude in degrees, -180..180.
        //   timestamp- optional. Fix time, either ISO 8601 or Unix epoch milliseconds. Defaults to
        //              server time if omitted or unparseable.
        //   hdop     - optional. Horizontal dilution of precision; stored as AccuracyMeters.
        //   speed    - optional. Speed in km/h; stored as SpeedKmh.
        //   altitude, bearing, eta, etfa, eda, edfa, batproc - accepted for compatibility with the
        //   reporting device's URL format, but GpsPoint has no matching field so they are not persisted.
        //
        // Any of these parameters may be left out of the query string entirely - only device/lat/lon
        // are actually required to build a GpsPoint. A request missing them is logged to the console
        // with all the parameters it did send, to help diagnose the reporting device's URL.
        _ = group.MapGet("/report", (
            [FromQuery(Name = "device")] string? device,
            [FromQuery(Name = "lat")] double? lat,
            [FromQuery(Name = "lon")] double? lon,
            [FromQuery(Name = "timestamp")] string? timestamp,
            [FromQuery(Name = "hdop")] double? hdop,
            [FromQuery(Name = "altitude")] double? altitude,
            [FromQuery(Name = "speed")] double? speed,
            [FromQuery(Name = "bearing")] double? bearing,
            [FromQuery(Name = "eta")] double? eta,
            [FromQuery(Name = "etfa")] double? etfa,
            [FromQuery(Name = "eda")] double? eda,
            [FromQuery(Name = "edfa")] double? edfa,
            [FromQuery(Name = "batproc")] double? batproc,
            GpsTrackService gpsTrackService) =>
        {
            void LogFailure(string reason) =>
                Console.WriteLine(
                    $"[GPS] Rejected /api/gps/report call ({reason}): device={device}, lat={lat}, lon={lon}, timestamp={timestamp}, " +
                    $"hdop={hdop}, altitude={altitude}, speed={speed}, bearing={bearing}, eta={eta}, etfa={etfa}, " +
                    $"eda={eda}, edfa={edfa}, batproc={batproc}");

            try
            {
                if (string.IsNullOrWhiteSpace(device))
                {
                    LogFailure("missing device");
                    return Results.BadRequest("device is required.");
                }
                if (lat is null || lon is null)
                {
                    LogFailure("missing lat/lon");
                    return Results.BadRequest("lat and lon are required.");
                }
                if (lat is < -90 or > 90)
                {
                    LogFailure("lat out of range");
                    return Results.BadRequest("lat must be between -90 and 90.");
                }
                if (lon is < -180 or > 180)
                {
                    LogFailure("lon out of range");
                    return Results.BadRequest("lon must be between -180 and 180.");
                }

                var point = gpsTrackService.Add(new GpsPoint
                {
                    DeviceName = device,
                    Timestamp = ParseTimestamp(timestamp) ?? DateTime.Now,
                    Latitude = lat.Value,
                    Longitude = lon.Value,
                    SpeedKmh = speed,
                    AccuracyMeters = hdop
                });

                return Results.Created($"/api/gps/{point.Id}", point);
            }
            catch (Exception ex)
            {
                LogFailure($"unhandled exception: {ex.Message}");
                return Results.Problem("Failed to process GPS report.", statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    // Accepts either an ISO 8601 string or Unix epoch milliseconds (some reporting devices send
    // the latter), falling back to null - and therefore server time - for anything else.
    private static DateTime? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            return NormalizeToLocal(parsed);

        if (long.TryParse(raw, out var epochMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).LocalDateTime;

        return null;
    }

    // DateTime.TryParse with RoundtripKind keeps a "Z"/offset timestamp as Kind=Utc without
    // converting its value, which otherwise silently skews every DateTime.Now comparison
    // (e.g. the GPS LED's flowing check) by the local UTC offset.
    private static DateTime? NormalizeToLocal(DateTime? dt) =>
        dt is { Kind: DateTimeKind.Utc } utc ? utc.ToLocalTime() : dt;

    public const string GpsCorsPolicy = "GpsCorsPolicy";
}

public record GpsLocationRequest(string DeviceName, double Latitude, double Longitude, DateTime? Timestamp, double? SpeedKmh, double? AccuracyMeters);
