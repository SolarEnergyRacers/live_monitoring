import argparse
import random
import struct
import time

import serial

PORT = "COM1"
BAUDRATE = 115200
INTERVAL = 1.0

MPPT_ADDR = {1: 0x600, 2: 0x610, 3: 0x620, 4: 0x630}
MPPT_MAX_CURRENT = {1: 3.0, 2: 2.8, 3: 3.1, 4: 2.9}  # amps, panel-to-panel variance

AC_ADDR = 0x650
MC_BASE_ADDR = 0x500
DC_SPEED_ADDR = 0x661
DC_POWER_ADDR = 0x662
BMS_PACK_ADDR = 0x7FA
BMS_PRECHARGE_ADDR = 0x7F7
BMS_MINMAX_VOLTAGE_ADDR = 0x7F8
BMS_MINMAX_TEMP_ADDR = 0x7F9
BMS_STATUS_ADDR = 0x7FD

NUM_CMU = 4
CELLS_PER_CMU = 8
CELL_VOLTAGE_RANGE = (3.30, 4.20)
CMU_VOLTAGE_SUBS = {0: (0x02, 0x03), 1: (0x05, 0x06), 2: (0x08, 0x09), 3: (0x0B, 0x0C)}
CMU_HEARTBEAT_SUBS = {0: 0x01, 1: 0x04, 2: 0x07, 3: 0x0A}

BMU_HW_VERSION = 2
BMU_MODEL_ID = 1
PREC_STATE_RUN = 4

# Fault registry, mirrors SER Live Monitoring/Services/VehicleWarnings.cs.

# BMS extended-status fault bits (frame 0x7FD, bits 0-12 across bytes 0-1).
BATTERY_FD_FAULT_BITS = {
    "cell_over_voltage": (0, "Cell over-voltage"),
    "cell_under_voltage": (1, "Cell under-voltage"),
    "cell_over_temp": (2, "Cell over-temperature"),
    "measurement_untrusted": (3, "BMS measurement untrusted"),
    "cmu_comm_timeout": (4, "CMU communication timeout"),
    "vehicle_comm_timeout": (5, "Vehicle communication timeout"),
    "bmu_in_setup_mode": (6, "BMU is in setup mode"),
    "cmu_can_power": (7, "CMU CAN power fault"),
    "pack_isolation_fail": (8, "Pack isolation failure"),
    "soc_invalid": (9, "State of charge invalid"),
    "can_power_low": (10, "CAN power low"),
    "contactor_stuck": (11, "Contactor stuck"),
    "unexpected_cell": (12, "Unexpected cell detected"),
}
# Precharge-frame (0x7F7) driver-error bits, byte 0.
BATTERY_PRECHARGE_FAULT_BITS = {
    "err_cont_1_driver": (0, "Contactor 1 driver error"),
    "err_cont_2_driver": (1, "Contactor 2 driver error"),
    "err_cont_3_driver": (6, "Contactor 3 driver error"),
}
# MPPT status-frame (sub 0x5) fault bits, all within byte 3.
MPPT_STATUS_FLAG_BITS = {
    "mppt_mosfet_overheat": (1, "MOSFET overheating"),
    "mppt_12V_undervoltage": (4, "12V rail under-voltage"),
    "mppt_hw_over_curr": (6, "hardware over-current"),
    "mppt_hw_over_volt": (7, "hardware over-voltage"),
}
DEVICE_TAGS = ["Bms", "Mppt1", "Mppt2", "Mppt3", "Mppt4", "Ac", "Dc", "Mc"]

# Which devices get simulated at all, selected via --mode.
MODES = {
    "full": set(DEVICE_TAGS),
    "ac": {"Ac"},
    "ac_dc": {"Ac", "Dc"},
}

