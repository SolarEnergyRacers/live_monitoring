#!/usr/bin/env python3
"""Print a persisted SER Live Monitoring time series from this directory."""

from datetime import datetime, timezone
from pathlib import Path
import re
import struct
import sys


MAGIC = b"SRTS"
VERSION = 1
HEADER_SIZE = 13


def ask(prompt: str) -> str:
    print(prompt, end="", file=sys.stderr, flush=True)
    return input().strip()


def read_series(path: Path) -> tuple[int, list[float]]:
    data = path.read_bytes()

    if len(data) < HEADER_SIZE:
        raise ValueError("file is too short to contain a time-series header")
    if data[:4] != MAGIC:
        raise ValueError("unrecognized file magic; expected SRTS")
    if data[4] != VERSION:
        raise ValueError(f"unsupported format version: {data[4]}")

    start_timestamp = struct.unpack_from("<q", data, 5)[0]
    payload = data[HEADER_SIZE:]
    if len(payload) % 8 != 0:
        raise ValueError("corrupt payload: sample data is not a whole number of doubles")

    values = list(struct.unpack(f"<{len(payload) // 8}d", payload)) if payload else []
    return start_timestamp, values


def choose_tail_length(count: int) -> int | None:
    while True:
        try:
            response = ask(f"This series has {count} entries. Tail length to display: ")
        except EOFError:
            print("No tail length provided.", file=sys.stderr)
            return None
        try:
            length = int(response)
        except ValueError:
            print("Enter a whole number.", file=sys.stderr)
            continue

        if 1 <= length <= count:
            return length
        print(f"Enter a number from 1 to {count}.", file=sys.stderr)


def parse_utc_datetime(value: str, round_up: bool = False) -> datetime:
    match = re.fullmatch(
        r"(?P<year>\d{2}|\d{4})-(?P<month>\d{1,2})-(?P<day>\d{1,2})"
        r"(?:T(?P<hour>\d{1,2})(?::(?P<minute>\d{1,2})(?::(?P<second>\d{1,2})(?P<fraction>\.\d+)?)?)?(?P<timezone>Z|[+-]\d{2}:?\d{2})?)?",
        value,
    )
    if not match:
        raise ValueError("invalid date/time")

    parts = match.groupdict()
    year = parts["year"]
    normalized = f"20{year}" if len(year) == 2 else year
    normalized += f"-{int(parts['month']):02d}-{int(parts['day']):02d}"
    if parts["hour"] is not None:
        normalized += f"T{int(parts['hour']):02d}"
        if parts["minute"] is not None:
            normalized += f":{int(parts['minute']):02d}"
        if parts["second"] is not None:
            normalized += f":{int(parts['second']):02d}{parts['fraction'] or ''}"
        normalized += parts["timezone"] or ""

    timestamp = datetime.fromisoformat(normalized)
    if round_up:
        if parts["hour"] is None:
            timestamp = timestamp.replace(hour=23, minute=59, second=59, microsecond=999999)
        elif parts["minute"] is None:
            timestamp = timestamp.replace(minute=59, second=59, microsecond=999999)
        elif parts["second"] is None:
            timestamp = timestamp.replace(second=59, microsecond=999999)
    if timestamp.tzinfo is None:
        return timestamp.replace(tzinfo=timezone.utc)
    return timestamp.astimezone(timezone.utc)


def choose_time_range(start_timestamp: int, count: int) -> tuple[int, int] | None:
    first_time = datetime.fromtimestamp(start_timestamp, timezone.utc)
    last_time = datetime.fromtimestamp(start_timestamp + count - 1, timezone.utc)
    print(f"Available range: {first_time.isoformat()} to {last_time.isoformat()}", file=sys.stderr)
    print("Enter UTC timestamps, e.g. 2026-08-07, 20-08-07T8, or 2026-08-07T13:23:17+00:00.", file=sys.stderr)

    while True:
        try:
            start = parse_utc_datetime(ask("Start time: "))
            end = parse_utc_datetime(ask("End time: "), round_up=True)
        except EOFError:
            print("No time range provided.", file=sys.stderr)
            return None
        except ValueError:
            print("Enter valid ISO date/time values.", file=sys.stderr)
            continue

        if start > end:
            print("Start time must not be after end time.", file=sys.stderr)
            continue
        if end < first_time or start > last_time:
            print("The selected range does not overlap the available range.", file=sys.stderr)
            continue

        start = max(start, first_time)
        end = min(end, last_time)
        first_index = max(0, int((start - first_time).total_seconds()))
        last_index = min(count - 1, int((end - first_time).total_seconds()))
        return first_index, last_index + 1


def choose_display_range(start_timestamp: int, count: int) -> tuple[int, int, str] | None:
    print("1. Display tail", file=sys.stderr)
    print("2. Select a UTC date/time range", file=sys.stderr)

    while True:
        try:
            mode = ask("Select display mode: ")
        except EOFError:
            print("No display mode selected.", file=sys.stderr)
            return None

        if mode == "1":
            length = choose_tail_length(count)
            if length is None:
                return None
            return count - length, count, f"tail_length={length}"
        if mode == "2":
            selection = choose_time_range(start_timestamp, count)
            if selection is None:
                return None
            first_index, end_index = selection
            first_time = datetime.fromtimestamp(start_timestamp + first_index, timezone.utc)
            last_time = datetime.fromtimestamp(start_timestamp + end_index - 1, timezone.utc)
            return first_index, end_index, f"range={first_time.isoformat()} to {last_time.isoformat()}"
        print("Enter 1 or 2.", file=sys.stderr)


def choose_series_file(data_directory: Path) -> Path | None:
    files = sorted(data_directory.rglob("*.bin"))
    if not files:
        print("No .bin time-series files found.", file=sys.stderr)
        return None

    print("Available time-series files:", file=sys.stderr)
    for index, path in enumerate(files, start=1):
        print(f"{index}. {path.relative_to(data_directory)}", file=sys.stderr)

    while True:
        try:
            response = ask("Select a file number (or x to exit): ")
        except EOFError:
            print("No file selected.", file=sys.stderr)
            return None
        if response.lower() == "x" or not response:
            return None
        try:
            selection = int(response)
        except ValueError:
            print("Enter a whole number.", file=sys.stderr)
            continue

        if 1 <= selection <= len(files):
            return files[selection - 1]
        print(f"Enter a number from 1 to {len(files)}.", file=sys.stderr)


def main() -> int:
    data_directory = Path(__file__).resolve().parent
    path = choose_series_file(data_directory)
    if path is None:
        return 0

    try:
        start_timestamp, values = read_series(path)
    except (OSError, ValueError, struct.error) as error:
        print(f"Could not read {path.name}: {error}", file=sys.stderr)
        return 1

    if not values:
        print("This series has no entries.", file=sys.stderr)
        return 0

    display_range = choose_display_range(start_timestamp, len(values))
    if display_range is None:
        return 1
    first_index, end_index, selection_description = display_range

    print(f"; filename={path.relative_to(data_directory)}")
    print(f"; {selection_description}")
    print("timestamp,value")

    for index, value in enumerate(values[first_index:end_index], start=first_index):
        timestamp = datetime.fromtimestamp(start_timestamp + index, timezone.utc)
        print(f"{timestamp.isoformat()},{value}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())