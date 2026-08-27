#!/usr/bin/env python3
"""Example: pull SER Live Monitoring telemetry over the network into pandas.

Demonstrates GET /api/timeseries (see SERLiveMonitoring/Endpoints/TimeseriesEndpoints.cs), which
returns one CSV row per second across every recorded channel - speed, MPPT power, motor/battery
current/voltage/power - for analysis on a separate machine, e.g. after a race. This is a minimal
starting point to copy from, not a full tool.

Usage:
    pip install requests pandas
    python fetch_timeseries.py --host 192.168.1.50:5240 --minutes 30
"""

import argparse
import io
import sys
from datetime import datetime, timedelta, timezone

import pandas as pd
import requests


def fetch_timeseries(host: str, start: datetime, end: datetime) -> pd.DataFrame:
    """Fetches [start, end) as a DataFrame indexed by UTC timestamp, one column per series."""
    response = requests.get(
        f"http://{host}/api/timeseries",
        params={"start": int(start.timestamp()), "end": int(end.timestamp())},
        timeout=30,
    )
    response.raise_for_status()

    df = pd.read_csv(io.StringIO(response.text))
    df["timestamp"] = pd.to_datetime(df["timestamp"], unit="s", utc=True)
    return df.set_index("timestamp")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Fetch SER Live Monitoring telemetry for analysis")
    parser.add_argument("--host", default="localhost:5240", help="app host:port (default: localhost:5240)")
    parser.add_argument("--minutes", type=float, default=10,
                         help="minutes of the most recent data to fetch (default: 10)")
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    end = datetime.now(timezone.utc)
    start = end - timedelta(minutes=args.minutes)

    try:
        df = fetch_timeseries(args.host, start, end)
    except requests.RequestException as error:
        print(f"Could not reach {args.host}: {error}", file=sys.stderr)
        return 1

    if df.empty:
        print("No data in that time range.", file=sys.stderr)
        return 1

    print(f"Fetched {len(df)} rows from {df.index[0]} to {df.index[-1]}\n")
    print(df.describe())

    # Example derived value: total solar power isn't a stored column, but sums trivially from the
    # four MPPT columns pandas already gave us.
    solar_total = df[["mppt1_power", "mppt2_power", "mppt3_power", "mppt4_power"]].sum(axis=1)
    print(f"\nAverage solar power over the window: {solar_total.mean():.1f} W")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
