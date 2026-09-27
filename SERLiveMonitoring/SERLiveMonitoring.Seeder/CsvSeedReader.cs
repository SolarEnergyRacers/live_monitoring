using System.Globalization;

namespace SERLiveMonitoring.Seeder;

// One CSV data row: datetimestamp/longitude/latitude are always present, SeriesValues only
// contains the bin series columns that were actually in the CSV header.
public record CsvSeedRow(DateTime Timestamp, double Longitude, double Latitude, IReadOnlyDictionary<string, double> SeriesValues);

public static class CsvSeedReader
{
    // Bin series columns the CSV may carry; "speed" doubles as both a bin series and the GPS fix's SpeedKmh.
    public static readonly string[] KnownSeriesColumns =
    [
        "speed", "battery_current", "battery_voltage", "battery_power",
        "mppt1_power", "mppt2_power", "mppt3_power", "mppt4_power",
        "motor_current", "motor_voltage", "motor_power"
    ];

    private static readonly string[] RequiredColumns = ["datetimestamp", "longitude", "latitude"];

    public static List<CsvSeedRow> Read(string path)
    {
        using var reader = new StreamReader(path);

        string[]? header = null;
        var seriesColumnIndex = new Dictionary<string, int>();
        int datetimestampIndex = -1, longitudeIndex = -1, latitudeIndex = -1;
        var rows = new List<CsvSeedRow>();

        int lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            if (header == null)
            {
                header = trimmed.Split(',').Select(c => c.Trim().ToLowerInvariant()).ToArray();

                foreach (var required in RequiredColumns)
                {
                    if (!header.Contains(required))
                        throw new InvalidDataException($"CSV header is missing required column '{required}'.");
                }

                datetimestampIndex = Array.IndexOf(header, "datetimestamp");
                longitudeIndex = Array.IndexOf(header, "longitude");
                latitudeIndex = Array.IndexOf(header, "latitude");

                foreach (var column in KnownSeriesColumns)
                {
                    var index = Array.IndexOf(header, column);
                    if (index >= 0)
                        seriesColumnIndex[column] = index;
                }

                continue;
            }

            var fields = trimmed.Split(',');
            if (fields.Length != header.Length)
            {
                Console.Error.WriteLine($"line {lineNumber}: expected {header.Length} columns, got {fields.Length} - skipped.");
                continue;
            }

            try
            {
                var timestamp = DateTime.Parse(fields[datetimestampIndex].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None);
                var longitude = double.Parse(fields[longitudeIndex].Trim(), CultureInfo.InvariantCulture);
                var latitude = double.Parse(fields[latitudeIndex].Trim(), CultureInfo.InvariantCulture);

                var seriesValues = new Dictionary<string, double>();
                foreach (var (column, index) in seriesColumnIndex)
                    seriesValues[column] = double.Parse(fields[index].Trim(), CultureInfo.InvariantCulture);

                rows.Add(new CsvSeedRow(timestamp, longitude, latitude, seriesValues));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                Console.Error.WriteLine($"line {lineNumber}: {ex.Message} - skipped.");
            }
        }

        if (header == null)
            throw new InvalidDataException("CSV file has no header row.");

        rows.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return rows;
    }
}
