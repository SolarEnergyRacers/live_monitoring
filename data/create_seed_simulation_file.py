import csv
import random
from datetime import datetime, timedelta

random.seed(42)  # Ensures reproducible output

output_file = "telemetry_100_lines.csv"
number_of_rows = 100

timestamp = datetime.fromisoformat("2026-09-05T07:17:32.44")

values = {
    "longitude": -26.0850634,
    "latitude": 27.9341261,
    "speed": 90.5,
    "battery_current": 10.0,
    "battery_voltage": 110.0,
    "mppt1_power": 400.0,
    "mppt2_power": 400.0,
    "mppt3_power": 400.0,
    "mppt4_power": 400.0,
}

headers = [
    "datetimestamp",
    "longitude",
    "latitude",
    "speed",
    "battery_current",
    "battery_voltage",
    "battery_power",
    "mppt1_power",
    "mppt2_power",
    "mppt3_power",
    "mppt4_power",
]


def move(value, maximum_change, minimum=None, maximum=None):
    """Move a value smoothly up or down by a small random amount."""
    value += random.uniform(-maximum_change, maximum_change)

    if minimum is not None:
        value = max(minimum, value)
    if maximum is not None:
        value = min(maximum, value)

    return value


with open(output_file, "w", newline="", encoding="utf-8") as csv_file:
    writer = csv.writer(csv_file)
    writer.writerow(headers)

    for row_number in range(number_of_rows):
        if row_number > 0:
            timestamp += timedelta(seconds=1)

            values["longitude"] = move(values["longitude"], 0.000025)
            values["latitude"] = move(values["latitude"], 0.000025)
            values["speed"] = move(values["speed"], 1.2, 0, 140)
            values["battery_current"] = move(
                values["battery_current"], 0.35, 0, 30
            )
            values["battery_voltage"] = move(
                values["battery_voltage"], 0.25, 90, 125
            )

            for mppt in (
                "mppt1_power",
                "mppt2_power",
                "mppt3_power",
                "mppt4_power",
            ):
                values[mppt] = move(values[mppt], 8.0, 0, 500)

        # Battery power is calculated from current and voltage.
        battery_power = (
            values["battery_current"] * values["battery_voltage"]
        )

        writer.writerow([
            timestamp.isoformat(timespec="milliseconds"),
            f'{values["longitude"]:.7f}',
            f'{values["latitude"]:.7f}',
            f'{values["speed"]:.1f}',
            f'{values["battery_current"]:.2f}',
            f'{values["battery_voltage"]:.2f}',
            f"{battery_power:.2f}",
            f'{values["mppt1_power"]:.2f}',
            f'{values["mppt2_power"]:.2f}',
            f'{values["mppt3_power"]:.2f}',
            f'{values["mppt4_power"]:.2f}',
        ])

print(f"Created {output_file} with {number_of_rows} data rows.")
