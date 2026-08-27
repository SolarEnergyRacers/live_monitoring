# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

This repo has two independent parts:

- **`SERLiveMonitoring/`** — an ASP.NET Core Blazor Server app (.NET 10) that displays live telemetry
  for a solar race car, received as CAN-bus frames over a serial/radio link.
- **`data/`** — standalone Python scripts, independent of each other: `simulate_solar_car.py`
  fabricates wire-format CAN traffic over a real/virtual COM port so the app can be developed and
  tested without real vehicle hardware; `read_timeseries.py` reads a persisted `.bin` series file
  directly off disk; `fetch_timeseries.py` is a minimal example of pulling data from the app's
  `/api/timeseries` export endpoint into pandas.

There is no root-level solution file; all `dotnet` commands run from inside `SERLiveMonitoring/`.

## Commands

```bash
# Build
cd SERLiveMonitoring && dotnet build

# Run the app (serves on http://0.0.0.0:5240, opens a browser)
cd SERLiveMonitoring && dotnet run

# Run all tests (xunit, in SERLiveMonitoring.Tests)
cd SERLiveMonitoring && dotnet test

# Run a single test class or method
dotnet test --filter "FullyQualifiedName~CANFrameDecoderTests"
dotnet test --filter "FullyQualifiedName~CANFrameDecoderTests.DecodesBatteryVoltage"
```

```bash
# Run the telemetry simulator against a COM port (needs a null-modem/virtual COM pair,
# with the app's Home page connected to the other end)
pip install pyserial
python data/simulate_solar_car.py                  # every device, full telemetry
python data/simulate_solar_car.py --mode ac         # only the AC controller frame
python data/simulate_solar_car.py --mode ac_dc      # only AC + DC dash-unit frames
python data/simulate_solar_car.py --mode no_mc      # every device except the motor controller
python data/simulate_solar_car.py --radio           # also simulate a lossy/split radio link
```

```bash
# Pull timeseries data over the network for analysis (see /api/timeseries below)
pip install requests pandas
python data/fetch_timeseries.py --host localhost:5240 --minutes 30
```

## Architecture

### Wire format and ingestion pipeline

