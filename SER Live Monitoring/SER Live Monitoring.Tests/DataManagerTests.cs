using SER_Live_Monitoring.Models;
using SER_Live_Monitoring.Services;

namespace SER_Live_Monitoring.Tests;

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
}
