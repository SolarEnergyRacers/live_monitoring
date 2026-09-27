namespace SERLiveMonitoring.Seeder;

// Reads/writes the same "SRTS" per-second bin format as Services/PersistenceService.cs, and
// replicates Services/DataManager.cs's TimeSeries.AddAndInterpolate fill rule so seeded files stay
// byte-compatible with what the live app produces.
public static class SeriesBuilder
{
    private static readonly byte[] Magic = "SRTS"u8.ToArray();
    private const byte FormatVersion = 1;
    private const double FillValue = 0.0;

    public static Dictionary<long, double> ReadExisting(string path)
    {
        var result = new Dictionary<long, double>();
        if (!File.Exists(path))
            return result;

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic) || reader.ReadByte() != FormatVersion)
            return result; // unrecognized/corrupt file - treat as empty rather than fail the seed run

        var startTimestamp = reader.ReadInt64();
        long index = 0;
        while (stream.Position < stream.Length)
        {
            result[startTimestamp + index] = reader.ReadDouble();
            index++;
        }

        return result;
    }

    // Runs the same forward-fill rule as TimeSeries.AddAndInterpolate over a merged/sorted point
    // set: gaps of up to 5s are filled with the next value, larger gaps are filled with zero.
    public static (long StartTimestamp, List<double> Values) Build(IEnumerable<(long Timestamp, double Value)> points)
    {
        var ordered = points.OrderBy(p => p.Timestamp).ToList();
        var values = new List<double>();

        if (ordered.Count == 0)
            return (0, values);

        var startTimestamp = ordered[0].Timestamp;
        var lastTimestamp = startTimestamp;
        values.Add(ordered[0].Value);

        for (var i = 1; i < ordered.Count; i++)
        {
            var (timestamp, value) = ordered[i];
            var delta = timestamp - lastTimestamp;
            var fill = delta <= 5 ? value : FillValue;
            for (var s = 0L; s < delta; s++)
                values.Add(fill);

            lastTimestamp = timestamp;
        }

        return (startTimestamp, values);
    }

    public static void Write(string path, long startTimestamp, IReadOnlyList<double> values)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(startTimestamp);
        foreach (var value in values)
            writer.Write(value);
    }
}
