using SER_Live_Monitoring.Models;
namespace SER_Live_Monitoring.Services;

/// <summary>
/// Stores 1Hz timeseries data in a consistent format, interpolates automatically for easier calculations.
/// </summary>
/// 
public class TimeSeries {
    public long StartTimestamp;
    public long LastTimestamp;
    public List<double> Datapoints = new();

    public void AddAndInterpolate(Reading r) {
        if (StartTimestamp == 0 && LastTimestamp == 0)
        {
            StartTimestamp = new DateTimeOffset(r.Timestamp).ToUnixTimeSeconds();
            LastTimestamp = StartTimestamp;
            Datapoints.Add(r.Value);
            return;
        }
        
        long newTimestamp = new DateTimeOffset(r.Timestamp).ToUnixTimeSeconds();
        long delta = newTimestamp - LastTimestamp;
        
        for (int i = 0; i < delta; i++){
            Datapoints.Add(r.Value);
        }

        LastTimestamp = newTimestamp;
    }

    public List<double> getTimeframe(DateTime start, DateTime end) {
        long startUnix = new DateTimeOffset(start).ToUnixTimeSeconds();
        long endUnix = new DateTimeOffset(end).ToUnixTimeSeconds();

        if (start > end) throw new ArgumentException("Start time is after end time.");
        if (startUnix > LastTimestamp || endUnix < StartTimestamp) return new();

        int startIndex = (int)(startUnix - StartTimestamp);
        int length = (int)(endUnix - startUnix);

        if (startIndex < 0) startIndex = 0;
        if (startIndex + length > Datapoints.Count) length = Datapoints.Count - startIndex;

        return Datapoints.Slice(startIndex, length);
    }
}


/// <summary>
/// Buffers the most recently decoded reading for every (name, tag-set) combination so the UI can
/// pull current state (e.g. the battery dashboard) without replaying history. Subscribes to
/// SerialPortMonitorService.ReadingReceived and is the single source of truth for the UI.
/// </summary>
/// 
public class DataManager : IDisposable
{
    private readonly SerialPortMonitorService _serialService;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Reading> _latestByKey = new();

    private readonly TimeSeries _vCar = new();
    private readonly TimeSeries _UBat = new();
    private readonly TimeSeries _PMppt1 = new();
    private readonly TimeSeries _PMppt2 = new();
    private readonly TimeSeries _PMppt3 = new();
    private readonly TimeSeries _PMppt4 = new();
    private readonly TimeSeries _IMotor = new();
    private readonly TimeSeries _UMotor = new();
    private readonly TimeSeries _PBattery = new();

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

            UpdateTimeseries(readings);
        }

        ReadingsAdded?.Invoke(readings);
    }

    public void UpdateTimeseries(List<Reading> readings) {

        foreach(Reading reading in readings) {

            switch (reading.ReadingName)
            {
                case "speed":
                    _vCar.AddAndInterpolate(reading);
                    break;
                case "calc_mppt_out_power":
                    switch (reading.Tags["mppt_id"])
                    {
                        case "1":
                            _PMppt1.AddAndInterpolate(reading);
                            break;
                        case "2":
                            _PMppt2.AddAndInterpolate(reading);
                            break;
                        case "3":
                            _PMppt3.AddAndInterpolate(reading);
                            break;
                        case "4":
                            _PMppt4.AddAndInterpolate(reading);
                            break;
                        default:
                            break;
                    }
                    break;
                case "mc_curr_in":
                    _IMotor.AddAndInterpolate(reading);
                    break;
                case "mc_volt_in":
                    _UMotor.AddAndInterpolate(reading);
                    break;
                case "calc_battery_power":
                    _PBattery.AddAndInterpolate(reading);
                    break;
                case "batt_volt":
                    _UBat.AddAndInterpolate(reading);
                    break;
                default:
                    break;
            }
        }
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
