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
    public CanAddressSettings CanAddresses { get; set; } = new();
    public WarningThresholds WarningThresholds { get; set; } = new();
}
