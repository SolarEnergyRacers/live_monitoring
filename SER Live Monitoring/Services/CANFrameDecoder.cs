using System.Buffers.Binary;
using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

public class CANFrameDecoder : IDataDecoder
{
    public List<Reading> Decode(byte[] rawLine)
    {
        short addr = (short)(BinaryPrimitives.ReadInt16BigEndian(rawLine.AsSpan(0, 2)) & 0x7FF);
        byte[] data = rawLine[2..];
        var timestamp = DateTime.Now;

        if (IsBmsFrame(addr)) return DecodeBms(addr, data, timestamp);
        if (IsMpptFrame(addr)) return DecodeMppt(addr, data, timestamp);
        if (IsDcFrame(addr)) return DecodeDc(addr, data, timestamp);
        if (IsAcFrame(addr)) return DecodeAc(addr, data, timestamp);
        if (IsMcFrame(addr)) return DecodeMc(addr, data, timestamp);

        return [];
    }

    public bool IsMpptFrame(short addr)
    {
        var masked = addr & 0xFF0;
        return masked == CanConfig.Mppt1Addr || masked == CanConfig.Mppt2Addr || masked == CanConfig.Mppt3Addr || masked == CanConfig.Mppt4Addr;
    }

    public bool IsBmsFrame(short addr)
    {
        return CanConfig.BmsBaseAddr == (addr & 0xF00);
    }

    public bool IsAcFrame(short addr)
    {
        return CanConfig.AcBaseAddr == (addr & 0xFF0);
    }

    public bool IsDcFrame(short addr)
    {
        return CanConfig.DcBaseAddr == (addr & 0xFF0);
    }

    public bool IsMcFrame(short addr)
    {
        return CanConfig.McBaseAddr == (addr & 0xF00);
    }