Every CAN frame is fixed at 11 bytes on the serial link: `[sync+addrHi][addrLo][8 data bytes][0x0A]`.
The sync byte's top 5 bits are always `1`; its low 3 bits plus the next byte form the 11-bit CAN
address. `SerialPortMonitorService` buffers raw bytes across however many chunks they arrive in and
resyncs on that sync marker + terminator (`ExtractPackets`) — this matters because the real radio
link can (and the Python simulator's `--radio` flag deliberately does) split one frame across two
separate reads or truncate one entirely.

Flow: `SerialPortMonitorService` (raw bytes) → `IDataDecoder`/`CANFrameDecoder` (11-byte packet →
`List<Reading>`, tagged e.g. `mppt_id`/`cmu_num`) → `DataManager` (the single in-process source of
truth for the UI) → Blazor pages.

`CANFrameDecoder.cs` dispatches on the masked address (configurable per-device base addresses, see
below) to per-device decode methods (BMS, MPPT ×4, AC, DC, MC). Every recognized frame also emits a
synthetic `device_heartbeat` reading regardless of sub-address, which is what drives the
comm-loss/"not responding" warnings.

**The frame layouts in `CANFrameDecoder.cs` and the packing logic in `data/simulate_solar_car.py`
are two independent implementations of the same wire format with no shared source of truth.**
Changing byte order, scaling, or bit positions on one side requires manually mirroring the change on
the other, or the simulator will silently send frames the decoder misparses or ignores.

### DataManager

`DataManager` is the app's single source of truth, fed by `SerialPortMonitorService.ReadingReceived`.
It keeps two different shapes of the same data:

- A latest-value-by-key map (`GetLatest`/`GetLatestSingle`, keyed by reading name + sorted tags) for
  "current state" pages (Battery, Mppt, AcDc, Home stat tiles).
- A fixed set of named 1Hz `TimeSeries` (`ChartSeries` enum: speed, per-MPPT power, motor/battery
  power, etc.) for chart pages (Home sparklines, Analytics). Only a hardcoded subset of reading names
  feeds these series (see `UpdateTimeseries`) — adding a new plottable signal means adding both a
  `_series` entry and a case in that switch.

It also owns driver messages (see below) and GPS points, and raises change events
(`ReadingsAdded`, `DriverMessagesChanged`, `GpsPointAdded`) rather than the UI polling it directly.

When `SettingsService.Current.NoMcCanData` is set (motor controller doesn't put current/power on the
CAN bus), `DataManager` ignores incoming `mc_curr_in`/`mc_volt_in` and instead derives motor
current/power from the pack's current balance: `motor_current = battery_current + total MPPT output
current` (battery current is already net of MPPT contribution — see `data/simulate_solar_car.py`'s
`net_current = motor_in_current - total_mppt_current`), times battery voltage as a stand-in for motor
voltage (same shared bus). See `UpdateDerivedMotorPower`.

### Persistence

Two unrelated persistence mechanisms:

- `PersistenceService` periodically drains new `DataManager` timeseries points and appends them to
  one binary file per series (`<series>.bin`, custom append-only format — magic header once, then raw
  doubles) under a data directory. Restores everything on startup before the UI can connect. The data
  directory comes from `appsettings.json`'s `Storage:DataDirectory` (infra config, restart to change).
- GPS fixes and user-marked event timestamps are persisted separately, to SQLite
  (`GpsTrackService`/`EventTimestampService`), in the same data directory.

### Settings vs. appsettings.json

Two separate, easily-confused config layers:

- `SettingsService` manages `CanAddressSettings` (per-device base CAN addresses), `WarningThresholds`,
  `GoogleMapsSourcePointCount` (see GPS below), and `NoMcCanData` (see DataManager above), persisted
  as JSON under `%LocalAppData%\SER Live Monitoring\settings.json`, editable live from `/settings`
  with no restart — other services always read `SettingsService.Current`.
- ASP.NET's own `appsettings.json` covers infra-level config (Kestrel binding, `Storage:DataDirectory`)
  that's read once at startup.

Known inconsistency: `data/simulate_solar_car.py`'s hardcoded `MPPT_ADDR`/`AC_ADDR` do not currently
match `CanAddressSettings`'s defaults — check both before assuming simulated frames will decode.

### Warnings

`VehicleWarnings.Evaluate` (called fresh on every Home page render) checks `DataManager`'s latest
readings against `WarningThresholds` plus a fixed set of BMS/MPPT fault bits. Most checks are
snapshot comparisons, but MPPT underperformance and comm-loss are time-window checks (trailing
average / time-since-last-heartbeat), so they only trigger once the underlying condition has held
for several seconds — a single bad reading won't do it.

### Driver messages

The app can push short text to the car's physical dash over the same serial link
(`SerialPortMonitorService.SendInfo`/`SendWarn`, protocol `:<text>` / `!<text>`) and toggle
speed-up/down arrows. The dash reflects a "confirm" back as a CAN bit (`driver_confirm`); `DataManager`
watches for its rising edge (not just "is set", since the car holds it high for a few seconds) and
marks the most recently-sent unconfirmed message as confirmed.

### GPS

Entirely separate from the CAN telemetry path: an external device (e.g. a phone or a tracker app
like OsmAnd) reports fixes to this app over HTTP (`Endpoints/GpsEndpoints.cs`, CORS wide open, no
auth — trusted-network tool only), persisted via `GpsTrackService` (SQLite) and pushed into
`DataManager` for the UI. Two ingestion routes for the same data: `POST /api/gps` (JSON body) for
clients that can send one, and `GET /api/gps/report` (query-string params) for reporting apps that
can only fire a plain GET/"share location via URL" request — see the root README for the exact query
format and OsmAnd configuration steps.

The Home page's "Coordinates" tile also renders a Google Maps track link built from the most recent
GPS points (`DataManager.GetLastGpsPoints` → `GoogleMapsTrackBuilder.Build`). Google Maps directions
URLs have a practical length limit, so the builder binary-searches for the largest evenly-spaced
subsample of points that still fits under that limit rather than always linking every point; how many
recent points it starts from is `AppSettings.GoogleMapsSourcePointCount`, editable on `/settings`.

### Timeseries export API

`GET /api/timeseries?start={unixSeconds}&end={unixSeconds}` (`Endpoints/TimeseriesEndpoints.cs`)
returns every series `DataManager` tracks as one CSV table — one row per second, one column per
series (see `DataManager.SeriesNames`) — for external analysis tooling (e.g. a Python script pulling
a race's data into pandas), not the Blazor UI. `DataManager.GetAllSeriesRange` fetches each series'
raw points for the range; `TimeseriesCsvBuilder` outer-joins them onto the union of timestamps
actually present (series can start/stop recording at slightly different times, e.g. a device
connecting late) and leaves a blank cell — not 0 — wherever a series has no value at a given second,
so `pandas.read_csv` reads it as NaN rather than a false zero reading. Same trusted-network/no-auth
assumption as the GPS API, no CORS policy (irrelevant for a non-browser client like `requests`).
`data/fetch_timeseries.py` is a minimal example consumer.

### Pages (`Components/Pages/`)

Home (overview + live warnings), Battery, Mppt, AcDc — all read `DataManager`'s current-state map
directly. Analytics is different: it queries arbitrary historical time windows via
`AnalyticsDataService`, which turns raw per-second series into distance/energy summaries, a
speed-vs-power scatter, and downsampled chart series. Settings edits `SettingsService.Current`.

### Python simulator (`data/simulate_solar_car.py`)

Generates physically-consistent fake telemetry rather than random per-field noise: battery voltage,
MPPT-out voltage, and motor-in voltage are always equal (same shared bus, computed once per tick from
32 simulated cell voltages that sum to it); MPPT input current is derived from output power via a
fixed conversion efficiency; battery current is derived from the motor/MPPT power balance.

A fault-injection scheduler fires random `VehicleWarnings.cs`-triggering conditions on a timer,
several of which can be active simultaneously (battery faults, cell over-voltage/temp, motor/MPPT
overtemp, MPPT underperformance, per-device comm-loss). `--mode` restricts which devices it pretends
to be (`full`/`ac`/`ac_dc`/`no_mc`, the last for exercising the app's "No MC CAN Data" setting);
fault selection is scoped to only the devices currently enabled. `--radio`
makes frame delivery lossy the way the real radio link is, to exercise
`SerialPortMonitorService`'s resync logic.