FAULT_HOLD_SECONDS = 4.0
# Temperature channels normally drift slowly (thermal inertia); while a temperature fault
# is forced, use a much faster approach rate so it actually crosses its threshold within
# the short FAULT_HOLD_SECONDS window instead of still climbing when the fault clears.
FAULT_TEMP_RATE = 0.6
# VehicleWarnings averages MPPT power over a trailing 15s window before flagging
# underperformance, so that fault needs to outlast the window to ever show up.
MPPT_UNDERPERF_HOLD_SECONDS = 20.0
# ...and comm-loss warnings only fire once a device has been silent for 5s.
COMM_TIMEOUT_HOLD_SECONDS = 7.0
FAULT_IDLE_SECONDS = (9.0, 11.0)
# Cap concurrent faults so the console stays readable and BMS/MPPT frames don't all
# blank out at once from stacked comm-loss picks.
MAX_CONCURRENT_FAULTS = 3

# --radio: simulates the real deployment's radio link, which (per SerialPortMonitorService's
# resync/buffering logic) can deliver a frame truncated - the rest of the bytes never arrive,
# so the frame is simply lost - or split across two separate over-the-air transmissions, where
# both halves land but as two distinct chunks instead of one atomic write.
RADIO_PARTIAL_PROB = 0.04
RADIO_SPLIT_PROB = 0.12
RADIO_SPLIT_DELAY_SECONDS = (0.005, 0.03)


def clamp(value, lo, hi):
    return max(lo, min(hi, value))


def walk(value, step, lo, hi):
    return clamp(value + random.uniform(-step, step), lo, hi)


def approach(value, target, rate, noise, lo, hi):
    return clamp(value + (target - value) * rate + random.uniform(-noise, noise), lo, hi)


def frame_addr_bytes(addr):
    return bytes([0xF8 | ((addr >> 8) & 0x07), addr & 0xFF])


def build_packet(addr, data):
    assert len(data) == 8
    return frame_addr_bytes(addr) + data + b"\x0a"


def send_packet(ser, packet, radio_enabled):
    """Writes one 11-byte packet to the serial port. With radio_enabled, occasionally
    mangles delivery the way a real radio link would: truncates the frame and drops the
    rest, or sends it whole but as two separate write() calls with a short gap between
    them - both are exactly what SerialPortMonitorService's buffering/resync logic
    (ExtractPackets) exists to handle."""
    if not radio_enabled:
        ser.write(packet)
        return

    roll = random.random()
    if roll < RADIO_PARTIAL_PROB:
        cut = random.randint(1, len(packet) - 1)
        ser.write(packet[:cut])
    elif roll < RADIO_PARTIAL_PROB + RADIO_SPLIT_PROB:
        cut = random.randint(1, len(packet) - 1)
        ser.write(packet[:cut])
        time.sleep(random.uniform(*RADIO_SPLIT_DELAY_SECONDS))
        ser.write(packet[cut:])
    else:
        ser.write(packet)


