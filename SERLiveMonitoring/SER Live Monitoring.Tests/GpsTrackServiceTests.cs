using SERLiveMonitoring.Models;
using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Tests;

public class GpsTrackServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ser-live-monitoring-tests-{Guid.NewGuid():N}.db");
    private readonly DataManager _dataManager = new(new SerialPortMonitorService(new CANFrameDecoder(new SettingsService(TestSettingsPath.NewTempPath()))));

    private static GpsPoint NewPoint(DateTime ts, double lat, double lon) => new() { Timestamp = ts, Latitude = lat, Longitude = lon };

    private static DataManager NewDataManager() => new(new SerialPortMonitorService(new CANFrameDecoder(new SettingsService(TestSettingsPath.NewTempPath()))));

    [Fact]
    public void Add_AssignsAnId()
    {
        using var service = new GpsTrackService(_dataManager, _dbPath);

        var added = service.Add(NewPoint(DateTime.Now, 51.5, -0.1));

        Assert.True(added.Id > 0);
    }

    [Fact]
    public void Add_PushesThePointIntoDataManager()
    {
        using var service = new GpsTrackService(_dataManager, _dbPath);

        service.Add(NewPoint(DateTime.Now, 51.5, -0.1));

        var latest = _dataManager.GetLatestGpsPoint();
        Assert.NotNull(latest);
        Assert.Equal(51.5, latest!.Latitude);
        Assert.Equal(-0.1, latest.Longitude);
    }

    [Fact]
    public void ReopeningSameDbPath_RestoresPreviouslyAddedPointsIntoDataManager()
    {
        var timestamp = DateTime.Now;
        using (var service = new GpsTrackService(_dataManager, _dbPath))
            service.Add(NewPoint(timestamp, 12.34, 56.78));

        // A fresh DataManager, so the restore can only be coming from disk - not the first
        // GpsTrackService's in-memory state.
        using var freshDataManager = NewDataManager();
        using var reopened = new GpsTrackService(freshDataManager, _dbPath);

        var latest = freshDataManager.GetLatestGpsPoint();
        Assert.NotNull(latest);
        Assert.Equal(12.34, latest!.Latitude);
        Assert.Equal(56.78, latest.Longitude);
    }

    [Fact]
    public void OptionalFields_RoundTripThroughPersistence()
    {
        using (var service = new GpsTrackService(_dataManager, _dbPath))
            service.Add(new GpsPoint { Timestamp = DateTime.Now, Latitude = 1, Longitude = 2, SpeedKmh = 42.5, AccuracyMeters = 3.2 });

        using var freshDataManager = NewDataManager();
        using var reopened = new GpsTrackService(freshDataManager, _dbPath);

        var latest = freshDataManager.GetLatestGpsPoint();
        Assert.NotNull(latest);
        Assert.Equal(42.5, latest!.SpeedKmh);
        Assert.Equal(3.2, latest.AccuracyMeters);
    }

    [Fact]
    public void OmittedOptionalFields_RoundTripAsNull()
    {
        using (var service = new GpsTrackService(_dataManager, _dbPath))
            service.Add(NewPoint(DateTime.Now, 1, 2));

        using var freshDataManager = NewDataManager();
        using var reopened = new GpsTrackService(freshDataManager, _dbPath);

        var latest = freshDataManager.GetLatestGpsPoint();
        Assert.NotNull(latest);
        Assert.Null(latest!.SpeedKmh);
        Assert.Null(latest.AccuracyMeters);
    }

    public void Dispose()
    {
        _dataManager.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }
}