    private static List<Reading> DecodeBms(short addr, byte[] data, DateTime ts)
    {
        var readings = new List<Reading>();
        int sub = addr & 0xFF;

        if (addr == CanConfig.BmsBaseAddr) // BMU heartbeat / serial number
        {
            readings.Add(NewReading(ts, "bms_heartbeat", 1, tags: [("bmu_id", GetInt(data, 32, false, 1).ToString())]));
        }
        else if (sub <= 0x0C) // CMU status / cell data, CMU0 = 0x01-0x03, CMU1 = 0x04-0x06, ...
        {
            int cmuNum = (sub - 1) / 3;

            if (sub is 0x01 or 0x04 or 0x07 or 0x0A) // CMU serial number & temperatures
            {
                readings.Add(NewReading(ts, "cmu_heartbeat", 1, tags: [("cmu_id", GetInt(data, 32, false, 0).ToString()), ("cmu_num", cmuNum.ToString())]));
                readings.Add(NewReading(ts, "pcb_temp", GetInt(data, 16, true, 2) / 10.0, "°C", ("cmu_num", cmuNum.ToString())));
                readings.Add(NewReading(ts, "cell_temp", GetInt(data, 16, true, 3) / 10.0, "°C", ("cmu_num", cmuNum.ToString())));
            }
            else // Voltages 1 & 2 (four cells each)
            {
                int indexOffset = sub % 3 == 0 ? 4 : 0;
                for (int i = 0; i < 4; i++)
                {
                    int cellNum = i + indexOffset;
                    readings.Add(NewReading(ts, "cell_voltage", GetInt(data, 16, true, i) / 1000.0, "V",
                        ("cmu_num", cmuNum.ToString()), ("cell_num", cellNum.ToString()), ("cell_index", (cmuNum * 8 + cellNum).ToString())));
                }
            }
        }
        else if (sub == 0xF7) // Precharge status
        {
            readings.Add(NewReading(ts, "err_cont_1_driver", GetBit(data, 0) ? 1 : 0));
            readings.Add(NewReading(ts, "err_cont_2_driver", GetBit(data, 1) ? 1 : 0));
            readings.Add(NewReading(ts, "output_cont_1_driver", GetBit(data, 3) ? 1 : 0));
            readings.Add(NewReading(ts, "output_cont_2_driver", GetBit(data, 4) ? 1 : 0));
            readings.Add(NewReading(ts, "err_cont_12v_supp", GetBit(data, 5) ? 0 : 1));
            readings.Add(NewReading(ts, "err_cont_3_driver", GetBit(data, 6) ? 1 : 0));
            readings.Add(NewReading(ts, "output_cont_3_driver", GetBit(data, 7) ? 1 : 0));

            // prec_state codes: 0=error, 1=idle, 2=measure, 3=precharge, 4=run, 5=enable
            readings.Add(NewReading(ts, "prec_state", GetInt(data, 8, false, 1)));

            readings.Add(NewReading(ts, "cont_voltage", GetInt(data, 16, false, 1) / 1000.0, "V"));
            readings.Add(NewReading(ts, "prec_timer_elaps", GetBit(data, 6 * 8) ? 1 : 0));
            readings.Add(NewReading(ts, "prec_timer", GetInt(data, 8, false, 7)));
        }
        else if (sub == 0xF8) // Min/max cell voltage
        {
            readings.Add(NewReading(ts, "min_voltage", GetInt(data, 16, false, 0) / 1000.0, "V",
                ("cmu_num", GetInt(data, 8, false, 4).ToString()), ("cell_num", GetInt(data, 8, false, 5).ToString())));
            readings.Add(NewReading(ts, "max_voltage", GetInt(data, 16, false, 1) / 1000.0, "V",
                ("cmu_num", GetInt(data, 8, false, 6).ToString()), ("cell_num", GetInt(data, 8, false, 7).ToString())));
        }
        else if (sub == 0xF9) // Min/max cell temp
        {
            readings.Add(NewReading(ts, "min_temp", GetInt(data, 16, false, 0) / 10.0, "°C", ("cmu_num", GetInt(data, 8, false, 4).ToString())));
            readings.Add(NewReading(ts, "max_temp", GetInt(data, 16, false, 1) / 10.0, "°C", ("cmu_num", GetInt(data, 8, false, 6).ToString())));
        }
        else if (sub == 0xFA) // Pack voltage & current
        {
            double batteryVoltage = GetInt(data, 32, false, 0) / 1000.0;
            double batteryCurrent = GetInt(data, 32, true, 1) / 1000.0;
            readings.Add(NewReading(ts, "batt_volt", batteryVoltage, "V"));
            readings.Add(NewReading(ts, "batt_curr", batteryCurrent, "A"));
            readings.Add(NewReading(ts, "calc_batt_power", batteryCurrent * batteryVoltage, "W"));
        }
        else if (sub == 0xFD) // Extended pack status
        {
            readings.Add(NewReading(ts, "cell_over_voltage", GetBit(data, 0) ? 1 : 0));
            readings.Add(NewReading(ts, "cell_under_voltage", GetBit(data, 1) ? 1 : 0));
            readings.Add(NewReading(ts, "cell_over_temp", GetBit(data, 2) ? 1 : 0));
            readings.Add(NewReading(ts, "measurement_untrusted", GetBit(data, 3) ? 1 : 0));
            readings.Add(NewReading(ts, "cmu_comm_timeout", GetBit(data, 4) ? 1 : 0));
            readings.Add(NewReading(ts, "vehicle_comm_timeout", GetBit(data, 5) ? 1 : 0));
            readings.Add(NewReading(ts, "bmu_in_setup_mode", GetBit(data, 6) ? 1 : 0));
            readings.Add(NewReading(ts, "cmu_can_power", GetBit(data, 7) ? 1 : 0));
            readings.Add(NewReading(ts, "pack_isolation_fail", GetBit(data, 8) ? 1 : 0));
            readings.Add(NewReading(ts, "soc_invalid", GetBit(data, 9) ? 1 : 0));
            readings.Add(NewReading(ts, "can_power_low", GetBit(data, 10) ? 1 : 0));
            readings.Add(NewReading(ts, "contactor_stuck", GetBit(data, 11) ? 1 : 0));
            readings.Add(NewReading(ts, "unexpected_cell", GetBit(data, 12) ? 1 : 0));
            readings.Add(NewReading(ts, "bmu_hw_version", GetInt(data, 8, false, 4)));
            readings.Add(NewReading(ts, "bmu_model_id", GetInt(data, 8, false, 5)));
        }

        return readings;
    }

