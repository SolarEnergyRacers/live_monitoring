namespace SERLiveMonitoring.Models;

public class GpsPoint
{
    public long Id { get; set; }
    public required string DeviceName { get; set; }
    public DateTime Timestamp { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    // Optional - not every GPS-reporting device sends these.
    public double? SpeedKmh { get; set; }
    public double? AccuracyMeters { get; set; }
}
