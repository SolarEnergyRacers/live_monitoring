# SERLiveMonitoring

A Blazor Server dashboard for live-monitoring a solar-powered race car during a race. It decodes
CAN bus frames from the car (MPPT solar chargers, battery, motor) into timeseries data, tracks the
car's GPS position, surfaces driver messages and vehicle warnings, and exposes GPS/timeseries data
over a small HTTP API for external tooling.

- [docs/deployment_manual.md](docs/deployment_manual.md) - how to build, install and update a
  release.
- [docs/user_manual.md](docs/user_manual.md) - how to use the dashboard's pages and settings.
- [docs/api_manual.md](docs/api_manual.md) - the GPS and timeseries HTTP APIs.
