# SERLiveMonitoring - API Manual

Swagger is implemented to explore the api: `http://<this machine's LAN IP>:5240/swagger`.

## GPS API

A GPS-reporting device (phone, tracker, etc.) can report its position to this app over HTTP.
Kestrel binds to `0.0.0.0` (see `appsettings.json`), so a phone on the same network can reach
these endpoints without any extra setup - just `http://<this machine's LAN IP>:5240/api/gps`.

All GPS points are persisted to SQLite (`gps.db`) via `GpsTrackService` and kept in `DataManager`'s
in-memory history, which powers the GPS tile and diagnostics on the Live Overview page.

### Endpoints

- `GET /api/gps` - lists the endpoints below (useful when browsing to the base URL directly).
- `POST /api/gps` - JSON body: `{ latitude, longitude, timestamp?, speedKmh?, accuracyMeters? }`.
- `GET /api/gps/latest?deviceName={0}` - returns the most recently recorded GPS point, optionally
  filtered to a single device.
- `GET /api/gps/range?from={0}&to={1}&deviceName={2}` - returns the GPS points recorded within a
  time range. `from` is required, `to` is optional (open-ended up to the newest record).
  Both accept a full ISO 8601 timestamp (e.g. `2026-09-02T19:43:04+00:00`) or a shortened prefix
  (e.g. `2026-09-02T19` or `2026-09-02`), which is floored to the start of that unit - shortened
  timestamps mark the outer limits of the range. If no offset/`Z` is given, the server's local
  time zone is assumed.
- `GET /api/gps/report` - query-string variant of the POST endpoint, for GPS-reporting apps that
  can only fire plain GET requests (e.g. a "share location via URL" feature) rather than send a
  JSON body.

#### `GET /api/gps/report`

```HTTP
GET /api/gps/report?lat={0}&lon={1}&timestamp={2}&hdop={3}&altitude={4}&speed={5}&bearing={6}&eta={7}&etfa={8}&eda={9}&edfa={10}&batproc={11}
```

| Parameter                                                      | Required | Meaning                                                                                                                           |
| -------------------------------------------------------------- | -------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `lat`                                                          | yes      | Latitude in degrees, -90..90.                                                                                                     |
| `lon`                                                          | yes      | Longitude in degrees, -180..180.                                                                                                  |
| `timestamp`                                                    | no       | Fix time, either ISO 8601 or Unix epoch milliseconds. Defaults to server time if omitted or unparseable.                          |
| `hdop`                                                         | no       | Horizontal dilution of precision; stored as`AccuracyMeters`.                                                                      |
| `speed`                                                        | no       | Speed in km/h; stored as`SpeedKmh`.                                                                                               |
| `altitude`, `bearing`, `eta`, `etfa`, `eda`, `edfa`, `batproc` | no       | Accepted for compatibility with the reporting device's URL format, but`GpsPoint` has no matching field so they are not persisted. |

Any of these parameters may be left out of the query string entirely - only `lat`/`lon` are
required to build a `GpsPoint`. A request missing or failing validation on `lat`/`lon` is logged to
the console with all the parameters it did send, to help diagnose the reporting device's URL.

## Timeseries API

- `GET /api/timeseries?start={unixSeconds}&end={unixSeconds}` - CSV export of every stored series
  (`speed`, `mppt1-4_power`, `motor_current/voltage/power`, `battery_voltage/current/power`) for
  external analysis tooling (e.g. pandas). `start`/`end` are Unix seconds.
- `GET /api/timeseries/range?from={0}&to={1}&series={2}` - JSON equivalent for querying a time
  range. `from` is required, `to` is optional (open-ended up to the newest record). Both accept a
  full ISO 8601 timestamp (e.g. `2026-09-02T19:43:04+00:00`) or a shortened prefix (e.g.
  `2026-09-02T19` or `2026-09-02`), which is floored to the start of that unit. If no offset/`Z`
  is given, the server's local time zone is assumed. `series` is an
  optional comma-separated list of series names to include (defaults to every series). Response
  shape: `{ series: string[], points: [{ timestamp, values: (number|null)[] }] }`, one `points`
  entry per distinct timestamp across the included series, `values` aligned to `series` order with
  `null` where a series has no sample at that timestamp.

### OsmAnd Tracker Configuration

Hamburger-Menu → Einstellungen → [PROFIL] → Streckenaufzeichnung → Online-Aufzeichnung

WebAdresse: `http://[IPADDRESS]:5240/api/gps/report?lat={0}&lon={1}&timestamp={2}&hdop={3}&altitude={4}&speed={5}&bearing={6}&eta={7}&etfa={8}&eda={9}&edfa={10}&batproc={11}`
Aufzeichnungsintervall: `1 Sekunde`
Zeitpuffer: `1h30min`

