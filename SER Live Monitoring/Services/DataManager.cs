using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

/// <summary>
/// Buffers the most recently decoded reading for every (name, tag-set) combination so the UI can
/// pull current state (e.g. the battery dashboard) without replaying history. Subscribes to
/// SerialPortMonitorService.ReadingReceived and is the single source of truth for the UI.
/// </summary>
public class DataManager : IDisposable
{
    private readonly SerialPortMonitorService _serialService;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Reading> _latestByKey = new();

    public event Action<List<Reading>>? ReadingsAdded;

    public DataManager(SerialPortMonitorService serialService)
    {
        _serialService = serialService;
        _serialService.ReadingReceived += OnReadingReceived;
    }

    private void OnReadingReceived(List<Reading> readings)
    {
        lock (_lock)
        {
            foreach (var reading in readings)
                _latestByKey[BuildKey(reading.ReadingName, reading.Tags)] = reading;
        }

        ReadingsAdded?.Invoke(readings);
    }


    public Reading? GetLatest(string readingName, params (string Key, string Value)[] tags)
    {
        lock (_lock)
        {
            return _latestByKey.GetValueOrDefault(BuildKey(readingName, tags));
        }
    }

    public Reading? GetLatestSingle(string readingName)
    {
        lock (_lock)
        {
            Reading? newest = null;
            foreach (var reading in _latestByKey.Values)
            {
                if (reading.ReadingName == readingName && (newest is null || reading.Timestamp > newest.Timestamp))
                    newest = reading;
            }
            return newest;
        }
    }

    private static string BuildKey(string readingName, IEnumerable<KeyValuePair<string, string>> tags)
        => BuildKey(readingName, tags.Select(t => (t.Key, t.Value)));

    private static string BuildKey(string readingName, IEnumerable<(string Key, string Value)> tags)
        => $"{readingName}|{string.Join(",", tags.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"))}";

    public void Dispose()
    {
        _serialService.ReadingReceived -= OnReadingReceived;
    }
}
