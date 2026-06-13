# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Does

A smart home battery controller that optimizes charging/discharging against Tibber dynamic electricity prices using Victron hardware telemetry. Runs on a Raspberry Pi as a systemd service. The product name in the frontend is **EnergyFlowPilot**.

## Commands

### Backend

```powershell
dotnet build
dotnet test
dotnet test tests/TibberVictronController.Business.Tests   # single project
dotnet test --filter "FullyQualifiedName~ClassName"         # single test class
dotnet run --project src/TibberVictronController.Api
```

### Frontend

```powershell
cd src/TibberVictronController.Frontend
npm install
npm run dev    # Vite dev server on port 5173, proxies /api to backend
npm run build
```

## Architecture

### Layers and Dependency Rules

```
Api  →  Business  ←  Dal
         ↑
     (owns all interfaces)
```

- **Api** (`src/TibberVictronController.Api`): ASP.NET Core host, endpoints, background services, DI composition. Entry point: `Program.cs`.
- **Business** (`src/TibberVictronController.Business`): Framework-independent domain logic. Owns all interfaces for hardware, external APIs, persistence, and time. Must not reference Api or Dal.
- **Dal** (`src/TibberVictronController.Dal`): EF Core/SQLite implementations of Business interfaces. Includes the Tibber API client and Victron MQTT adapter.
- **Frontend** (`src/TibberVictronController.Frontend`): Vue 3 + Vuetify 4 dashboard. API-backed only — no business logic.

### Background Services (Api layer)

- `DecisionExecutionBackgroundService` — runs the battery decision loop
- `MqttTelemetryBackgroundService` — reads live Victron telemetry via MQTT
- `BatterySavingsAccountingBackgroundService` — tracks savings over time

### Battery Decision Engine (Business layer)

Two parallel paths that share the same rules (for explainability):

1. **Forecast path** (`BatteryForecastSimulator.cs`) — calculates 15-minute slots over a future window for the frontend chart
2. **Direct decision path** (`CurrentBatteryDecisionService.cs`) — real-time decision using live telemetry

Rules: charge (cheap price window), discharge (expensive price + battery has energy), idle (missing/stale data or no clear signal). Every decision is logged with a structured rule ID and human-readable reason.

### Key Interfaces (Business/Abstractions)

- `IBatteryForecastService`, `ICurrentBatteryDecisionService` — consumed by Api
- `IBatteryStateProvider`, `ICurrentSiteTelemetryProvider` — implemented in Dal (Victron MQTT)
- `ITibberPriceForecastProvider` — implemented in Dal (Tibber API client)
- `ITimeProvider` — injected everywhere time is needed (enables test control)

### Settings and Configuration

Runtime settings (Tibber token, MQTT credentials, battery capacity, etc.) are persisted in SQLite via `ControllerSetting` entities. They are editable through the frontend API using DTO-based contracts. **Sensitive values must never be included in frontend read DTOs.**

### Timezone Convention

UTC everywhere internally. Convert to `Europe/Berlin` only at API/display boundaries.

## Coding Standards

- Error messages for logs, exceptions, API responses, and UI **must be in German**.
- No business logic in endpoints/controllers — HTTP concerns only.
- Use interfaces at architectural boundaries; do not create interfaces for every small class.
- Prefer parameter objects over constructors/methods with more than 2–3 parameters.
- Every frontend exchange uses DTOs — never expose persistence entities or domain models directly.
- Non-trivial code blocks need intent-focused comments (explain why, not what).

## Working Agreement

- Plan before coding; work in small steps.
- Change only files necessary for the current task; avoid large refactorings unless explicitly requested.
- If tests fail, stop and report — do not iterate without user input.
- Run at most one build or test pass per task.
- User requirements are documented in `docs/` — follow updated docs, not previous behavior.

## Documentation

Architecture and requirements live in `docs/`:
- `docs/00-governance/` — working agreement, coding standards, requirement handling
- `docs/10-architecture/` — target architecture, project structure, frontend architecture
- `docs/20-domain/` — battery decision engine rules, time/observability
- `docs/30-operations/` — deployment, security, persistence model, Tibber API
- `docs/40-delivery/` — testing strategy, backlog

## Deployment

Target is a Raspberry Pi running the backend as a systemd service. Deployment scripts are in `scripts/deploy/`. The frontend is built and served as static files from the Api project.
