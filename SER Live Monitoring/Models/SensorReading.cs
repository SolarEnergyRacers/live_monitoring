namespace SER_Live_Monitoring.Models;

public class SensorReading
{
    public DateTime Timestamp { get; set; }
    public string RawLine { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
}