class SolarCarState:
    def __init__(self, enabled_devices=None):
        self.enabled_devices = enabled_devices if enabled_devices is not None else set(DEVICE_TAGS)

        self.irradiance = 0.7
        self.mppt_current = {mppt_id: MPPT_MAX_CURRENT[mppt_id] * 0.6 for mppt_id in MPPT_ADDR}
        self.mppt_mosfet_temp = {mppt_id: 32.0 for mppt_id in MPPT_ADDR}
        self.mppt_controller_temp = {mppt_id: 30.0 for mppt_id in MPPT_ADDR}

        self.speed = 40.0
        self.target_speed = 40.0

        self.motor_in_current = 8.0
        self.fet_temp = 35.0
        self.motor_temp = 30.0
        self.tacho = 0

        self.pack_baseline = 3.85
        self.cell_offsets = [random.uniform(-0.02, 0.02) for _ in range(NUM_CMU * CELLS_PER_CMU)]
        self.cells = [clamp(self.pack_baseline + off, *CELL_VOLTAGE_RANGE) for off in self.cell_offsets]

        self.net_current = 0.0

        self.cmu_serial = [100000 + i for i in range(NUM_CMU)]
        self.pcb_temp = [30.0] * NUM_CMU
        self.cell_temp = [28.0] * NUM_CMU

        self.ac_lifesign = 0

        # Active fault overrides; empty/False = normal operation. Multiple faults can be
        # active at once, so each is a set of independent instances. Set by the scheduler
        # in main() via the activate/deactivate closures from pick_fault().
        self.fault_battery_bits = set()     # keys into BATTERY_FD_FAULT_BITS / BATTERY_PRECHARGE_FAULT_BITS
        self.fault_cell_spread = False
        self.fault_cmu_overtemp = set()     # cmu indices 0-3
        self.fault_motor_overtemp = set()   # subset of {"fet", "motor"}
        self.fault_mppt_overtemp = set()    # mppt ids 1-4
        self.fault_mppt_status_bits = set()  # (mppt_id, key into MPPT_STATUS_FLAG_BITS)
        self.fault_mppt_underperf = set()   # mppt ids 1-4
        self.fault_comm_loss = set()        # subset of DEVICE_TAGS

    def step(self):
        self.irradiance = walk(self.irradiance, 0.05, 0.15, 1.0)
        for mppt_id, capacity in MPPT_MAX_CURRENT.items():
            if mppt_id in self.fault_mppt_underperf:
                self.mppt_current[mppt_id] = approach(self.mppt_current[mppt_id], 0.05, 0.5, 0.02, 0, capacity)
                continue
            target = self.irradiance * capacity
            self.mppt_current[mppt_id] = approach(self.mppt_current[mppt_id], target, 0.3, 0.05, 0, capacity)

        for mppt_id in MPPT_ADDR:
            is_faulted = mppt_id in self.fault_mppt_overtemp
            temp_target = 92.0 if is_faulted else 30.0 + self.irradiance * 15.0
            temp_rate = FAULT_TEMP_RATE if is_faulted else 0.15
            self.mppt_mosfet_temp[mppt_id] = approach(self.mppt_mosfet_temp[mppt_id], temp_target, temp_rate, 0.3, 15, 100)
            self.mppt_controller_temp[mppt_id] = approach(self.mppt_controller_temp[mppt_id],
                                                            temp_target - 4.0, temp_rate, 0.3, 15, 100)

        self.target_speed = walk(self.target_speed, 4.0, 0, 100)
        self.speed = approach(self.speed, self.target_speed, 0.25, 1.0, 0, 100)

        drag_current = 2.0 + (self.speed / 100.0) ** 2 * 20.0
        self.motor_in_current = approach(self.motor_in_current, drag_current, 0.3, 0.5, 0, 30)

        fet_target = 95.0 if "fet" in self.fault_motor_overtemp else 25.0 + self.motor_in_current * 1.2
        motor_target = 95.0 if "motor" in self.fault_motor_overtemp else 20.0 + self.motor_in_current * 1.5
        fet_rate = FAULT_TEMP_RATE if "fet" in self.fault_motor_overtemp else 0.2
        motor_rate = FAULT_TEMP_RATE if "motor" in self.fault_motor_overtemp else 0.15
        self.fet_temp = approach(self.fet_temp, fet_target, fet_rate, 0.3, 15, 100)
        self.motor_temp = approach(self.motor_temp, motor_target, motor_rate, 0.3, 15, 100)
        self.tacho = (self.tacho + int(self.speed * 6)) & 0x7FFFFFFF

        self.pack_baseline = clamp(self.pack_baseline - self.net_current * 0.0002, *CELL_VOLTAGE_RANGE)
        self.cells = [
            clamp(self.pack_baseline + off + random.uniform(-0.002, 0.002), *CELL_VOLTAGE_RANGE)
            for off in self.cell_offsets
        ]
        if self.fault_cell_spread:
            self.cells[0] = max(2.5, self.cells[0] - random.uniform(0.4, 0.6))

        cmu_temp_target = 24.0 + abs(self.net_current) * 0.3
        self.cell_temp = [
            68.0 if i in self.fault_cmu_overtemp else approach(t, cmu_temp_target, 0.05, 0.2, 15, 55)
            for i, t in enumerate(self.cell_temp)
        ]
        self.pcb_temp = [approach(t, 26.0 + abs(self.net_current) * 0.3, 0.08, 0.2, 15, 60) for t in self.pcb_temp]

        battery_voltage = sum(self.cells)
        total_mppt_current = sum(self.mppt_current.values())
        self.net_current = self.motor_in_current - total_mppt_current

        return battery_voltage

    def build_packets(self):
        battery_voltage = self.step()
        packets = []

        def active(device_tag):
            return device_tag in self.enabled_devices and device_tag not in self.fault_comm_loss

        # Speed
        if active("Dc"):
            # targetspeed is now decoded as raw/1000.0 with no unit label; transmit it as
            # mm/s (km/h -> m/s -> *1000) so it has headroom up to ~236 km/h instead of
            # overflowing the u16 at ~65.5 if it stayed a plain km/h*1000 value.
            targetspeed_raw = int(self.target_speed * 1000 / 3.6)
            target_power_kw = self.target_speed / 100.0 * 1.5
            accel_display = int(clamp((self.target_speed - self.speed) * 5, -100, 100))
            dc_drive = 1  # drive engaged; upstream layout doesn't document other values
            flags = 0
            flags |= 1 << 0  # drive_direction: forward
            if self.motor_in_current > 0.5:
                flags |= 1 << 2  # motor_on
            flags |= 1 << 4  # driver_confirm
            speed_data = struct.pack("<HHbBBB",
                                      targetspeed_raw, int(target_power_kw * 1000),
                                      accel_display, int(self.speed), dc_drive, flags)
            packets.append(build_packet(DC_SPEED_ADDR, speed_data))

            # dc_motor_current/dc_battery_voltage/dc_pv_voltage are whole-number echoes of
            # the same shared measurements sent elsewhere (mc_curr_in, batt_volt, mppt_out_voltage).
            dc_status_bits = 0
            if self.motor_in_current > 0.5:
                dc_status_bits |= 1 << 0  # dc_motor_on
            dc_status_bits |= 1 << 1      # dc_battery_on (contactors closed / Run state)
            dc_status_bits |= 1 << 3      # dc_pv_on (bit position per upstream decoder)
            power_data = struct.pack("<HHHBB",
                                      int(round(self.motor_in_current)), int(round(battery_voltage)),
                                      int(round(battery_voltage)), dc_status_bits, 0)
            packets.append(build_packet(DC_POWER_ADDR, power_data))

        # MPPT output/temps/status (voltage shared with battery bus)
        for mppt_id, addr_base in MPPT_ADDR.items():
            if not active(f"Mppt{mppt_id}"):
                continue

            out_data = struct.pack(">ff", battery_voltage, self.mppt_current[mppt_id])
            packets.append(build_packet(addr_base | 0x1, out_data))

            temp_data = struct.pack(">ff", self.mppt_mosfet_temp[mppt_id], self.mppt_controller_temp[mppt_id])
            packets.append(build_packet(addr_base | 0x2, temp_data))

            status_byte3 = 0
            for fault_mppt_id, name in self.fault_mppt_status_bits:
                if fault_mppt_id == mppt_id:
                    bit, _ = MPPT_STATUS_FLAG_BITS[name]
                    status_byte3 |= 1 << bit
            status_data = bytes([0, 0, 0, status_byte3, 0, 1, 0, 0])  # byte5 bit0 = mppt_is_on
            packets.append(build_packet(addr_base | 0x5, status_data))

        # AC controller heartbeat
        if active("Ac"):
            self.ac_lifesign = (self.ac_lifesign + 1) & 0xFFFF
            ac_data = struct.pack("<H", self.ac_lifesign) + bytes([10, 5, 2, 0, 0, 0])
            packets.append(build_packet(AC_ADDR, ac_data))

        # Motor in current/voltage (+ FET/motor temps, tachometer)
        if active("Mc"):
            mc_data = struct.pack(">hhhh",
                                   int(self.fet_temp * 10), int(self.motor_temp * 10),
                                   int(self.motor_in_current * 10), 0)
            packets.append(build_packet(MC_BASE_ADDR | 0x10, mc_data))

            mc_volt_data = struct.pack(">ihH", self.tacho, int(battery_voltage * 10), 0)
            packets.append(build_packet(MC_BASE_ADDR | 0x1B, mc_volt_data))

        # Everything below comes from the BMS
        if not active("Bms"):
            return packets

        batt_data = struct.pack("<Ii", int(battery_voltage * 1000), int(self.net_current * 1000))
        packets.append(build_packet(BMS_PACK_ADDR, batt_data))

        # Battery cell voltages, 4 cells per frame
        cell_mv = [int(v * 1000) for v in self.cells]
        for cmu_num, (sub_low, sub_high) in CMU_VOLTAGE_SUBS.items():
            base = cmu_num * CELLS_PER_CMU
            low_cells = cell_mv[base:base + 4]
            high_cells = cell_mv[base + 4:base + 8]
            packets.append(build_packet(0x700 | sub_low, struct.pack("<hhhh", *low_cells)))
            packets.append(build_packet(0x700 | sub_high, struct.pack("<hhhh", *high_cells)))

        # CMU heartbeat + PCB/cell temperature, one frame per CMU
        for cmu_num, sub in CMU_HEARTBEAT_SUBS.items():
            data = struct.pack("<Ihh", self.cmu_serial[cmu_num],
                                int(self.pcb_temp[cmu_num] * 10), int(self.cell_temp[cmu_num] * 10))
            packets.append(build_packet(0x700 | sub, data))

        # Precharge status / contactors (contactors closed, run state; voltage across a
        # closed contactor is near-zero, unlike the pack voltage it gates)
        prec_flags = (1 << 3) | (1 << 4) | (1 << 7)  # output_cont_1/2/3 closed
        for name in self.fault_battery_bits:
            if name in BATTERY_PRECHARGE_FAULT_BITS:
                prec_flags |= 1 << BATTERY_PRECHARGE_FAULT_BITS[name][0]
        cont_voltage = random.uniform(0.05, 0.3)
        precharge_data = struct.pack("<BBHHBB", prec_flags, PREC_STATE_RUN,
                                      int(cont_voltage * 1000), 0, 1, 0)
        packets.append(build_packet(BMS_PRECHARGE_ADDR, precharge_data))

        # Min/max cell voltage, with the CMU/cell location of each
        min_idx = min(range(len(cell_mv)), key=lambda i: cell_mv[i])
        max_idx = max(range(len(cell_mv)), key=lambda i: cell_mv[i])
        min_cmu, min_cell = divmod(min_idx, CELLS_PER_CMU)
        max_cmu, max_cell = divmod(max_idx, CELLS_PER_CMU)
        minmax_volt_data = struct.pack("<HHBBBB", cell_mv[min_idx], cell_mv[max_idx],
                                        min_cmu, min_cell, max_cmu, max_cell)
        packets.append(build_packet(BMS_MINMAX_VOLTAGE_ADDR, minmax_volt_data))

        # Min/max cell temp, with the CMU of each
        temp_ct = [int(t * 10) for t in self.cell_temp]
        min_temp_cmu = min(range(NUM_CMU), key=lambda i: temp_ct[i])
        max_temp_cmu = max(range(NUM_CMU), key=lambda i: temp_ct[i])
        minmax_temp_data = struct.pack("<HHBBBB", temp_ct[min_temp_cmu], temp_ct[max_temp_cmu],
                                        min_temp_cmu, 0, max_temp_cmu, 0)
        packets.append(build_packet(BMS_MINMAX_TEMP_ADDR, minmax_temp_data))

        # Extended pack status
        status_bits = 0
        for name in self.fault_battery_bits:
            if name in BATTERY_FD_FAULT_BITS:
                status_bits |= 1 << BATTERY_FD_FAULT_BITS[name][0]
        status_data = bytes([status_bits & 0xFF, (status_bits >> 8) & 0xFF, 0, 0,
                              BMU_HW_VERSION, BMU_MODEL_ID, 0, 0])
        packets.append(build_packet(BMS_STATUS_ADDR, status_data))

        return packets


