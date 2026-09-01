using SERLiveMonitoring.Models;
namespace SERLiveMonitoring.Services;

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
    BatteryCurrent,
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
    private readonly SettingsService _settings;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Reading> _latestByKey = new();

    // Driver messages sent to the car's display, in-memory only (not persisted across restarts).
    private readonly List<DriverMessage> _driverMessages = new();
    private double _lastDriverConfirm;

    // GPS fixes reported by an external device via the REST API (see GpsTrackService). Persisted
    // separately from the CAN timeseries - GpsTrackService restores prior history into this list at
    // startup and appends to it (and to disk) on every new fix, so it's always ordered oldest-first.
    private readonly List<GpsPoint> _gpsPoints = new();

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
        ["battery_current"] = new(0.0),
        ["battery_power"] = new(0.0),
    };

    public event Action<List<Reading>>? ReadingsAdded;

    // Driver messages change from a background thread (the serial read thread, via Ingest) as
    // well as from UI event handlers (AddDriverMessage). The UI-handler case already re-renders
    // itself for free, but the background case doesn't touch any Blazor circuit at all, so
    // DriverMessagePanel subscribes to this to know when to StateHasChanged - matching how
    // SerialPortMonitorService.StatusChanged and EventTimestampService.EventsChanged are used.
    public event Action? DriverMessagesChanged;

    // Raised whenever a new GPS fix is added (not for the bulk restore at startup) - lets a future
    // live map subscribe instead of polling, matching DriverMessagesChanged's purpose.
    public event Action<GpsPoint>? GpsPointAdded;

    public DataManager(SerialPortMonitorService serialService, SettingsService settings)
    {
        _serialService = serialService;
        _settings = settings;
        _serialService.ReadingReceived += OnReadingReceived;
    }

    // Public so tests can feed readings through the same path real serial data takes, without a
    // live SerialPortMonitorService.
    public void Ingest(List<Reading> readings)
    {
        bool confirmed;

        lock (_lock)
        {
            foreach (var reading in readings)
                _latestByKey[BuildKey(reading.ReadingName, reading.Tags)] = reading;

            UpdateTimeseries(readings);
            confirmed = ProcessDriverConfirm(readings);
        }

        if (confirmed)
            DriverMessagesChanged?.Invoke();
    }

    // The car sets driver_confirm for a couple of seconds after the driver presses the confirm
    // button; only the rising edge means "just pressed", since the reading keeps arriving at 1 for
    // as long as the flag is held. The car's dash only ever shows the latest message, so a press
    // acknowledges that one specifically, not every message sent since the last confirm.
    // Must only be called while _lock is held; returns whether a message got confirmed, so the
    // caller can raise DriverMessagesChanged after releasing the lock.
    private bool ProcessDriverConfirm(List<Reading> readings)
    {
        var confirmed = false;

        foreach (var reading in readings)
        {
            if (reading.ReadingName != "driver_confirm")
                continue;

            var rising = reading.Value != 0 && _lastDriverConfirm == 0;
            _lastDriverConfirm = reading.Value;

            if (!rising)
                continue;

            var latestUnconfirmed = _driverMessages
                .Where(m => m.ConfirmedAt is null)
                .OrderByDescending(m => m.SentAt)
                .FirstOrDefault();

            if (latestUnconfirmed is null)
                continue;

            var index = _driverMessages.IndexOf(latestUnconfirmed);
            _driverMessages[index] = latestUnconfirmed with { ConfirmedAt = reading.Timestamp };
            confirmed = true;
        }

        return confirmed;
    }

    public DriverMessage AddDriverMessage(DriverMessageSeverity severity, string text)
    {
        var message = new DriverMessage
        {
            Id = Guid.NewGuid(),
            Severity = severity,
            Text = text,
            SentAt = DateTime.Now
        };

        lock (_lock)
            _driverMessages.Add(message);

        DriverMessagesChanged?.Invoke();
        return message;
    }

    public List<DriverMessage> GetDriverMessages()
    {
        lock (_lock)
            return _driverMessages.OrderByDescending(m => m.SentAt).ToList();
    }

    // Called by GpsTrackService after it has persisted the point, so the two never disagree about
    // what's been saved. Fires GpsPointAdded for live consumers.
    public GpsPoint AddGpsPoint(GpsPoint point)
    {
        lock (_lock)
            _gpsPoints.Add(point);

        GpsPointAdded?.Invoke(point);
        return point;
    }

    // Bulk-loads history GpsTrackService already had on disk. No event fired - there's no live UI
    // to notify about data that isn't new, matching TimeSeries' RestoreSeries/AddAndInterpolate split.
    public void RestoreGpsPoints(IEnumerable<GpsPoint> points)
    {
        lock (_lock)
            _gpsPoints.AddRange(points);
    }

    public GpsPoint? GetLatestGpsPoint()
    {
        lock (_lock)
            return _gpsPoints.Count == 0 ? null : _gpsPoints[^1];
    }

    // Latest fix from a specific device, for when multiple reporters are active and the UI needs
    // to show just one of them rather than whichever happened to report most recently overall.
    public GpsPoint? GetLatestGpsPoint(string deviceName)
    {
        lock (_lock)
            return _gpsPoints.LastOrDefault(p => p.DeviceName == deviceName);
    }

    // Distinct device names seen so far, oldest-first-seen, so a device selector lists them in the
    // order they started reporting rather than shuffling as new fixes arrive.
    public List<string> GetGpsDeviceNames()
    {
        lock (_lock)
            return _gpsPoints.Select(p => p.DeviceName).Distinct().ToList();
    }

    public int GetGpsPointCount()
    {
        lock (_lock)
            return _gpsPoints.Count;
    }

    public int GetGpsPointCount(string deviceName)
    {
        lock (_lock)
            return _gpsPoints.Count(p => p.DeviceName == deviceName);
    }

    public List<GpsPoint> GetGpsHistory(TimeSpan window)
    {
        var cutoff = DateTime.Now - window;
        lock (_lock)
            return _gpsPoints.Where(p => p.Timestamp >= cutoff).ToList();
    }

    public List<GpsPoint> GetGpsHistory(DateTime start, DateTime end)
    {
        lock (_lock)
            return _gpsPoints.Where(p => p.Timestamp >= start && p.Timestamp <= end).ToList();
    }

    // Oldest-first, for drawing as a track (Google Maps directions URLs read waypoints in order).
    public List<GpsPoint> GetLastGpsPoints(int count)
    {
        lock (_lock)
            return _gpsPoints.Skip(Math.Max(0, _gpsPoints.Count - count)).ToList();
    }

    // Same as above, restricted to one device's points - needed once more than one device reports,
    // since otherwise another device's fixes would dilute the requested count.
    public List<GpsPoint> GetLastGpsPoints(int count, string deviceName)
    {
        lock (_lock)
            return _gpsPoints.Where(p => p.DeviceName == deviceName).TakeLast(count).ToList();
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
                    // If the MC doesn't send CAN data at all, it can't send this either - but guard
                    // explicitly anyway so a stray/leftover mc_curr_in frame can't clobber the
                    // derived value once the setting is on.
                    if (!_settings.Current.NoMcCanData)
                    {
                        _series["motor_current"].AddAndInterpolate(reading);
                        UpdateMotorPower(reading.Timestamp);
                    }
                    break;
                case "mc_volt_in":
                    if (!_settings.Current.NoMcCanData)
                    {
                        _series["motor_voltage"].AddAndInterpolate(reading);
                        UpdateMotorPower(reading.Timestamp);
                    }
                    break;
                case "calc_batt_power":
                    _series["battery_power"].AddAndInterpolate(reading);
                    break;
                case "batt_volt":
                    _series["battery_voltage"].AddAndInterpolate(reading);
                    if (_settings.Current.NoMcCanData)
                        UpdateDerivedMotorPower(reading.Timestamp);
                    break;
                case "batt_curr":
                    _series["battery_current"].AddAndInterpolate(reading);
                    if (_settings.Current.NoMcCanData)
                        UpdateDerivedMotorPower(reading.Timestamp);
                    break;
                case "mppt_out_current":
                    // Only feeds the NoMcCanData derivation below - there's no standalone "mppt
                    // current" series otherwise (calc_mppt_out_power already covers per-MPPT power).
                    if (_settings.Current.NoMcCanData)
                        UpdateDerivedMotorPower(reading.Timestamp);
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

    private static readonly string[] MpptIds = ["1", "2", "3", "4"];

    // Used when the motor controller doesn't put current/power on the CAN bus at all (settings
    // checkbox "No MC CAN Data"). data/simulate_solar_car.py defines the pack's net current as
    // motor draw minus total MPPT output current (net_current = motor_in_current -
    // total_mppt_current, sent as batt_curr), so motor current is battery current *plus* the summed
    // MPPT output current - not minus, even though "battery current less what the panels made" reads
    // intuitively like the right subtraction; the BMS convention makes battery current already net of
    // MPPT contribution, so adding it back out recovers the motor draw.
    // There's no equivalent way to derive motor voltage, but battery, MPPT-output, and motor-input
    // voltage are all the same shared bus (see CLAUDE.md's DataManager notes), so battery voltage
    // stands in for it here. Must only be called while _lock is held.
    private void UpdateDerivedMotorPower(DateTime timestamp)
    {
        if (_latestByKey.GetValueOrDefault(BuildKey("batt_curr", Array.Empty<(string Key, string Value)>())) is not { } batteryCurrent
            || _latestByKey.GetValueOrDefault(BuildKey("batt_volt", Array.Empty<(string Key, string Value)>())) is not { } batteryVoltage)
            return;

        var totalMpptCurrent = MpptIds.Sum(id =>
            _latestByKey.GetValueOrDefault(BuildKey("mppt_out_current", new[] { ("mppt_id", id) }))?.Value ?? 0);

        var motorCurrent = batteryCurrent.Value + totalMpptCurrent;

        _series["motor_current"].AddAndInterpolate(new Reading
        {
            Timestamp = timestamp,
            ReadingName = "calc_motor_current",
            Value = motorCurrent,
            Unit = "A",
            Tags = new()
        });

        _series["motor_power"].AddAndInterpolate(new Reading
        {
            Timestamp = timestamp,
            ReadingName = "calc_motor_power",
            Value = motorCurrent * batteryVoltage.Value,
            Unit = "W",
            Tags = new()
        });
    }

    private static readonly string[] MpptSeriesNames = ["mppt1_power", "mppt2_power", "mppt3_power", "mppt4_power"];

    // Every ChartSeries except SolarTotal maps onto exactly one stored TimeSeries; SolarTotal is
    // derived (see SumByTimestamp) since it's the sum of the four MPPT series, not a stored one.
    private TimeSeries? ResolveSeries(ChartSeries series) => series switch
    {
        ChartSeries.Speed => _series["speed"],
        ChartSeries.BatteryVoltage => _series["battery_voltage"],
        ChartSeries.BatteryCurrent => _series["battery_current"],
        ChartSeries.BatteryPower => _series["battery_power"],
        ChartSeries.MotorPower => _series["motor_power"],
        ChartSeries.Mppt1 => _series["mppt1_power"],
        ChartSeries.Mppt2 => _series["mppt2_power"],
        ChartSeries.Mppt3 => _series["mppt3_power"],
        ChartSeries.Mppt4 => _series["mppt4_power"],
        _ => null
    };

    public List<double> GetSeries(ChartSeries series, TimeSpan window)
    {
        lock (_lock)
        {
            var end = DateTime.Now;
            var start = end - window;

            if (series == ChartSeries.SolarTotal)
                return SumByTimestamp(MpptSeriesNames.Select(n => _series[n].GetTimeframeWithTimestamps(start, end)))
                    .Select(p => p.Value)
                    .ToList();

            return ResolveSeries(series)?.getTimeframe(start, end) ?? new();
        }
    }

    // Absolute-range counterpart to GetSeries, with real timestamps attached - used by the
    // Analytics page, which needs to query arbitrary historical windows rather than a rolling
    // "now minus window" range.
    public List<(long UnixTimestamp, double Value)> GetSeriesRange(ChartSeries series, DateTime start, DateTime end)
    {
        lock (_lock)
        {
            if (series == ChartSeries.SolarTotal)
                return SumByTimestamp(MpptSeriesNames.Select(n => _series[n].GetTimeframeWithTimestamps(start, end)));

            return ResolveSeries(series)?.GetTimeframeWithTimestamps(start, end) ?? new();
        }
    }

    // Every stored series for [start, end], keyed by series name (see SeriesNames) - for the bulk
    // export endpoint used by external analysis tooling. Unlike GetSeriesRange this isn't scoped to
    // a single ChartSeries, so it also includes channels with no ChartSeries entry of their own
    // (motor_current, motor_voltage) - and unlike SolarTotal, nothing here is summed/derived.
    public Dictionary<string, List<(long UnixTimestamp, double Value)>> GetAllSeriesRange(DateTime start, DateTime end)
    {
        lock (_lock)
            return _series.ToDictionary(kv => kv.Key, kv => kv.Value.GetTimeframeWithTimestamps(start, end));
    }

    // The earliest and latest timestamps recorded across every series, i.e. the full extent of
    // history currently held (in memory plus anything PersistenceService restored on startup).
    // Null when nothing has been recorded yet.
    public (DateTime? Start, DateTime? End) GetOverallTimeRange()
    {
        lock (_lock)
        {
            long? minStart = null;
            long? maxEnd = null;

            foreach (var series in _series.Values)
            {
                if (series.Datapoints.Count == 0)
                    continue;

                minStart = minStart is null ? series.StartTimestamp : Math.Min(minStart.Value, series.StartTimestamp);
                maxEnd = maxEnd is null ? series.LastTimestamp : Math.Max(maxEnd.Value, series.LastTimestamp);
            }

            return minStart is null
                ? (null, null)
                : (DateTimeOffset.FromUnixTimeSeconds(minStart.Value).LocalDateTime, DateTimeOffset.FromUnixTimeSeconds(maxEnd!.Value).LocalDateTime);
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

    // Sums timestamped series by matching real timestamps rather than by trailing-edge index
    // alignment, so series that started recording at different times (e.g. one MPPT reporting
    // later than the others) are still summed correctly rather than merely lined up by their
    // most recent sample.
    private static List<(long UnixTimestamp, double Value)> SumByTimestamp(IEnumerable<List<(long UnixTimestamp, double Value)>> serieses)
    {
        var totals = new SortedDictionary<long, double>();

        foreach (var series in serieses)
            foreach (var (timestamp, value) in series)
                totals[timestamp] = totals.GetValueOrDefault(timestamp) + value;

        return totals.Select(kv => (kv.Key, kv.Value)).ToList();
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