    private static List<Reading> DecodeMppt(short addr, byte[] data, DateTime ts)
    {
        var masked = addr & 0xFF0;
        string mpptId = masked switch
        {
            var m when m == CanConfig.Mppt1Addr => "1",
            var m when m == CanConfig.Mppt2Addr => "2",
            var m when m == CanConfig.Mppt3Addr => "3",
            var m when m == CanConfig.Mppt4Addr => "4",
            _ => $"Err:{addr}"
        };
        var tag = ("mppt_id", mpptId);

        var readings = new List<Reading>();

        switch (addr & 0xF)
        {
            case 0x0: // Input
                float mpptInVoltage = GetFloat(data, 0, false);
                float mpptInCurrent = GetFloat(data, 1, false);
                readings.Add(NewReading(ts, "mppt_in_voltage", mpptInVoltage, "V", tag));
                readings.Add(NewReading(ts, "mppt_in_current", mpptInCurrent, "A", tag));
                readings.Add(NewReading(ts, "calc_mppt_in_power", mpptInVoltage * mpptInCurrent, "W", tag));
                break;
            case 0x1: // Output
                float mpptOutVoltage = GetFloat(data, 0, false);
                float mpptOutCurrent = GetFloat(data, 1, false);
                readings.Add(NewReading(ts, "mppt_out_voltage", mpptOutVoltage, "V", tag));
                readings.Add(NewReading(ts, "mppt_out_current", mpptOutCurrent, "A", tag));
                readings.Add(NewReading(ts, "calc_mppt_out_power", mpptOutVoltage * mpptOutCurrent, "W", tag));
                break;
            case 0x2: // Temps
                readings.Add(NewReading(ts, "mppt_mosfet_temp", GetFloat(data, 0, false), "°C", tag));
                readings.Add(NewReading(ts, "mppt_controller_temp", GetFloat(data, 1, false), "°C", tag));
                break;
            case 0x3: // Aux power voltages
                readings.Add(NewReading(ts, "aux_12V_voltage", GetFloat(data, 1, true), "V", tag));
                readings.Add(NewReading(ts, "aux_3V_voltage", GetFloat(data, 0, true), "V", tag));
                break;
            case 0x4: // Limits
                readings.Add(NewReading(ts, "mppt_max_out_voltage", GetFloat(data, 1, true), "V", tag));
                readings.Add(NewReading(ts, "mppt_max_in_current", GetFloat(data, 0, true), "A", tag));
                break;
            case 0x5: // Status
                readings.Add(NewReading(ts, "mppt_can_rx_err", GetInt(data, 8, false, 0), "", tag));
                readings.Add(NewReading(ts, "mppt_can_tx_err", GetInt(data, 8, false, 1), "", tag));
                readings.Add(NewReading(ts, "mppt_can_overflow", GetInt(data, 8, false, 2), "", tag));

                readings.Add(NewReading(ts, "mppt_low_array_power", GetBit(data, 24) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_mosfet_overheat", GetBit(data, 25) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_batt_low", GetBit(data, 26) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_batt_full", GetBit(data, 27) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_12V_undervoltage", GetBit(data, 28) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_hw_over_curr", GetBit(data, 30) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_hw_over_volt", GetBit(data, 31) ? 1 : 0, "", tag));

                readings.Add(NewReading(ts, "mppt_inp_curr_min", GetBit(data, 32) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_inp_curr_max", GetBit(data, 33) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_out_volt_max", GetBit(data, 34) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_lim_mosfet_temp", GetBit(data, 35) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_dutycycle_min", GetBit(data, 36) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_dutycycle_max", GetBit(data, 37) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_local", GetBit(data, 38) ? 1 : 0, "", tag));
                readings.Add(NewReading(ts, "mppt_global", GetBit(data, 39) ? 1 : 0, "", tag));

                readings.Add(NewReading(ts, "mppt_is_on", GetBit(data, 40) ? 1 : 0, "", tag));
                break;
            case 0x6: // Power connector
                readings.Add(NewReading(ts, "mppt_conn_out_voltage", GetFloat(data, 1, true), "V", tag));
                readings.Add(NewReading(ts, "mppt_conn_temp", GetFloat(data, 0, true), "°C", tag));
                break;
        }

        return readings;
    }

    private static List<Reading> DecodeDc(short addr, byte[] data, DateTime ts)
    {
        var readings = new List<Reading>();

        switch (addr & 0xF)
        {
            case 0x0:
                readings.Add(NewReading(ts, "dc_lifesign", GetInt(data, 16, false, 0)));
                readings.Add(NewReading(ts, "dc_potentiometer", GetInt(data, 16, false, 1)));
                readings.Add(NewReading(ts, "dc_acceleration", GetInt(data, 16, false, 2)));
                readings.Add(NewReading(ts, "dc_deceleration", GetInt(data, 16, false, 3)));
                break;
            case 0x1:
                readings.Add(NewReading(ts, "targetspeed", GetInt(data, 16, false, 0)));
                readings.Add(NewReading(ts, "targetpower", GetInt(data, 16, false, 1) / 1000.0));
                readings.Add(NewReading(ts, "accel_display", GetInt(data, 8, true, 4)));
                readings.Add(NewReading(ts, "speed", GetInt(data, 8, false, 6)));

                // drive_direction: 1 = fwd, 0 = rwd
                readings.Add(NewReading(ts, "drive_direction", GetBit(data, 56) ? 1 : 0));
                readings.Add(NewReading(ts, "brake_pedal", GetBit(data, 57) ? 1 : 0));
                readings.Add(NewReading(ts, "motor_on", GetBit(data, 58) ? 1 : 0));
                readings.Add(NewReading(ts, "const_mode_on", GetBit(data, 59) ? 1 : 0));
                readings.Add(NewReading(ts, "driver_confirm", GetBit(data, 60) ? 1 : 0));
                break;
        }

        return readings;
    }

