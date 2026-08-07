using System.Globalization;
using Microsoft.Data.Sqlite;
using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

/// <summary>
/// Persists user-marked event timestamps (e.g. "driver change", "charge stop") to a SQLite database
/// so they survive a restart, and are loaded automatically. The DB path is resolved once at startup
/// from AppSettings.DataDirectory - changing that setting later takes effect after a restart, same
/// as it would for a database file rather than the appendable timeseries logs.
/// </summary>
public class EventTimestampService : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SqliteConnection _connection;

    public event Action? EventsChanged;

    public EventTimestampService(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS EventTimestamps (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Timestamp TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public List<EventTimestamp> GetAll()
    {
        lock (_lock)
        {
            var result = new List<EventTimestamp>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, Timestamp FROM EventTimestamps ORDER BY Timestamp DESC";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new EventTimestamp
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Timestamp = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                });
            }

            return result;
        }
    }

    public EventTimestamp Add(string name, DateTime timestamp)
    {
        EventTimestamp added;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO EventTimestamps (Name, Timestamp) VALUES ($name, $timestamp);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$timestamp", timestamp.ToString("o", CultureInfo.InvariantCulture));

            var id = (long)cmd.ExecuteScalar()!;
            added = new EventTimestamp { Id = id, Name = name, Timestamp = timestamp };
        }

        EventsChanged?.Invoke();
        return added;
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM EventTimestamps WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        EventsChanged?.Invoke();
    }

    public void Dispose() => _connection.Dispose();
}
