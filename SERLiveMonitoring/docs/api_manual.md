# SERLiveMonitoring - API Manual

The HTTP API is intended for trusted networks. It has no authentication. GPS endpoints allow
cross-origin requests so that a browser-based tracker can send fixes; do not expose this service
directly to the public internet.

## Swagger / OpenAPI

When the application is running, open Swagger UI at:

```text
http://localhost:5240/swagger
```

For a device on the same network, replace `localhost` with the server's LAN address, for example
`http://192.168.1.20:5240/swagger`. The application listens on port `5240` by default. The OpenAPI
document used by Swagger UI is available at `http://localhost:5240/swagger/v1/swagger.json`.

To try an operation in Swagger UI:

1. Expand an endpoint and select **Try it out**.
2. Enter required path or query parameters. Optional parameters can be left blank.
3. For `POST /api/gps`, enter the JSON request body, then select **Execute**.
4. Review the generated request URL, response status, headers, and response body.

Swagger describes the endpoint shapes, but it does not add authentication or change validation.
The same requests can be sent by a tracker, `curl`, or another HTTP client.

## GPS API

A GPS-reporting device (phone, tracker, etc.) can report its position to this app over HTTP.
Kestrel binds to `0.0.0.0` (see `appsettings.json`), so a phone on the same network can reach
these endpoints without any extra setup - just `http://<this machine's LAN IP>:5240/api/gps`.

All GPS points are persisted to SQLite (`gps.db`) via `GpsTrackService` and kept in `DataManager`'s
in-memory history, which powers the GPS tile and diagnostics on the Live Overview page.

### Endpoints

- `GET /api/gps` - lists the endpoints below (useful when browsing to the base URL directly).
- `POST /api/gps` - records a GPS point. JSON body:
  `{ deviceName, latitude, longitude, timestamp?, speedKmh?, accuracyMeters? }`.
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

#### `POST /api/gps`

Example request:

```http
POST /api/gps
Content-Type: application/json

{
  "deviceName": "phone-1",
  "latitude": 48.1234,
  "longitude": 11.5678,
  "timestamp": "2026-09-27T12:34:56Z",
  "speedKmh": 42.5,
  "accuracyMeters": 5.0
}
```

The response is `201 Created` and contains the stored GPS point. `deviceName`, `latitude`, and
`longitude` are required. Latitude must be between -90 and 90; longitude must be between -180 and
180. Invalid input returns `400 Bad Request`.

#### `GET /api/gps/report`

```HTTP
GET /api/gps/report?device={0}&lat={1}&lon={2}&timestamp={3}&hdop={4}&altitude={5}&speed={6}&bearing={7}&eta={8}&etfa={9}&eda={10}&edfa={11}&batproc={12}
```

| Parameter                                                      | Required | Meaning                                                                                                                              |
| -------------------------------------------------------------- | -------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `device`                                                       | yes      | Name identifying the reporting device.                                                                                              |
| `lat`                                                          | yes      | Latitude in degrees, -90..90.                                                                                                       |
| `lon`                                                           | yes      | Longitude in degrees, -180..180.                                                                                                    |
| `timestamp`                                                    | no       | Fix time, either ISO 8601 or Unix epoch milliseconds. Defaults to server time if omitted or unparseable.                            |
| `hdop`                                                         | no       | Horizontal dilution of precision; stored as `AccuracyMeters`.                                                                       |
| `speed`                                                        | no       | Speed in m/s in the reporting URL; converted to km/h and stored as `SpeedKmh`.                                                       |
| `altitude`, `bearing`, `eta`, `etfa`, `eda`, `edfa`, `batproc` | no       | Accepted for compatibility with the reporting device's URL format, but `GpsPoint` has no matching field so they are not persisted. |

Any optional parameter may be left out of the query string entirely. A request missing `device`,
`lat`, or `lon`, or failing coordinate validation, returns `400 Bad Request` and is logged to the
console with the parameters it did send.

Example:

```text
http://192.168.1.20:5240/api/gps/report?device=phone-1&lat=48.1234&lon=11.5678&speed=11.8
```

The report endpoint returns `201 Created` with the stored GPS point on success.

## Timeseries API

- `GET /api/timeseries?start={unixSeconds}&end={unixSeconds}` - CSV export of every stored series
  (`speed`, `mppt1_power`, `mppt2_power`, `mppt3_power`, `mppt4_power`, `motor_current`,
  `motor_voltage`, `motor_power`, `battery_voltage`, `battery_current`, `battery_power`) for
  external analysis tooling (e.g. pandas). `start` and `end` are Unix seconds; `end` must be after
  `start`. The response content type is `text/csv`.
- `GET /api/timeseries/range?from={0}&to={1}&series={2}` - JSON equivalent for querying a time
  range. `from` is required, `to` is optional (open-ended up to the newest record). Both accept a
  full ISO 8601 timestamp (e.g. `2026-09-02T19:43:04+00:00`) or a shortened prefix (e.g.
  `2026-09-02T19` or `2026-09-02`), which is floored to the start of that unit. If no offset/`Z`
  is given, the server's local time zone is assumed. `series` is an
  optional comma-separated list of series names to include (defaults to every series). Response
  shape: `{ series: string[], points: [{ timestamp, values: (number|null)[] }] }`, one `points`
  entry per distinct timestamp across the included series, `values` aligned to `series` order with
  `null` where a series has no sample at that timestamp.

The JSON range endpoint returns `400 Bad Request` when `from` is missing or invalid, `to` is
invalid or earlier than `from`, or a requested series name is unknown. It is useful when selecting
only a few channels, for example:

```text
http://localhost:5240/api/timeseries/range?from=2026-09-27T12:00Z&series=speed,battery_power
```

The CSV endpoint also returns `400 Bad Request` when `start` or `end` is missing, or when `end` is
not after `start`. It outer-joins timestamps across series; an empty CSV cell means that series had
no value at that timestamp, rather than a measured zero.

### OsmAnd Tracker Configuration

Hamburger-Menu → Einstellungen → [PROFIL] → Streckenaufzeichnung → Online-Aufzeichnung

WebAdresse: `http://[IPADDRESS]:5240/api/gps/report?device=osmand&lat={0}&lon={1}&timestamp={2}&hdop={3}&altitude={4}&speed={5}&bearing={6}&eta={7}&etfa={8}&eda={9}&edfa={10}&batproc={11}`
Aufzeichnungsintervall: `1 Sekunde`
Zeitpuffer: `1h30min`

