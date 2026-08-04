using SER_Live_Monitoring.Models;
using SER_Live_Monitoring.Services;

namespace SER_Live_Monitoring.Tests;

public class CANFrameDecoderTests
{
    private readonly CANFrameDecoder _decoder = new(new SettingsService(TestSettingsPath.NewTempPath()));

    private static byte[] BuildFrame(short addr, byte[]? data = null)
    {
        data ??= new byte[8];
        var frame = new byte[2 + data.Length];
        frame[0] = (byte)(addr >> 8);
        frame[1] = (byte)addr;
        data.CopyTo(frame, 2);
        return frame;
    }

    // Mppt1Addr = 0x600, Mppt2Addr = 0x610, Mppt3Addr = 0x620, mask 0xFF0 => each matches its own 16-address block
    [Theory]
    [InlineData(0x600)]
    [InlineData(0x605)]
    [InlineData(0x60F)]
    [InlineData(0x610)]
    [InlineData(0x615)]
    [InlineData(0x61F)]
    [InlineData(0x620)]
    [InlineData(0x625)]
    [InlineData(0x62F)]
    public void IsMpptFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsMpptFrame(addr));
    }

    [Theory]
    [InlineData(0x5FF)]
    [InlineData(0x630)]
    [InlineData(0x700)]
    [InlineData(0)]
    public void IsMpptFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsMpptFrame(addr));
    }

    // BmsBaseAddr = 0x700, mask 0xF00 => matches 0x700-0x7FF
    [Theory]
    [InlineData(0x700)]
    [InlineData(0x750)]
    [InlineData(0x7FF)]
    public void IsBmsFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsBmsFrame(addr));
    }

    [Theory]
    [InlineData(0x6FF)]
    [InlineData(0x800)]
    [InlineData(0x600)]
    [InlineData(0)]
    public void IsBmsFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsBmsFrame(addr));
    }

    // AcBaseAddr = 0x630, mask 0xFF0 => matches 0x630-0x63F
    [Theory]
    [InlineData(0x630)]
    [InlineData(0x635)]
    [InlineData(0x63F)]
    public void IsAcFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsAcFrame(addr));
    }

    [Theory]
    [InlineData(0x62F)]
    [InlineData(0x640)]
    [InlineData(0x660)]
    [InlineData(0)]
    public void IsAcFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsAcFrame(addr));
    }

    // DcBaseAddr = 0x660, mask 0xFF0 => matches 0x660-0x66F
    [Theory]
    [InlineData(0x660)]
    [InlineData(0x665)]
    [InlineData(0x66F)]
    public void IsDcFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsDcFrame(addr));
    }

    [Theory]
    [InlineData(0x65F)]
    [InlineData(0x670)]
    [InlineData(0x630)]
    [InlineData(0)]
    public void IsDcFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsDcFrame(addr));
    }

    // McBaseAddr = 0x500, mask 0xF00 => matches 0x500-0x5FF
    [Theory]
    [InlineData(0x500)]
    [InlineData(0x580)]
    [InlineData(0x5FF)]
    public void IsMcFrame_AddressInRange_ReturnsTrue(short addr)
    {
        Assert.True(_decoder.IsMcFrame(addr));
    }

    [Theory]
    [InlineData(0x4FF)]
    [InlineData(0x600)]
    [InlineData(0x700)]
    [InlineData(0)]
    public void IsMcFrame_AddressOutOfRange_ReturnsFalse(short addr)
    {
        Assert.False(_decoder.IsMcFrame(addr));
    }

    // Each base address should be recognized only by its own Is*Frame method.
    [Theory]
    [InlineData(0x600)]
    [InlineData(0x700)]
    [InlineData(0x630)]
    [InlineData(0x660)]
    [InlineData(0x500)]
    public void IsFrameMethods_AreMutuallyExclusive(short baseAddr)
    {
        bool[] results =
        [
            _decoder.IsMpptFrame(baseAddr),
            _decoder.IsBmsFrame(baseAddr),
            _decoder.IsAcFrame(baseAddr),
            _decoder.IsDcFrame(baseAddr),
            _decoder.IsMcFrame(baseAddr),
        ];

        Assert.Single(results, r => r);
    }

    [Theory]
    [InlineData(0x700, "Bms")]
    [InlineData(0x6A0, "Mppt1")]
    [InlineData(0x6B0, "Mppt2")]
    [InlineData(0x6C0, "Mppt3")]
    [InlineData(0x6D0, "Mppt4")]
    [InlineData(0x660, "Dc")]
    [InlineData(0x630, "Ac")]
    [InlineData(0x500, "Mc")]
    public void Decode_KnownFrame_TagsDeviceHeartbeat(short addr, string expectedDevice)
    {
        var readings = _decoder.Decode(BuildFrame(addr));

        var heartbeat = Assert.Single(readings, r => r.ReadingName == "device_heartbeat");
        Assert.Equal(expectedDevice, heartbeat.Tags["device"]);
    }

    [Fact]
    public void Decode_FrameWithUnhandledSubAddress_StillTagsDeviceHeartbeat()
    {
        // McBaseAddr's sub-address 0x00 isn't one of the cases DecodeMc understands (0x09/0x0e/0x0f/0x10/0x1b),
        // so it produces no named readings on its own - the heartbeat must still fire so "device alive"
        // tracking doesn't depend on which specific CAN message happens to arrive.
        var readings = _decoder.Decode(BuildFrame(0x500)); // McBaseAddr default

        var heartbeat = Assert.Single(readings);
        Assert.Equal("device_heartbeat", heartbeat.ReadingName);
        Assert.Equal("Mc", heartbeat.Tags["device"]);
    }

    [Fact]
    public void IsMcFrame_AfterSettingsUpdated_UsesNewAddressImmediately()
    {
        // The whole point of settings-backed addresses is that a change on the settings page takes
        // effect without recreating/restarting anything - CANFrameDecoder must re-read
        // SettingsService.Current on every call rather than caching the addresses at construction.
        var settings = new SettingsService(TestSettingsPath.NewTempPath());
        var decoder = new CANFrameDecoder(settings);

        Assert.False(decoder.IsMcFrame(0x123));

        var updated = new AppSettings();
        updated.CanAddresses.McBaseAddr = 0x100;
        settings.Update(updated);

        Assert.True(decoder.IsMcFrame(0x123));
    }

    [Fact]
    public void Decode_UnrecognizedAddress_ReturnsNoReadings()
    {
        var readings = _decoder.Decode(BuildFrame(0x000));

        Assert.Empty(readings);
    }

    [Fact]
    public void Decode_CellVoltageFrame_TagsOnlyCmuAndCellNum()
    {
        // Regression test: Battery.razor's CellVoltage() looks up readings by exactly
        // (cmu_num, cell_num). An extra tag here (like the old unused cell_index) silently breaks
        // every individual cell voltage lookup, since DataManager.GetLatest matches the full tag set -
        // while min/max keep working because they don't match by tags at all.
        var data = new byte[8];
        data[0] = 0xE4; data[1] = 0x0C; // 3300 raw (little-endian int16) -> 3.300V for cell 0

        var readings = _decoder.Decode(BuildFrame(0x700 + 0x02, data)); // CMU0 voltages 1

        var cellVoltage = readings.Single(r => r.ReadingName == "cell_voltage" && r.Tags["cell_num"] == "0");
        Assert.Equal(3.300, cellVoltage.Value, 3);
        Assert.Equal(new Dictionary<string, string> { ["cmu_num"] = "0", ["cell_num"] = "0" }, cellVoltage.Tags);
    }
}
