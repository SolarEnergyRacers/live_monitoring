using SERLiveMonitoring.Models;
using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Tests;

public class DataManagerTests
{
    private readonly DataManager _dataManager = new(new SerialPortMonitorService(new CANFrameDecoder(new SettingsService(TestSettingsPath.NewTempPath()))));

    private static Reading NewReading(DateTime ts, string name, double value, params (string Key, string Value)[] tags)
        => new() { Timestamp = ts, ReadingName = name, Value = value, Unit = "", Tags = tags.ToDictionary(t => t.Key, t => t.Value) };

    [Fact]
    public void GetAverage_NoData_ReturnsNull()
    {
        Assert.Null(_dataManager.GetAverage(ChartSeries.Speed, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void GetAverage_Speed_AveragesRecentWindow()
    {
        var now = DateTime.Now;
        _dataManager.UpdateTimeseries(
        [
            NewReading(now.AddSeconds(-4), "speed", 10),
            NewReading(now.AddSeconds(-2), "speed", 20),
            NewReading(now, "speed", 30),
        ]);

        var avg = _dataManager.GetAverage(ChartSeries.Speed, TimeSpan.FromSeconds(15));

        Assert.NotNull(avg);
        Assert.InRange(avg!.Value, 10, 30);
    }

    [Fact]
    public void GetAverage_BatteryPower_MatchesDecoderReadingName()
    {
        // The decoder emits "calc_batt_power" (see CANFrameDecoder.DecodeBms); DataManager must
        // listen for that exact name or battery power silently never gets tracked.
        var now = DateTime.Now;
        _dataManager.UpdateTimeseries([NewReading(now, "calc_batt_power", 500)]);

        Assert.Equal(500, _dataManager.GetAverage(ChartSeries.BatteryPower, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void GetSeries_SolarTotal_SumsAllConnectedMppts()
    {
        var now = DateTime.Now;
        _dataManager.UpdateTimeseries(
        [
            NewReading(now, "calc_mppt_out_power", 100, ("mppt_id", "1")),
            NewReading(now, "calc_mppt_out_power", 200, ("mppt_id", "2")),
            NewReading(now, "calc_mppt_out_power", 300, ("mppt_id", "3")),
            NewReading(now, "calc_mppt_out_power", 400, ("mppt_id", "4")),
        ]);

        var total = _dataManager.GetSeries(ChartSeries.SolarTotal, TimeSpan.FromSeconds(15));

        Assert.Equal(1000, total[^1]);
    }

    [Fact]
    public void GetSeries_SolarTotal_HandlesMpptsWithNoData()
    {
        var now = DateTime.Now;
        // Only two of the four MPPTs have ever reported in; the other two must contribute 0, not
        // shrink the result or throw, even though their series are empty.
        _dataManager.UpdateTimeseries(
        [
            NewReading(now, "calc_mppt_out_power", 150, ("mppt_id", "1")),
            NewReading(now, "calc_mppt_out_power", 250, ("mppt_id", "2")),
        ]);

        var total = _dataManager.GetSeries(ChartSeries.SolarTotal, TimeSpan.FromSeconds(15));

        Assert.Equal(400, total[^1]);
    }

    [Fact]
    public void AddDriverMessage_AppearsUnconfirmed()
    {
        var message = _dataManager.AddDriverMessage(DriverMessageSeverity.Info, "pit stop");

        var messages = _dataManager.GetDriverMessages();

        Assert.Single(messages);
        Assert.Equal(message.Id, messages[0].Id);
        Assert.Null(messages[0].ConfirmedAt);
    }

    [Fact]
    public void DriverConfirmRisingEdge_ConfirmsLatestUnconfirmedMessage()
    {
        _dataManager.AddDriverMessage(DriverMessageSeverity.Warn, "charge stop");
        var confirmTime = DateTime.Now;

        _dataManager.Ingest([NewReading(confirmTime, "driver_confirm", 1)]);

        var message = _dataManager.GetDriverMessages().Single();
        Assert.Equal(confirmTime, message.ConfirmedAt);
    }

    [Fact]
    public void DriverConfirmHeldHigh_DoesNotConfirmMessagesSentAfterTheEdge()
    {
        // driver_confirm stays at 1 for a couple of seconds once pressed; only the 0->1 transition
        // should count as "just pressed", otherwise a message sent while the flag is still high
        // would be wrongly marked confirmed without the driver ever having pressed anything for it.
        _dataManager.Ingest([NewReading(DateTime.Now, "driver_confirm", 1)]);
        _dataManager.Ingest([NewReading(DateTime.Now, "driver_confirm", 1)]);

        var laterMessage = _dataManager.AddDriverMessage(DriverMessageSeverity.Info, "sent after the press");
        _dataManager.Ingest([NewReading(DateTime.Now, "driver_confirm", 1)]);

        Assert.Null(_dataManager.GetDriverMessages().Single(m => m.Id == laterMessage.Id).ConfirmedAt);
    }

    [Fact]
    public void DriverConfirm_OnlyConfirmsLatestMessage_OlderUnconfirmedStaysUnconfirmed()
    {
        var older = _dataManager.AddDriverMessage(DriverMessageSeverity.Info, "first");
        var newer = _dataManager.AddDriverMessage(DriverMessageSeverity.Info, "second");

        _dataManager.Ingest([NewReading(DateTime.Now, "driver_confirm", 1)]);

        var messages = _dataManager.GetDriverMessages().ToDictionary(m => m.Id);
        Assert.NotNull(messages[newer.Id].ConfirmedAt);
        Assert.Null(messages[older.Id].ConfirmedAt);
    }

    private static GpsPoint NewGpsPoint(DateTime ts, double lat, double lon) => new() { Timestamp = ts, Latitude = lat, Longitude = lon };

    [Fact]
    public void GetLatestGpsPoint_NoData_ReturnsNull()
    {
        Assert.Null(_dataManager.GetLatestGpsPoint());
    }

    [Fact]
    public void AddGpsPoint_UpdatesLatest()
    {
        var now = DateTime.Now;
        _dataManager.AddGpsPoint(NewGpsPoint(now.AddSeconds(-5), 1, 1));
        _dataManager.AddGpsPoint(NewGpsPoint(now, 51.5, -0.1));

        var latest = _dataManager.GetLatestGpsPoint();

        Assert.NotNull(latest);
        Assert.Equal(51.5, latest!.Latitude);
        Assert.Equal(-0.1, latest.Longitude);
    }

    [Fact]
    public void AddGpsPoint_RaisesGpsPointAdded()
    {
        GpsPoint? raised = null;
        _dataManager.GpsPointAdded += p => raised = p;

        var added = _dataManager.AddGpsPoint(NewGpsPoint(DateTime.Now, 1, 2));

        Assert.Same(added, raised);
    }

    [Fact]
    public void RestoreGpsPoints_DoesNotRaiseGpsPointAdded()
    {
        var raised = false;
        _dataManager.GpsPointAdded += _ => raised = true;

        _dataManager.RestoreGpsPoints([NewGpsPoint(DateTime.Now, 1, 2)]);

        Assert.False(raised);
        Assert.NotNull(_dataManager.GetLatestGpsPoint());
    }

    [Fact]
    public void GetGpsHistory_Window_ExcludesPointsOlderThanWindow()
    {
        var now = DateTime.Now;
        _dataManager.AddGpsPoint(NewGpsPoint(now.AddMinutes(-10), 1, 1));
        _dataManager.AddGpsPoint(NewGpsPoint(now, 2, 2));

        var recent = _dataManager.GetGpsHistory(TimeSpan.FromMinutes(1));

        Assert.Single(recent);
        Assert.Equal(2, recent[0].Latitude);
    }

    [Fact]
    public void GetGpsHistory_AbsoluteRange_ReturnsOnlyPointsWithinRange()
    {
        var t0 = DateTime.Now;
        _dataManager.AddGpsPoint(NewGpsPoint(t0, 1, 1));
        _dataManager.AddGpsPoint(NewGpsPoint(t0.AddSeconds(30), 2, 2));
        _dataManager.AddGpsPoint(NewGpsPoint(t0.AddMinutes(5), 3, 3));

        var inRange = _dataManager.GetGpsHistory(t0, t0.AddMinutes(1));

        Assert.Equal(2, inRange.Count);
        Assert.DoesNotContain(inRange, p => p.Latitude == 3);
    }
}
