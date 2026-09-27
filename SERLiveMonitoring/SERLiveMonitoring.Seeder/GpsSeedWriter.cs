using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SERLiveMonitoring.Seeder;

// Mirrors Services/GpsTrackService.cs's GpsPoints schema so seeded gps.db files load unmodified.
public static class GpsSeedWriter
{
    public static void Overwrite(string dbPath, IReadOnlyList<CsvSeedRow> rows)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        using var connection = OpenAndEnsureSchema(dbPath);
        InsertRows(connection, rows);
    }

    public static void Merge(string dbPath, IReadOnlyList<CsvSeedRow> rows)
    {
        using var connection = OpenAndEnsureSchema(dbPath);

        // Comparing floored-second overlap is done in C# (not SQL strftime) because
        // Timestamp is stored via DateTime.ToString("o") without an offset, and SQLite's
        // date functions parse naive strings as UTC while DateTimeOffset(Unspecified) here
        // uses the local offset - the two would disagree on non-UTC machines.
        var csvFloorSeconds = rows.Select(r => new DateTimeOffset(r.Timestamp).ToUnixTimeSeconds()).ToHashSet();

        var idsToDelete = new List<long>();
        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT Id, Timestamp FROM GpsPoints;";
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                var timestamp = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var floorSecond = new DateTimeOffset(timestamp).ToUnixTimeSeconds();
                if (csvFloorSeconds.Contains(floorSecond))
                    idsToDelete.Add(reader.GetInt64(0));
            }
        }

        using (var transaction = connection.BeginTransaction())
        {
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM GpsPoints WHERE Id = $id;";
            var idParam = deleteCmd.CreateParameter();
            idParam.ParameterName = "$id";
            deleteCmd.Parameters.Add(idParam);

            foreach (var id in idsToDelete)
            {
                idParam.Value = id;
                deleteCmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        InsertRows(connection, rows);
    }

    private static SqliteConnection OpenAndEnsureSchema(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS GpsPoints (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Latitude REAL NOT NULL,
                Longitude REAL NOT NULL,
                SpeedKmh REAL NULL,
                AccuracyMeters REAL NULL,
                DeviceName TEXT NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();

        return connection;
    }

    private static void InsertRows(SqliteConnection connection, IReadOnlyList<CsvSeedRow> rows)
    {
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO GpsPoints (Timestamp, Latitude, Longitude, SpeedKmh, AccuracyMeters, DeviceName)
            VALUES ($timestamp, $latitude, $longitude, $speedKmh, $accuracyMeters, $deviceName);
            """;

        var timestampParam = cmd.CreateParameter();
        timestampParam.ParameterName = "$timestamp";
        cmd.Parameters.Add(timestampParam);
        var latitudeParam = cmd.CreateParameter();
        latitudeParam.ParameterName = "$latitude";
        cmd.Parameters.Add(latitudeParam);
        var longitudeParam = cmd.CreateParameter();
        longitudeParam.ParameterName = "$longitude";
        cmd.Parameters.Add(longitudeParam);
        var speedParam = cmd.CreateParameter();
        speedParam.ParameterName = "$speedKmh";
        cmd.Parameters.Add(speedParam);
        var accuracyParam = cmd.CreateParameter();
        accuracyParam.ParameterName = "$accuracyMeters";
        accuracyParam.Value = DBNull.Value;
        cmd.Parameters.Add(accuracyParam);
        var deviceNameParam = cmd.CreateParameter();
        deviceNameParam.ParameterName = "$deviceName";
        deviceNameParam.Value = "csv-seed";
        cmd.Parameters.Add(deviceNameParam);

        foreach (var row in rows)
        {
            timestampParam.Value = row.Timestamp.ToString("o", CultureInfo.InvariantCulture);
            latitudeParam.Value = row.Latitude;
            longitudeParam.Value = row.Longitude;
            speedParam.Value = row.SeriesValues.TryGetValue("speed", out var speed) ? speed : DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
