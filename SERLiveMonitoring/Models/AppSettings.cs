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

public class AppSettings
{
    public const int MinGoogleMapsSourcePointCount = 2;
    public const int MaxGoogleMapsSourcePointCount = 1_000_000;
    public const int DefaultGoogleMapsSourcePointCount = 3600;

    public CanAddressSettings CanAddresses { get; set; } = new();
    public WarningThresholds WarningThresholds { get; set; } = new();
    public int GoogleMapsSourcePointCount { get; set; } = DefaultGoogleMapsSourcePointCount;

    // One of the names in Services.ThemeCatalog.Names.
    public string Theme { get; set; } = "Dark";

    // When the motor controller board isn't wired up (or its model doesn't put current/power on the
    // CAN bus at all), DataManager derives motor current/power from the battery/MPPT current balance
    // instead of mc_curr_in/mc_volt_in - see DataManager.UpdateDerivedMotorPower.
    public bool NoMcCanData { get; set; } = false;
}
