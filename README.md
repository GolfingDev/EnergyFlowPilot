# Tibber Victron Controller

Smart battery controller for a Victron-based home energy system with Tibber dynamic electricity prices, battery forecast simulation, decision logging and a Vue dashboard.

The project is built for private residential energy optimization: it combines dynamic electricity tariffs, battery state of charge, grid import, configurable battery limits and forecast data to decide when a home battery should charge, discharge or stay idle.

## GitHub Description

ASP.NET Core and Vue home battery controller for Tibber dynamic prices, Victron energy systems, Pylontech storage, MQTT telemetry and forecast-based battery optimization.

## Topics

`tibber`, `victron`, `pylontech`, `cerbo-gx`, `multiplus-ii`, `home-energy-management`, `battery-storage`, `dynamic-electricity-prices`, `mqtt`, `aspnetcore`, `dotnet`, `vue`, `sqlite`, `raspberry-pi`, `energy-automation`, `solar-battery`

## Project Goals

- Optimize battery charging and discharging around Tibber spot prices.
- Keep every decision explainable with rule IDs and structured reasons.
- Use live Victron telemetry before any real control decision.
- Avoid battery feed-in into the grid.
- Provide a clear dashboard for current decisions, forecasts, savings and settings.
- Run as a production-oriented service on a Raspberry Pi.

## Current Hardware Context

This project is designed around a Victron/Pylontech installation with:

- 4x Pylontech US5000 LiFePO4 48 V battery modules
- 19.2 kWh battery storage package
- Pylontech brackets and connection cables
- Victron Cerbo GX MK2 for system monitoring
- Victron MultiPlus-II 48/5000/70-50 inverter/charger
- Victron ET340 three-phase energy meter, max. 65 A per phase
- VE.Can to CAN-bus BMS Type B cable, 1.8 m

The first live integration focuses on Victron MQTT telemetry. A later expansion is planned for E3/DC integration to provide reliable live PV production values.

## Features

- Battery decision engine for `Charge`, `Discharge` and `Idle`
- Tibber price forecast integration
- Forecast-based state-of-charge simulation
- Charge-window planning for low or negative electricity price slots
- Configurable battery capacity, charge limits, discharge limits and reserves
- SQLite-backed controller settings
- Sensitive setting metadata so secrets are not exposed through frontend DTOs
- Decision logging with structured reasons
- Savings accounting for daily, weekly, monthly and yearly views
- Vue dashboard with forecast chart, decision details, savings and configurable live energy view
- Health/status endpoints
- Background worker structure for scheduled decision execution
- Raspberry Pi deployment scripts and documentation

## Architecture

The solution is split into clear layers:

- `src/TibberVictronController.Api`  
  ASP.NET Core host, API endpoints, background services and dependency composition.

- `src/TibberVictronController.Business`  
  Domain models, decision rules, forecast simulation, savings calculation and interfaces.

- `src/TibberVictronController.Dal`  
  EF Core, SQLite persistence, repositories, Tibber access, weather/PV forecast providers and Victron/MQTT telemetry providers.

- `src/TibberVictronController.Frontend`  
  Vue/Vuetify dashboard for status, settings, forecasts, savings and live energy visualization.

- `tests/`  
  Unit and integration tests for business rules, API contracts and DAL behavior.

Business logic is kept behind interfaces so local development and tests do not require real Tibber, MQTT or Victron hardware.

## Decision Engine

The battery strategy is intentionally conservative:

- Charge during cheap or negative Tibber price windows.
- Discharge during expensive price windows when enough battery energy is available.
- Stay idle when required live data is missing, stale or unsafe.
- Never discharge more than the current measured grid import.
- Preserve minimum state of charge and configured planning reserves.
- Record the rule and reason behind every decision.

Forecast decisions run in 15-minute slots and simulate state of charge over time. This makes the dashboard forecast explainable and keeps it close to the same rules used by the live decision path.

## Dashboard

The frontend includes:

- current controller status
- current battery decision
- forecast chart
- decision history
- savings overview
- settings page for controller configuration
- optional live energy flow visualization with light and dark theme assets

PV production is currently hidden from live views until a reliable source is integrated. The planned E3/DC integration should provide this in a later stage.

## Development

Requirements:

- .NET SDK matching `global.json`
- Node.js and npm for the Vue frontend
- SQLite for local persistence

Build frontend:

```powershell
cd src/TibberVictronController.Frontend
npm install
npm run build
```

Run backend tests:

```powershell
dotnet test
```

Start frontend dev server:

```powershell
cd src/TibberVictronController.Frontend
npm run dev
```

## Security

- Do not commit real Tibber tokens, MQTT credentials or production secrets.
- Runtime settings and access data are persisted through the application settings model.
- Sensitive values must not be returned to the frontend in plain text.
- Production SQLite database files should be protected by OS-level permissions.

## Deployment Target

The intended production setup is a Raspberry Pi running:

- ASP.NET Core service
- SQLite database
- MQTT-based Victron telemetry access
- Tibber API integration
- systemd service
- optional nginx reverse proxy

Deployment helper scripts live in `scripts/deploy/`.

## Status

This is an active private energy-management project. The current focus is:

- improving the dashboard experience
- tightening live telemetry handling
- expanding hardware integrations
- preparing reliable control behavior for production use

The project is not a generic plug-and-play product yet. It is tailored to the hardware and operational assumptions documented in this repository, but the architecture is intended to keep additional providers and hardware integrations replaceable.
