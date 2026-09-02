# SERLiveMonitoring - Deployment Manual

How to build a self-contained release and install it on the machine that will run alongside the
car's CAN bus / GPS hardware during a race. See [user_manual.md](user_manual.md) for how to use the
app once it's running, and [api_manual.md](api_manual.md) for its HTTP API.

Building requires the .NET SDK; the published output is self-contained, so the target machine does
not need .NET installed.

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

## Installation and Start

1. Copy the entire contents of the publish output folder (`./publish/linux-x64/` or
   `./publish/win-x64/`, see above) to a folder on the target machine, e.g. `SERLiveMonitoring/`:
   - `SERLiveMonitoring` (Linux) or `SERLiveMonitoring.exe` (Windows) - the self-contained app.
   - `SERLiveMonitoring.staticwebassets.endpoints.json` - required alongside the executable.
   - `wwwroot/` - static assets (CSS, JS) served by the app.
   - `appsettings.json` - configuration (see below). `appsettings.example.json` is a template if
     you need to recreate it; `appsettings.Development.json` is only used when running from source
     with `ASPNETCORE_ENVIRONMENT=Development` and can be deleted from a production deployment.
   - `datastore/` isn't required - it's (re)created automatically on first run if missing (see
     `Storage:DataDirectory` below).
2. Adjust `appsettings.json` for the target machine:
   - `Kestrel:Endpoints:Http:Url` - the address/port the app listens on. Defaults to
     `http://0.0.0.0:5240`, i.e. reachable from any device on the same network at
     `http://<this machine's LAN IP>:5240` - change the port here if 5240 is already in use.
   - `Storage:DataDirectory` - where persisted timeseries/GPS/event history is written and restored
     from (`datastore` by default, relative to the app's working directory).
   - `AllowedHosts` - leave as `*` unless the app sits behind a reverse proxy with its own host
     checks.
3. Run `./SERLiveMonitoring` (Linux) or `SERLiveMonitoring.exe` (Windows) from that folder, then
   open `http://localhost:5240` (or the LAN address) in a browser.

Everything else - CAN bus addresses, warning thresholds, UI theme, serial port selection - is
configured at runtime from the app itself (see [user_manual.md](user_manual.md)'s Settings and
Live Overview sections), not from `appsettings.json`.

## Updating a Deployment

1. Stop the running app (close the process / terminal it's running in).
2. Replace the executable, `SERLiveMonitoring.staticwebassets.endpoints.json`, and `wwwroot/` with
   the newly published versions.
3. Leave `appsettings.json` and `datastore/` in place - overwriting `appsettings.json` would lose
   the machine-specific settings from step 2 above, and `datastore/` holds all previously recorded
   race history.
4. Start the app again.
