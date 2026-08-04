using SER_Live_Monitoring.Models;
using SER_Live_Monitoring.Services;

namespace SER_Live_Monitoring.Tests;

public class VehicleWarningsTests
{
    private readonly DataManager _dataManager = new(new SerialPortMonitorService(new CANFrameDecoder()));

    private static Reading NewReading(string name, double value, params (string Key, string Value)[] tags)
        => new() { Timestamp = DateTime.Now, ReadingName = name, Value = value, Unit = "", Tags = tags.ToDictionary(t => t.Key, t => t.Value) };

    private static Reading NewReadingAt(DateTime timestamp, string name, double value, params (string Key, string Value)[] tags)
        => new() { Timestamp = timestamp, ReadingName = name, Value = value, Unit = "", Tags = tags.ToDictionary(t => t.Key, t => t.Value) };

    [Fact]
    public void Evaluate_NoData_ReturnsNoWarnings()
    {
        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Evaluate_BatteryFaultFlagSet_ReturnsErrorWarning()
    {
        _dataManager.Ingest([NewReading("pack_isolation_fail", 1)]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        var warning = Assert.Single(warnings);
        Assert.Equal(WarningLevel.Error, warning.Level);
        Assert.Contains("isolation", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_BatteryFaultFlagClear_ReturnsNoWarning()
    {
        _dataManager.Ingest([NewReading("pack_isolation_fail", 0)]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(3.500, 3.500, false)] // no spread
    [InlineData(3.500, 3.750, false)] // 0.25V spread, under threshold
    [InlineData(3.500, 3.900, true)]  // 0.4V spread, over threshold
    public void Evaluate_CellVoltageSpread_WarnsOnlyAboveThreshold(double min, double max, bool expectWarning)
    {
        _dataManager.Ingest([NewReading("min_voltage", min), NewReading("max_voltage", max)]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        Assert.Equal(expectWarning, warnings.Any(w => w.Message.Contains("spread", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Evaluate_HighCellTemperature_ReturnsErrorWarning()
    {
        _dataManager.Ingest([NewReading("max_temp", 65, ("cmu_num", "1"))]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        var warning = Assert.Single(warnings);
        Assert.Equal(WarningLevel.Error, warning.Level);
        Assert.Contains("temperature", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NormalCellTemperature_ReturnsNoWarning()
    {
        _dataManager.Ingest([NewReading("max_temp", 35, ("cmu_num", "1"))]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Evaluate_MpptHardwareFault_ReturnsWarningTaggedWithMpptNumber()
    {
        _dataManager.Ingest([NewReading("mppt_hw_over_curr", 1, ("mppt_id", "2"))]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        var warning = Assert.Single(warnings);
        Assert.Contains("MPPT 2", warning.Message);
    }

    [Fact]
    public void Evaluate_OneMpptFarBelowActiveOthers_ReturnsUnderperformanceWarning()
    {
        _dataManager.UpdateTimeseries(
        [
            NewReading("calc_mppt_out_power", 100, ("mppt_id", "1")),
            NewReading("calc_mppt_out_power", 100, ("mppt_id", "2")),
            NewReading("calc_mppt_out_power", 100, ("mppt_id", "3")),
            NewReading("calc_mppt_out_power", 5, ("mppt_id", "4")),
        ]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        Assert.Contains(warnings, w => w.Message.Contains("MPPT 4"));
        Assert.DoesNotContain(warnings, w => w.Message.Contains("MPPT 1"));
    }

    [Fact]
    public void Evaluate_AllMpptsLow_DoesNotFlagUnderperformance()
    {
        // No sun / parked - every panel producing near nothing is normal, not a fault, since there's
        // no evidence the array is generally productive right now.
        _dataManager.UpdateTimeseries(
        [
            NewReading("calc_mppt_out_power", 1, ("mppt_id", "1")),
            NewReading("calc_mppt_out_power", 2, ("mppt_id", "2")),
            NewReading("calc_mppt_out_power", 0, ("mppt_id", "3")),
            NewReading("calc_mppt_out_power", 1, ("mppt_id", "4")),
        ]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Evaluate_StaleDeviceHeartbeatWhileConnected_ReturnsCommunicationWarning()
    {
        _dataManager.Ingest([NewReadingAt(DateTime.Now.AddSeconds(-10), "device_heartbeat", 1, ("device", "Bms"))]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: true);

        var warning = Assert.Single(warnings);
        Assert.Contains("BMS", warning.Message);
        Assert.Contains("not responding", warning.Message);
    }

    [Fact]
    public void Evaluate_StaleDeviceHeartbeatWhileDisconnected_ReturnsNoWarning()
    {
        // Every device would trivially look "not responding" while disconnected - that's already
        // covered by the connection status chip, so the check is skipped entirely.
        _dataManager.Ingest([NewReadingAt(DateTime.Now.AddSeconds(-10), "device_heartbeat", 1, ("device", "Bms"))]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: false);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Evaluate_RecentDeviceHeartbeatWhileConnected_ReturnsNoWarning()
    {
        _dataManager.Ingest([NewReading("device_heartbeat", 1, ("device", "Bms"))]);

        var warnings = VehicleWarnings.Evaluate(_dataManager, isConnected: true);

        Assert.Empty(warnings);
    }
}
