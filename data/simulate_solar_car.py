import random
import struct
import time

import serial

PORT = "COM1"
BAUDRATE = 115200
INTERVAL = 1.0

MPPT_ADDR = {1: 0x6A0, 2: 0x6B0, 3: 0x6C0, 4: 0x6D0}
MPPT_MAX_CURRENT = {1: 3.0, 2: 2.8, 3: 3.1, 4: 2.9}  # amps, panel-to-panel variance

MC_BASE_ADDR = 0x500
DC_SPEED_ADDR = 0x661
BMS_PACK_ADDR = 0x7FA

NUM_CMU = 4
CELLS_PER_CMU = 8
CELL_VOLTAGE_RANGE = (3.30, 4.20)
CMU_VOLTAGE_SUBS = {0: (0x02, 0x03), 1: (0x05, 0x06), 2: (0x08, 0x09), 3: (0x0B, 0x0C)}


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


class SolarCarState:
    def __init__(self):
        self.irradiance = 0.7
        self.mppt_current = {mppt_id: MPPT_MAX_CURRENT[mppt_id] * 0.6 for mppt_id in MPPT_ADDR}

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

    def step(self):
        self.irradiance = walk(self.irradiance, 0.05, 0.15, 1.0)
        for mppt_id, capacity in MPPT_MAX_CURRENT.items():
            target = self.irradiance * capacity
            self.mppt_current[mppt_id] = approach(self.mppt_current[mppt_id], target, 0.3, 0.05, 0, capacity)

        self.target_speed = walk(self.target_speed, 4.0, 0, 100)
        self.speed = approach(self.speed, self.target_speed, 0.25, 1.0, 0, 100)

        drag_current = 2.0 + (self.speed / 100.0) ** 2 * 20.0
        self.motor_in_current = approach(self.motor_in_current, drag_current, 0.3, 0.5, 0, 30)

        self.fet_temp = approach(self.fet_temp, 25.0 + self.motor_in_current * 1.2, 0.1, 0.3, 15, 90)
        self.motor_temp = approach(self.motor_temp, 20.0 + self.motor_in_current * 1.5, 0.08, 0.3, 15, 100)
        self.tacho = (self.tacho + int(self.speed * 6)) & 0x7FFFFFFF

        self.pack_baseline = clamp(self.pack_baseline - self.net_current * 0.0002, *CELL_VOLTAGE_RANGE)
        self.cells = [
            clamp(self.pack_baseline + off + random.uniform(-0.002, 0.002), *CELL_VOLTAGE_RANGE)
            for off in self.cell_offsets
        ]

        battery_voltage = sum(self.cells)
        total_mppt_current = sum(self.mppt_current.values())
        self.net_current = self.motor_in_current - total_mppt_current

        return battery_voltage

    def build_packets(self):
        battery_voltage = self.step()
        packets = []

        # Speed
        target_power_kw = self.target_speed / 100.0 * 1.5
        accel_display = int(clamp((self.target_speed - self.speed) * 5, -100, 100))
        flags = 0
        flags |= 1 << 0  # drive_direction: forward
        if self.motor_in_current > 0.5:
            flags |= 1 << 2  # motor_on
        flags |= 1 << 4  # driver_confirm
        speed_data = struct.pack("<HHbBBB",
                                  int(self.target_speed), int(target_power_kw * 1000),
                                  accel_display, 0, int(self.speed), flags)
        packets.append(build_packet(DC_SPEED_ADDR, speed_data))

        # MPPT output (voltage/current), voltage shared with battery bus
        for mppt_id, addr_base in MPPT_ADDR.items():
            data = struct.pack(">ff", battery_voltage, self.mppt_current[mppt_id])
            packets.append(build_packet(addr_base | 0x1, data))

        # Motor in current (+ FET/motor temps, unused PID position)
        mc_data = struct.pack(">hhhh",
                               int(self.fet_temp * 10), int(self.motor_temp * 10),
                               int(self.motor_in_current * 10), 0)
        packets.append(build_packet(MC_BASE_ADDR | 0x10, mc_data))

        # Motor in voltage (+ tachometer), voltage shared with battery bus
        mc_volt_data = struct.pack(">ihH", self.tacho, int(battery_voltage * 10), 0)
        packets.append(build_packet(MC_BASE_ADDR | 0x1B, mc_volt_data))

        # Battery pack voltage/current
        batt_data = struct.pack("<Ii", int(battery_voltage * 1000), int(self.net_current * 1000))
        packets.append(build_packet(BMS_PACK_ADDR, batt_data))

        # Battery cell voltages, 4 cells per frame
        for cmu_num, (sub_low, sub_high) in CMU_VOLTAGE_SUBS.items():
            base = cmu_num * CELLS_PER_CMU
            low_cells = self.cells[base:base + 4]
            high_cells = self.cells[base + 4:base + 8]
            packets.append(build_packet(0x700 | sub_low,
                                         struct.pack("<hhhh", *(int(v * 1000) for v in low_cells))))
            packets.append(build_packet(0x700 | sub_high,
                                         struct.pack("<hhhh", *(int(v * 1000) for v in high_cells))))

        return packets


def main():
    state = SolarCarState()

    with serial.Serial(PORT, BAUDRATE) as ser:
        next_tick = time.monotonic()
        while True:
            packets = state.build_packets()
            ser.write(b"".join(packets))

            next_tick += INTERVAL
            time.sleep(max(0.0, next_tick - time.monotonic()))


if __name__ == "__main__":
    main()
