using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

/// <summary>
/// Evaluates the latest readings buffered in a DataManager against known fault flags and
/// heuristic thresholds, producing the list of warnings currently active for the vehicle.
/// </summary>
public static class VehicleWarnings
{
    // (Label, device tag) - the tag matches the "device" value CANFrameDecoder stamps on every
    // decoded frame's "device_heartbeat" reading. Shared with the homepage's device activity panel.
    public static readonly (string Label, string Tag)[] Devices =
    [
        ("MC", "Mc"),
        ("BMS", "Bms"),
        ("MPPT1", "Mppt1"),
        ("MPPT2", "Mppt2"),
        ("MPPT3", "Mppt3"),
        ("MPPT4", "Mppt4"),
        ("AC", "Ac"),
        ("DC", "Dc"),
    ];

    // BMS fault bits (see CANFrameDecoder.DecodeBms, sub 0xF7 and 0xFD) - all "1 = fault" already,
    // no inversion needed.
    private static readonly (string Name, string Label)[] BatteryFaultFlags =
    [
        ("cell_over_voltage", "Cell over-voltage"),
        ("cell_under_voltage", "Cell under-voltage"),
        ("cell_over_temp", "Cell over-temperature"),
        ("measurement_untrusted", "BMS measurement untrusted"),
        ("cmu_comm_timeout", "CMU communication timeout"),
        ("vehicle_comm_timeout", "Vehicle communication timeout"),
        ("bmu_in_setup_mode", "BMU is in setup mode"),
        ("cmu_can_power", "CMU CAN power fault"),
        ("pack_isolation_fail", "Pack isolation failure"),
        ("soc_invalid", "State of charge invalid"),
        ("can_power_low", "CAN power low"),
        ("contactor_stuck", "Contactor stuck"),
        ("unexpected_cell", "Unexpected cell detected"),
        ("err_cont_1_driver", "Contactor 1 driver error"),
        ("err_cont_2_driver", "Contactor 2 driver error"),
        ("err_cont_3_driver", "Contactor 3 driver error"),
    ];

    private static readonly (string Name, string Label)[] MpptFaultFlags =
    [
        ("mppt_mosfet_overheat", "MOSFET overheating"),
        ("mppt_hw_over_curr", "hardware over-current"),
        ("mppt_hw_over_volt", "hardware over-voltage"),
        ("mppt_12V_undervoltage", "12V rail under-voltage"),
    ];

    private static readonly TimeSpan AvgWindow = TimeSpan.FromSeconds(15);

    public static List<Warning> Evaluate(DataManager data, bool isConnected, WarningThresholds thresholds)
    {
        var warnings = new List<Warning>();

        AddBatteryFaultFlags(data, warnings);
        AddCellVoltageSpread(data, warnings, thresholds);
        AddTemperatureWarnings(data, warnings, thresholds);
        AddMpptFaultFlags(data, warnings);
        AddMpptUnderperformance(data, warnings, thresholds);

        // While disconnected every device would trivially look "not responding" - that's already
        // covered by the connection status chip, so only run this check while actually connected.
        if (isConnected)
            AddCommunicationLoss(data, warnings, thresholds);

        return warnings;
    }

    private static void AddBatteryFaultFlags(DataManager data, List<Warning> warnings)
    {
        foreach (var (name, label) in BatteryFaultFlags)
        {
            if (data.GetLatest(name)?.Value == 1)
                warnings.Add(new Warning(label, WarningLevel.Error));
        }
    }

    private static void AddCellVoltageSpread(DataManager data, List<Warning> warnings, WarningThresholds thresholds)
    {
        var min = data.GetLatestSingle("min_voltage");
        var max = data.GetLatestSingle("max_voltage");
        if (min is null || max is null)
            return;

        var spread = max.Value - min.Value;
        if (spread > thresholds.MaxCellVoltageSpreadV)
            warnings.Add(new Warning($"Cell voltage spread {spread:0.###} V exceeds {thresholds.MaxCellVoltageSpreadV} V", WarningLevel.Warning));
    }

    private static void AddTemperatureWarnings(DataManager data, List<Warning> warnings, WarningThresholds thresholds)
    {
        var cellTemp = data.GetLatestSingle("max_temp");
        if (cellTemp is not null && cellTemp.Value > thresholds.MaxCellTempC)
            warnings.Add(new Warning($"Battery cell temperature {cellTemp.Value:0.#} °C exceeds {thresholds.MaxCellTempC} °C", WarningLevel.Error));

        var fetTemp = data.GetLatest("mc_fet_temp");
        if (fetTemp is not null && fetTemp.Value > thresholds.MaxMotorTempC)
            warnings.Add(new Warning($"Motor controller FET temperature {fetTemp.Value:0.#} °C exceeds {thresholds.MaxMotorTempC} °C", WarningLevel.Error));

        var motorTemp = data.GetLatest("mc_motor_temp");
        if (motorTemp is not null && motorTemp.Value > thresholds.MaxMotorTempC)
            warnings.Add(new Warning($"Motor temperature {motorTemp.Value:0.#} °C exceeds {thresholds.MaxMotorTempC} °C", WarningLevel.Error));

        for (var i = 1; i <= 4; i++)
        {
            var mosfetTemp = data.GetLatest("mppt_mosfet_temp", ("mppt_id", i.ToString()));
            if (mosfetTemp is not null && mosfetTemp.Value > thresholds.MaxMpptTempC)
                warnings.Add(new Warning($"MPPT {i} MOSFET temperature {mosfetTemp.Value:0.#} °C exceeds {thresholds.MaxMpptTempC} °C", WarningLevel.Warning));
        }
    }

    private static void AddMpptFaultFlags(DataManager data, List<Warning> warnings)
    {
        for (var i = 1; i <= 4; i++)
        {
            foreach (var (name, label) in MpptFaultFlags)
            {
                if (data.GetLatest(name, ("mppt_id", i.ToString()))?.Value == 1)
                    warnings.Add(new Warning($"MPPT {i}: {label}", WarningLevel.Warning));
            }
        }
    }

    private static void AddMpptUnderperformance(DataManager data, List<Warning> warnings, WarningThresholds thresholds)
    {
        var powers = new[]
        {
            data.GetAverage(ChartSeries.Mppt1, AvgWindow) ?? 0,
            data.GetAverage(ChartSeries.Mppt2, AvgWindow) ?? 0,
            data.GetAverage(ChartSeries.Mppt3, AvgWindow) ?? 0,
            data.GetAverage(ChartSeries.Mppt4, AvgWindow) ?? 0,
        };

        for (var i = 0; i < powers.Length; i++)
        {
            var others = powers.Where((p, idx) => idx != i && p > thresholds.MinActiveMpptPowerW).ToList();
            if (others.Count < 2)
                continue; // not enough evidence the array is generally productive right now

            var othersAvg = others.Average();
            if (powers[i] < othersAvg * thresholds.LowMpptPowerRatio)
                warnings.Add(new Warning($"MPPT {i + 1} producing {powers[i]:0.#} W while other panels average {othersAvg:0.#} W", WarningLevel.Warning));
        }
    }

    private static void AddCommunicationLoss(DataManager data, List<Warning> warnings, WarningThresholds thresholds)
    {
        var timeout = TimeSpan.FromSeconds(thresholds.CommTimeoutSeconds);

        foreach (var (label, tag) in Devices)
        {
            var heartbeat = data.GetLatest("device_heartbeat", ("device", tag));
            if (heartbeat is not null && DateTime.Now - heartbeat.Timestamp > timeout)
                warnings.Add(new Warning($"{label} not responding", WarningLevel.Error));
        }
    }
}