def random_fault_candidate(state):
    """Randomly builds one VehicleWarnings.cs case and returns
    (key, label, hold_seconds, activate, deactivate). `key` uniquely identifies this
    specific fault instance (e.g. which battery flag, which MPPT) so the scheduler can
    avoid activating the same instance twice while it's already active.

    Only draws from categories whose device is actually being simulated (state.enabled_devices),
    so e.g. --mode ac never picks a battery fault when no BMS frames are being sent at all.
    Returns None if no category applies (shouldn't happen with any of the defined MODES,
    since comm_loss is always available whenever at least one device is enabled)."""
    enabled = state.enabled_devices
    enabled_mppts = [mppt_id for mppt_id in MPPT_ADDR if f"Mppt{mppt_id}" in enabled]

    categories = []
    if "Bms" in enabled:
        categories += ["battery_flag", "cell_spread", "cell_overtemp"]
    if "Mc" in enabled:
        categories.append("motor_overtemp")
    if enabled_mppts:
        categories += ["mppt_overtemp", "mppt_status", "mppt_underperf"]
    if enabled:
        categories.append("comm_loss")

    if not categories:
        return None

    category = random.choice(categories)

    if category == "battery_flag":
        labels = {**{k: v[1] for k, v in BATTERY_FD_FAULT_BITS.items()},
                  **{k: v[1] for k, v in BATTERY_PRECHARGE_FAULT_BITS.items()}}
        name = random.choice(list(labels))

        def activate():
            state.fault_battery_bits.add(name)

        def deactivate():
            state.fault_battery_bits.discard(name)

        return ("battery_flag", name), f"Battery: {labels[name]}", FAULT_HOLD_SECONDS, activate, deactivate

    if category == "cell_spread":
        def activate():
            state.fault_cell_spread = True

        def deactivate():
            state.fault_cell_spread = False

        return ("cell_spread",), "Battery: cell voltage spread exceeds limit", FAULT_HOLD_SECONDS, activate, deactivate

    if category == "cell_overtemp":
        cmu = random.randrange(NUM_CMU)

        def activate():
            state.fault_cmu_overtemp.add(cmu)

        def deactivate():
            state.fault_cmu_overtemp.discard(cmu)

        return ("cell_overtemp", cmu), f"Battery: CMU {cmu} cell over-temperature", FAULT_HOLD_SECONDS, activate, deactivate

    if category == "motor_overtemp":
        which = random.choice(["fet", "motor"])

        def activate():
            state.fault_motor_overtemp.add(which)

        def deactivate():
            state.fault_motor_overtemp.discard(which)

        label = "Motor controller FET over-temperature" if which == "fet" else "Motor over-temperature"
        return ("motor_overtemp", which), label, FAULT_HOLD_SECONDS, activate, deactivate

    if category == "mppt_overtemp":
        mppt_id = random.choice(enabled_mppts)

        def activate():
            state.fault_mppt_overtemp.add(mppt_id)

        def deactivate():
            state.fault_mppt_overtemp.discard(mppt_id)

        return ("mppt_overtemp", mppt_id), f"MPPT {mppt_id}: MOSFET over-temperature", FAULT_HOLD_SECONDS, activate, deactivate

    if category == "mppt_status":
        mppt_id = random.choice(enabled_mppts)
        name = random.choice(list(MPPT_STATUS_FLAG_BITS))

        def activate():
            state.fault_mppt_status_bits.add((mppt_id, name))

        def deactivate():
            state.fault_mppt_status_bits.discard((mppt_id, name))

        label = f"MPPT {mppt_id}: {MPPT_STATUS_FLAG_BITS[name][1]}"
        return ("mppt_status", mppt_id, name), label, FAULT_HOLD_SECONDS, activate, deactivate

    if category == "mppt_underperf":
        mppt_id = random.choice(enabled_mppts)

        def activate():
            state.fault_mppt_underperf.add(mppt_id)

        def deactivate():
            state.fault_mppt_underperf.discard(mppt_id)

        label = f"MPPT {mppt_id}: underperforming vs other panels"
        return ("mppt_underperf", mppt_id), label, MPPT_UNDERPERF_HOLD_SECONDS, activate, deactivate

    device = random.choice(list(enabled))

    def activate():
        state.fault_comm_loss.add(device)

    def deactivate():
        state.fault_comm_loss.discard(device)

    label = f"{device}: not responding (comm loss)"
    return ("comm_loss", device), label, COMM_TIMEOUT_HOLD_SECONDS, activate, deactivate


