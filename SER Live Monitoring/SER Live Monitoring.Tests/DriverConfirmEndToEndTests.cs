using SER_Live_Monitoring.Models;
using SER_Live_Monitoring.Services;

namespace SER_Live_Monitoring.Tests;

// Exercises the real CANFrameDecoder output (not the synthetic NewReading helper other
// DataManagerTests use) to isolate whether driver_confirm handling breaks specifically when
// driven by actual decoded frames.
public class DriverConfirmEndToEndTests
{
    [Fact]
    public void RealDecodedDcFrame_WithConfirmBitSet_ConfirmsLatestMessage()
    {
        var decoder = new CANFrameDecoder(new SettingsService(TestSettingsPath.NewTempPath()));
        var dataManager = new DataManager(new SerialPortMonitorService(decoder));

        var message = dataManager.AddDriverMessage(DriverMessageSeverity.Info, "test message");

        // DC speed frame (addr 0x661), matching data/simulate_solar_car.py's exact byte layout:
        // struct.pack("<HHbBBB", targetspeed_raw, target_power, accel_display, speed, dc_drive, flags)
        // with flags = (1<<0 drive_direction) | (1<<4 driver_confirm).
        byte[] rawLine = [0xFE, 0x61, 0xF4, 0x01, 0x64, 0x00, 0x00, 0x2A, 0x01, 0x11];

        var readings = decoder.Decode(rawLine);

        Assert.Contains(readings, r => r.ReadingName == "driver_confirm" && r.Value == 1);

        dataManager.Ingest(readings);

        var reloaded = dataManager.GetDriverMessages().Single(m => m.Id == message.Id);
        Assert.NotNull(reloaded.ConfirmedAt);
    }
}
