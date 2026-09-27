using SERLiveMonitoring.Models;
using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void Current_NoFileOnDisk_ReturnsDefaults()
    {
        var service = new SettingsService(TestSettingsPath.NewTempPath());

        Assert.Equal(0x6A0, service.Current.CanAddresses.Mppt1Addr);
        Assert.Equal(60, service.Current.WarningThresholds.MaxCellTempC);
        Assert.Equal(AppSettings.DefaultTileAverageSeconds, service.Current.TileAverageSeconds);
        Assert.Equal(AppSettings.DefaultGoogleMapsSourcePointCount, service.Current.GoogleMapsSourcePointCount);
        Assert.False(service.Current.NoMcCanData);
    }

    [Fact]
    public void Update_PersistsToDiskAndIsPickedUpByNewInstance()
    {
        var path = TestSettingsPath.NewTempPath();
        var first = new SettingsService(path);

        var updated = new AppSettings();
        updated.CanAddresses.Mppt1Addr = 0x111;
        updated.WarningThresholds.MaxCellTempC = 45;
        updated.TileAverageSeconds = 30;
        updated.GoogleMapsSourcePointCount = 7200;
        updated.NoMcCanData = true;
        first.Update(updated);

        // Simulates the app restarting and loading whatever was last saved.
        var second = new SettingsService(path);

        Assert.Equal(0x111, second.Current.CanAddresses.Mppt1Addr);
        Assert.Equal(45, second.Current.WarningThresholds.MaxCellTempC);
        Assert.Equal(30, second.Current.TileAverageSeconds);
        Assert.Equal(7200, second.Current.GoogleMapsSourcePointCount);
        Assert.True(second.Current.NoMcCanData);
    }

    [Fact]
    public void Current_ChartRanges_DefaultsMatchExpectedAbsoluteAndOptimalBounds()
    {
        var service = new SettingsService(TestSettingsPath.NewTempPath());

        Assert.Equal(0, service.Current.ChartRanges.Speed.Min);
        Assert.Equal(110, service.Current.ChartRanges.Speed.Max);
        Assert.Equal(0, service.Current.ChartRanges.Speed.OptimalMin);
        Assert.Equal(90, service.Current.ChartRanges.Speed.OptimalMax);

        Assert.Equal(-1000, service.Current.ChartRanges.MotorPower.Min);
        Assert.Equal(3000, service.Current.ChartRanges.MotorPower.Max);
        Assert.Equal(-1500, service.Current.ChartRanges.MotorPower.OptimalMin);
        Assert.Equal(3000, service.Current.ChartRanges.MotorPower.OptimalMax);
    }

    [Fact]
    public void Update_RaisesSettingsChanged()
    {
        var service = new SettingsService(TestSettingsPath.NewTempPath());
        var raised = false;
        service.SettingsChanged += () => raised = true;

        service.Update(new AppSettings());

        Assert.True(raised);
    }

    [Fact]
    public void Constructor_CorruptFile_FallsBackToDefaults()
    {
        var path = TestSettingsPath.NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not valid json ");

        var service = new SettingsService(path);

        Assert.Equal(0x6A0, service.Current.CanAddresses.Mppt1Addr);
    }
}
