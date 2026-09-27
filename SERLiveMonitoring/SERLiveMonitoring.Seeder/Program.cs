namespace SERLiveMonitoring.Seeder;

public static class Program
{
    private const string GpsDbFileName = "gps.db";

    public static int Main(string[] args)
    {
        string? dataDirectory = null;
        string? csvPath = null;
        string? mode = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--mode" && i + 1 < args.Length)
            {
                mode = args[++i].ToLowerInvariant();
            }
            else if (dataDirectory == null)
            {
                dataDirectory = args[i];
            }
            else if (csvPath == null)
            {
                csvPath = args[i];
            }
        }

        if (dataDirectory == null || csvPath == null)
        {
            Console.Error.WriteLine("Usage: SERLiveMonitoring.Seeder <datastoreDir> <csvPath> [--mode overwrite|merge]");
            return 1;
        }

        if (mode != null && mode != "overwrite" && mode != "merge")
        {
            Console.Error.WriteLine($"Invalid --mode '{mode}'; expected 'overwrite' or 'merge'.");
            return 1;
        }

        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"CSV file not found: {csvPath}");
            return 1;
        }

        dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var gpsDbPath = Path.Combine(dataDirectory, GpsDbFileName);
        var datastoreExists = Directory.EnumerateFiles(dataDirectory, "*.bin").Any() || File.Exists(gpsDbPath);

        if (datastoreExists && mode == null)
            mode = PromptForMode();
        else if (!datastoreExists)
            mode = "o"; // overwrite

        List<CsvSeedRow> rows;
        try
        {
            rows = CsvSeedReader.Read(csvPath);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Failed to read CSV: {ex.Message}");
            return 1;
        }

        if (rows.Count == 0)
        {
            Console.Error.WriteLine("No usable data rows found in CSV - nothing to seed.");
            return 1;
        }

        var isMerge = mode == "m";

        var seriesWritten = 0;
        foreach (var seriesName in CsvSeedReader.KnownSeriesColumns)
        {
            var path = Path.Combine(dataDirectory, $"{seriesName}.bin");
            var csvPoints = rows
                .Where(r => r.SeriesValues.ContainsKey(seriesName))
                .Select(r => (Timestamp: new DateTimeOffset(r.Timestamp).ToUnixTimeSeconds(), Value: r.SeriesValues[seriesName]))
                .ToList();

            if (csvPoints.Count == 0 && isMerge && File.Exists(path))
                continue; // merge mode only touches series the CSV actually carries

            IEnumerable<(long Timestamp, double Value)> points;
            if (csvPoints.Count == 0)
            {
                // Column absent from CSV entirely: zero-fill across the CSV's covered time span.
                var minTs = rows.Min(r => new DateTimeOffset(r.Timestamp).ToUnixTimeSeconds());
                var maxTs = rows.Max(r => new DateTimeOffset(r.Timestamp).ToUnixTimeSeconds());
                points = new[] { (minTs, 0.0), (maxTs, 0.0) };
            }
            else if (isMerge)
            {
                var merged = SeriesBuilder.ReadExisting(path);
                foreach (var (timestamp, value) in csvPoints)
                    merged[timestamp] = value;
                points = merged.Select(kvp => (kvp.Key, kvp.Value));
            }
            else
            {
                points = csvPoints;
            }

            var (startTimestamp, values) = SeriesBuilder.Build(points);
            SeriesBuilder.Write(path, startTimestamp, values);
            seriesWritten++;
        }

        if (isMerge)
            GpsSeedWriter.Merge(gpsDbPath, rows);
        else
            GpsSeedWriter.Overwrite(gpsDbPath, rows);

        Console.WriteLine($"Seeded {rows.Count} CSV rows into '{dataDirectory}' ({mode}): {seriesWritten} series file(s) written, gps.db updated.");
        return 0;
    }

    private static string PromptForMode()
    {
        while (true)
        {
            Console.Error.Write("A datastore already exists in this directory. Choose '[o]verwrite' or '[m]erge': ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is "o" or "m")
                return answer;

            Console.Error.WriteLine("Please answer 'overwrite' or 'merge'.");
        }
    }
}
