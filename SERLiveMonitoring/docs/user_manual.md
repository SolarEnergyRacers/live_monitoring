# SERLiveMonitoring - User Manual

SERLiveMonitoring is a browser-based dashboard for monitoring a solar-powered race car live, over
a serial connection to the car's CAN bus. Once the app is running (see
[deployment_manual.md](deployment_manual.md)), open it in a browser at the address printed at
startup (typically `http://localhost:5240`, or `http://<host machine's LAN IP>:5240` from another
device on the same network).

The navigation drawer on the left links to six pages: **Live Overview**, **Battery**, **MPPT**,
**AC / DC**, **Analytics** and **Settings**. Use the arrow icon at the top of the drawer to
collapse/expand it.

## Connecting to the car

Live data only appears once the app is connected to the car's CAN interface over a serial port.
On the **Live Overview** page, under "Serial Connection":

1. Pick the serial port the CAN interface is attached to from the dropdown (refresh the page if a
   newly-plugged-in device doesn't show up).
2. Set the baud rate to match the interface (defaults to 115200).
3. Click **Connect**. The status chip (also shown on the Battery/MPPT/AC-DC pages) turns green
   once connected.
4. Click **Disconnect** to stop reading, e.g. before unplugging the interface.

If the connection drops or the car stops sending data, tiles keep showing the last known values -
check the status chip and the "Device communication timeout" warning (see Settings below) to tell
stale data from a live feed.

## Live Overview (`/`)

The main dashboard, shown once connected:

- **Stat tiles** - speed, battery, motor and per-MPPT power tiles, each with a small spark chart
  of recent history.
- **Coordinates tile** - the latest GPS fix, with a device selector when more than one
  GPS-reporting device has sent data (see the [API manual](api_manual.md) for how a device
  reports its position), links to view the current location or recent track on Google Maps.
- **Driver Messages** - send a short **Info** or **Warn** message to the car's dashboard display;
  the table below shows sent messages and whether/when the driver confirmed them.
- **Serial Connection** panel - see "Connecting to the car" above.
- Warnings (voltage spread, over-temperature, MPPT underperformance, communication timeout, etc.)
  are evaluated against the thresholds on the Settings page and surfaced here.

## Battery (`/battery`)

Pack-level battery telemetry: pack voltage/current/power, precharge state, contactor voltage, and
the state of each contactor/driver flag repi/gpsported by the BMS.

## MPPT (`/mppt`)

Total solar input/output power and how many MPPTs are currently active, plus a card per MPPT
channel with its individual input/output power and on/off state.

## AC / DC (`/ac-dc`)

Drive controller (DC) telemetry - speed, target speed, motor current, battery voltage, motor
on/off state and direction - alongside detailed rows of additional DC channel data.

## Analytics (`/analytics`)

Historical analysis over a selected timeframe, independent of whether the car is currently
connected (it reads from previously recorded/persisted history):

- **Timeseries chart** - pick one or more series (speed, battery/motor/solar power, etc.) to plot;
  drag on the chart to select a timeframe directly.
- **Select Timeframe** - alternatively, pick a `From event`/`To event` pair (see event timestamps
  below) and click **Use Events**, or enter an exact from/to date and time manually and click
  **Apply**. **Clear Selection** resets it.
- Once a timeframe is selected, summary tiles show distance, motor energy consumed/regenerated,
  solar energy generated, net energy delta, average energy per km and average motor power for that
  window.
- **Refresh Data** reloads the underlying history (e.g. after new data has been recorded).

For programmatic/bulk access to the same historical data (CSV or JSON), see the
[API manual](api_manual.md).

## Settings (`/settings`)

Configuration that applies immediately, without restarting the app, and is saved to disk and
restored the next time the app starts:

- **CAN Bus Addresses** - the base CAN address (hex, 0x000-0x7FF) for each device (MPPTs 1-4, BMS,
  AC, DC, motor controller). Change these if the car's CAN wiring/firmware uses non-default
  addresses.
- **Warning Thresholds** - limits that trigger the warnings shown on Live Overview: max battery
  cell voltage spread/temperature, max motor/MPPT temperature, minimum active MPPT power (below
  which a panel is treated as idle rather than underperforming), low MPPT power ratio (relative to
  other active panels), and the device communication timeout.
- **General** - number of GPS source points used when building a Google Maps track link, and the
  color theme ((Dark, Light, Solarized)). No restart needed.
