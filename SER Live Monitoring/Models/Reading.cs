namespace SER_Live_Monitoring.Models;

public class Reading
{
    public DateTime Timestamp { get; set; }
    public string ReadingName { get; set; }
    public string Unit {  get; set; }
    public double Value { get; set; }
    public Dictionary<string, string> Tags { get; set; }
}
