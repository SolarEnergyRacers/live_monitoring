using SER_Live_Monitoring.Models;
using SER_Live_Monitoring.Services;

namespace SER_Live_Monitoring.Tests;

public class AnalyticsDataServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ser-live-monitoring-tests-{Guid.NewGuid():N}.db");
    private readonly DataManager _dataManager = new(new SerialPortMonitorService(new CANFrameDecoder(new SettingsService(TestSettingsPath.NewTempPath()))));
    private readonly EventTimestampService _eventService;
    private readonly AnalyticsDataService _analytics;

    public AnalyticsDataServiceTests()
    {
        _eventService = new EventTimestampService(_dbPath);
        _analytics = new AnalyticsDataService(_dataManager, _eventService);
    }

    private static Reading NewReading(DateTime ts, string name, double value, params (string Key, string Value)[] tags)
        => new() { Timestamp = ts, ReadingName = name, Value = value, Unit = "", Tags = tags.ToDictionary(t => t.Key, t => t.Value) };

    // Truncated to a whole second so it lines up exactly with TimeSeries' 1Hz bucketing - avoids
    // any ambiguity from sub-second rounding when asserting on summed values below.
    private static DateTime WholeSecondNow()
    {
        var now = DateTime.Now;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
    }

    // Feeds 5 seconds of readings (t0..t0+4s) covering speed, motor power (mixed consumption and
    // regen), solar and battery power, all one second apart so each Ingest call appends exactly one
    // datapoint - keeping the expected sums simple to hand-compute.
    private DateTime SeedFiveSecondsOfData()
    {
        var t0 = WholeSecondNow();
        double[] speed = [10, 20, 30, 40, 50];
        double[] motorPower = [100, -50, 200, -30, 60]; // consumption positive, regen negative
        double[] solarPower = [5, 10, 15, 20, 10];
        double[] batteryPower = [50, -20, 80, -10, 30];

        for (var i = 0; i < 5; i++)
        {
            var ts = t0.AddSeconds(i);
            _dataManager.Ingest(
            [
                NewReading(ts, "speed", speed[i]),
                // motor_power is derived by DataManager from current * voltage; voltage=1 makes
                // the derived power equal the current value directly, so the table above can just
                // list the desired power.
                NewReading(ts, "mc_curr_in", motorPower[i]),
                NewReading(ts, "mc_volt_in", 1),
                NewReading(ts, "calc_mppt_out_power", solarPower[i], ("mppt_id", "1")),
                NewReading(ts, "calc_batt_power", batteryPower[i]),
            ]);
        }

        return t0;
    }

    [Fact]
    public void Summarize_NoData_ReturnsNull()
    {
        var now = DateTime.Now;
        Assert.Null(_analytics.Summarize(now.AddMinutes(-1), now));
    }

    [Fact]
    public void Summarize_EndNotAfterStart_ReturnsNull()
    {
        var now = DateTime.Now;
        Assert.Null(_analytics.Summarize(now, now));
        Assert.Null(_analytics.Summarize(now, now.AddSeconds(-1)));
    }

    [Fact]
    public void Summarize_ComputesDistanceAndEnergyTotals()
    {
        var t0 = SeedFiveSecondsOfData();

        var summary = _analytics.Summarize(t0, t0.AddSeconds(5));

        Assert.NotNull(summary);
        // distanceKm = sum(speed) / 3600 = (10+20+30+40+50) / 3600
        Assert.Equal(150.0 / 3600, summary!.DistanceKm, 6);
        // motorConsumedWh = sum(positive motor power) / 3600 = (100+200+60) / 3600
        Assert.Equal(360.0 / 3600, summary.MotorConsumedWh, 6);
        // motorRegeneratedWh = sum(abs(negative motor power)) / 3600 = (50+30) / 3600
        Assert.Equal(80.0 / 3600, summary.MotorRegeneratedWh, 6);
        // solarGeneratedWh = sum(solar power) / 3600 = (5+10+15+20+10) / 3600
        Assert.Equal(60.0 / 3600, summary.SolarGeneratedWh, 6);
        // batteryDischargedWh = sum(positive battery power) / 3600 = (50+80+30) / 3600
        Assert.Equal(160.0 / 3600, summary.BatteryDischargedWh, 6);
        // deltaWh = solarGeneratedWh - motorConsumedWh
        Assert.Equal(60.0 / 3600 - 360.0 / 3600, summary.DeltaWh, 6);
        // avgEnergyPerKm = (motorConsumedWh - motorRegeneratedWh) / distanceKm; the /3600 on both
        // sides cancels, leaving the raw sums.
        Assert.Equal(280.0 / 150, summary.AvgEnergyPerKm, 6);
        // avgMotorPowerW = mean(100,-50,200,-30,60)
        Assert.Equal(280.0 / 5, summary.AvgMotorPowerW, 6);
    }

    [Fact]
    public void GetSpeedVsMotorPower_PairsSamplesBySharedTimestamp()
    {
        var t0 = SeedFiveSecondsOfData();

        var points = _analytics.GetSpeedVsMotorPower(t0, t0.AddSeconds(5));

        Assert.Equal(5, points.Count);
        Assert.Equal(new ScatterPoint(10, 100), points[0]);
        Assert.Equal(new ScatterPoint(20, -50), points[1]);
        Assert.Equal(new ScatterPoint(30, 200), points[2]);
        Assert.Equal(new ScatterPoint(40, -30), points[3]);
        Assert.Equal(new ScatterPoint(50, 60), points[4]);
    }

    [Fact]
    public void GetEnergySources_ReturnsSolarAndBatterySlices()
    {
        var t0 = SeedFiveSecondsOfData();

        var sources = _analytics.GetEnergySources(t0, t0.AddSeconds(5));

        Assert.Equal(2, sources.Count);
        var solar = sources.Single(s => s.Source == "Solar");
        var battery = sources.Single(s => s.Source == "Battery");
        Assert.Equal(60.0 / 3600, solar.EnergyWh, 6);
        Assert.Equal(160.0 / 3600, battery.EnergyWh, 6);
    }

    [Fact]
    public void GetEnergySources_NoData_ReturnsEmpty()
    {
        var now = DateTime.Now;
        Assert.Empty(_analytics.GetEnergySources(now.AddMinutes(-1), now));
    }

    [Fact]
    public void GetChartSeries_ReturnsTimestampedPoints()
    {
        var t0 = SeedFiveSecondsOfData();

        var points = _analytics.GetChartSeries(ChartSeries.Speed, t0, t0.AddSeconds(5));

        Assert.Equal(5, points.Count);
        Assert.Equal(10, points[0].Value);
        Assert.Equal(50, points[^1].Value);
    }

    public void Dispose()
    {
        _eventService.Dispose();
        _dataManager.Dispose();
    }
}
