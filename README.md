# SER Live Monitoring

A live telemetry dashboard for SER solar cars. It reads CAN-bus data streamed
over a serial/radio link and shows battery, solar (MPPT), motor, and driving telemetry in real time,
with automatic warnings when something drifts out of range.

## Features

- **Home** - overview of stats, speed/power lines, and live warnings (fault flags, over-temp,
  cell voltage spread, MPPT underperformance, lost communication with a device).
- **Battery** - pack voltage/current, per-cell voltages across all CMUs, precharge/contactor status,
  min/max cell voltage and temperature.
- **MPPT** - per-panel input/output power, temperatures, aux rails, limits, and fault flags for all
  four solar charge controllers.
- **AC/DC** - Aux Controller and Drive Controller telemetry.
- **Analytics** - query any historical time window for distance, energy consumed/generated, and a
  speed-vs-power breakdown, with user-markable event timestamps.
- **Driver messaging** - push short info/warning text to the car's dash display, with confirmation
  tracking.
- **GPS tracking** - accepts live location fixes posted from an external device (e.g. a phone) over
  a simple REST endpoint.
- **Settings** - CAN-bus addresses and warning thresholds are editable at runtime, no restart needed.

## Requirements

- .NET 10 SDK
- A serial connection to the XBee radio module or directly to the AC

## Getting started

```bash
git clone https://github.com/SolarEnergyRacers/live_monitoring.git
cd live_monitoring/SERLiveMonitoring
dotnet run
```

The app listens on `http://0.0.0.0:5240` and opens in your browser automatically. From the Home page,
pick the serial port the car (or simulator) is connected to and hit Connect.

## Running tests

```bash
cd SERLiveMonitoring
dotnet test
```

## Developing without a car

`data/simulate_solar_car.py` generates realistic, physically-consistent fake telemetry over a serial
port, so you can develop and test the dashboard without real hardware. Point it at one end of a
null-modem/virtual COM port pair and connect the app to the other end.

```bash
pip install pyserial
python data/simulate_solar_car.py                # simulate every device
python data/simulate_solar_car.py --mode ac_dc    # simulate only the AC + DC units
python data/simulate_solar_car.py --mode no_mc    # simulate everything except the motor controller
python data/simulate_solar_car.py --radio         # also simulate a lossy radio link
```

It also randomly triggers (and clears) warning conditions on a timer - cell over-voltage, over-temp,
comm loss, MPPT underperformance, and more - so the warnings UI can be exercised without waiting for
something to actually go wrong.

## Configuration

Two separate places control app behavior:

- **Settings page** (`/settings`) - CAN bus addresses per device and warning thresholds. Takes effect
  immediately, persisted to a JSON file under `%LocalAppData%`.
- **`appsettings.json`** - infrastructure config: the port Kestrel listens on and where timeseries/GPS
  history is stored on disk. Requires a restart to take effect.

## GPS API

A GPS-reporting device (phone, tracker, etc.) can report its position to this app over HTTP.
Kestrel binds to `0.0.0.0` (see `appsettings.json`), so a phone on the same network can reach
these endpoints without any extra setup - just `http://<this machine's LAN IP>:5240/api/gps`.

All GPS points are persisted to SQLite (`gps.db`) via `GpsTrackService` and kept in `DataManager`'s
in-memory history, which powers the GPS tile and diagnostics on the Live Overview page.

### Endpoints

- `GET /api/gps` - lists the endpoints below (useful when browsing to the base URL directly).
- `POST /api/gps` - JSON body: `{ latitude, longitude, timestamp?, speedKmh?, accuracyMeters? }`.
- `GET /api/gps/latest` - returns the most recently recorded GPS point.
- `GET /api/gps/report` - query-string variant of the POST endpoint, for GPS-reporting apps that
  can only fire plain GET requests (e.g. a "share location via URL" feature) rather than send a
  JSON body.

#### `GET /api/gps/report`

```HTTP
GET /api/gps/report?lat={0}&lon={1}&timestamp={2}&hdop={3}&altitude={4}&speed={5}&bearing={6}&eta={7}&etfa={8}&eda={9}&edfa={10}&batproc={11}
```

| Parameter                                                                    | Required | Meaning                                                                                                                             |
| ---------------------------------------------------------------------------- | -------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| `lat`                                                                      | yes      | Latitude in degrees, -90..90.                                                                                                       |
| `lon`                                                                      | yes      | Longitude in degrees, -180..180.                                                                                                    |
| `timestamp`                                                                | no       | Fix time, either ISO 8601 or Unix epoch milliseconds. Defaults to server time if omitted or unparseable.                            |
| `hdop`                                                                     | no       | Horizontal dilution of precision; stored as`AccuracyMeters`.                                                                      |
| `speed`                                                                    | no       | Speed in km/h; stored as`SpeedKmh`.                                                                                               |
| `altitude`, `bearing`, `eta`, `etfa`, `eda`, `edfa`, `batproc` | no       | Accepted for compatibility with the reporting device's URL format, but`GpsPoint` has no matching field so they are not persisted. |

Any of these parameters may be left out of the query string entirely - only `lat`/`lon` are
required to build a `GpsPoint`. A request missing or failing validation on `lat`/`lon` is logged to
the console with all the parameters it did send, to help diagnose the reporting device's URL.

### OsmAnd Tracker Configuration

Hamburger-Menu → Einstellungen → [PROFIL] → Streckenaufzeichnung → Online-Aufzeichnung

WebAdresse: `http://[IPADDRESS]:5240/api/gps/report?lat={0}&lon={1}&timestamp={2}&hdop={3}&altitude={4}&speed={5}&bearing={6}&eta={7}&etfa={8}&eda={9}&edfa={10}&batproc={11}`
Aufzeichnungsintervall: `1 Sekunde`
Zeitpuffer: `1h30min`

## Project layout

```
SERLiveMonitoring/   ASP.NET Core Blazor Server app (.NET 10)
  Components/Pages/  One .razor page per dashboard section
  Services/          Serial ingestion, decoding, data storage, warnings, settings
  Models/            Plain data types shared across the app
  Endpoints/          Minimal API endpoints (GPS ingestion)
  SERLiveMonitoring.Tests/  xunit test suite

data/                Python CAN telemetry simulator for development
```
