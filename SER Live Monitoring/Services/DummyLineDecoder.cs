using System.Globalization;
using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

/// <summary>
/// Placeholder decoder: expects "value,unit" (e.g. "23.4,C"), falls back to raw text with Value = 0.
/// Replace with the real protocol decoder once it is known.
/// </summary>
public class DummyLineDecoder : IDataDecoder
{
    public SensorReading? Decode(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return null;

        var reading = new SensorReading
        {
            Timestamp = DateTime.Now,
            RawLine = rawLine
        };

        var parts = rawLine.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            reading.Value = value;
            reading.Unit = parts.Length >= 2 ? parts[1] : string.Empty;
        }

        return reading;
    }
}
