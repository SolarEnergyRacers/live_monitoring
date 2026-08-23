using System.Globalization;
using Microsoft.Data.Sqlite;
using SERLiveMonitoring.Models;

namespace SERLiveMonitoring.Services;

/// <summary>
/// Persists GPS fixes reported by an external device (e.g. a phone) to SQLite, and keeps
/// DataManager's in-memory GPS history in sync - on construction by restoring everything already
/// on disk, and on every subsequent Add by pushing the new point straight into DataManager. Mirrors
/// EventTimestampService's persistence shape, but - unlike events - GPS data is meant to be read
/// live through DataManager rather than queried from this service directly.
/// </summary>
public class GpsTrackService : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SqliteConnection _connection;
    private readonly DataManager _dataManager;

    public GpsTrackService(DataManager dataManager, string dbPath)
    {
        _dataManager = dataManager;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS GpsPoints (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Latitude REAL NOT NULL,
                Longitude REAL NOT NULL,
                SpeedKmh REAL NULL,
                AccuracyMeters REAL NULL
            );
            """;
        cmd.ExecuteNonQuery();

        LoadAll();
    }

    // Restores previously-persisted points into DataManager once at startup, ordered oldest-first
    // so DataManager's "latest point is the last in the list" assumption holds from the start.
    private void LoadAll()
    {
        var points = new List<GpsPoint>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Timestamp, Latitude, Longitude, SpeedKmh, AccuracyMeters FROM GpsPoints ORDER BY Timestamp ASC";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            points.Add(new GpsPoint
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Latitude = reader.GetDouble(2),
                Longitude = reader.GetDouble(3),
                SpeedKmh = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                AccuracyMeters = reader.IsDBNull(5) ? null : reader.GetDouble(5)
            });
        }

        _dataManager.RestoreGpsPoints(points);
    }

    public GpsPoint Add(GpsPoint point)
    {
        GpsPoint added;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO GpsPoints (Timestamp, Latitude, Longitude, SpeedKmh, AccuracyMeters)
                VALUES ($timestamp, $latitude, $longitude, $speedKmh, $accuracyMeters);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$timestamp", point.Timestamp.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$latitude", point.Latitude);
            cmd.Parameters.AddWithValue("$longitude", point.Longitude);
            cmd.Parameters.AddWithValue("$speedKmh", (object?)point.SpeedKmh ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$accuracyMeters", (object?)point.AccuracyMeters ?? DBNull.Value);

            var id = (long)cmd.ExecuteScalar()!;
            added = new GpsPoint
            {
                Id = id,
                Timestamp = point.Timestamp,
                Latitude = point.Latitude,
                Longitude = point.Longitude,
                SpeedKmh = point.SpeedKmh,
                AccuracyMeters = point.AccuracyMeters
            };
        }

        _dataManager.AddGpsPoint(added);
        return added;
    }

    public void Dispose() => _connection.Dispose();
}
