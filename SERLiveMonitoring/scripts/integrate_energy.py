#!/usr/bin/env python3
"""Integrate solar, battery and motor power series into energy (Wh) and plot them.

Reads the persisted per-second power series (.bin files, see datastore-*/read_timeseries.py
for the format) from a datastore directory, integrates each one over time to get cumulative
energy, prints the totals, and shows/saves a diagram of power and cumulative energy over time.

Usage:
    python3 scripts/integrate_energy.py <datastore_dir> [--start ISO] [--end ISO] [--out FILE.png]

Solar power is the sum of mppt1_power .. mppt4_power (whichever of those files exist),
matching how the app's "Solar Power" series is derived (see DataManager.SumByTimestamp).
"""

from __future__ import annotations

import argparse
import struct
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

MAGIC = b"SRTS"
VERSION = 1
HEADER_SIZE = 13

MPPT_SERIES_NAMES = ["mppt1_power", "mppt2_power", "mppt3_power", "mppt4_power"]


@dataclass
class Series:
    name: str
    start_timestamp: int  # unix seconds
    values: list[float]  # one sample per second, dense (see repo datastore format notes)


def read_series(path: Path) -> Series:
    data = path.read_bytes()

    if len(data) < HEADER_SIZE:
        raise ValueError(f"{path.name}: file is too short to contain a time-series header")
    if data[:4] != MAGIC:
        raise ValueError(f"{path.name}: unrecognized file magic; expected SRTS")
    if data[4] != VERSION:
        raise ValueError(f"{path.name}: unsupported format version: {data[4]}")

    start_timestamp = struct.unpack_from("<q", data, 5)[0]
    payload = data[HEADER_SIZE:]
    if len(payload) % 8 != 0:
        raise ValueError(f"{path.name}: corrupt payload; not a whole number of doubles")

    values = list(struct.unpack(f"<{len(payload) // 8}d", payload)) if payload else []
    return Series(path.stem, start_timestamp, values)


def load_power_series(datastore_dir: Path, name: str) -> Series | None:
    path = datastore_dir / f"{name}.bin"
    if not path.exists():
        return None
    return read_series(path)


def sum_series(series_list: list[Series], label: str) -> Series | None:
    series_list = [s for s in series_list if s is not None]
    if not series_list:
        return None

    start_timestamp = min(s.start_timestamp for s in series_list)
    end_timestamp = max(s.start_timestamp + len(s.values) for s in series_list)
    length = end_timestamp - start_timestamp

    total = [0.0] * length
    for s in series_list:
        offset = s.start_timestamp - start_timestamp
        for i, value in enumerate(s.values):
            total[offset + i] += value

    return Series(label, start_timestamp, total)


def clip_to_range(series: Series, start: datetime | None, end: datetime | None) -> Series:
    first_index = 0
    last_index = len(series.values)

    if start is not None:
        first_index = max(0, int(start.timestamp()) - series.start_timestamp)
    if end is not None:
        last_index = min(len(series.values), int(end.timestamp()) - series.start_timestamp)

    first_index = min(first_index, len(series.values))
    last_index = max(last_index, first_index)

    return Series(
        series.name,
        series.start_timestamp + first_index,
        series.values[first_index:last_index],
    )


def integrate_wh(series: Series) -> list[float]:
    """Cumulative energy (Wh) assuming each sample is that second's power held for 1s."""
    cumulative = []
    running = 0.0
    for value in series.values:
        running += value / 3600.0  # Ws -> Wh for a 1-second sample
        cumulative.append(running)
    return cumulative


def parse_iso_utc(value: str) -> datetime:
    dt = datetime.fromisoformat(value)
    return dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt.astimezone(timezone.utc)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("datastore_dir", type=Path, help="Directory containing the .bin series files")
    parser.add_argument("--start", type=parse_iso_utc, help="UTC start time (ISO 8601), e.g. 2026-08-07T00:00:00")
    parser.add_argument("--end", type=parse_iso_utc, help="UTC end time (ISO 8601)")
    parser.add_argument("--out", type=Path, help="Save the diagram to this file instead of showing it")
    args = parser.parse_args()

    datastore_dir: Path = args.datastore_dir
    if not datastore_dir.is_dir():
        print(f"Not a directory: {datastore_dir}", file=sys.stderr)
        return 1

    mppt_series = [load_power_series(datastore_dir, name) for name in MPPT_SERIES_NAMES]
    solar = sum_series(mppt_series, "solar_power")
    battery = load_power_series(datastore_dir, "battery_power")
    motor = load_power_series(datastore_dir, "motor_power")

    channels = {"Solar": solar, "Battery": battery, "Motor": motor}
    missing = [name for name, series in channels.items() if series is None]
    if missing:
        print(f"No data found for: {', '.join(missing)}", file=sys.stderr)
    channels = {name: series for name, series in channels.items() if series is not None}
    if not channels:
        print("No power series found in this datastore.", file=sys.stderr)
        return 1

    channels = {name: clip_to_range(series, args.start, args.end) for name, series in channels.items()}
    channels = {name: series for name, series in channels.items() if series.values}
    if not channels:
        print("Selected time range has no data.", file=sys.stderr)
        return 1

    print("Energy totals:")
    energy_wh = {}
    for name, series in channels.items():
        cumulative = integrate_wh(series)
        energy_wh[name] = cumulative
        start_time = datetime.fromtimestamp(series.start_timestamp, timezone.utc)
        end_time = datetime.fromtimestamp(series.start_timestamp + len(series.values), timezone.utc)
        print(f"  {name:8s}: {cumulative[-1]:10.2f} Wh  ({start_time.isoformat()} to {end_time.isoformat()})")

    net_wh = energy_wh.get("Solar", [0])[-1] - energy_wh.get("Motor", [0])[-1] if "Solar" in energy_wh and "Motor" in energy_wh else None
    if net_wh is not None:
        print(f"  {'Net (Solar - Motor)':8s}: {net_wh:10.2f} Wh")

    plot_energy(channels, energy_wh, args.out)
    return 0


def plot_energy(channels: dict[str, Series], energy_wh: dict[str, list[float]], out: Path | None) -> None:
    import matplotlib.pyplot as plt
    import matplotlib.dates as mdates

    colors = {"Solar": "#FFC107", "Battery": "#26A69A", "Motor": "#FF7043"}

    fig, (ax_power, ax_energy) = plt.subplots(2, 1, sharex=True, figsize=(11, 7))

    for name, series in channels.items():
        times = [datetime.fromtimestamp(series.start_timestamp + i, timezone.utc) for i in range(len(series.values))]
        ax_power.plot(times, series.values, label=f"{name} Power", color=colors.get(name), linewidth=1)
        ax_energy.plot(times, energy_wh[name], label=f"{name} Energy", color=colors.get(name), linewidth=1.5)

    ax_power.set_ylabel("Power (W)")
    ax_power.axhline(0, color="grey", linewidth=0.8)
    ax_power.legend(loc="upper right")
    ax_power.set_title("Power over time")

    ax_energy.set_ylabel("Cumulative energy (Wh)")
    ax_energy.axhline(0, color="grey", linewidth=0.8)
    ax_energy.legend(loc="upper left")
    ax_energy.set_title("Integrated energy over time")
    ax_energy.xaxis.set_major_formatter(mdates.DateFormatter("%Y-%m-%d %H:%M", tz=timezone.utc))
    fig.autofmt_xdate()

    fig.tight_layout()

    if out is not None:
        fig.savefig(out, dpi=150)
        print(f"Saved diagram to {out}")
    else:
        plt.show()


if __name__ == "__main__":
    raise SystemExit(main())
