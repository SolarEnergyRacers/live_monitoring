using SERLiveMonitoring.Models;

namespace SERLiveMonitoring.Services;

// A timeseries the Analytics page's main chart can plot, with the display metadata (label/unit/
// color) the chart, legend and tooltip need - kept in one place so adding a new plottable series
// only means adding one entry here.
public record ChartSeriesOption(ChartSeries Series, string Label, string Unit, string Color);

public record AnalyticsSummary(
    double DistanceKm,
    double MotorConsumedWh,
    double MotorRegeneratedWh,
    double SolarGeneratedWh,
    double BatteryDischargedWh,
    double DeltaWh,
    double AvgEnergyPerKm,
    double AvgMotorPowerW);

public record ScatterPoint(double Speed, double MotorPower);

public record EnergySlice(string Source, double EnergyWh);

/// <summary>
/// Turns DataManager's raw per-second timeseries into the aggregates and chart-ready shapes the
/// Analytics page needs for an arbitrary, user-selected time window - distance/energy totals,
/// a speed-vs-motor-power scatter, and an energy-source breakdown - none of which DataManager
/// itself needs to know about.
/// </summary>
public class AnalyticsDataService
{
    private readonly DataManager _dataManager;
    private readonly EventTimestampService _eventService;

    public static readonly ChartSeriesOption[] PlottableSeries =
    [
        new(ChartSeries.Speed, "Speed", "km/h", "#42A5F5"),
        new(ChartSeries.BatteryVoltage, "Battery Voltage", "V", "#7E57C2"),
        new(ChartSeries.BatteryCurrent, "Battery Current", "A", "#AB47BC"),
        new(ChartSeries.BatteryPower, "Battery Power", "W", "#26A69A"),
        new(ChartSeries.MotorPower, "Motor Power", "W", "#FF7043"),
        new(ChartSeries.SolarTotal, "Solar Power", "W", "#FFC107"),
        new(ChartSeries.Mppt1, "MPPT 1", "W", "#FFCA28"),
        new(ChartSeries.Mppt2, "MPPT 2", "W", "#FFB300"),
        new(ChartSeries.Mppt3, "MPPT 3", "W", "#FFA000"),
        new(ChartSeries.Mppt4, "MPPT 4", "W", "#FF8F00"),
    ];

    public AnalyticsDataService(DataManager dataManager, EventTimestampService eventService)
    {
        _dataManager = dataManager;
        _eventService = eventService;
    }

    public (DateTime? Start, DateTime? End) GetOverallTimeRange() => _dataManager.GetOverallTimeRange();

    public List<EventTimestamp> GetEvents() => _eventService.GetAll();

    // Downsampled to a fixed stride so multi-hour sessions stay smooth to render - good enough for
    // a visual trend line; no need for min/max-preserving decimation on a chart this size.
    public List<TimeSeriesPoint> GetChartSeries(ChartSeries series, DateTime start, DateTime end, int maxPoints = 2000)
    {
        var points = Decimate(_dataManager.GetSeriesRange(series, start, end), maxPoints);
        return points
            .Select(p => new TimeSeriesPoint(DateTimeOffset.FromUnixTimeSeconds(p.UnixTimestamp).LocalDateTime, p.Value))
            .ToList();
    }

    // Null when the window has no data at all, so the page can show "no data" instead of a
    // misleading all-zero summary.
    public AnalyticsSummary? Summarize(DateTime start, DateTime end)
    {
        if (end <= start)
            return null;

        var speed = _dataManager.GetSeriesRange(ChartSeries.Speed, start, end);
        var motorPower = _dataManager.GetSeriesRange(ChartSeries.MotorPower, start, end);
        var solarPower = _dataManager.GetSeriesRange(ChartSeries.SolarTotal, start, end);
        var batteryPower = _dataManager.GetSeriesRange(ChartSeries.BatteryPower, start, end);

        if (speed.Count == 0 && motorPower.Count == 0 && solarPower.Count == 0 && batteryPower.Count == 0)
            return null;

        // Every series is sampled at 1Hz, so each stored value is that signal's rate held for the
        // following second - summing values and dividing by 3600 turns a running speed/power
        // series directly into km / Wh for the selected window.
        const double PerSecondToPerHour = 1.0 / 3600.0;

        var distanceKm = speed.Sum(p => p.Value) * PerSecondToPerHour;
        var motorConsumedWh = motorPower.Where(p => p.Value > 0).Sum(p => p.Value) * PerSecondToPerHour;
        var motorRegeneratedWh = motorPower.Where(p => p.Value < 0).Sum(p => -p.Value) * PerSecondToPerHour;
        var solarGeneratedWh = solarPower.Sum(p => p.Value) * PerSecondToPerHour;

        // Assumes the BMS's usual convention: positive pack current/power means the pack is
        // discharging (same sign the Battery page already displays uninverted).
        var batteryDischargedWh = batteryPower.Where(p => p.Value > 0).Sum(p => p.Value) * PerSecondToPerHour;

        var netMotorWh = motorConsumedWh - motorRegeneratedWh;

        return new AnalyticsSummary(
            DistanceKm: distanceKm,
            MotorConsumedWh: motorConsumedWh,
            MotorRegeneratedWh: motorRegeneratedWh,
            SolarGeneratedWh: solarGeneratedWh,
            BatteryDischargedWh: batteryDischargedWh,
            DeltaWh: solarGeneratedWh - motorConsumedWh,
            AvgEnergyPerKm: distanceKm > 0 ? netMotorWh / distanceKm : 0,
            AvgMotorPowerW: motorPower.Count > 0 ? motorPower.Average(p => p.Value) : 0);
    }

    public List<ScatterPoint> GetSpeedVsMotorPower(DateTime start, DateTime end, int maxPoints = 3000)
    {
        var speedByTime = _dataManager.GetSeriesRange(ChartSeries.Speed, start, end).ToDictionary(p => p.UnixTimestamp, p => p.Value);
        var motorPower = _dataManager.GetSeriesRange(ChartSeries.MotorPower, start, end);

        var points = motorPower
            .Where(p => speedByTime.ContainsKey(p.UnixTimestamp))
            .Select(p => new ScatterPoint(speedByTime[p.UnixTimestamp], p.Value))
            .ToList();

        return Decimate(points, maxPoints);
    }

    // Approximates "where did the energy come from" as solar generation plus whatever the battery
    // discharged - not an exact attribution (both feed the same bus), but a reasonable breakdown
    // given the available signals.
    public List<EnergySlice> GetEnergySources(DateTime start, DateTime end)
    {
        var summary = Summarize(start, end);
        if (summary is null || (summary.SolarGeneratedWh <= 0 && summary.BatteryDischargedWh <= 0))
            return [];

        return
        [
            new("Solar", Math.Max(summary.SolarGeneratedWh, 0)),
            new("Battery", Math.Max(summary.BatteryDischargedWh, 0)),
        ];
    }

    private static List<T> Decimate<T>(List<T> items, int maxPoints)
    {
        if (items.Count <= maxPoints)
            return items;

        var stride = (int)Math.Ceiling(items.Count / (double)maxPoints);
        return items.Where((_, i) => i % stride == 0).ToList();
    }
}
