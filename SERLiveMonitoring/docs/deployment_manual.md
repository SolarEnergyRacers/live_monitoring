# SERLiveMonitoring - Deployment Manual

How to build a self-contained release and install it on the machine that will run alongside the
car's CAN bus / GPS hardware during a race. See [user_manual.md](user_manual.md) for how to use the
app once it's running, and [api_manual.md](api_manual.md) for its HTTP API.

Building requires the .NET 10 SDK; the published output is self-contained, so the target machine
does not need .NET installed. Build on a machine with internet access so NuGet dependencies can be
restored.


## Deployment

From the solution directory, run the publish script with the target shortcut:

```bash
python3 ./scripts/publish_release.py            # both targets
python3 ./scripts/publish_release.py lx64       # Linux x64
python3 ./scripts/publish_release.py wx64       # Windows x64
python3 ./scripts/publish_release.py lx64 wx64  # both targets
```

Use `--dry-run` to print the generated `dotnet publish` command without running it. The script
publishes self-contained, single-file releases to `./publish/linux-x64` or
`./publish/win-x64`. Before each non-dry-run publish it removes that target's existing output and
builds from a temporary copy of the source, so stale intermediate files cannot affect the release.

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

## Installation and Start

1. Copy the entire contents of the publish output folder (`./publish/linux-x64/` or
   `./publish/win-x64/`, see above) to a folder on the target machine, e.g. `SERLiveMonitoring/`:
   - `SERLiveMonitoring` (Linux) or `SERLiveMonitoring.exe` (Windows) - the self-contained app.
   - `SERLiveMonitoring.staticwebassets.endpoints.json` - required alongside the executable.
   - `wwwroot/` - static assets (CSS, JS) served by the app.
   - `appsettings.json` - configuration (see below). `appsettings.example.json` is a template if
     you need to recreate it; `appsettings.Development.json` is only needed when intentionally
     running with `ASPNETCORE_ENVIRONMENT=Development` and can be omitted from a normal production
     deployment.
   - `datastore/` isn't required - it's (re)created automatically on first run if missing (see
     `Storage:DataDirectory` below).
2. Adjust `appsettings.json` for the target machine:
   - `Kestrel:Endpoints:Http:Url` - the address/port the app listens on. Defaults to
     `http://0.0.0.0:5240`, i.e. reachable from any device on the same network at
     `http://<this machine's LAN IP>:5240` - change the port here if 5240 is already in use.
   - `Storage:DataDirectory` - where persisted timeseries/GPS/event history is written and restored
     from (`datastore` in the supplied `appsettings.json`, relative to the app's working directory).
     The directory is created when data is first written and contains `.bin` timeseries files,
     `gps.db`, and `events.db`.
   - `AllowedHosts` - leave as `*` unless the app sits behind a reverse proxy with its own host
     checks.
3. Run `./SERLiveMonitoring` (Linux) or `SERLiveMonitoring.exe` (Windows) from that folder, then
   open `http://localhost:5240` (or the LAN address) in a browser.

Everything else - CAN bus addresses, warning thresholds, chart ranges, tile averaging, UI theme,
serial port selection, and the optional motor-controller setting - is configured at runtime from
the app itself and saved under the operating system's local application data directory as
`SER Live Monitoring/settings.json` (see [user_manual.md](user_manual.md)'s Settings and Live
Overview sections). It is separate from `Storage:DataDirectory` and should be backed up if
preserving UI/device settings matters.

On Linux, the account running the app must have permission to open the selected serial device,
often by being a member of the distribution's serial-device group (commonly `dialout`).

After startup, open the dashboard at `http://localhost:5240`; Swagger UI is available at
`http://localhost:5240/swagger` for the HTTP API.

## Updating a Deployment

1. Stop the running app (close the process / terminal it's running in).
2. Replace the executable, `SERLiveMonitoring.staticwebassets.endpoints.json`, and `wwwroot/` with
   the newly published versions.
3. Leave `appsettings.json` and `datastore/` in place - overwriting `appsettings.json` would lose
   the machine-specific settings from step 2 above, and `datastore/` holds all previously recorded
   race history.
4. Start the app again.

## Appendix: Manual Compilation

Run these from the solution directory:

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

### Data Seeder - Linux

```bash
cd ~/work/ser6/live_monitoring/SERLiveMonitoring/SERLiveMonitoring.Seeder

dotnet publish ./SERLiveMonitoring.Seeder.csproj -c Release -r linux-x64 --self-contained true -o ./publish/linux-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None

cd ~/work/ser6/live_monitoring/data

~/work/ser6/live_monitoring/SERLiveMonitoring/SERLiveMonitoring.Seeder/publish/linux-x64/SERLiveMonitoring.Seeder ~/work/ser6/live_monitoring/data/datastore-100lines telemetry_100_lines.csv 

```
