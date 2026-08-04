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


public enum ChartSeries
{
    Speed,
    BatteryVoltage,
    BatteryPower,
    MotorPower,
    SolarTotal,
    Mppt1,
    Mppt2,
    Mppt3,
    Mppt4
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
    private readonly TimeSeries _PMppt1 = new();
    private readonly TimeSeries _PMppt2 = new();
    private readonly TimeSeries _PMppt3 = new();
    private readonly TimeSeries _PMppt4 = new();
    private readonly TimeSeries _IMotor = new();
    private readonly TimeSeries _UMotor = new();
    private readonly TimeSeries _PMotor = new();
    private readonly TimeSeries _UBattery = new();
    private readonly TimeSeries _PBattery = new();

    public event Action<List<Reading>>? ReadingsAdded;

    public DataManager(SerialPortMonitorService serialService)
    {
        _serialService = serialService;
        _serialService.ReadingReceived += OnReadingReceived;
    }

    // Public so tests can feed readings through the same path real serial data takes, without a
    // live SerialPortMonitorService.
    public void Ingest(List<Reading> readings)
    {
        lock (_lock)
        {
            foreach (var reading in readings)
                _latestByKey[BuildKey(reading.ReadingName, reading.Tags)] = reading;

            UpdateTimeseries(readings);
        }
    }

    private void OnReadingReceived(List<Reading> readings)
    {
        Ingest(readings);
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
                    UpdateMotorPower(reading.Timestamp);
                    break;
                case "mc_volt_in":
                    _UMotor.AddAndInterpolate(reading);
                    UpdateMotorPower(reading.Timestamp);
                    break;
                case "calc_batt_power":
                    _PBattery.AddAndInterpolate(reading);
                    break;
                case "batt_volt":
                    _UBattery.AddAndInterpolate(reading);
                    break;
                default:
                    break;
            }
        }
    }

    // mc_curr_in and mc_volt_in arrive in separate CAN frames, so motor power is approximated from
    // whichever value last arrived for the other half of the pair. Must only be called while _lock is held.
    private void UpdateMotorPower(DateTime timestamp)
    {
        if (_latestByKey.GetValueOrDefault(BuildKey("mc_curr_in", Array.Empty<(string Key, string Value)>())) is not { } current
            || _latestByKey.GetValueOrDefault(BuildKey("mc_volt_in", Array.Empty<(string Key, string Value)>())) is not { } voltage)
            return;

        _PMotor.AddAndInterpolate(new Reading
        {
            Timestamp = timestamp,
            ReadingName = "calc_motor_power",
            Value = current.Value * voltage.Value,
            Unit = "W",
            Tags = new()
        });
    }

    public List<double> GetSeries(ChartSeries series, TimeSpan window)
    {
        lock (_lock)
        {
            var end = DateTime.Now;
            var start = end - window;

            return series switch
            {
                ChartSeries.Speed => _vCar.getTimeframe(start, end),
                ChartSeries.BatteryVoltage => _UBattery.getTimeframe(start, end),
                ChartSeries.BatteryPower => _PBattery.getTimeframe(start, end),
                ChartSeries.MotorPower => _PMotor.getTimeframe(start, end),
                ChartSeries.Mppt1 => _PMppt1.getTimeframe(start, end),
                ChartSeries.Mppt2 => _PMppt2.getTimeframe(start, end),
                ChartSeries.Mppt3 => _PMppt3.getTimeframe(start, end),
                ChartSeries.Mppt4 => _PMppt4.getTimeframe(start, end),
                ChartSeries.SolarTotal => SumRightAligned(
                    _PMppt1.getTimeframe(start, end),
                    _PMppt2.getTimeframe(start, end),
                    _PMppt3.getTimeframe(start, end),
                    _PMppt4.getTimeframe(start, end)),
                _ => new()
            };
        }
    }

    public double? GetAverage(ChartSeries series, TimeSpan window)
    {
        var points = GetSeries(series, window);
        return points.Count == 0 ? null : points.Average();
    }

    // Sums timeseries slices that may differ in length (e.g. an MPPT with no data returns an empty
    // slice), aligning them on their most recent sample since that's always where they agree.
    private static List<double> SumRightAligned(params List<double>[] series)
    {
        int maxLen = series.Max(s => s.Count);
        var result = new List<double>(maxLen);

        for (int i = 0; i < maxLen; i++)
        {
            double sum = 0;
            foreach (var s in series)
            {
                int idx = s.Count - maxLen + i;
                if (idx >= 0)
                    sum += s[idx];
            }
            result.Add(sum);
        }

        return result;
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
