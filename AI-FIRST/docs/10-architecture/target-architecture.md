# Target Architecture

## Goal

Build a production-oriented ASP.NET Core .NET 10 application for a Tibber/Victron controller running on a Raspberry Pi.

## Layers

- `Api`: ASP.NET Core host, HTTP API, background services and dependency composition.
- `Business`: domain models, use cases, interfaces and battery decision engine.
- `Dal`: persistence and repository implementations, primarily EF Core with SQLite.
- `Frontend`: Vue.js dashboard for status, telemetry, prices and decision history.

## Dependency Direction

- `Api` may reference `Business` and `Dal`.
- `Dal` may reference `Business`.
- `Business` must not reference `Api` or `Dal`.
- Hardware and external API access must be behind interfaces owned by `Business`.

## Architectural Rules

- Keep business logic framework-independent where practical.
- Keep business logic out of controllers and endpoints.
- Use DTOs for every exchange with the frontend.
- Keep hardware access behind interfaces.
- Validate live telemetry through interfaces before every real control decision.
- Use interfaces at architectural boundaries, especially for hardware, external APIs, persistence, time and test seams.
- Keep external-service failures explicit and logged.
- Store decision inputs and reasons so every battery decision remains explainable.
- Persist controller configuration that affects decisions, including total battery capacity in kWh.
- Persist required access data in the database so the running system can be configured through the frontend.
- Make frontend-editable configuration available only through DTO-based API contracts.
- Never expose sensitive persisted values through frontend read DTOs.
- Use UTC internally and convert to `Europe/Berlin` at display/API edges.
- Keep the layer model readable: avoid spaghetti code and avoid excessive abstraction layers.