    private static List<Reading> DecodeAc(short addr, byte[] data, DateTime ts)
    {
        var readings = new List<Reading>
        {
            NewReading(ts, "ac_life_sign", GetInt(data, 16, false, 0)),
            NewReading(ts, "Kp", GetInt(data, 8, false, 2)),
            NewReading(ts, "Ki", GetInt(data, 8, false, 3)),
            NewReading(ts, "Kd", GetInt(data, 8, false, 4)),
            // ac_mode: 1 = speed, 0 = power
            NewReading(ts, "ac_mode", GetBit(data, 33) ? 1 : 0)
        };

        return readings;
    }

    private static List<Reading> DecodeMc(short addr, byte[] data, DateTime ts)
    {
        var readings = new List<Reading>();

        switch (addr & 0xFF)
        {
            case 0x09: // ERPM, current, duty cycle
                readings.Add(NewReading(ts, "mc_erpm", GetInt(data, 32, true, 0, littleEndian: false), "erpm"));
                readings.Add(NewReading(ts, "mc_current", GetInt(data, 16, true, 2, littleEndian: false) / 10.0, "A"));
                readings.Add(NewReading(ts, "mc_duty_cycle", GetInt(data, 16, true, 3, littleEndian: false) / 1000.0));
                break;
            case 0x0e: // Ah used/charged
                readings.Add(NewReading(ts, "mc_Ah_used", GetInt(data, 32, true, 0, littleEndian: false) / 10000.0, "Ah"));
                readings.Add(NewReading(ts, "mc_Ah_charged", GetInt(data, 32, true, 1, littleEndian: false) / 10000.0, "Ah"));
                break;
            case 0x0f: // Wh used/charged
                readings.Add(NewReading(ts, "mc_Wh_used", GetInt(data, 32, true, 0, littleEndian: false) / 10000.0, "Wh"));
                readings.Add(NewReading(ts, "mc_Wh_charged", GetInt(data, 32, true, 1, littleEndian: false) / 10000.0, "Wh"));
                break;
            case 0x10: // Fet/motor temp, current in, PID position
                readings.Add(NewReading(ts, "mc_fet_temp", GetInt(data, 16, true, 0, littleEndian: false) / 10.0, "°C"));
                readings.Add(NewReading(ts, "mc_motor_temp", GetInt(data, 16, true, 1, littleEndian: false) / 10.0, "°C"));
                readings.Add(NewReading(ts, "mc_curr_in", GetInt(data, 16, true, 2, littleEndian: false) / 10.0, "A"));
                readings.Add(NewReading(ts, "mc_pid_pos", GetInt(data, 16, true, 3, littleEndian: false) / 50.0));
                break;
            case 0x1b: // Tachometer, voltage in
                readings.Add(NewReading(ts, "mc_tacho", GetInt(data, 32, true, 0, littleEndian: false) / 6.0, "EREV"));
                readings.Add(NewReading(ts, "mc_volt_in", GetInt(data, 16, true, 2, littleEndian: false) / 10.0, "V"));
                break;
        }

        return readings;
    }

    private static Reading NewReading(DateTime timestamp, string name, double value, string unit = "", params (string Key, string Value)[] tags)
        => new()
        {
            Timestamp = timestamp,
            ReadingName = name,
            Value = value,
            Unit = unit,
            Tags = tags.ToDictionary(t => t.Key, t => t.Value)
        };

    // Reads a big-endian IEEE-754 float when littleEndian is false, matching the original protocol's per-message byte order.
    private static float GetFloat(byte[] data, int index, bool littleEndian)
    {
        var span = data.AsSpan(index * 4, 4);
        return littleEndian ? BinaryPrimitives.ReadSingleLittleEndian(span) : BinaryPrimitives.ReadSingleBigEndian(span);
    }

    private static long GetInt(byte[] data, int lengthBits, bool signed, int index, bool littleEndian = true)
    {
        int numBytes = lengthBits / 8;
        var span = data.AsSpan(numBytes * index, numBytes);

        return numBytes switch
        {
            1 => signed ? (sbyte)span[0] : span[0],
            2 => littleEndian
                ? signed ? BinaryPrimitives.ReadInt16LittleEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span)
                : signed ? BinaryPrimitives.ReadInt16BigEndian(span) : BinaryPrimitives.ReadUInt16BigEndian(span),
            4 => littleEndian
                ? signed ? BinaryPrimitives.ReadInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span)
                : signed ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span),
            _ => throw new ArgumentOutOfRangeException(nameof(lengthBits))
        };
    }

    private static bool GetBit(byte[] data, int bitIndex)
        => ((data[bitIndex / 8] >> (bitIndex % 8)) & 1) == 1;
}
