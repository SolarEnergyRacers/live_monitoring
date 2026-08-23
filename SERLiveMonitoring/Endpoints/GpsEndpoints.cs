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

        group.MapPost("/", (GpsLocationRequest request, GpsTrackService gpsTrackService) =>
        {
            if (request.Latitude is < -90 or > 90)
                return Results.BadRequest("Latitude must be between -90 and 90.");
            if (request.Longitude is < -180 or > 180)
                return Results.BadRequest("Longitude must be between -180 and 180.");

            var point = gpsTrackService.Add(new GpsPoint
            {
                // Prefer the device's own fix time when it sends one - it knows when the fix was
                // actually taken, which can lag slightly behind when the upload arrives.
                Timestamp = request.Timestamp ?? DateTime.Now,
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
    }

    public const string GpsCorsPolicy = "GpsCorsPolicy";
}

public record GpsLocationRequest(double Latitude, double Longitude, DateTime? Timestamp, double? SpeedKmh, double? AccuracyMeters);
