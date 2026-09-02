# SERLiveMonitoring
## Color theme

The UI color theme (Dark, Light, Solarized) is selected on the Settings page and takes effect
immediately, no restart needed. See [Services/ThemeCatalog.cs](Services/ThemeCatalog.cs) to add more.

## Deployment
### Linux

Run these from the solution directory:

Eventually `rm -rf publish bin obj`

```bash
dotnet publish ./SERLiveMonitoring.csproj -c Release -r linux-x64 \
--self-contained true \
-o ./publish/linux-x64 \
-p:PublishSingleFile=true \
-p:IncludeNativeLibrariesForSelfExtract=true \
-p:DebugType=None
```

### Windows

Run these from the solution directory:

Eventually `rm -rf publish bin obj`

```bash
dotnet publish ./SERLiveMonitoring.csproj -c Release -r win-x64 \
  --self-contained true \
  -o ./publish/win-x64 \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None
```

Outputs:

- Linux: `./publish/linux-x64/SERLiveMonitoring`
- Windows: `./publish/win-x64/SERLiveMonitoring.exe`

<!-- If the solution contains multiple executable projects, publish the desired `.csproj` instead to avoid mixing outputs:

```bash
dotnet publish ./src/MyApp/MyApp.csproj -c Release -r linux-x64 --self-contained true -o ./publish/linux-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
```

```bash
dotnet publish ./src/MyApp/MyApp.csproj -c Release -r win-x64 --self-contained true -o ./publish/win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
``` -->

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

```
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
