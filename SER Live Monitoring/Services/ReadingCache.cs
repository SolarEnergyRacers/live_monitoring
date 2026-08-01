using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

/// <summary>
/// Buffers the most recent decoded readings so the UI never talks to the serial port directly.
/// Subscribes to SerialPortMonitorService.ReadingReceived and is the single source of truth for the UI.
/// </summary>
public class ReadingCache : IReadingCache, IDisposable
{
    private const int DefaultCapacity = 200;

    private readonly SerialPortMonitorService _serialService;
    private readonly int _capacity;
    private readonly LinkedList<Reading> _history = new();
    private readonly Lock _lock = new();

    public event Action<Reading>? ReadingAdded;

    public Reading? Latest { get; private set; }

    public ReadingCache(SerialPortMonitorService serialService, int capacity = DefaultCapacity)
    {
        _serialService = serialService;
        _capacity = capacity;
        _serialService.ReadingReceived += OnReadingReceived;
    }

    public IReadOnlyList<Reading> GetHistory()
    {
        lock (_lock)
        {
            return [.. _history];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _history.Clear();
            Latest = null;
        }
    }

    private void OnReadingReceived(List<Reading> readings)
    {
        /*lock (_lock)
        {
            Latest = reading;
            _history.AddFirst(reading);
            while (_history.Count > _capacity)
                _history.RemoveLast();
        }

        ReadingAdded?.Invoke(reading);*/
    }

    public void Dispose()
    {
        _serialService.ReadingReceived -= OnReadingReceived;
    }
}
