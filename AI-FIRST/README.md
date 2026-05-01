# Tibber Victron Controller

Production-oriented ASP.NET Core/.NET 10 controller for Tibber price data and Victron energy hardware.

## Project layout

- `src/TibberVictronController.Api`: ASP.NET Core host, HTTP API, background services and composition root.
- `src/TibberVictronController.Business`: domain models, use cases, decision engine and hardware/API abstractions.
- `src/TibberVictronController.Dal`: persistence, EF Core/SQLite implementation and repository adapters.
- `src/TibberVictronController.Frontend`: Vue.js frontend.
- `tests/*`: xUnit test projects per backend layer.
- `docs/*`: source of truth for requirements, architectural rules and delivery decisions.

## Architectural rules

- Hardware access is only allowed through interfaces defined in `Business`.
- Business logic must not depend on `Dal` or `Api`.
- Internal time handling uses UTC. Display conversion to Europe/Berlin happens at the edges.
- Battery decisions must produce structured reasons before they are executed.
- External-service fallbacks must be explicit and logged.

## Requirements and decisions

Project requirements are documented in `docs/`. When those documents change, future implementation must follow the updated rules.
