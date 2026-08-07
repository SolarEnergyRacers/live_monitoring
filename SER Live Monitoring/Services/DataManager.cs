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

    private double _fillValue;

    public TimeSeries(double fillValue) {
        _fillValue = fillValue;
    }

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
        if (delta <= 5)
        {
            for (int i = 0; i < delta; i++)
            {
                Datapoints.Add(r.Value);
            }
        }
        else 
        {
            for (int i = 0; i < delta; i++)
            {
                Datapoints.Add(_fillValue);
            }
        }
        

        LastTimestamp = newTimestamp;
    }

    public List<double> getTimeframe(DateTime start, DateTime end) {
        var (startIndex, length) = SliceRange(start, end);
        return Datapoints.Slice(startIndex, length);
    }

    // Same slice as getTimeframe, but paired with each point's real timestamp - for charts that
    // need an actual datetime x-axis (e.g. to line up event annotations) rather than a bare series.
    public List<(long UnixTimestamp, double Value)> GetTimeframeWithTimestamps(DateTime start, DateTime end) {
        var (startIndex, length) = SliceRange(start, end);
        var result = new List<(long, double)>(length);
        for (int i = 0; i < length; i++)
            result.Add((StartTimestamp + startIndex + i, Datapoints[startIndex + i]));
        return result;
    }

    private (int StartIndex, int Length) SliceRange(DateTime start, DateTime end) {
        long startUnix = new DateTimeOffset(start).ToUnixTimeSeconds();
        long endUnix = new DateTimeOffset(end).ToUnixTimeSeconds();

        if (start > end) throw new ArgumentException("Start time is after end time.");
        if (startUnix > LastTimestamp || endUnix < StartTimestamp) return (0, 0);

        int startIndex = (int)(startUnix - StartTimestamp);
        int length = (int)(endUnix - startUnix);

        if (startIndex < 0) startIndex = 0;
        if (startIndex + length > Datapoints.Count) length = Datapoints.Count - startIndex;
        if (length < 0) length = 0;

        return (startIndex, length);
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

    // Keyed storage (rather than one field per series) so PersistenceService can enumerate and
    // restore every series generically, without a parallel hardcoded list to keep in sync.
    private readonly Dictionary<string, TimeSeries> _series = new()
    {
        ["speed"] = new(0.0),
        ["mppt1_power"] = new(0.0),
        ["mppt2_power"] = new(0.0),
        ["mppt3_power"] = new(0.0),
        ["mppt4_power"] = new(0.0),
        ["motor_current"] = new(0.0),
        ["motor_voltage"] = new(0.0),
        ["motor_power"] = new(0.0),
        ["battery_voltage"] = new(0.0),
        ["battery_power"] = new(0.0),
    };

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
                    _series["speed"].AddAndInterpolate(reading);
                    break;
                case "calc_mppt_out_power":
                    switch (reading.Tags["mppt_id"])
                    {
                        case "1":
                            _series["mppt1_power"].AddAndInterpolate(reading);
                            break;
                        case "2":
                            _series["mppt2_power"].AddAndInterpolate(reading);
                            break;
                        case "3":
                            _series["mppt3_power"].AddAndInterpolate(reading);
                            break;
                        case "4":
                            _series["mppt4_power"].AddAndInterpolate(reading);
                            break;
                        default:
                            break;
                    }
                    break;
                case "mc_curr_in":
                    _series["motor_current"].AddAndInterpolate(reading);
                    UpdateMotorPower(reading.Timestamp);
                    break;
                case "mc_volt_in":
                    _series["motor_voltage"].AddAndInterpolate(reading);
                    UpdateMotorPower(reading.Timestamp);
                    break;
                case "calc_batt_power":
                    _series["battery_power"].AddAndInterpolate(reading);
                    break;
                case "batt_volt":
                    _series["battery_voltage"].AddAndInterpolate(reading);
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

        _series["motor_power"].AddAndInterpolate(new Reading
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
                ChartSeries.Speed => _series["speed"].getTimeframe(start, end),
                ChartSeries.BatteryVoltage => _series["battery_voltage"].getTimeframe(start, end),
                ChartSeries.BatteryPower => _series["battery_power"].getTimeframe(start, end),
                ChartSeries.MotorPower => _series["motor_power"].getTimeframe(start, end),
                ChartSeries.Mppt1 => _series["mppt1_power"].getTimeframe(start, end),
                ChartSeries.Mppt2 => _series["mppt2_power"].getTimeframe(start, end),
                ChartSeries.Mppt3 => _series["mppt3_power"].getTimeframe(start, end),
                ChartSeries.Mppt4 => _series["mppt4_power"].getTimeframe(start, end),
                ChartSeries.SolarTotal => SumRightAligned(
                    _series["mppt1_power"].getTimeframe(start, end),
                    _series["mppt2_power"].getTimeframe(start, end),
                    _series["mppt3_power"].getTimeframe(start, end),
                    _series["mppt4_power"].getTimeframe(start, end)),
                _ => new()
            };
        }
    }

    // Timestamped speed history for the Home page's event-timestamp chart, which needs a real
    // datetime x-axis to line up annotations - unlike GetSeries, which returns bare values for the
    // spark tiles.
    public List<TimeSeriesPoint> GetSpeedHistory(TimeSpan window)
    {
        lock (_lock)
        {
            var end = DateTime.Now;
            var start = end - window;

            return _series["speed"].GetTimeframeWithTimestamps(start, end)
                .Select(p => new TimeSeriesPoint(DateTimeOffset.FromUnixTimeSeconds(p.UnixTimestamp).LocalDateTime, p.Value))
                .ToList();
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

    // Series names PersistenceService can look up via RestoreSeries/DrainNewPoints.
    public IReadOnlyCollection<string> SeriesNames => _series.Keys;

    // Restores a series loaded from disk by PersistenceService. Must run before any live reading
    // for this series arrives - otherwise AddAndInterpolate's "first sample ever" branch will
    // already have picked its own StartTimestamp and this would silently misalign the restored data.
    public void RestoreSeries(string name, long startTimestamp, IReadOnlyList<double> points)
    {
        if (points.Count == 0)
            return;

        lock (_lock)
        {
            if (!_series.TryGetValue(name, out var series))
                return;

            series.StartTimestamp = startTimestamp;
            series.Datapoints.AddRange(points);
            series.LastTimestamp = startTimestamp + series.Datapoints.Count - 1;
        }
    }

    // For every series with more datapoints than its last recorded count in flushedCounts, returns
    // the series' StartTimestamp and the new tail of points, and advances flushedCounts to match.
    // Used by PersistenceService so it only ever appends what's new. Passing a fresh empty
    // dictionary yields every point currently held, e.g. for a full re-dump.
    public List<(string Name, long StartTimestamp, double[] NewPoints)> DrainNewPoints(Dictionary<string, int> flushedCounts)
    {
        var result = new List<(string, long, double[])>();

        lock (_lock)
        {
            foreach (var (name, series) in _series)
            {
                var already = flushedCounts.GetValueOrDefault(name);
                if (series.Datapoints.Count <= already)
                    continue;

                result.Add((name, series.StartTimestamp, series.Datapoints.Skip(already).ToArray()));
                flushedCounts[name] = series.Datapoints.Count;
            }
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
