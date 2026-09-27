using SERLiveMonitoring.Services;

namespace SERLiveMonitoring.Models;

public class CanAddressSettings
{
    public short Mppt1Addr { get; set; } = 0x6A0;
    public short Mppt2Addr { get; set; } = 0x6B0;
    public short Mppt3Addr { get; set; } = 0x6C0;
    public short Mppt4Addr { get; set; } = 0x6D0;
    public short BmsBaseAddr { get; set; } = 0x700;
    public short AcBaseAddr { get; set; } = 0x630;
    public short DcBaseAddr { get; set; } = 0x660;
    public short McBaseAddr { get; set; } = 0x500;
}

public class WarningThresholds
{
    public double MaxCellVoltageSpreadV { get; set; } = 0.3;
    public double MaxCellTempC { get; set; } = 60;
    public double MaxMotorTempC { get; set; } = 90;
    public double MaxMpptTempC { get; set; } = 85;
    public double MinActiveMpptPowerW { get; set; } = 20;
    public double LowMpptPowerRatio { get; set; } = 0.3;
    public double CommTimeoutSeconds { get; set; } = 5;
}

public class ChartRangeDefinition
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double OptimalMin { get; set; }
    public double OptimalMax { get; set; }
}

public class ChartRangeSettings
{
    public ChartRangeDefinition Speed { get; set; } = new() { Min = 0, Max = 110, OptimalMin = 0, OptimalMax = 90 };
    public ChartRangeDefinition SolarPower { get; set; } = new() { Min = 0, Max = 1500, OptimalMin = 200, OptimalMax = 500 };
    public ChartRangeDefinition MotorPower { get; set; } = new() { Min = -1000, Max = 3000, OptimalMin = -1500, OptimalMax = 3000 };
    public ChartRangeDefinition BatteryPower { get; set; } = new() { Min = -1000, Max = 3000, OptimalMin = 0, OptimalMax = 3000 };
    public ChartRangeDefinition BatteryVoltage { get; set; } = new() { Min = 80, Max = 133, OptimalMin = 94, OptimalMax = 94 };
    public ChartRangeDefinition BatteryCurrent { get; set; } = new() { Min = -15, Max = 15, OptimalMin = -5, OptimalMax = 15 };
    public ChartRangeDefinition Mppt { get; set; } = new() { Min = 0, Max = 380, OptimalMin = 100, OptimalMax = 380 };

    public ChartRangeDefinition Get(ChartSeries series) => series switch
    {
        ChartSeries.Speed => Speed,
        ChartSeries.BatteryVoltage => BatteryVoltage,
        ChartSeries.BatteryCurrent => BatteryCurrent,
        ChartSeries.BatteryPower => BatteryPower,
        ChartSeries.MotorPower => MotorPower,
        ChartSeries.SolarTotal => SolarPower,
        ChartSeries.Mppt1 or ChartSeries.Mppt2 or ChartSeries.Mppt3 or ChartSeries.Mppt4 => Mppt,
        _ => Speed,
    };
}

public class AppSettings
{
    public const int MinTileAverageSeconds = 1;
    public const int MaxTileAverageSeconds = 3600;
    public const int DefaultTileAverageSeconds = 15;
    public const int MinGoogleMapsSourcePointCount = 2;
    public const int MaxGoogleMapsSourcePointCount = 1_000_000;
    public const int DefaultGoogleMapsSourcePointCount = 3600;

    public CanAddressSettings CanAddresses { get; set; } = new();
    public WarningThresholds WarningThresholds { get; set; } = new();
    public ChartRangeSettings ChartRanges { get; set; } = new();
    public int TileAverageSeconds { get; set; } = DefaultTileAverageSeconds;
    public int GoogleMapsSourcePointCount { get; set; } = DefaultGoogleMapsSourcePointCount;

    // Remembers the Analytics page's series dropdown selection between visits/restarts.
    public List<ChartSeries> LastAnalyticsSeries { get; set; } = [ChartSeries.Speed];

    // One of the names in Services.ThemeCatalog.Names.
    public string Theme { get; set; } = "Dark";

    // When the motor controller board isn't wired up (or its model doesn't put current/power on the
    // CAN bus at all), DataManager derives motor current/power from the battery/MPPT current balance
    // instead of mc_curr_in/mc_volt_in - see DataManager.UpdateDerivedMotorPower.
    public bool NoMcCanData { get; set; } = false;
}
