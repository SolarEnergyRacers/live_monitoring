using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

/// <summary>
/// Buffers the most recent decoded readings so the UI never talks to the serial port directly.
/// Subscribes to SerialPortMonitorService.ReadingReceived and is the single source of truth for the UI.
/// </summary>


public class TimeSeriesData {
    public List<int> timestamps = new();
    private List<int> timestampDeltas = new();
    public List<double> values = new();

    public List<Reading> readings = new();

    public void add(int timestamp, int value) {
        timestamps.Add(timestamp);
        values.Add(value);
    }
}


public class DataManager : IDisposable
{
    private readonly SerialPortMonitorService _serialService;
    private readonly Lock _lock = new();

    public event Action<List<Reading>>? ReadingsAdded;

    public List<Reading> Readings = new();

    public DataManager(SerialPortMonitorService serialService)
    {
        _serialService = serialService;
        _serialService.ReadingReceived += OnReadingReceived;
    }

    private void OnReadingReceived(List<Reading> readings)
    {
        lock (_lock)
        {
            //readings.AddRange(readings);
        }

        ReadingsAdded?.Invoke(readings);
    }

    public void Dispose()
    {
        _serialService.ReadingReceived -= OnReadingReceived;
    }
}