def pick_fault(state, active_keys, max_attempts=20):
    """Picks a fault candidate that isn't already active. Returns None if no free
    candidate turned up within max_attempts (collisions are rare given the size of the
    candidate space, so this only matters when most cases are already active)."""
    for _ in range(max_attempts):
        candidate = random_fault_candidate(state)
        if candidate is not None and candidate[0] not in active_keys:
            return candidate
    return None


def parse_args():
    parser = argparse.ArgumentParser(description="Solar car CAN telemetry simulator")
    parser.add_argument("--mode", choices=sorted(MODES), default="full",
                         help="Which devices to simulate: full (everything, default), "
                              "ac (AC controller only), ac_dc (AC + DC dash unit only)")
    parser.add_argument("--radio", action="store_true",
                         help="Simulate an unreliable radio link: some frames arrive truncated "
                              "(rest of the bytes lost) and some arrive whole but split across "
                              "two separate transmissions. Off by default (clean serial link).")
    return parser.parse_args()


def main():
    args = parse_args()
    enabled_devices = MODES[args.mode]

    state = SolarCarState(enabled_devices)
    active_faults = []  # list of {"key", "label", "deactivate", "clear_at"}, several can overlap
    next_fault_at = time.monotonic() + random.uniform(*FAULT_IDLE_SECONDS)

    radio_note = " with a simulated radio link (partial/split frames)" if args.radio else ""
    print(f"Solar car simulator running on {PORT} in '{args.mode}' mode "
          f"({', '.join(sorted(enabled_devices))}){radio_note} - Ctrl+C to stop.")

    with serial.Serial(PORT, BAUDRATE) as ser:
        next_tick = time.monotonic()
        while True:
            now = time.monotonic()

            still_active = []
            for fault in active_faults:
                if now >= fault["clear_at"]:
                    fault["deactivate"]()
                    print(f"[{time.strftime('%H:%M:%S')}] FAULT CLEARED : {fault['label']}")
                else:
                    still_active.append(fault)
            active_faults = still_active

            if now >= next_fault_at:
                if len(active_faults) < MAX_CONCURRENT_FAULTS:
                    active_keys = {f["key"] for f in active_faults}
                    picked = pick_fault(state, active_keys)
                    if picked is not None:
                        key, label, hold, activate, deactivate = picked
                        activate()
                        active_faults.append({"key": key, "label": label, "deactivate": deactivate, "clear_at": now + hold})
                        suffix = f"  [{len(active_faults)} active]" if len(active_faults) > 1 else ""
                        print(f"[{time.strftime('%H:%M:%S')}] FAULT ACTIVE  : {label}  (holding {hold:.0f}s){suffix}")
                next_fault_at = now + random.uniform(*FAULT_IDLE_SECONDS)

            packets = state.build_packets()
            for packet in packets:
                send_packet(ser, packet, args.radio)

            next_tick += INTERVAL
            time.sleep(max(0.0, next_tick - time.monotonic()))


if __name__ == "__main__":
    main()
