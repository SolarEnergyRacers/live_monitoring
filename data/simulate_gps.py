#!/usr/bin/env python3
"""Simulates a GPS tracker driving through South Africa and reports its position by
sending GET /api/gps/report once a second:

    GET /api/gps/report?lat={0}&lon={1}&timestamp={2}&hdop={3}&altitude={4}&speed={5}
        &bearing={6}&eta={7}&etfa={8}&eda={9}&edfa={10}&batproc={11}

The car drives a straight line between two waypoints (default: Stellenbosch -> Bloemfontein)
and turns around at each end, so it runs indefinitely. eta/eda are relative to the current
waypoint; etfa/edfa are relative to the final destination of the current leg (same value here
since there's only one leg, but kept distinct since the endpoint expects both).

Usage:
    pip install requests
    python simulate_gps.py --host localhost:5240
"""

import argparse
import math
import random
import sys
import time
from datetime import datetime, timezone

import requests

# Stellenbosch -> Bloemfontein, both well within South Africa.
WAYPOINT_A = (-33.9321, 18.8602)
WAYPOINT_B = (-29.0852, 26.1596)

EARTH_RADIUS_M = 6_371_000

INTERVAL = 1.0
TARGET_SPEED_KMH = 90.0
SPEED_STEP_KMH = 3.0
HDOP_RANGE = (0.8, 2.5)
BATTERY_DRAIN_PER_HOUR = 8.0  # percent


def clamp(value, lo, hi):
    return max(lo, min(hi, value))


def walk(value, step, lo, hi):
    return clamp(value + random.uniform(-step, step), lo, hi)


def haversine_m(lat1, lon1, lat2, lon2):
    phi1, phi2 = math.radians(lat1), math.radians(lat2)
    dphi = math.radians(lat2 - lat1)
    dlambda = math.radians(lon2 - lon1)
    a = math.sin(dphi / 2) ** 2 + math.cos(phi1) * math.cos(phi2) * math.sin(dlambda / 2) ** 2
    return 2 * EARTH_RADIUS_M * math.asin(math.sqrt(a))


def bearing_deg(lat1, lon1, lat2, lon2):
    phi1, phi2 = math.radians(lat1), math.radians(lat2)
    dlambda = math.radians(lon2 - lon1)
    x = math.sin(dlambda) * math.cos(phi2)
    y = math.cos(phi1) * math.sin(phi2) - math.sin(phi1) * math.cos(phi2) * math.cos(dlambda)
    return (math.degrees(math.atan2(x, y)) + 360) % 360


def destination(lat, lon, bearing, distance_m):
    """Moves `distance_m` from (lat, lon) along `bearing` (degrees), returns the new point."""
    phi1, lam1 = math.radians(lat), math.radians(lon)
    theta = math.radians(bearing)
    delta = distance_m / EARTH_RADIUS_M

    phi2 = math.asin(math.sin(phi1) * math.cos(delta) + math.cos(phi1) * math.sin(delta) * math.cos(theta))
    lam2 = lam1 + math.atan2(
        math.sin(theta) * math.sin(delta) * math.cos(phi1),
        math.cos(delta) - math.sin(phi1) * math.sin(phi2),
    )
    return math.degrees(phi2), (math.degrees(lam2) + 540) % 360 - 180


class GpsState:
    def __init__(self):
        self.lat, self.lon = WAYPOINT_A
        self.target = WAYPOINT_B
        self.altitude = 900.0
        self.speed = 0.0
        self.battery = 100.0

    def step(self, dt_seconds):
        self.speed = walk(self.speed, SPEED_STEP_KMH, 0.0, TARGET_SPEED_KMH * 1.1)
        self.speed += (TARGET_SPEED_KMH - self.speed) * 0.05

        bearing = bearing_deg(self.lat, self.lon, *self.target)
        distance_remaining_m = haversine_m(self.lat, self.lon, *self.target)
        travel_m = self.speed * 1000 / 3600 * dt_seconds

        if travel_m >= distance_remaining_m:
            self.lat, self.lon = self.target
            self.target = WAYPOINT_A if self.target == WAYPOINT_B else WAYPOINT_B
        else:
            self.lat, self.lon = destination(self.lat, self.lon, bearing, travel_m)

        self.altitude = walk(self.altitude, 5.0, 0.0, 1800.0)
        self.battery = clamp(self.battery - BATTERY_DRAIN_PER_HOUR * dt_seconds / 3600, 0.0, 100.0)

        eda_km = haversine_m(self.lat, self.lon, *self.target) / 1000
        eta_seconds = eda_km / max(self.speed, 1.0) * 3600
        return bearing, eda_km, eta_seconds


def send_report(host, state, bearing, eda_km, eta_seconds):
    now = datetime.now(timezone.utc)
    params = {
        "lat": round(state.lat, 6),
        "lon": round(state.lon, 6),
        "timestamp": int(now.timestamp()),
        "hdop": round(random.uniform(*HDOP_RANGE), 2),
        "altitude": round(state.altitude, 1),
        "speed": round(state.speed, 1),
        "bearing": round(bearing, 1),
        "eta": int(now.timestamp() + eta_seconds),
        "etfa": int(now.timestamp() + eta_seconds),
        "eda": round(eda_km, 3),
        "edfa": round(eda_km, 3),
        "batproc": round(state.battery, 1),
    }
    response = requests.get(f"http://{host}/api/gps/report", params=params, timeout=5)
    response.raise_for_status()
    if response.text.strip():
        print(f"  -> {response.status_code} {response.text.strip()}")
    return params


def parse_args():
    parser = argparse.ArgumentParser(description="Simulate a GPS tracker driving through South Africa")
    parser.add_argument("--host", default="192.168.248.212:5240", help="app host:port (default: localhost:5240)")
    parser.add_argument("--interval", type=float, default=INTERVAL, help="seconds between reports (default: 1)")
    return parser.parse_args()


def main():
    args = parse_args()
    state = GpsState()

    print(f"Reporting to http://{args.host}/api/gps/report every {args.interval}s, Ctrl+C to stop.")
    try:
        while True:
            start = time.monotonic()
            bearing, eda_km, eta_seconds = state.step(args.interval)
            try:
                params = send_report(args.host, state, bearing, eda_km, eta_seconds)
                print(params)
            except requests.RequestException as error:
                print(f"Could not reach {args.host}: {error}", file=sys.stderr)

            elapsed = time.monotonic() - start
            time.sleep(max(0.0, args.interval - elapsed))
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()